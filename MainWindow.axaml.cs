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

public partial class MainWindow : Window
{
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
    private readonly DispatcherTimer _screenshotAutoRotateTimer;
    private readonly DispatcherTimer _mainBackdropTimer;
    private bool _settingAutoRotateScreenshots = true;
    private int _settingAutoRotateIntervalSec = 6;
    private bool _settingDynamicMainBackdrop = true;
    private int _settingMainBackdropIntervalSec = 15;
    private bool _settingSearchThumbnailsEnabled = true;

    private List<string> _mainBackdropPool = new();
    private int _mainBackdropIndex = 0;

    private int _detailActionIndex = 0;

    private enum SettingsTab
    {
        Theme,
        Api,
        Sls,
        Visuals
    }

    private SettingsTab _activeSettingsTab = SettingsTab.Theme;
    private int _settingsOptionIndex = 0;

    private CancellationTokenSource? _detailCts;

    private List<string> _currentScreenshots = new();
    private int _currentScreenshotIndex = 0;

    private List<PluginGame> _allGames = new();
    private ObservableCollection<PluginGame> _displayedGames = new();
    private ObservableCollection<SearchResultItem> _searchResults = new();
    private PluginGame? _selectedGame;
    private SearchResultItem? _selectedSearchResult;

    // Dynamic rotating search placeholders matching ASSella fetchmanifest.py
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

    private readonly DispatcherTimer _liveSearchTimer;
    private readonly DispatcherTimer _placeholderTimer;
    private CancellationTokenSource? _searchCts;
    private int _placeholderIndex = 0;

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
        _hubcapSearchService = new HubcapSearchService(_configService, _depotKeyService);
        _themeService = new ThemeService(_configService);
        _rawgService = new RawgService(_configService);
        _sgdbService = new SteamGridDbService(_configService);
        _steamTagService = new SteamTagService();
        _eosProxyService = new EosProxyService();

        _screenshotAutoRotateTimer = new DispatcherTimer();
        _screenshotAutoRotateTimer.Tick += OnScreenshotTimerTick;

        _mainBackdropTimer = new DispatcherTimer();
        _mainBackdropTimer.Tick += OnMainBackdropTimerTick;

        ApplyThemeColors();

        GamesListBox.ItemsSource = _displayedGames;
        SearchResultsListBox.ItemsSource = _searchResults;

        // Dynamic gliding gap: mark accessed on focus, unmark when focus is lost
        GamesListBox.GotFocus += (_, _) => GamesListBox.Classes.Set("accessed", true);
        GamesListBox.LostFocus += (_, _) =>
        {
            if (!GamesListBox.IsFocused && !GamesListBox.IsKeyboardFocusWithin)
            {
                GamesListBox.Classes.Set("accessed", false);
            }
        };

        // Debounce timer for live search suggestions (400ms)
        _liveSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _liveSearchTimer.Tick += OnLiveSearchTimerTick;

