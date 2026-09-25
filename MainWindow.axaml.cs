using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pluto.Models;
using Pluto.Services;

namespace Pluto;

// MainWindow — core fields, constructor and lifecycle.
// Logic is split across sibling partial files:
//   MainWindow.Navigation.cs  — keyboard + gamepad routing
//   MainWindow.Detail.cs      — game detail page, artwork, screenshots, actions
//   MainWindow.Search.cs      — search box, live search, search results
//   MainWindow.Settings.cs    — settings page, themes, toggles
public partial class MainWindow : Window
{
    // Services
    private readonly PluginLibraryService _libraryService;
    private readonly SlsSteamService _slsService;
    private readonly DepotKeyService _depotKeyService;
    private readonly AccelaConfigService _configService;
    private readonly GameTransitionService _transitionService;
    private readonly GamepadService _gamepadService;
    private readonly HubcapSearchService _hubcapSearchService;
    private readonly ThemeService _themeService;
    private readonly RawgService _rawgService;
    private readonly SteamGridDbService _sgdbService;
    private readonly SteamTagService _steamTagService;
    private readonly EosProxyService _eosProxyService;
    private readonly SteamlessService _steamlessService;
    private readonly HealthService _healthService;
    private readonly Pluto.Engine.DepotDownloader.DepotDownloaderService _ddmService;
    private readonly Pluto.Engine.Installation.GameInstallService _installService;
    private CancellationTokenSource? _downloadCts;

    // Timers
    private readonly DispatcherTimer _screenshotAutoRotateTimer;
    private readonly DispatcherTimer _mainBackdropTimer;
    private readonly DispatcherTimer _liveSearchTimer;
    private readonly DispatcherTimer _placeholderTimer;
    private readonly DispatcherTimer _visorTimer;

    // Visual settings
    private bool _settingAutoRotateScreenshots  = true;
    private int  _settingAutoRotateIntervalSec   = 6;
    private bool _settingDynamicMainBackdrop     = true;
    private int  _settingMainBackdropIntervalSec = 15;
    private bool _settingSearchThumbnailsEnabled = true;

    // Backdrop pool
    private List<string> _mainBackdropPool  = new();
    private int          _mainBackdropIndex = 0;

    // Gamepad focus indices
    private int _detailActionIndex   = 0;
    private int _settingsOptionIndex = 0;

    // Settings submenu state
    private enum SettingsTab { Theme, Api, Sls, Visuals, Health }
    private SettingsTab _activeSettingsTab = SettingsTab.Theme;

    // Detail page state
    private CancellationTokenSource? _detailCts;
    private List<string> _currentScreenshots     = new();
    private int          _currentScreenshotIndex = 0;

    // Library / search state
    private List<PluginGame>                       _allGames         = new();
    private ObservableCollection<PluginGame>       _displayedGames   = new();
    private ObservableCollection<SearchResultItem> _searchResults    = new();
    private PluginGame?       _selectedGame;
    private SearchResultItem? _selectedSearchResult;
    private CancellationTokenSource? _searchCts;
    private int _placeholderIndex = 0;

    // SLS & UI settings (cached)
    private bool   _settingVaporEnabled   = true;
    private string _settingDownloadAction = "native";
    private bool   _settingDisableUpdates = false;
    private bool   _settingSearchResetOnAccess = true;

    // View state
    private enum ActiveView { MainList, GameDetail, Settings }
    private ActiveView _currentView = ActiveView.MainList;

    // Rotating search placeholders
    private static readonly string[] SearchPlaceholders =
    {
        "search: cyberpunk 2077...",
        "search: 1091500...",
        "search: elden ring...",
        "search: 1245620...",
        "search: baldur's gate 3...",
        "search: 1086940...",
        "search: rimworld...",
        "search: 294100...",
        "search: black myth: wukong...",
        "search: 2358720...",
        "search: vampire survivors...",
        "search: 1794680...",
        "search: hollow knight...",
        "search: 367520...",
        "search: hades ii...",
        "search: 1145350...",
        "search: palworld...",
        "search: 1623730...",
        "search by game name or appid..."
    };

