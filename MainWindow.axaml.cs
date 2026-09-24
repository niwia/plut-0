using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
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
    private readonly GamepadService _gamepadService;

    private List<PluginGame> _allGames = new();
    private ObservableCollection<PluginGame> _displayedGames = new();
    private PluginGame? _currentModalGame;

    public MainWindow()
    {
        InitializeComponent();

        _libraryService = new PluginLibraryService();
        _slsService = new SlsSteamService();
        _gamepadService = new GamepadService();

        GamesListBox.ItemsSource = _displayedGames;

        // Wire events
        _libraryService.LibraryChanged += () =>
        {
            Dispatcher.UIThread.Post(async () => await ReloadLibraryAsync());
        };

        _gamepadService.ControllerStateChanged += (name, connected) =>
        {
            Dispatcher.UIThread.Post(() => UpdateControllerStatus(name, connected));
        };

        _gamepadService.ActionTriggered += action =>
        {
            Dispatcher.UIThread.Post(() => HandleGamepadAction(action));
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
        _gamepadService.Start();
        UpdateControllerStatus(_gamepadService.ActiveControllerName, _gamepadService.HasConnectedController);
        await ReloadLibraryAsync();
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

        if (GameCountText != null)
        {
            GameCountText.Text = $"{list.Count} { (list.Count == 1 ? "game" : "games") }";
        }

        if (EmptyStatePanel != null)
        {
            EmptyStatePanel.IsVisible = list.Count == 0;
            if (list.Count == 0 && !string.IsNullOrEmpty(trimmed))
            {
                EmptyStateSubtext.Text = $"No games match \"{trimmed}\".";
            }
            else
            {
                EmptyStateSubtext.Text = "No games found in plugin_library.json.";
            }
        }

        if (ClearSearchBtn != null)
        {
            ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(trimmed);
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
        if (ControllerStatusText != null)
        {
            if (connected)
            {
                ControllerStatusText.Text = $"Connected: {name}";
                ControllerStatusText.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
            }
            else
            {
                ControllerStatusText.Text = "No controller (Keyboard mode)";
                ControllerStatusText.Foreground = Avalonia.Media.Brushes.SlateGray;
            }
        }
    }

    private void HandleGamepadAction(GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.NavigateUp:
                NavigateList(-1);
                break;

            case GamepadAction.NavigateDown:
                NavigateList(1);
                break;

            case GamepadAction.PageUp:
                NavigateList(-5);
                break;

            case GamepadAction.PageDown:
                NavigateList(5);
                break;

            case GamepadAction.ConfirmLaunch:
                if (DetailsModal.IsVisible && _currentModalGame != null)
                {
                    LaunchGame(_currentModalGame);
                }
                else if (GamesListBox.SelectedItem is PluginGame selected)
                {
                    LaunchGame(selected);
                }
                break;

            case GamepadAction.ManageGame:
                if (!DetailsModal.IsVisible && GamesListBox.SelectedItem is PluginGame item)
                {
                    OpenDetailsModal(item);
                }
                break;

            case GamepadAction.FocusSearch:
                if (DetailsModal.IsVisible)
                {
                    CloseDetailsModal();
                }
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;

            case GamepadAction.BackOrCancel:
                if (DetailsModal.IsVisible)
                {
                    CloseDetailsModal();
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

            case GamepadAction.SyncAll:
                SyncAllGames();
                break;

            case GamepadAction.ToggleDetails:
                if (DetailsModal.IsVisible)
                {
                    CloseDetailsModal();
                }
                else if (GamesListBox.SelectedItem is PluginGame target)
                {
                    OpenDetailsModal(target);
                }
                break;
        }
    }

    private void NavigateList(int offset)
    {
        if (DetailsModal.IsVisible || _displayedGames.Count == 0) return;

        int currentIndex = GamesListBox.SelectedIndex;
        int newIndex = currentIndex + offset;

        if (newIndex < 0) newIndex = 0;
        if (newIndex >= _displayedGames.Count) newIndex = _displayedGames.Count - 1;

        GamesListBox.SelectedIndex = newIndex;
        if (GamesListBox.SelectedItem != null)
        {
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
        }
    }

    private void LaunchGame(PluginGame game)
    {
        Console.WriteLine($"[Pluto] Launching game: {game.Name} ({game.AppId})");
        _slsService.LaunchGame(game.AppId);
    }

    private void OpenDetailsModal(PluginGame game)
    {
        _currentModalGame = game;
        ModalGameTitle.Text = game.Name;
        ModalGameSubtitle.Text = $"AppID: {game.AppId} • InstallDir: {game.InstallDir}";
        ModalUpdatedText.Text = $"Last updated in library: {game.UpdatedAtString}";

        var sb = new StringBuilder();
        sb.AppendLine($"[Depots ({game.DepotCount})]");
        foreach (var d in game.Depots)
        {
            game.DepotNames.TryGetValue(d, out var dName);
            var nameStr = string.IsNullOrWhiteSpace(dName) ? "" : $" - {dName}";
            sb.AppendLine($"  • Depot {d}{nameStr}");
        }

        sb.AppendLine();
        sb.AppendLine($"[Decryption Keys ({game.KeyCount})]");
        if (game.Keys.Count == 0)
        {
            sb.AppendLine("  (No AES decryption keys registered)");
        }
        else
        {
            foreach (var kvp in game.Keys)
            {
                game.DepotNames.TryGetValue(kvp.Key, out var dName);
                var nameStr = string.IsNullOrWhiteSpace(dName) ? "" : $" ({dName})";
                sb.AppendLine($"  • Depot {kvp.Key}{nameStr}:");
                sb.AppendLine($"    {kvp.Value}");
            }
        }

        ModalDepotsKeysText.Text = sb.ToString();
        DetailsModal.IsVisible = true;
    }

    private void CloseDetailsModal()
    {
        DetailsModal.IsVisible = false;
        _currentModalGame = null;
        GamesListBox.Focus();
    }

    private async void SyncAllGames()
    {
        Console.WriteLine("[Pluto] Syncing all games to SLS config.yaml...");
        foreach (var g in _allGames)
        {
            await _slsService.SyncGameToConfigAsync(g);
        }
        _slsService.NotifyReload();
        await ReloadLibraryAsync();
    }

    // UI Event Handlers
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Enter)
        {
            GamesListBox.Focus();
            if (GamesListBox.SelectedIndex < 0 && _displayedGames.Count > 0)
            {
                GamesListBox.SelectedIndex = 0;
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

    private void OnClearSearchClicked(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void OnGameListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GamesListBox.SelectedItem is PluginGame selected)
        {
            LaunchGame(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.X && GamesListBox.SelectedItem is PluginGame game)
        {
            OpenDetailsModal(game);
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
            LaunchGame(selected);
        }
    }

    private void OnGameSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Keep focus responsive
    }

    private void OnLaunchGameClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is PluginGame game)
        {
            LaunchGame(game);
        }
    }

    private void OnOpenDetailsClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is PluginGame game)
        {
            OpenDetailsModal(game);
        }
    }

    private void OnCloseModalClicked(object? sender, RoutedEventArgs e)
    {
        CloseDetailsModal();
    }

    private async void OnModalSyncClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentModalGame != null)
        {
            await _slsService.SyncGameToConfigAsync(_currentModalGame);
            await ReloadLibraryAsync();
        }
    }

    private void OnModalLaunchClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentModalGame != null)
        {
            LaunchGame(_currentModalGame);
        }
    }

    private async void OnModalRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentModalGame != null)
        {
            var target = _currentModalGame;
            var remaining = _allGames.Where(g => g.AppId != target.AppId).ToList();
            await _slsService.RemoveGameFromConfigAsync(target, remaining);
            await _libraryService.RemoveGameAsync(target.AppId);
            CloseDetailsModal();
            await ReloadLibraryAsync();
        }
    }

    private void OnSyncAllClicked(object? sender, RoutedEventArgs e)
    {
        SyncAllGames();
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await ReloadLibraryAsync();
    }
}