        // Dynamic placeholder rotation timer (3500ms)
        _placeholderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _placeholderTimer.Tick += OnPlaceholderTimerTick;
        _placeholderTimer.Start();

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
            _liveSearchTimer.Stop();
            _placeholderTimer.Stop();
            _screenshotAutoRotateTimer.Stop();
            _mainBackdropTimer.Stop();
            _searchCts?.Cancel();
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
        _mainBackdropPool = _allGames.Select(g => g.AppId).Distinct().ToList();
        if (_settingDynamicMainBackdrop && _currentView == ActiveView.MainList && _mainBackdropPool.Count > 0 && !_mainBackdropTimer.IsEnabled)
        {
            _mainBackdropTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settingMainBackdropIntervalSec));
            _mainBackdropTimer.Start();
            TriggerNextMainBackdrop();
        }

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
                if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                {
                    NavigateSearchResults(-1);
                }
                else
                {
                    NavigateList(-1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down && !SearchBox.IsFocused)
            {
                if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                {
                    NavigateSearchResults(1);
                }
                else
                {
                    NavigateList(1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.PageUp && !SearchBox.IsFocused)
            {
                if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                {
                    NavigateSearchResults(-5);
                }
                else
                {
                    NavigateList(-5);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown && !SearchBox.IsFocused)
            {
                if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                {
                    NavigateSearchResults(5);
                }
                else
                {
                    NavigateList(5);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !SearchBox.IsFocused)
            {
                if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && SearchResultsListBox.SelectedItem is SearchResultItem searchItem)
                {
                    OpenSearchResultDetailPage(searchItem);
                    e.Handled = true;
                }
                else if (GamesListBox.SelectedItem is PluginGame selected)
                {
                    OpenGameDetailPage(selected);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Tab || e.Key == Key.F1)
            {
                OpenSettingsPage();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearSearchAndReset();
                e.Handled = true;
            }
        }
        else
        {
            // Inside GameDetail or Settings
            if (_currentView == ActiveView.GameDetail)
            {
                if (e.Key == Key.Left || e.Key == Key.PageUp)
                {
                    OnPrevScreenshotClicked(null, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Right || e.Key == Key.PageDown)
                {
                    OnNextScreenshotClicked(null, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
            }

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

        _detailCts?.Cancel();
        _screenshotAutoRotateTimer.Stop();

        if (_settingDynamicMainBackdrop && _mainBackdropPool.Count > 0)
        {
            _mainBackdropTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settingMainBackdropIntervalSec));
            _mainBackdropTimer.Start();
        }

        if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
        {
            SearchResultsListBox.Focus();
        }
        else
        {
            GamesListBox.Focus();
        }
    }

    private void OpenGameDetailPage(PluginGame game)
    {
        _selectedGame = game;
        _selectedSearchResult = null;
        _currentView = ActiveView.GameDetail;

        // Reset detail header, rating and gallery state
        _currentScreenshots.Clear();
        _currentScreenshotIndex = 0;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        if (DetailGameLogo != null) { DetailGameLogo.Source = null; DetailGameLogo.IsVisible = false; }
        if (DetailGameTitle != null) { DetailGameTitle.Text = game.Name; DetailGameTitle.IsVisible = true; }
        if (DetailGameSubtitle != null) DetailGameSubtitle.Text = $"{game.AppId}  •  {(game.IsAccela ? "assella" : "native")}";
        if (DetailGameCredits != null) { DetailGameCredits.Text = string.Empty; DetailGameCredits.IsVisible = false; }
        if (DetailRatingRow != null) DetailRatingRow.IsVisible = false;
        if (DetailRawgMeta != null) { DetailRawgMeta.Text = string.Empty; DetailRawgMeta.IsVisible = false; }
        if (DetailTagsPanel != null) { DetailTagsPanel.Children.Clear(); DetailTagsPanel.IsVisible = false; }
        if (DetailGalleryControls != null) DetailGalleryControls.IsVisible = false;

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
        DetailSwitchModeBtn.IsEnabled = true;

        if (DetailActionStatus != null) DetailActionStatus.IsVisible = false;
        if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = true;
        if (DetailSteamlessRow != null) DetailSteamlessRow.IsVisible = true;

        // SLSonline, Netsock & EOS Proxy status
        bool isOnline = _slsService.IsSlsOnline(game.AppId);
        bool isNetsock = _slsService.IsNetsock(game.AppId);
        UpdateOnlineTogglesUi(isOnline, isNetsock);
        UpdateEosProxyUi();

        // Reset Steamless UI state
        DetailSteamlessBtn.IsEnabled = true;
        DetailSteamlessBtn.Content = "apply steamless";
        DetailSteamlessStatus.IsVisible = false;

        _detailActionIndex = 0;
        UpdateDetailActionHighlight();

        // Load Artwork, SteamGridDB Logo & Hero, and RAWG metadata on-demand
        _ = LoadGameArtworkAndMetadataAsync(game.AppId, game.Name);

        MainListPanel.IsVisible = false;
        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible = false;
    }

    private void OpenSearchResultDetailPage(SearchResultItem item)
    {
        // 1. If game already exists in library, open as standard game
        var localGame = _allGames.FirstOrDefault(g => g.AppId == item.AppId);
        if (localGame != null)
        {
            OpenGameDetailPage(localGame);
            return;
        }

        // 2. Open details for uninstalled online game
        _selectedSearchResult = item;
        _selectedGame = null;
        _currentView = ActiveView.GameDetail;

        // Reset detail header, rating and gallery state
        _currentScreenshots.Clear();
        _currentScreenshotIndex = 0;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        if (DetailGameLogo != null) { DetailGameLogo.Source = null; DetailGameLogo.IsVisible = false; }
        if (DetailGameTitle != null) { DetailGameTitle.Text = item.Name; DetailGameTitle.IsVisible = true; }
        if (DetailGameSubtitle != null) DetailGameSubtitle.Text = $"{item.AppId}  •  online";
        if (DetailGameCredits != null) { DetailGameCredits.Text = string.Empty; DetailGameCredits.IsVisible = false; }
        if (DetailRatingRow != null) DetailRatingRow.IsVisible = false;
        if (DetailRawgMeta != null) { DetailRawgMeta.Text = string.Empty; DetailRawgMeta.IsVisible = false; }
        if (DetailTagsPanel != null) { DetailTagsPanel.Children.Clear(); DetailTagsPanel.IsVisible = false; }
        if (DetailGalleryControls != null) DetailGalleryControls.IsVisible = false;

        DetailSwitchModeBtn.Content = "add to plugin";
        DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
        DetailSwitchModeBtn.IsEnabled = true;

        if (DetailActionStatus != null) DetailActionStatus.IsVisible = false;
        if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = false;
        if (DetailSteamlessRow != null) DetailSteamlessRow.IsVisible = false;

        _detailActionIndex = 0;
        UpdateDetailActionHighlight();

        // Load Artwork, SteamGridDB Logo & Hero, and RAWG metadata on-demand
        _ = LoadGameArtworkAndMetadataAsync(item.AppId, item.Name);

        MainListPanel.IsVisible = false;
        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible = false;
    }

    private async Task LoadGameArtworkAndMetadataAsync(string appId, string gameName)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        // 1. Check local image cache
        var imageCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "ACCELA", "image_cache", $"{appId}.jpg");

        bool imageLoaded = false;
        if (File.Exists(imageCachePath))
        {
            try
            {
                DetailGameBackdrop.Source = new Bitmap(imageCachePath);
                DetailGameBackdrop.IsVisible = true;
                imageLoaded = true;
            }
            catch
            {
                // Fall through to on-demand fetch
            }
        }

        // 2. If no local image, on-demand fetch steam header asynchronously
        if (!imageLoaded)
        {
            DetailGameBackdrop.Source = null;
            DetailGameBackdrop.IsVisible = false;

            _ = Task.Run(async () =>
            {
                var bmp = await _hubcapSearchService.FetchAndCacheGameThumbnailAsync(appId, null, ct);
                if (bmp != null && !ct.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ct.IsCancellationRequested && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                        {
                            DetailGameBackdrop.Source = bmp;
                            DetailGameBackdrop.IsVisible = true;
                        }
                    });
                }
            }, ct);
        }

        // 3. SteamGridDB: Fetch Logo and Hero backdrop
        if (_sgdbService.HasApiKey)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var logoBmp = await _sgdbService.FetchLogoBitmapAsync(appId, ct);
                    if (logoBmp != null && !ct.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ct.IsCancellationRequested && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                            {
                                DetailGameLogo.Source = logoBmp;
                                DetailGameLogo.IsVisible = true;
                                DetailGameTitle.IsVisible = false; // Authentic logo replaces raw text!
                            }
                        });
                    }

                    var heroBmp = await _sgdbService.FetchHeroBitmapAsync(appId, ct);
                    if (heroBmp != null && !ct.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ct.IsCancellationRequested && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                            {
                                DetailGameBackdrop.Source = heroBmp;
                                DetailGameBackdrop.IsVisible = true;
                            }
                        });
                    }
                }
                catch
                {
                    // Fall through gracefully
                }
            }, ct);
        }

        // 4. Query RAWG API for studio, publisher, playtime, tags, verdicts, and screenshots
        if (_rawgService.HasApiKey)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var meta = await _rawgService.FetchMetadataAsync(gameName, ct);
                    if (meta != null && !ct.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.Post(async () =>
                        {
                            if (ct.IsCancellationRequested || (_selectedGame?.AppId != appId && _selectedSearchResult?.AppId != appId))
                                return;

                            if (!string.IsNullOrEmpty(meta.CreditsLine) && DetailGameCredits != null)
                            {
                                DetailGameCredits.Text = meta.CreditsLine;
                                DetailGameCredits.IsVisible = true;
                            }

                            // Modern Rating Card: Score + Star Icon + Reviews count / Verdict
                            // Simple inline rating row matching other metadata
                            if (meta.Rating.HasValue && meta.Rating.Value > 0 && DetailRatingRow != null)
                            {
                                DetailRatingScoreText.Text = $"{meta.Rating.Value:0.0} / 5";
                                var subParts = new List<string>();
                                if (!string.IsNullOrEmpty(meta.FormattedReviewsCount)) subParts.Add(meta.FormattedReviewsCount);
                                if (!string.IsNullOrEmpty(meta.Verdict)) subParts.Add(meta.Verdict);
                                DetailRatingSubText.Text = subParts.Count > 0 ? string.Join("  •  ", subParts) : "";
                                DetailRatingRow.IsVisible = true;
                            }

                            // Secondary Info: Release Year, Average Playtime & Metacritic
                            var metaParts = new List<string>();
                            if (!string.IsNullOrEmpty(meta.ReleaseYear)) metaParts.Add(meta.ReleaseYear);
                            if (meta.PlaytimeHours.HasValue && meta.PlaytimeHours.Value > 0) metaParts.Add($"~{meta.PlaytimeHours.Value} hrs avg");
                            if (meta.MetacriticScore.HasValue && meta.MetacriticScore.Value > 0) metaParts.Add($"{meta.MetacriticScore.Value} metacritic");

                            if (metaParts.Count > 0 && DetailRawgMeta != null)
                            {
                                DetailRawgMeta.Text = string.Join("  •  ", metaParts);
                                DetailRawgMeta.IsVisible = true;
                            }

                            // Populate clean tag pills with SteamDB icons (limit to top 3 tags)
                            if (meta.Tags != null && meta.Tags.Count > 0 && DetailTagsPanel != null)
                            {
                                DetailTagsPanel.Children.Clear();
                                foreach (var tag in meta.Tags.Take(3))
                                {
                                    var formatted = _steamTagService.FormatTagWithIcon(tag);
                                    var border = new Border { Classes = { "tagPill" } };
                                    border.Child = new TextBlock { Text = formatted, Classes = { "tagPillText" } };
                                    DetailTagsPanel.Children.Add(border);
                                }
                                DetailTagsPanel.IsVisible = true;
                            }

                            // Initialize screenshot gallery
                            if (meta.Screenshots != null && meta.Screenshots.Count > 0)
                            {
                                _currentScreenshots = meta.Screenshots;
                                _currentScreenshotIndex = 0;
                                if (DetailGalleryIndexText != null)
                                {
                                    DetailGalleryIndexText.Text = $"1 / {_currentScreenshots.Count}";
                                }
                                if (DetailGalleryControls != null)
                                {
                                    DetailGalleryControls.IsVisible = _currentScreenshots.Count > 1;
                                }

                                if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
                                {
                                    _screenshotAutoRotateTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settingAutoRotateIntervalSec));
                                    _screenshotAutoRotateTimer.Start();
                                }

                                // If no hero backdrop was loaded yet, load the first screenshot
                                if (DetailGameBackdrop.Source == null && !string.IsNullOrEmpty(meta.BackgroundImageUrl))
                                {
                                    var rawgBmp = await _rawgService.FetchBackdropBitmapAsync(meta.BackgroundImageUrl, appId, ct);
                                    if (rawgBmp != null && !ct.IsCancellationRequested)
                                    {
                                        DetailGameBackdrop.Source = rawgBmp;
                                        DetailGameBackdrop.IsVisible = true;
                                    }
                                }
                            }
                        });
                    }
                }
                catch
                {
                    // Gracefully continue
                }
            }, ct);
        }
    }

    // Screenshot Gallery Cycling
    private void OnPrevScreenshotClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentScreenshots.Count <= 1) return;
        _currentScreenshotIndex = (_currentScreenshotIndex - 1 + _currentScreenshots.Count) % _currentScreenshots.Count;
        _ = ShowScreenshotAtIndexAsync(_currentScreenshotIndex);
        if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
        {
            _screenshotAutoRotateTimer.Stop();
            _screenshotAutoRotateTimer.Start();
        }
    }

    private void OnNextScreenshotClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentScreenshots.Count <= 1) return;
        _currentScreenshotIndex = (_currentScreenshotIndex + 1) % _currentScreenshots.Count;
        _ = ShowScreenshotAtIndexAsync(_currentScreenshotIndex);
        if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
        {
            _screenshotAutoRotateTimer.Stop();
            _screenshotAutoRotateTimer.Start();
        }
    }

    private async Task ShowScreenshotAtIndexAsync(int index)
    {
        if (index < 0 || index >= _currentScreenshots.Count) return;

        if (DetailGalleryIndexText != null)
        {
            DetailGalleryIndexText.Text = $"{index + 1} / {_currentScreenshots.Count}";
        }

        var appId = _selectedGame?.AppId ?? _selectedSearchResult?.AppId;
        if (string.IsNullOrEmpty(appId)) return;

        var url = _currentScreenshots[index];
        var bmp = await _rawgService.FetchScreenshotBitmapAsync(url, appId, index);
        if (bmp != null && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
        {
            DetailGameBackdrop.Source = bmp;
            DetailGameBackdrop.IsVisible = true;
        }
    }

    private void OnScreenshotTimerTick(object? sender, EventArgs e)
    {
        if (_currentView == ActiveView.GameDetail && _currentScreenshots.Count > 1)
        {
            _currentScreenshotIndex = (_currentScreenshotIndex + 1) % _currentScreenshots.Count;
            _ = ShowScreenshotAtIndexAsync(_currentScreenshotIndex);
        }
    }

    private void OpenSettingsPage()
    {
        _currentView = ActiveView.Settings;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        SetActiveSettingsTab(_activeSettingsTab);
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
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                    {
                        if (SearchResultsListBox.SelectedIndex == 0)
                        {
                            SearchBox.Focus();
                        }
                        else
                        {
                            NavigateSearchResults(-1);
                        }
                    }
                    else
                    {
                        NavigateList(-1);
                    }
                }
                else if (_currentView == ActiveView.GameDetail)
                {
                    NavigateDetailActions(-1);
                }
                else if (_currentView == ActiveView.Settings)
                {
                    NavigateSettingsOptions(-1);
                }
                break;

            case GamepadAction.NavigateDown:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchBox.IsFocused)
                    {
                        if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        {
                            SearchResultsListBox.SelectedIndex = 0;
                            SearchResultsListBox.Focus();
                        }
                        else
                        {
                            GamesListBox.Focus();
                            NavigateList(0);
                        }
                    }
                    else if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                    {
                        NavigateSearchResults(1);
                    }
                    else
                    {
                        NavigateList(1);
                    }
                }
                else if (_currentView == ActiveView.GameDetail)
                {
                    NavigateDetailActions(1);
                }
                else if (_currentView == ActiveView.Settings)
                {
                    NavigateSettingsOptions(1);
                }
                break;

            case GamepadAction.PageUp:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        NavigateSearchResults(-5);
                    else
                        NavigateList(-5);
                }
                else if (_currentView == ActiveView.GameDetail)
                {
                    OnPrevScreenshotClicked(null, new RoutedEventArgs());
                }
                else if (_currentView == ActiveView.Settings)
                {
                    CycleSettingsTab(-1);
                }
                break;

            case GamepadAction.PageDown:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        NavigateSearchResults(5);
                    else
                        NavigateList(5);
                }
                else if (_currentView == ActiveView.GameDetail)
                {
                    OnNextScreenshotClicked(null, new RoutedEventArgs());
                }
                else if (_currentView == ActiveView.Settings)
                {
                    CycleSettingsTab(1);
                }
                break;

            case GamepadAction.Confirm:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                    {
                        var searchItem = SearchResultsListBox.SelectedItem as SearchResultItem ?? _searchResults[0];
                        OpenSearchResultDetailPage(searchItem);
                    }
                    else if (GamesListBox.SelectedItem is PluginGame selected)
                    {
                        OpenGameDetailPage(selected);
                    }
                }
                else if (_currentView == ActiveView.GameDetail)
                {
                    TriggerDetailAction();
                }
                else if (_currentView == ActiveView.Settings)
                {
                    TriggerSettingsOption();
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
                else
                {
                    ClearSearchAndReset();
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

    private void NavigateSearchResults(int offset)
    {
        if (_searchResults.Count == 0 || SearchResultsListBox == null) return;

        int currentIndex = SearchResultsListBox.SelectedIndex;
        if (currentIndex < 0) currentIndex = 0;

        int newIndex = Math.Clamp(currentIndex + offset, 0, _searchResults.Count - 1);
        SearchResultsListBox.SelectedIndex = newIndex;
        var item = SearchResultsListBox.SelectedItem;
        if (item != null)
        {
            SearchResultsListBox.ScrollIntoView(item);
        }
    }

    private async void SyncAllGames()
    {
        PlutoLogger.Info("Pluto", "Syncing all plugin games into config.yaml...");
        foreach (var g in _allGames.Where(g => !g.IsAccela))
        {
            await _slsService.SyncGameToConfigAsync(g);
        }
    }

    // Dynamic rotating placeholder timer tick
    private void OnPlaceholderTimerTick(object? sender, EventArgs e)
    {
        if (SearchBox != null && !SearchBox.IsFocused && string.IsNullOrEmpty(SearchBox.Text))
        {
            _placeholderIndex = (_placeholderIndex + 1) % SearchPlaceholders.Length;
            SearchBox.PlaceholderText = SearchPlaceholders[_placeholderIndex];
        }
    }

    // UI Event Handlers
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = SearchBox?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _liveSearchTimer.Stop();
            _searchCts?.Cancel();
            if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
            if (SearchStatusText != null) SearchStatusText.IsVisible = false;
            if (GamesListBox != null) GamesListBox.IsVisible = true;
            ApplyFilter(null);
            return;
        }

        // 1. Immediately filter local library games
        ApplyFilter(text);

        // 2. Schedule live search if query >= 2 chars
        if (text.Trim().Length >= 2)
        {
            if (SearchStatusText != null)
            {
                SearchStatusText.Text = $"searching for \"{text.Trim()}\"...";
                SearchStatusText.IsVisible = true;
            }
            _liveSearchTimer.Stop();
            _liveSearchTimer.Start();
        }
    }

    private async void OnLiveSearchTimerTick(object? sender, EventArgs e)
    {
        _liveSearchTimer.Stop();
        await ExecuteSearchAsync(SearchBox?.Text);
    }

    private async Task ExecuteSearchAsync(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        if (SearchStatusText != null)
        {
            SearchStatusText.Text = $"searching online for \"{trimmed}\"...";
            SearchStatusText.IsVisible = true;
        }

        try
        {
            var results = await _hubcapSearchService.SearchAsync(trimmed, _allGames, ct);
            if (ct.IsCancellationRequested) return;

            _searchResults.Clear();
            foreach (var r in results)
            {
                _searchResults.Add(r);
            }

            // In-memory capsule thumbnail fetching for search results (without writing disk files)
            if (_settingSearchThumbnailsEnabled)
            {
                foreach (var r in results)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var thumbUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{r.AppId}/header.jpg";
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                            var bytes = await http.GetByteArrayAsync(thumbUrl, ct);
                            if (bytes.Length > 0 && !ct.IsCancellationRequested)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    using var ms = new MemoryStream(bytes);
                                    r.Thumbnail = new Bitmap(ms);
                                });
                            }
                        }
                        catch { }
                    }, ct);
                }
            }

            if (results.Count > 0)
            {
                if (SearchResultsListBox != null)
                {
                    SearchResultsListBox.IsVisible = true;
                    SearchResultsListBox.SelectedIndex = 0;
                }
                if (GamesListBox != null) GamesListBox.IsVisible = false;
                if (EmptyStateText != null) EmptyStateText.IsVisible = false;
                if (SearchStatusText != null)
                {
                    SearchStatusText.Text = $"found {results.Count} games";
                    SearchStatusText.IsVisible = true;
                }
            }
            else
            {
                if (_displayedGames.Count > 0)
                {
                    if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
                    if (GamesListBox != null) GamesListBox.IsVisible = true;
                    if (SearchStatusText != null)
                    {
                        SearchStatusText.Text = "no online matches (showing local library)";
                        SearchStatusText.IsVisible = true;
                    }
                }
                else
                {
                    if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
                    if (GamesListBox != null) GamesListBox.IsVisible = false;
                    if (EmptyStateText != null)
                    {
                        EmptyStateText.Text = $"no games found for \"{trimmed}\"";
                        EmptyStateText.IsVisible = true;
                    }
                    if (SearchStatusText != null) SearchStatusText.IsVisible = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Discard superseded search
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Search", $"Search failed: {ex.Message}");
            if (SearchStatusText != null)
            {
                SearchStatusText.Text = "search error • press enter to retry";
            }
        }
    }

    private void ClearSearchAndReset()
    {
        _liveSearchTimer.Stop();
        _searchCts?.Cancel();
        SearchBox.Text = string.Empty;
        if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
        if (SearchStatusText != null) SearchStatusText.IsVisible = false;
        if (GamesListBox != null)
        {
            GamesListBox.IsVisible = true;
            ApplyFilter(null);
            GamesListBox.Focus();
        }
    }

    private void OnSearchBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        GamesListBox?.Classes.Set("accessed", false);
    }

    private async void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
            {
                SearchResultsListBox.SelectedIndex = 0;
                SearchResultsListBox.Focus();
            }
            else
            {
                NavigateList(1);
                GamesListBox.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _liveSearchTimer.Stop();
            if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && SearchResultsListBox.SelectedItem is SearchResultItem selectedResult)
            {
                OpenSearchResultDetailPage(selectedResult);
            }
            else if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                await ExecuteSearchAsync(SearchBox.Text);
            }
            else if (GamesListBox.SelectedItem is PluginGame selectedLocal)
            {
                OpenGameDetailPage(selectedLocal);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSearchAndReset();
            e.Handled = true;
        }
    }

    private void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SearchResultsListBox.SelectedItem is SearchResultItem selected)
        {
            OpenSearchResultDetailPage(selected);
        }
    }

    private void OnSearchResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsListBox.SelectedItem != null)
        {
            SearchResultsListBox.ScrollIntoView(SearchResultsListBox.SelectedItem);
        }
    }

    private void OnSearchResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsListBox.SelectedItem is SearchResultItem selected)
        {
            OpenSearchResultDetailPage(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SearchResultsListBox.SelectedIndex == 0)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSearchAndReset();
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
        // 1. Adding an uninstalled game from search results
        if (_selectedSearchResult != null && _selectedGame == null)
        {
            var appId = _selectedSearchResult.AppId;
            var name = _selectedSearchResult.Name;

            DetailSwitchModeBtn.IsEnabled = false;
            if (DetailActionStatus != null)
            {
                DetailActionStatus.Text = "fetching keys and configuring slssteam...";
                DetailActionStatus.IsVisible = true;
            }
            PlutoLogger.Info("Search", $"Adding search result {name} ({appId}) to plugin native...");

            try
            {
                var keys = await _hubcapSearchService.FetchAndCacheManifestKeysAsync(appId);
                var depots = keys.Keys.ToList();

                var newGame = new PluginGame
                {
                    AppId = appId,
                    Name = name,
                    InstallDir = name,
                    IsAtom = true,
                    IsAccela = false,
                    Mode = "at0m",
                    Source = "plugin_native",
                    Depots = depots,
                    Keys = keys,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Sync directly to SLSsteam config.yaml preserving file inode
                await _slsService.SyncGameToConfigAsync(newGame);

                // Save to plugin_library.json
                await _libraryService.SaveGameAsync(newGame);

                // Refresh library
                await ReloadLibraryAsync();

                _selectedGame = newGame;
                _selectedSearchResult.IsInstalled = true;
                _selectedSearchResult.InstallMode = "native";

                DetailGameSubtitle.Text = $"{appId}  •  native";
                DetailSwitchModeBtn.Content = "move to assella";
                DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.LightSkyBlue;
                DetailSwitchModeBtn.IsEnabled = true;

                if (DetailActionStatus != null)
                {
                    DetailActionStatus.Text = "added to slssteam plugin";
                }

                if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = true;
                if (DetailSteamlessRow != null) DetailSteamlessRow.IsVisible = true;

                bool isOnline = _slsService.IsSlsOnline(appId);
                bool isNetsock = _slsService.IsNetsock(appId);
                UpdateOnlineTogglesUi(isOnline, isNetsock);

                PlutoLogger.Info("Search", $"Successfully added {name} ({appId}) to SLSsteam config and library.");
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Search", $"Failed to add {name} ({appId}) to plugin", ex);
                if (DetailActionStatus != null)
                {
                    DetailActionStatus.Text = $"failed: {ex.Message}";
                }
                DetailSwitchModeBtn.IsEnabled = true;
            }
            return;
        }

        // 2. Existing toggle between native and assella for installed games
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
            _settingAutoRotateScreenshots = _configService.GetBool("screenshot_autorotate_enabled", true);
            _settingAutoRotateIntervalSec = _configService.GetInt("screenshot_autorotate_interval_sec", 6);
            if (_settingAutoRotateIntervalSec < 2) _settingAutoRotateIntervalSec = 6;

            _settingDynamicMainBackdrop = _configService.GetBool("main_dynamic_backdrop_enabled", true);
            _settingMainBackdropIntervalSec = _configService.GetInt("main_dynamic_backdrop_interval_sec", 15);
            if (_settingMainBackdropIntervalSec < 5) _settingMainBackdropIntervalSec = 15;

            _settingSearchThumbnailsEnabled = _configService.GetBool("search_thumbnails_enabled", true);

            UpdateSettingsUi();
            PlutoLogger.Info("Pluto", $"Settings loaded: vapor={_settingVaporEnabled}, downloadAction={_settingDownloadAction}, disableUpdates={_settingDisableUpdates}, autoRotate={_settingAutoRotateScreenshots} ({_settingAutoRotateIntervalSec}s), mainBackdrop={_settingDynamicMainBackdrop} ({_settingMainBackdropIntervalSec}s), searchThumbnails={_settingSearchThumbnailsEnabled}");
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

        if (ToggleAutoRotateBtn != null)
        {
            ToggleAutoRotateBtn.Content = _settingAutoRotateScreenshots ? "enabled" : "disabled";
            ToggleAutoRotateBtn.Foreground = _settingAutoRotateScreenshots
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }

        if (ToggleAutoRotateIntervalBtn != null)
        {
            ToggleAutoRotateIntervalBtn.Content = $"{_settingAutoRotateIntervalSec} seconds";
        }

        if (ToggleMainBackdropBtn != null)
        {
            ToggleMainBackdropBtn.Content = _settingDynamicMainBackdrop ? "enabled" : "disabled";
            ToggleMainBackdropBtn.Foreground = _settingDynamicMainBackdrop
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }

        if (ToggleMainBackdropIntervalBtn != null)
        {
            ToggleMainBackdropIntervalBtn.Content = $"{_settingMainBackdropIntervalSec} seconds";
        }

        if (ToggleSearchThumbnailsBtn != null)
        {
            ToggleSearchThumbnailsBtn.Content = _settingSearchThumbnailsEnabled ? "enabled" : "disabled";
            ToggleSearchThumbnailsBtn.Foreground = _settingSearchThumbnailsEnabled
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.Gray;
        }
    }

    private void SetActiveSettingsTab(SettingsTab tab)
    {
        _activeSettingsTab = tab;

        if (SettingsTabThemeBtn != null) SettingsTabThemeBtn.Classes.Set("active", tab == SettingsTab.Theme);
        if (SettingsTabApiBtn != null) SettingsTabApiBtn.Classes.Set("active", tab == SettingsTab.Api);
        if (SettingsTabSlsBtn != null) SettingsTabSlsBtn.Classes.Set("active", tab == SettingsTab.Sls);
        if (SettingsTabVisualsBtn != null) SettingsTabVisualsBtn.Classes.Set("active", tab == SettingsTab.Visuals);

        if (SettingsThemePanel != null) SettingsThemePanel.IsVisible = tab == SettingsTab.Theme;
        if (SettingsApiPanel != null) SettingsApiPanel.IsVisible = tab == SettingsTab.Api;
        if (SettingsSlsPanel != null) SettingsSlsPanel.IsVisible = tab == SettingsTab.Sls;
        if (SettingsVisualsPanel != null) SettingsVisualsPanel.IsVisible = tab == SettingsTab.Visuals;

        _settingsOptionIndex = 0;
        UpdateSettingsOptionHighlight();
    }

    private void OnSettingsTabThemeClicked(object? sender, RoutedEventArgs e) => SetActiveSettingsTab(SettingsTab.Theme);
    private void OnSettingsTabApiClicked(object? sender, RoutedEventArgs e) => SetActiveSettingsTab(SettingsTab.Api);
    private void OnSettingsTabSlsClicked(object? sender, RoutedEventArgs e) => SetActiveSettingsTab(SettingsTab.Sls);
    private void OnSettingsTabVisualsClicked(object? sender, RoutedEventArgs e) => SetActiveSettingsTab(SettingsTab.Visuals);

    private void OnToggleAutoRotateClicked(object? sender, RoutedEventArgs e)
    {
        _settingAutoRotateScreenshots = !_settingAutoRotateScreenshots;
        UpdateSettingsUi();
        _configService.SetBool("screenshot_autorotate_enabled", _settingAutoRotateScreenshots);
        if (!_settingAutoRotateScreenshots)
        {
            _screenshotAutoRotateTimer.Stop();
        }
        else if (_currentView == ActiveView.GameDetail && _currentScreenshots.Count > 1)
        {
            _screenshotAutoRotateTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settingAutoRotateIntervalSec));
            _screenshotAutoRotateTimer.Start();
        }
    }

    private void OnToggleAutoRotateIntervalClicked(object? sender, RoutedEventArgs e)
    {
        _settingAutoRotateIntervalSec = _settingAutoRotateIntervalSec switch
        {
            <= 3 => 6,
            <= 6 => 10,
            <= 10 => 15,
            _ => 3
        };
        UpdateSettingsUi();
        _configService.SetInt("screenshot_autorotate_interval_sec", _settingAutoRotateIntervalSec);
        _screenshotAutoRotateTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settingAutoRotateIntervalSec));
    }

    private void OnToggleMainBackdropClicked(object? sender, RoutedEventArgs e)
    {
        _settingDynamicMainBackdrop = !_settingDynamicMainBackdrop;
        UpdateSettingsUi();
        _configService.SetBool("main_dynamic_backdrop_enabled", _settingDynamicMainBackdrop);
        if (!_settingDynamicMainBackdrop)
        {
            _mainBackdropTimer.Stop();
            if (MainBackdropImage != null) MainBackdropImage.Source = null;
        }
        else
        {
            _mainBackdropTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settingMainBackdropIntervalSec));
            _mainBackdropTimer.Start();
            TriggerNextMainBackdrop();
        }
    }

    private void OnToggleMainBackdropIntervalClicked(object? sender, RoutedEventArgs e)
    {
        _settingMainBackdropIntervalSec = _settingMainBackdropIntervalSec switch
        {
            <= 5 => 10,
            <= 10 => 15,
            <= 15 => 30,
            _ => 5
        };
        UpdateSettingsUi();
        _configService.SetInt("main_dynamic_backdrop_interval_sec", _settingMainBackdropIntervalSec);
        _mainBackdropTimer.Interval = TimeSpan.FromSeconds(_settingMainBackdropIntervalSec);
    }

    private void OnToggleSearchThumbnailsClicked(object? sender, RoutedEventArgs e)
    {
        _settingSearchThumbnailsEnabled = !_settingSearchThumbnailsEnabled;
        UpdateSettingsUi();
        _configService.SetBool("search_thumbnails_enabled", _settingSearchThumbnailsEnabled);
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

    // Dynamic Main Page Backdrop Rotation
    private void OnMainBackdropTimerTick(object? sender, EventArgs e)
    {
        if (_currentView == ActiveView.MainList && _settingDynamicMainBackdrop && _mainBackdropPool.Count > 0)
        {
            TriggerNextMainBackdrop();
        }
    }

    private async void TriggerNextMainBackdrop()
    {
        if (_mainBackdropPool.Count == 0) return;
        _mainBackdropIndex = (_mainBackdropIndex + 1) % _mainBackdropPool.Count;
        var appId = _mainBackdropPool[_mainBackdropIndex];

        try
        {
            var bmp = await _sgdbService.FetchHeroBitmapAsync(appId);
            if (bmp == null)
            {
                var heroUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var bytes = await http.GetByteArrayAsync(heroUrl);
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    bmp = new Bitmap(ms);
                }
            }

            if (bmp != null && _currentView == ActiveView.MainList && MainBackdropImage != null)
            {
                MainBackdropImage.Source = bmp;
            }
        }
        catch
        {
            // Fall through gracefully on network error
        }
    }

    // EOS Proxy State & Handlers
    private void UpdateEosProxyUi()
    {
        if (DetailEosProxyBtn == null) return;
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath))
        {
            DetailEosProxyBtn.IsVisible = false;
            return;
        }

        var status = _eosProxyService.GetProxyStatus(_selectedGame.InstallPath);
        switch (status)
        {
            case EosProxyStatus.Active:
                DetailEosProxyBtn.IsVisible = true;
                DetailEosProxyBtn.Content = "eos proxy: active";
                DetailEosProxyBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
                break;
            case EosProxyStatus.Inactive:
                DetailEosProxyBtn.IsVisible = true;
                DetailEosProxyBtn.Content = "enable eos proxy";
                DetailEosProxyBtn.Foreground = Avalonia.Media.Brushes.Gray;
                break;
            case EosProxyStatus.Stale:
                DetailEosProxyBtn.IsVisible = true;
                DetailEosProxyBtn.Content = "reapply eos proxy";
                DetailEosProxyBtn.Foreground = Avalonia.Media.Brushes.Goldenrod;
                break;
            case EosProxyStatus.None:
            default:
                DetailEosProxyBtn.IsVisible = false;
                break;
        }
    }

    private async void OnToggleEosProxyClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath)) return;
        var status = _eosProxyService.GetProxyStatus(_selectedGame.InstallPath);
        if (status == EosProxyStatus.Active)
        {
            await _eosProxyService.RemoveProxyAsync(_selectedGame.InstallPath);
        }
        else
        {
            await _eosProxyService.ApplyProxyAsync(_selectedGame.InstallPath);
        }
        UpdateEosProxyUi();
    }

    // Gamepad Controller Navigation for Detail Actions
    private List<Button> GetVisibleDetailActionButtons()
    {
        var list = new List<Button>();
        if (DetailSwitchModeBtn != null && DetailSwitchModeBtn.IsVisible && DetailSwitchModeBtn.IsEnabled) list.Add(DetailSwitchModeBtn);
        if (DetailSlsOnlineBtn != null && DetailSlsOnlineBtn.IsVisible && DetailSlsOnlineBtn.IsEnabled) list.Add(DetailSlsOnlineBtn);
        if (DetailNetsockBtn != null && DetailNetsockBtn.IsVisible && DetailNetsockBtn.IsEnabled) list.Add(DetailNetsockBtn);
        if (DetailEosProxyBtn != null && DetailEosProxyBtn.IsVisible && DetailEosProxyBtn.IsEnabled) list.Add(DetailEosProxyBtn);
        if (DetailSteamlessBtn != null && DetailSteamlessBtn.IsVisible && DetailSteamlessBtn.IsEnabled) list.Add(DetailSteamlessBtn);
        return list;
    }

    private void NavigateDetailActions(int offset)
    {
        var buttons = GetVisibleDetailActionButtons();
        if (buttons.Count == 0) return;

        _detailActionIndex = Math.Clamp(_detailActionIndex + offset, 0, buttons.Count - 1);
        UpdateDetailActionHighlight();
    }

    private void TriggerDetailAction()
    {
        var buttons = GetVisibleDetailActionButtons();
        if (_detailActionIndex >= 0 && _detailActionIndex < buttons.Count)
        {
            buttons[_detailActionIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void UpdateDetailActionHighlight()
    {
        var buttons = GetVisibleDetailActionButtons();
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].Classes.Set("actionBtnFocused", i == _detailActionIndex);
        }
    }

    // Gamepad Controller Navigation for Settings Options
    private List<Button> GetVisibleSettingsButtons()
    {
        var list = new List<Button>();
        switch (_activeSettingsTab)
        {
            case SettingsTab.Theme:
                if (ToggleNativeThemeBtn != null && ToggleNativeThemeBtn.IsVisible) list.Add(ToggleNativeThemeBtn);
                if (ToggleAccelaThemeBtn != null && ToggleAccelaThemeBtn.IsVisible) list.Add(ToggleAccelaThemeBtn);
                break;
            case SettingsTab.Api:
                if (ToggleSgdbApiBtn != null && ToggleSgdbApiBtn.IsVisible) list.Add(ToggleSgdbApiBtn);
                if (ToggleRawgBtn != null && ToggleRawgBtn.IsVisible) list.Add(ToggleRawgBtn);
                if (ToggleHubcapApiBtn != null && ToggleHubcapApiBtn.IsVisible) list.Add(ToggleHubcapApiBtn);
                break;
            case SettingsTab.Sls:
                if (ToggleVaporBtn != null && ToggleVaporBtn.IsVisible) list.Add(ToggleVaporBtn);
                if (ToggleDownloadActionBtn != null && ToggleDownloadActionBtn.IsVisible) list.Add(ToggleDownloadActionBtn);
                if (ToggleUpdatesBtn != null && ToggleUpdatesBtn.IsVisible) list.Add(ToggleUpdatesBtn);
                break;
            case SettingsTab.Visuals:
                if (ToggleMainBackdropBtn != null && ToggleMainBackdropBtn.IsVisible) list.Add(ToggleMainBackdropBtn);
                if (ToggleMainBackdropIntervalBtn != null && ToggleMainBackdropIntervalBtn.IsVisible) list.Add(ToggleMainBackdropIntervalBtn);
                if (ToggleSearchThumbnailsBtn != null && ToggleSearchThumbnailsBtn.IsVisible) list.Add(ToggleSearchThumbnailsBtn);
                if (ToggleAutoRotateBtn != null && ToggleAutoRotateBtn.IsVisible) list.Add(ToggleAutoRotateBtn);
                if (ToggleAutoRotateIntervalBtn != null && ToggleAutoRotateIntervalBtn.IsVisible) list.Add(ToggleAutoRotateIntervalBtn);
                break;
        }
        return list;
    }

    private void CycleSettingsTab(int offset)
    {
        int count = 4;
        int next = (((int)_activeSettingsTab + offset) % count + count) % count;
        SetActiveSettingsTab((SettingsTab)next);
    }

    private void NavigateSettingsOptions(int offset)
    {
        var buttons = GetVisibleSettingsButtons();
        if (buttons.Count == 0) return;

        _settingsOptionIndex = Math.Clamp(_settingsOptionIndex + offset, 0, buttons.Count - 1);
        UpdateSettingsOptionHighlight();
    }

    private void TriggerSettingsOption()
    {
        var buttons = GetVisibleSettingsButtons();
        if (_settingsOptionIndex >= 0 && _settingsOptionIndex < buttons.Count)
        {
            buttons[_settingsOptionIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void UpdateSettingsOptionHighlight()
    {
        var buttons = GetVisibleSettingsButtons();
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].Classes.Set("actionBtnFocused", i == _settingsOptionIndex);
        }
    }

    // Theme Submenu & Color Configuration
    private void ApplyThemeColors()
    {
        var native = _themeService.CurrentNative;
        var accela = _themeService.CurrentAccela;

        if (Color.TryParse(native.HighlightHex, out var nCol))
        {
            Resources["NativeGameColor"] = new SolidColorBrush(nCol);
            Resources["NativeGameHoverColor"] = new SolidColorBrush(Color.FromArgb(200, nCol.R, nCol.G, nCol.B));
        }
        if (Color.TryParse(native.DimmedHex, out var nDim))
        {
            Resources["NativeGameUnselectedColor"] = new SolidColorBrush(nDim);
        }

        if (Color.TryParse(accela.HighlightHex, out var aCol))
        {
            Resources["AccelaGameColor"] = new SolidColorBrush(aCol);
            Resources["AccelaGameHoverColor"] = new SolidColorBrush(Color.FromArgb(200, aCol.R, aCol.G, aCol.B));
        }
        if (Color.TryParse(accela.DimmedHex, out var aDim))
        {
            Resources["AccelaGameUnselectedColor"] = new SolidColorBrush(aDim);
        }

        if (ToggleNativeThemeBtn != null)
        {
            ToggleNativeThemeBtn.Content = native.Name;
            ToggleNativeThemeBtn.Foreground = new SolidColorBrush(nCol);
        }

        if (ToggleAccelaThemeBtn != null)
        {
            ToggleAccelaThemeBtn.Content = accela.Name;
            ToggleAccelaThemeBtn.Foreground = new SolidColorBrush(aCol);
        }

        if (ToggleSgdbApiBtn != null)
        {
            bool hasKey = _sgdbService.HasApiKey;
            ToggleSgdbApiBtn.Content = hasKey ? "api active" : "key missing (add to ~/.config/pluto/steamgriddb_api.txt)";
            ToggleSgdbApiBtn.Foreground = hasKey ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }

        if (ToggleRawgBtn != null)
        {
            bool hasKey = _rawgService.HasApiKey;
            ToggleRawgBtn.Content = hasKey ? "api active" : "key missing (add to ~/.config/pluto/rawg_api.txt)";
            ToggleRawgBtn.Foreground = hasKey ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }

        if (ToggleHubcapApiBtn != null)
        {
            bool hasKey = !string.IsNullOrWhiteSpace(_configService.GetValue("morrenus_api_key"));
            ToggleHubcapApiBtn.Content = hasKey ? "api configured" : "key not set (add to ACCELA.conf)";
            ToggleHubcapApiBtn.Foreground = hasKey ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
    }

    private void OnToggleNativeThemeClicked(object? sender, RoutedEventArgs e)
    {
        _themeService.CycleNextNative();
        ApplyThemeColors();
    }

    private void OnToggleAccelaThemeClicked(object? sender, RoutedEventArgs e)
    {
        _themeService.CycleNextAccela();
        ApplyThemeColors();
    }

    private void OnToggleSgdbApiClicked(object? sender, RoutedEventArgs e)
    {
        // Re-read and refresh SteamGridDB status
        ApplyThemeColors();
    }

    private void OnToggleRawgClicked(object? sender, RoutedEventArgs e)
    {
        // Re-read and refresh RAWG status
        ApplyThemeColors();
    }

    private void OnToggleHubcapApiClicked(object? sender, RoutedEventArgs e)
    {
        // Re-read and refresh Hubcap status
        ApplyThemeColors();
    }
}