    // Constructor
    public MainWindow()
    {
        InitializeComponent();

        _libraryService      = new PluginLibraryService();
        _slsService          = new SlsSteamService();
        _depotKeyService     = new DepotKeyService();
        _configService       = new AccelaConfigService();
        _transitionService   = new GameTransitionService(_slsService, _libraryService, _depotKeyService);
        _gamepadService      = new GamepadService();
        _steamlessService    = new SteamlessService();
        _hubcapSearchService = new HubcapSearchService(_configService, _depotKeyService);
        _themeService        = new ThemeService(_configService);
        _rawgService         = new RawgService(_configService);
        _sgdbService         = new SteamGridDbService(_configService);
        _steamTagService     = new SteamTagService();
        _eosProxyService     = new EosProxyService();

        _screenshotAutoRotateTimer = new DispatcherTimer();
        _screenshotAutoRotateTimer.Tick += OnScreenshotTimerTick;

        _mainBackdropTimer = new DispatcherTimer();
        _mainBackdropTimer.Tick += OnMainBackdropTimerTick;

        _liveSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _liveSearchTimer.Tick += OnLiveSearchTimerTick;

        _placeholderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _placeholderTimer.Tick += OnPlaceholderTimerTick;
        _placeholderTimer.Start();

        _healthService = new HealthService(_configService);
        _ddmService    = new Pluto.Engine.DepotDownloader.DepotDownloaderService();
        _installService = new Pluto.Engine.Installation.GameInstallService(_ddmService, _slsService, _configService, _depotKeyService);
        _visorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _visorTimer.Tick += async (_, _) => await RefreshVisorAsync();
        _visorTimer.Start();

        ApplyThemeColors();

        GamesListBox.ItemsSource         = _displayedGames;
        SearchResultsListBox.ItemsSource = _searchResults;

        GamesListBox.GotFocus  += (_, _) => GamesListBox.Classes.Set("accessed", true);
        GamesListBox.LostFocus += (_, _) =>
        {
            if (!GamesListBox.IsFocused && !GamesListBox.IsKeyboardFocusWithin)
                GamesListBox.Classes.Set("accessed", false);
        };

        _libraryService.LibraryChanged += () =>
        {
            Dispatcher.UIThread.Post(async () => await ReloadLibraryAsync());
        };

        _gamepadService.ActionTriggered += action =>
        {
            Dispatcher.UIThread.Post(() => HandleGamepadAction(action));
        };
        _gamepadService.ControllerStateChanged += (name, connected) =>
        {
            Dispatcher.UIThread.Post(() => UpdateControllerStatus(name, connected));
        };

        Loaded  += OnWindowLoaded;
        Closing += (_, _) =>
        {
            _liveSearchTimer.Stop();
            _placeholderTimer.Stop();
            _screenshotAutoRotateTimer.Stop();
            _mainBackdropTimer.Stop();
            _searchCts?.Cancel();
            _gamepadService.Dispose();
            _libraryService.Dispose();
        };
    }

