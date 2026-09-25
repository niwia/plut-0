using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pluto.Models;
using Pluto.Services;

namespace Pluto;

public partial class MainWindow : Window
{
    private readonly PluginLibraryService _libraryService;
    private readonly SlsSteamService _slsService;
    private readonly DepotKeyService _depotKeyService;
    private readonly AccelaConfigService _configService;
    private readonly GameTransitionService _transitionService;
    private readonly GamepadService _gamepadService;

    private List<PluginGame> _allGames = new();
    private ObservableCollection<PluginGame> _displayedGames = new();
    private PluginGame? _selectedGame;

    // Cached Settings
    private bool _settingVaporEnabled = true;
    private string _settingDownloadAction = "native";
    private bool _settingDisableUpdates = false;

    private enum ActiveView
    {
        MainList,
        GameDetail,
        Settings
    }

    private ActiveView _currentView = ActiveView.MainList;

    private readonly SteamlessService _steamlessService;

    public MainWindow()
    {
        InitializeComponent();

        _libraryService = new PluginLibraryService();
        _slsService = new SlsSteamService();
        _depotKeyService = new DepotKeyService();
        _configService = new AccelaConfigService();
        _transitionService = new GameTransitionService(_slsService, _libraryService, _depotKeyService);
        _gamepadService = new GamepadService();
        _steamlessService = new SteamlessService();

        GamesListBox.ItemsSource = _displayedGames;

        // Auto-refresh when plugin_library.json or games_cache.json changes on disk
        _libraryService.LibraryChanged += () =>
        {
            Dispatcher.UIThread.Post(async () => await ReloadLibraryAsync());
        };

        // Wire gamepad navigation and actions
        _gamepadService.ActionTriggered += action =>
        {
            Dispatcher.UIThread.Post(() => HandleGamepadAction(action));
        };

        _gamepadService.ControllerStateChanged += (name, connected) =>
        {
            Dispatcher.UIThread.Post(() => UpdateControllerStatus(name, connected));
        };

        Loaded += OnWindowLoaded;
        Closing += (_, _) =>
        {
            _gamepadService.Dispose();
            _libraryService.Dispose();
        };
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        PlutoLogger.Info("Pluto", "Starting PLUT-0");
        _gamepadService.Start();
        UpdateControllerStatus(_gamepadService.ActiveControllerName, _gamepadService.HasConnectedController);
        await ReloadLibraryAsync();
        LoadSettingsFromConfig();
        GamesListBox.Focus();
    }

    private async Task ReloadLibraryAsync()
    {
        var games = await _libraryService.LoadGamesAsync();
        foreach (var g in games)
        {
            g.IsSlsSynced = _slsService.IsAppConfigured(g.AppId);
        }

        _allGames = games;
        ApplyFilter(SearchBox?.Text);
        UpdateGameCountsText();
        PlutoLogger.Info("Library", $"Library updated: {_allGames.Count} total games ({_allGames.Count(g => !g.IsAccela)} at0-m, {_allGames.Count(g => g.IsAccela)} accela)");
    }

