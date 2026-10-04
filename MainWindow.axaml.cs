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
using Pluto.Controls;
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
    private readonly UpdateStatusService _updateService;
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
    private bool _settingLibraryThumbnailsEnabled = false;
    private CancellationTokenSource? _thumbCts;

    // Backdrop pool
    private List<string> _mainBackdropPool  = new();
    private int          _mainBackdropIndex = 0;

    // Gamepad focus indices
    private int _detailActionIndex   = 0;
    private int _settingsOptionIndex = 0;

    // Settings submenu state
    private enum SettingsTab { Theme, Api, Sls, Visuals, Health, Ddm }
    private SettingsTab _activeSettingsTab = SettingsTab.Theme;

    // Detail page state
    private CancellationTokenSource? _detailCts;
    private List<string> _currentScreenshots     = new();
    private int          _currentScreenshotIndex = 0;

    /// <summary>
    /// The game the filmstrip is focused on, or null when the strip is empty.
    /// The home screen dropped the long library list; the filmstrip is now the
    /// single way to move through the library.
    /// </summary>
    private PluginGame? SelectedGame =>
        Filmstrip?.FocusedItem is FilmstripEntry entry ? entry.Game as PluginGame : null;

    /// <summary>Feeds the filmstrip from the filtered library.</summary>
    private void SyncFilmstrip(List<PluginGame> games)
    {
        if (Filmstrip == null) return;

        var entries = new List<FilmstripEntry>(games.Count);
        var stillPresent = new HashSet<PluginGame>(games);

        // Preserve card objects for games that survived the filter so their
        // already-loaded artwork is not thrown away and refetched on every keystroke.
        var previous = Filmstrip.Items?
            .OfType<FilmstripEntry>()
            .Where(e => e.Game is PluginGame g && stillPresent.Contains(g))
            .ToDictionary(e => (PluginGame)e.Game!, e => e)
            ?? new Dictionary<PluginGame, FilmstripEntry>();

        foreach (var g in games)
        {
            if (previous.TryGetValue(g, out var reuse))
            {
                // Refresh volatile state only.
                reuse.HasUpdate = _updateService.HasUpdate(g.AppId);
                reuse.Subtitle = BuildCardSubtitle(g);
                entries.Add(reuse);
                continue;
            }

            entries.Add(new FilmstripEntry
            {
                Game = g,
                Title = g.Name,
                Subtitle = BuildCardSubtitle(g),
                HasUpdate = _updateService.HasUpdate(g.AppId),
                Artwork = g.Thumbnail
            });
        }

        Filmstrip.ItemsSource = entries;

        // Keep the user's place across re-filters, same as the old list did.
        var current = SelectedGame;
        if (current != null && entries.FirstOrDefault(e => ReferenceEquals(e.Game, current)) is { } found)
        {
            Filmstrip.FocusedIndex = entries.IndexOf(found);
        }
        else
        {
            Filmstrip.FocusedIndex = -1;
        }

        foreach (var e in entries)
        {
            // Artwork may finish loading after the entry was created.
            if (e.Game is PluginGame pg && e.Artwork == null && pg.Thumbnail != null)
                e.Artwork = pg.Thumbnail;
        }
    }

    /// <summary>Secondary line under a card: mode and sync state, kept terse.</summary>
    private string BuildCardSubtitle(PluginGame g)
    {
        var mode = g.IsAccela ? "assella" : "native";
        return g.IsSlsSynced ? mode : $"{mode} - not synced";
    }

    /// <summary>Moves focus into the filmstrip.</summary>
    private void FocusLibrary()
    {
        if (Filmstrip == null) return;
        if (Filmstrip.FocusedIndex < 0) Filmstrip.FocusFirst();
        Filmstrip.Focus();
    }

    /// <summary>Opens whatever the filmstrip is focused on.</summary>
    private void ActivateFocusedCard()
    {
        if (SelectedGame is { } game) OpenGameDetailPage(game);
    }

    private void OnFilmstripItemActivated(object? sender, RoutedEventArgs e)
    {
        // The routed event carries no payload, so resolve from the strip itself.
        if (sender is Filmstrip { FocusedItem: FilmstripEntry { Game: PluginGame game } })
        {
            OpenGameDetailPage(game);
        }
    }

    // Library / search state
    private List<PluginGame>                       _allGames         = new();
    private ObservableCollection<PluginGame>       _displayedGames   = new();
    private ObservableCollection<SearchResultItem> _searchResults    = new();
    private PluginGame?       _selectedGame;
    private SearchResultItem? _selectedSearchResult;
    private CancellationTokenSource? _searchCts;
    private int _placeholderIndex = 0;

    /// <summary>How the library list is ordered.</summary>
    public enum LibrarySort
    {
        Name,
        RecentlyUpdated,
        AppId
    }

    /// <summary>Which subset of the library is shown.</summary>
    public enum LibraryMode
    {
        All,
        Synced,
        NotSynced,
        Assella,
        Plugin
    }

    private LibrarySort _librarySort = LibrarySort.Name;
    private LibraryMode _libraryMode = LibraryMode.All;
    private bool _showUpdatesOnly = false;

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

        PlutoLogger.Info("Startup", $"Pluto {PlutoVersion.FullVersion} starting (log: {PlutoLogger.LogFilePath})");

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

        _liveSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _liveSearchTimer.Tick += OnLiveSearchTimerTick;

        _placeholderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _placeholderTimer.Tick += OnPlaceholderTimerTick;
        _placeholderTimer.Start();

        _healthService = new HealthService(_configService);
        _updateService = new UpdateStatusService();
        _ddmService    = new Pluto.Engine.DepotDownloader.DepotDownloaderService();
        _installService = new Pluto.Engine.Installation.GameInstallService(_ddmService, _slsService, _configService, _depotKeyService);
        _visorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _visorTimer.Tick += async (_, _) => await RefreshVisorAsync();
        _visorTimer.Start();

        ApplyThemeColors();

        SearchResultsListBox.ItemsSource = _searchResults;

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
            _detailCts?.Cancel();
            _visorTimer.Stop();
            _downloadCts?.Cancel();
            _thumbCts?.Cancel();
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
        RefreshLibraryChips();
        UpdateRailCounts();
        FocusLibrary();
        _ = RefreshVisorAsync();

        // Update checks hit a third-party API, so they run after the window is
        // already usable rather than blocking the first frame.
        _ = RefreshUpdatesAsync();
    }

    private async Task ReloadLibraryAsync()
    {
        var games = await _libraryService.LoadGamesAsync();

        // One read of config.yaml for the whole library. Previously each game
        // called IsAppConfigured, which re-read the entire file, so 188 games
        // meant 188 reads (~5.5MB) and ~70ms of blocking work on the UI thread
        // on every watcher event.
        var syncedAppIds = _slsService.GetConfiguredAppIds();

        var seenNewIds = LoadSeenNewGames();

        foreach (var g in games)
        {
            g.IsSlsSynced = syncedAppIds.Contains(g.AppId);

            // The "New" badge is shown once, then remembered as seen so it clears
            // on the next launch instead of persisting until the file is touched.
            g.IsNew = !seenNewIds.Contains(g.AppId) && g.UpdatedAt > 0
                      && DateTimeOffset.FromUnixTimeSeconds(g.UpdatedAt) > _sessionStartUtc.AddDays(-1);
        }

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

    private static readonly string SeenNewGamesPath = PlutoPaths.PlutoConfig + "/seen_new_games.txt";

    private static readonly DateTimeOffset _sessionStartUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Loads AppIDs whose "New" badge has already been shown.
    ///
    /// The badge used to persist for the life of the list and was only cleared by
    /// restarting Pluto and re-adding the game. Recording it here lets the badge
    /// clear on the next launch instead.
    /// </summary>
    private static HashSet<string> LoadSeenNewGames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(SeenNewGamesPath)) return seen;

            foreach (var line in File.ReadAllLines(SeenNewGamesPath))
            {
                var id = line.Trim();
                if (!string.IsNullOrEmpty(id)) seen.Add(id);
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Pluto", $"Could not read seen-new list: {ex.Message}");
        }
        return seen;
    }

    private static void MarkNewGameSeen(string appId)
    {
        try
        {
            PlutoPaths.EnsureDirectory(Path.GetDirectoryName(SeenNewGamesPath)!);
            File.AppendAllText(SeenNewGamesPath, appId + Environment.NewLine);
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Pluto", $"Could not record seen game {appId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Local filter over the in-memory library.
    ///
    /// Sort order and mode filter are applied here rather than in the list
    /// template so the ordering is explicit and testable.
    /// </summary>
    private void ApplyFilter(string? query)
    {
        var trimmed = query?.Trim();
        var previous = SelectedGame;

        IEnumerable<PluginGame> filtered = _allGames;

        // Mode filter first - it is a subset of the whole library and cheap.
        filtered = _libraryMode switch
        {
            LibraryMode.Synced     => filtered.Where(g => g.IsSlsSynced),
            LibraryMode.NotSynced  => filtered.Where(g => !g.IsSlsSynced),
            LibraryMode.Assella    => filtered.Where(g => g.IsAccela),
            LibraryMode.Plugin     => filtered.Where(g => !g.IsAccela),
            _                      => filtered
        };

        if (!string.IsNullOrEmpty(trimmed))
        {
            filtered = filtered.Where(g =>
                g.Name.Contains(trimmed,       StringComparison.OrdinalIgnoreCase) ||
                g.AppId.Contains(trimmed,      StringComparison.OrdinalIgnoreCase) ||
                g.Depots.Any(d => d.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                g.InstallDir.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // Narrowing to updates is why the filmstrip is sorted with pending work
        // first, so this filter is most useful when paired with that ordering.
        if (_showUpdatesOnly)
        {
            filtered = filtered.Where(g => _updateService.HasUpdate(g.AppId));
        }

        // Sort last so ordering is stable regardless of which filter is active.
        // Name sort uses the current culture so accented titles land where a
        // reader expects rather than after every ASCII letter.
        //
        // Games with an available update are floated to the front in every
        // ordering: on a home screen the first cards are what the user sees,
        // so pending work should not be buried at the end of a 189-game strip.
        var list = filtered
            .OrderByDescending(g => _updateService.HasUpdate(g.AppId))
            .ThenBy(g => _librarySort switch
            {
                LibrarySort.AppId => long.TryParse(g.AppId, out var id) ? id : long.MaxValue,
                LibrarySort.RecentlyUpdated => -g.UpdatedAt,
                _ => 0L
            })
            .ThenBy(g => g.Name, StringComparer.CurrentCulture)
            .ToList();

        // The filmstrip is the library navigator now; the observable list is kept
        // only for counts and download bookkeeping.
        _displayedGames.Clear();
        foreach (var item in list) _displayedGames.Add(item);
        SyncFilmstrip(list);
        UpdateRailCounts();

        if (EmptyStateText != null)
        {
            EmptyStateText.IsVisible = list.Count == 0;
            EmptyStateText.Text = DescribeEmptyState(trimmed);
        }


        QueueThumbnailLoad(list);
    }

    /// <summary>
    /// Kicks off artwork loading for the current list, cancelling any previous run.
    /// Only the disk cache is consulted unless artwork fetching is enabled.
    /// </summary>
    private void QueueThumbnailLoad(List<PluginGame> games)
    {
        if (!_settingLibraryThumbnailsEnabled) return;

        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        var snapshot = games.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                // Bitmaps must be created and assigned on the UI thread.
                var loader = new LibraryThumbnailLoader(
                    allowNetwork: true,
                    postToUi: action => Dispatcher.UIThread.Post(action));
                using (loader)
                {
                    await loader.LoadAsync(snapshot, ct);
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Debug("Thumbnails", $"Library artwork load ended: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>Explains an empty list in terms of the filter that caused it.</summary>
    private string DescribeEmptyState(string? trimmed)
    {
        if (!string.IsNullOrEmpty(trimmed)) return $"no games matching \"{trimmed}\"";

        return _libraryMode switch
        {
            LibraryMode.Synced    => "every game is already synced to SLSsteam",
            LibraryMode.NotSynced => "every game is synced to SLSsteam",
            LibraryMode.Assella   => "no assella-managed games",
            LibraryMode.Plugin    => "no plugin-native games",
            _                     => "no games found in library"
        };
    }

    // ── Library sort and mode chips ──────────────────────────────────────────────

    private static readonly (LibrarySort Value, string Label)[] SortCycle =
    {
        (LibrarySort.Name, "name"),
        (LibrarySort.RecentlyUpdated, "recent"),
        (LibrarySort.AppId, "appid")
    };

    private static readonly (LibraryMode Value, string Label)[] ModeCycle =
    {
        (LibraryMode.All, "all"),
        (LibraryMode.NotSynced, "not synced"),
        (LibraryMode.Synced, "synced"),
        (LibraryMode.Plugin, "plugin"),
        (LibraryMode.Assella, "assella")
    };

    private void OnSortChipClicked(object? sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(SortCycle, s => s.Value == _librarySort);
        _librarySort = SortCycle[(idx + 1) % SortCycle.Length].Value;
        RefreshLibraryChips();
        ApplyFilter(SearchBox?.Text);
    }

    private void OnModeChipClicked(object? sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(ModeCycle, m => m.Value == _libraryMode);
        _libraryMode = ModeCycle[(idx + 1) % ModeCycle.Length].Value;
        RefreshLibraryChips();
        ApplyFilter(SearchBox?.Text);
    }

    /// <summary>Cycles the sort mode, used by the gamepad LB/RB binding.</summary>
    internal void CycleLibrarySort(int offset)
    {
        int idx = Array.FindIndex(SortCycle, s => s.Value == _librarySort);
        int next = ((idx + offset) % SortCycle.Length + SortCycle.Length) % SortCycle.Length;
        _librarySort = SortCycle[next].Value;
        RefreshLibraryChips();
        ApplyFilter(SearchBox?.Text);
    }

    /// <summary>Cycles the mode filter, used by the gamepad binding.</summary>
    internal void CycleLibraryMode()
    {
        int idx = Array.FindIndex(ModeCycle, m => m.Value == _libraryMode);
        _libraryMode = ModeCycle[(idx + 1) % ModeCycle.Length].Value;
        RefreshLibraryChips();
        ApplyFilter(SearchBox?.Text);
    }

    /// <summary>
    /// Toggles the "only games with updates" filter and surfaces the result on
    /// the rail so the user can see the count without reading the strip.
    /// </summary>
    private void OnUpdatesChipClicked(object? sender, RoutedEventArgs e)
    {
        _showUpdatesOnly = !_showUpdatesOnly;
        RefreshLibraryChips();
        ApplyFilter(SearchBox?.Text);
        UpdateRailCounts();
    }

    private void RefreshLibraryChips()
    {
        if (SortChipBtn != null)
            SortChipBtn.Content = $"sort: {SortCycle.First(s => s.Value == _librarySort).Label}";

        if (ModeChipBtn != null)
            ModeChipBtn.Content = ModeCycle.First(m => m.Value == _libraryMode).Label;

        if (UpdatesChipBtn != null)
        {
            UpdatesChipBtn.Content = _showUpdatesOnly ? "updates only" : "updates";
            UpdatesChipBtn.Foreground = _showUpdatesOnly
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }
    }

    /// <summary>Refreshes the top-rail counters from the current library and cache.</summary>
    private void UpdateRailCounts()
    {
        if (RailGameCount != null)
            RailGameCount.Text = $"{_displayedGames.Count} shown / {_allGames.Count}";

        int updates = _displayedGames.Count(g => _updateService.HasUpdate(g.AppId));
        if (RailUpdateCount != null)
        {
            RailUpdateCount.Text = updates > 0 ? $"{updates} update{(updates == 1 ? "" : "s")}" : "";
            RailUpdateCount.Foreground = updates > 0
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }
    }

    /// <summary>
    /// Refreshes update status in the background.
    ///
    /// Deliberately not awaited on the load path: a full check is hundreds of
    /// requests to a third-party API and must never delay the window appearing.
    /// Cards pick up their badges as results land.
    /// </summary>
    private async Task RefreshUpdatesAsync(bool force = false)
    {
        try
        {
            await _updateService.RefreshAsync(_allGames, force);

            ApplyFilter(SearchBox?.Text);
            UpdateRailCounts();
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Updates", $"Update refresh ended: {ex.Message}");
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