    // Lifecycle
    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        PlutoLogger.Info("Pluto", "Starting PLUT-0");
        _gamepadService.Start();
        UpdateControllerStatus(_gamepadService.ActiveControllerName, _gamepadService.HasConnectedController);
        await ReloadLibraryAsync();
        LoadSettingsFromConfig();
        GamesListBox.Focus();
        _ = RefreshVisorAsync();
    }

    private async Task ReloadLibraryAsync()
    {
        var games = await _libraryService.LoadGamesAsync();
        foreach (var g in games)
            g.IsSlsSynced = _slsService.IsAppConfigured(g.AppId);

        _allGames         = games;
        _mainBackdropPool = _allGames.Select(g => g.AppId).Distinct().ToList();

        if (_settingDynamicMainBackdrop
            && _currentView == ActiveView.MainList
            && _mainBackdropPool.Count > 0
            && !_mainBackdropTimer.IsEnabled)
        {
            _mainBackdropTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settingMainBackdropIntervalSec));
            _mainBackdropTimer.Start();
            TriggerNextMainBackdrop();
        }

        ApplyFilter(SearchBox?.Text);
        UpdateGameCountsText();
        PlutoLogger.Info("Library",
            $"Library updated: {_allGames.Count} total ({_allGames.Count(g => !g.IsAccela)} plugin, {_allGames.Count(g => g.IsAccela)} assella)");
    }

    // Local filter
    private void ApplyFilter(string? query)
    {
        var trimmed = query?.Trim();
        IEnumerable<PluginGame> filtered = _allGames;

        if (!string.IsNullOrEmpty(trimmed))
        {
            filtered = _allGames.Where(g =>
                g.Name.Contains(trimmed,       StringComparison.OrdinalIgnoreCase) ||
                g.AppId.Contains(trimmed,      StringComparison.OrdinalIgnoreCase) ||
                g.Depots.Any(d => d.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                g.InstallDir.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        _displayedGames.Clear();
        foreach (var item in list) _displayedGames.Add(item);

        if (EmptyStateText != null)
        {
            EmptyStateText.IsVisible = list.Count == 0;
            EmptyStateText.Text = list.Count == 0 && !string.IsNullOrEmpty(trimmed)
                ? $"no games matching \"{trimmed}\""
                : "no games found in library";
        }

        if (list.Count > 0 && GamesListBox != null)
        {
            GamesListBox.SelectedIndex = 0;
            if (GamesListBox.SelectedItem != null)
                GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
        }
    }

    // Status helpers
    private void UpdateControllerStatus(string name, bool connected)
    {
        if (SettingsControllerText != null)
            SettingsControllerText.Text = connected ? $"connected ({name})" : "no controller detected";
    }

    private void UpdateGameCountsText()
    {
        if (SettingsGameCountsText != null)
        {
            int p = _allGames.Count(g => !g.IsAccela);
            int a = _allGames.Count(g => g.IsAccela);
            SettingsGameCountsText.Text = $"{p} plugin games, {a} assella managed games";
        }
    }

    // Visor refresh & click handler
    public async Task RefreshVisorAsync()
    {
        try
        {
            var health = await _healthService.CheckHealthAsync();

            Dispatcher.UIThread.Post(() =>
            {
                if (PlutoVersionText != null)
                {
                    PlutoVersionText.Text = PlutoVersion.FullVersion;
                }

                if (VisorHubcapText != null)
                {
                    VisorHubcapText.Text = health.Hubcap.IsConfigured 
                        ? $"API: {health.Hubcap.DailyUsage}/{health.Hubcap.DailyLimit}" 
                        : "API: --/--";
                    VisorHubcapText.Foreground = health.Hubcap.IsConfigured
                        ? Avalonia.Media.Brushes.LightGray
                        : Avalonia.Media.Brushes.Gray;
                }

                if (VisorSlsText != null)
                {
                    string slsVal = health.SlsProcessActive ? "active" : (health.SlsBinaryDetected ? "inactive" : "not found");
                    VisorSlsText.Text = $"SLS: {slsVal}";
                    VisorSlsText.Foreground = health.SlsProcessActive
                        ? Avalonia.Media.Brushes.LightGray
                        : (health.SlsBinaryDetected ? Avalonia.Media.Brushes.Goldenrod : Avalonia.Media.Brushes.IndianRed);
                }

                if (VisorSteamText != null)
                {
                    string steamVal = health.SteamRunning ? "online" : "offline";
                    VisorSteamText.Text = $"Steam: {steamVal}";
                    VisorSteamText.Foreground = health.SteamRunning
                        ? Avalonia.Media.Brushes.LightGray
                        : Avalonia.Media.Brushes.IndianRed;
                }

                if (VisorHealthText != null)
                {
                    VisorHealthText.Text = $"health: {health.OverallState.ToLowerInvariant()}";
                    VisorHealthText.Foreground = health.IsOptimal
                        ? Avalonia.Media.Brushes.MediumSpringGreen
                        : (health.OverallState == "Attention" ? Avalonia.Media.Brushes.Goldenrod : Avalonia.Media.Brushes.IndianRed);
                }

                UpdateHealthTabUi(health);
            });
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Visor", "Failed to refresh visor status", ex);
        }
    }

    private void OnVisorHealthClicked(object? sender, PointerPressedEventArgs e)
    {
        OpenSettingsPage();
        SetActiveSettingsTab(SettingsTab.Health);
    }
}