    private void ApplyFilter(string? query)
    {
        var trimmed = query?.Trim();
        IEnumerable<PluginGame> filtered = _allGames;

        if (!string.IsNullOrEmpty(trimmed))
        {
            filtered = _allGames.Where(g =>
                g.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                g.Depots.Any(d => d.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                g.InstallDir.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        _displayedGames.Clear();
        foreach (var item in list)
        {
            _displayedGames.Add(item);
        }

        if (EmptyStateText != null)
        {
            EmptyStateText.IsVisible = list.Count == 0;
            if (list.Count == 0 && !string.IsNullOrEmpty(trimmed))
            {
                EmptyStateText.Text = $"no games matching \"{trimmed}\"";
            }
            else
            {
                EmptyStateText.Text = "no games found in library";
            }
        }

        if (list.Count > 0 && GamesListBox != null)
        {
            GamesListBox.SelectedIndex = 0;
            if (GamesListBox.SelectedItem != null)
            {
                GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
            }
        }
    }

    private void UpdateControllerStatus(string name, bool connected)
    {
        if (SettingsControllerText != null)
        {
            SettingsControllerText.Text = connected ? $"connected ({name})" : "no controller detected";
        }
    }

    private void UpdateGameCountsText()
    {
        if (SettingsGameCountsText != null)
        {
            int atomCount = _allGames.Count(g => !g.IsAccela);
            int accelaCount = _allGames.Count(g => g.IsAccela);
            SettingsGameCountsText.Text = $"{atomCount} at0-m plugin games, {accelaCount} accela managed games";
        }
    }

    // Window-level Keyboard Handling (Guarantees Up / Down / Enter always work)
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_currentView == ActiveView.MainList)
        {
            if (e.Key == Key.Up && !SearchBox.IsFocused)
            {
                NavigateList(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && !SearchBox.IsFocused)
            {
                NavigateList(1);
                e.Handled = true;
            }
            else if (e.Key == Key.PageUp && !SearchBox.IsFocused)
            {
                NavigateList(-5);
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown && !SearchBox.IsFocused)
            {
                NavigateList(5);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !SearchBox.IsFocused && GamesListBox.SelectedItem is PluginGame selected)
            {
                OpenGameDetailPage(selected);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab || e.Key == Key.F1)
            {
                OpenSettingsPage();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                GamesListBox.Focus();
                e.Handled = true;
            }
        }
        else
        {
            // Inside GameDetail or Settings
            if (e.Key == Key.Escape || e.Key == Key.Back)
            {
                ShowMainList();
                e.Handled = true;
            }
        }
    }

    // View Navigation
    private void ShowMainList()
    {
        _currentView = ActiveView.MainList;
        MainListPanel.IsVisible = true;
        GameDetailPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        GamesListBox.Focus();
    }

    private void OpenGameDetailPage(PluginGame game)
    {
        _selectedGame = game;
        _currentView = ActiveView.GameDetail;

        DetailGameTitle.Text = game.Name;
        DetailGameSubtitle.Text = $"{game.AppId}  •  {(game.IsAccela ? "assella" : "native")}";

        if (game.IsAccela)
        {
            DetailSwitchModeBtn.Content = "add to plugin";
            DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
        }
        else
        {
            DetailSwitchModeBtn.Content = "move to assella";
            DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.LightSkyBlue;
        }

        // SLSonline & Netsock status
        bool isOnline = _slsService.IsSlsOnline(game.AppId);
        bool isNetsock = _slsService.IsNetsock(game.AppId);
        UpdateOnlineTogglesUi(isOnline, isNetsock);

        // Reset Steamless UI state
        DetailSteamlessBtn.IsEnabled = true;
        DetailSteamlessBtn.Content = "apply steamless";
        DetailSteamlessStatus.IsVisible = false;

        // Load Thumbnail Artwork with gradient opacity mask from ACCELA cache
        var imageCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "ACCELA", "image_cache", $"{game.AppId}.jpg");

        if (File.Exists(imageCachePath))
        {
            try
            {
                DetailGameBackdrop.Source = new Avalonia.Media.Imaging.Bitmap(imageCachePath);
                DetailGameBackdrop.IsVisible = true;
            }
            catch
            {
                DetailGameBackdrop.Source = null;
                DetailGameBackdrop.IsVisible = false;
            }
        }
        else
        {
            DetailGameBackdrop.Source = null;
            DetailGameBackdrop.IsVisible = false;
        }

        MainListPanel.IsVisible = false;
        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible = false;
    }

    private void OpenSettingsPage()
    {
        _currentView = ActiveView.Settings;

        UpdateGameCountsText();
        UpdateControllerStatus(_gamepadService.ActiveControllerName, _gamepadService.HasConnectedController);

        if (SettingsLogPathText != null)
        {
            SettingsLogPathText.Text = PlutoLogger.LogFilePath;
        }

        MainListPanel.IsVisible = false;
        GameDetailPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }

    private void HandleGamepadAction(GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.NavigateUp:
                if (_currentView == ActiveView.MainList) NavigateList(-1);
                break;

            case GamepadAction.NavigateDown:
                if (_currentView == ActiveView.MainList) NavigateList(1);
                break;

            case GamepadAction.PageUp:
                if (_currentView == ActiveView.MainList) NavigateList(-5);
                break;

            case GamepadAction.PageDown:
                if (_currentView == ActiveView.MainList) NavigateList(5);
                break;

            case GamepadAction.Confirm:
                if (_currentView == ActiveView.MainList && GamesListBox.SelectedItem is PluginGame selected)
                {
                    OpenGameDetailPage(selected);
                }
                break;

            case GamepadAction.FocusSearch:
                if (_currentView == ActiveView.MainList)
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }
                break;

            case GamepadAction.BackOrCancel:
                if (_currentView != ActiveView.MainList)
                {
                    ShowMainList();
                }
                else if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                    GamesListBox.Focus();
                }
                else
                {
                    GamesListBox.Focus();
                }
                break;

            case GamepadAction.OpenSettings:
                if (_currentView == ActiveView.MainList)
                {
                    OpenSettingsPage();
                }
                else
                {
                    ShowMainList();
                }
                break;

            case GamepadAction.SyncAll:
                SyncAllGames();
                break;
        }
    }

    private void NavigateList(int offset)
    {
        if (_displayedGames.Count == 0) return;

        int currentIndex = GamesListBox.SelectedIndex;
        if (currentIndex < 0) currentIndex = 0;

        int newIndex = (currentIndex + offset) % _displayedGames.Count;
        if (newIndex < 0) newIndex += _displayedGames.Count;

        GamesListBox.SelectedIndex = newIndex;
        var item = GamesListBox.SelectedItem;
        if (item != null)
        {
            GamesListBox.ScrollIntoView(item);
        }
    }

    private async void SyncAllGames()
    {
        PlutoLogger.Info("Pluto", "Syncing all at0-m games into config.yaml...");
        foreach (var g in _allGames.Where(g => !g.IsAccela))
        {
            await _slsService.SyncGameToConfigAsync(g);
        }
        _slsService.NotifyReload();
    }

    // UI Event Handlers
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            NavigateList(1);
            GamesListBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (GamesListBox.SelectedItem is PluginGame selected)
            {
                OpenGameDetailPage(selected);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            GamesListBox.Focus();
            e.Handled = true;
        }
    }

    private void OnGameListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GamesListBox.SelectedItem is PluginGame selected)
        {
            OpenGameDetailPage(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            NavigateList(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            NavigateList(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab || e.Key == Key.F1)
        {
            OpenSettingsPage();
            e.Handled = true;
        }
        else if (e.Key == Key.Y || e.Key == Key.OemQuestion)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GamesListBox.SelectedItem is PluginGame selected)
        {
            OpenGameDetailPage(selected);
        }
    }

    private void OnGameSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GamesListBox.SelectedItem != null)
        {
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
        }
    }

    private void OnBackToMainClicked(object? sender, RoutedEventArgs e)
    {
        ShowMainList();
    }

    private void OnOpenSettingsClicked(object? sender, RoutedEventArgs e)
    {
        OpenSettingsPage();
    }

    private async void OnToggleModeClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame != null)
        {
            if (_selectedGame.IsAccela)
            {
                PlutoLogger.Info("Transition", $"Converting {_selectedGame.AppId} ({_selectedGame.Name}) to plugin native");
                await _transitionService.ConvertToAtomPluginAsync(_selectedGame);
                _selectedGame.IsAccela = false;
            }
            else
            {
                PlutoLogger.Info("Transition", $"Reverting {_selectedGame.AppId} ({_selectedGame.Name}) to assella managed mode");
                await _transitionService.ConvertToAccelaManagedAsync(_selectedGame);
                _selectedGame.IsAccela = true;
            }

            DetailGameSubtitle.Text = $"{_selectedGame.AppId}  •  {(_selectedGame.IsAccela ? "assella" : "native")}";
            DetailSwitchModeBtn.Content = _selectedGame.IsAccela ? "add to plugin" : "move to assella";
            DetailSwitchModeBtn.Foreground = _selectedGame.IsAccela ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.LightSkyBlue;

            await ReloadLibraryAsync();
        }
    }

    private async void OnToggleSlsOnlineClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame != null)
        {
            bool current = _slsService.IsSlsOnline(_selectedGame.AppId);
            await _slsService.SetSlsOnlineAsync(_selectedGame.AppId, _selectedGame.Name, !current);
            bool nowOnline = _slsService.IsSlsOnline(_selectedGame.AppId);
            bool nowNetsock = _slsService.IsNetsock(_selectedGame.AppId);
            UpdateOnlineTogglesUi(nowOnline, nowNetsock);
        }
    }

    private async void OnToggleNetsockClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame != null)
        {
            bool current = _slsService.IsNetsock(_selectedGame.AppId);
            await _slsService.SetNetsockAsync(_selectedGame.AppId, !current);
            bool isOnline = _slsService.IsSlsOnline(_selectedGame.AppId);
            bool nowNetsock = _slsService.IsNetsock(_selectedGame.AppId);
            UpdateOnlineTogglesUi(isOnline, nowNetsock);
        }
    }

    private void UpdateOnlineTogglesUi(bool isOnline, bool isNetsock)
    {
        if (isOnline)
        {
            DetailSlsOnlineBtn.Content = "slsonline: active";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
            DetailNetsockBtn.IsVisible = true;
            DetailNetsockBtn.Content = isNetsock ? "netsock proxy: active" : "enable netsock proxy";
            DetailNetsockBtn.Foreground = isNetsock ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        else
        {
            DetailSlsOnlineBtn.Content = "enable slsonline";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.Gray;
            DetailNetsockBtn.IsVisible = false;
        }
    }

    private async void OnApplySteamlessClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame != null)
        {
            DetailSteamlessBtn.IsEnabled = false;
            DetailSteamlessBtn.Content = "processing steamless...";
            DetailSteamlessStatus.Text = "scanning and unpacking executables...";
            DetailSteamlessStatus.Foreground = Avalonia.Media.Brushes.Gray;
            DetailSteamlessStatus.IsVisible = true;

            var result = await _steamlessService.ProcessGameAsync(_selectedGame.InstallPath, _selectedGame.Name);

            DetailSteamlessBtn.IsEnabled = true;
            DetailSteamlessBtn.Content = "apply steamless";
            DetailSteamlessStatus.Text = result.Message;
            DetailSteamlessStatus.Foreground = result.Success ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
    }

    private void OnSyncAllClicked(object? sender, RoutedEventArgs e)
    {
        SyncAllGames();
    }

    private void OnReloadSlsClicked(object? sender, RoutedEventArgs e)
    {
        _slsService.NotifyReload();
    }

    // Settings Management (Native C# via AccelaConfigService)
    private void LoadSettingsFromConfig()
    {
        try
        {
            _settingVaporEnabled = _configService.GetBool("enable_vapor", true) || _configService.GetBool("enable_at0m", true);
            _settingDownloadAction = _configService.GetValue("vapor_default_download_action", "native");
            _settingDisableUpdates = _configService.GetBool("vapor_disable_updates", false) || _configService.GetBool("at0m_disable_updates", false);
            UpdateSettingsUi();
            PlutoLogger.Info("Pluto", $"Settings loaded: vapor={_settingVaporEnabled}, downloadAction={_settingDownloadAction}, disableUpdates={_settingDisableUpdates}");
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Pluto", "Failed to read settings from config", ex);
        }
    }

    private void UpdateSettingsUi()
    {
        if (ToggleVaporBtn != null)
        {
            ToggleVaporBtn.Content = _settingVaporEnabled ? "enabled" : "disabled";
            ToggleVaporBtn.Foreground = _settingVaporEnabled
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }

        if (ToggleDownloadActionBtn != null)
        {
            ToggleDownloadActionBtn.Content = _settingDownloadAction == "native" ? "native steam" : "accela downloader";
        }

        if (ToggleUpdatesBtn != null)
        {
            ToggleUpdatesBtn.Content = _settingDisableUpdates ? "disabled" : "normal (updates allowed)";
        }
    }

    private void OnToggleVaporClicked(object? sender, RoutedEventArgs e)
    {
        _settingVaporEnabled = !_settingVaporEnabled;
        UpdateSettingsUi();
        _configService.SetBool("enable_vapor", _settingVaporEnabled);
        _configService.SetBool("enable_at0m", _settingVaporEnabled);
    }

    private void OnToggleDownloadActionClicked(object? sender, RoutedEventArgs e)
    {
        _settingDownloadAction = _settingDownloadAction == "native" ? "accela" : "native";
        UpdateSettingsUi();
        _configService.SetValue("vapor_default_download_action", _settingDownloadAction);
    }

    private void OnToggleUpdatesClicked(object? sender, RoutedEventArgs e)
    {
        _settingDisableUpdates = !_settingDisableUpdates;
        UpdateSettingsUi();
        _configService.SetBool("vapor_disable_updates", _settingDisableUpdates);
        _configService.SetBool("at0m_disable_updates", _settingDisableUpdates);
    }
}