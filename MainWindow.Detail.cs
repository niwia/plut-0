using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pluto.Models;
using Pluto.Services;

namespace Pluto;

// MainWindow partial — game detail page, artwork, screenshots, and detail actions
public partial class MainWindow
{
    private void OpenGameDetailPage(PluginGame game)
    {
        _selectedGame         = game;
        _selectedSearchResult = null;
        _currentView          = ActiveView.GameDetail;

        ResetDetailUi();

        if (DetailGameTitle    != null) { DetailGameTitle.Text    = game.Name; DetailGameTitle.IsVisible = true; }
        if (DetailGameSubtitle != null) DetailGameSubtitle.Text   = $"{game.AppId}  •  {(game.IsAccela ? "assella" : "native")}";

        if (game.IsAccela)
        {
            DetailSwitchModeBtn.Content    = "add to plugin";
            DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
        }
        else
        {
            DetailSwitchModeBtn.Content    = "move to assella";
            DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.LightSkyBlue;
        }
        DetailSwitchModeBtn.IsEnabled = true;

        if (DetailActionStatus    != null) DetailActionStatus.IsVisible    = false;
        if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = true;
        if (DetailSteamlessRow    != null) DetailSteamlessRow.IsVisible    = true;

        bool isOnline  = _slsService.IsSlsOnline(game.AppId);
        bool isNetsock = _slsService.IsNetsock(game.AppId);
        UpdateOnlineTogglesUi(isOnline, isNetsock);
        UpdateEosProxyUi();

        DetailSteamlessBtn.IsEnabled = true;
        DetailSteamlessBtn.Content   = "apply steamless";
        DetailSteamlessStatus.IsVisible = false;

        _detailActionIndex = 0;
        UpdateDetailActionHighlight();

        _ = LoadGameArtworkAndMetadataAsync(game.AppId, game.Name);

        MainListPanel.IsVisible   = false;
        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible   = false;
    }

    private void OpenSearchResultDetailPage(SearchResultItem item)
    {
        // If already in library, open as regular game
        var local = _allGames.FirstOrDefault(g => g.AppId == item.AppId);
        if (local != null) { OpenGameDetailPage(local); return; }

        _selectedSearchResult = item;
        _selectedGame         = null;
        _currentView          = ActiveView.GameDetail;

        ResetDetailUi();

        if (DetailGameTitle    != null) { DetailGameTitle.Text    = item.Name; DetailGameTitle.IsVisible = true; }
        if (DetailGameSubtitle != null) DetailGameSubtitle.Text   = $"{item.AppId}  •  online";

        DetailSwitchModeBtn.Content    = "add to plugin";
        DetailSwitchModeBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
        DetailSwitchModeBtn.IsEnabled  = true;

        if (DetailActionStatus    != null) DetailActionStatus.IsVisible    = false;
        if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = false;
        if (DetailSteamlessRow    != null) DetailSteamlessRow.IsVisible    = false;

        _detailActionIndex = 0;
        UpdateDetailActionHighlight();

        _ = LoadGameArtworkAndMetadataAsync(item.AppId, item.Name);

        MainListPanel.IsVisible   = false;
        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible   = false;
    }

    // Reset all detail UI elements to blank/hidden state
    private void ResetDetailUi()
    {
        _currentScreenshots.Clear();
        _currentScreenshotIndex = 0;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        if (DetailGameLogo         != null) { DetailGameLogo.Source  = null; DetailGameLogo.IsVisible = false; }
        if (DetailGameTitle        != null) DetailGameTitle.IsVisible  = false;
        if (DetailGameCredits      != null) { DetailGameCredits.Text   = string.Empty; DetailGameCredits.IsVisible = false; }
        if (DetailRatingRow        != null) DetailRatingRow.IsVisible   = false;
        if (DetailRawgMeta         != null) { DetailRawgMeta.Text       = string.Empty; DetailRawgMeta.IsVisible = false; }
        if (DetailTagsPanel        != null) { DetailTagsPanel.Children.Clear(); DetailTagsPanel.IsVisible = false; }
        if (DetailGalleryControls  != null) DetailGalleryControls.IsVisible = false;
        if (DetailGameBackdrop     != null) DetailGameBackdrop.IsVisible = false;
    }

    // Artwork + metadata loading
    private async Task LoadGameArtworkAndMetadataAsync(string appId, string gameName)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        // 1. Try local ACCELA image cache
        var imageCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "ACCELA", "image_cache", $"{appId}.jpg");

        bool imageLoaded = false;
        if (File.Exists(imageCachePath))
        {
            try
            {
                DetailGameBackdrop.Source     = new Bitmap(imageCachePath);
                DetailGameBackdrop.IsVisible  = true;
                imageLoaded = true;
            }
            catch { }
        }

        // 2. On-demand steam header fetch
        if (!imageLoaded)
        {
            DetailGameBackdrop.Source    = null;
            DetailGameBackdrop.IsVisible = false;

            _ = Task.Run(async () =>
            {
                var bmp = await _hubcapSearchService.FetchAndCacheGameThumbnailAsync(appId, null, ct);
                if (bmp != null && !ct.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ct.IsCancellationRequested
                            && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                        {
                            DetailGameBackdrop.Source    = bmp;
                            DetailGameBackdrop.IsVisible = true;
                        }
                    });
                }
            }, ct);
        }

        // 3. SteamGridDB logo + hero
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
                            if (!ct.IsCancellationRequested
                                && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                            {
                                DetailGameLogo.Source    = logoBmp;
                                DetailGameLogo.IsVisible = true;
                                DetailGameTitle.IsVisible = false;
                            }
                        });
                    }

                    var heroBmp = await _sgdbService.FetchHeroBitmapAsync(appId, ct);
                    if (heroBmp != null && !ct.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ct.IsCancellationRequested
                                && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
                            {
                                DetailGameBackdrop.Source    = heroBmp;
                                DetailGameBackdrop.IsVisible = true;
                            }
                        });
                    }
                }
                catch { }
            }, ct);
        }

        // 4. RAWG metadata: credits, rating, tags, screenshots
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
                            if (ct.IsCancellationRequested
                                || (_selectedGame?.AppId != appId && _selectedSearchResult?.AppId != appId))
                                return;

                            // Credits
                            if (!string.IsNullOrEmpty(meta.CreditsLine) && DetailGameCredits != null)
                            {
                                DetailGameCredits.Text      = meta.CreditsLine;
                                DetailGameCredits.IsVisible = true;
                            }

                            // Rating row
                            if (meta.Rating.HasValue && meta.Rating.Value > 0 && DetailRatingRow != null)
                            {
                                DetailRatingScoreText.Text = $"{meta.Rating.Value:0.0} / 5";
                                var parts = new List<string>();
                                if (!string.IsNullOrEmpty(meta.FormattedReviewsCount)) parts.Add(meta.FormattedReviewsCount);
                                if (!string.IsNullOrEmpty(meta.Verdict))               parts.Add(meta.Verdict);
                                DetailRatingSubText.Text = parts.Count > 0 ? string.Join("  •  ", parts) : "";
                                DetailRatingRow.IsVisible = true;
                            }

                            // Secondary meta line
                            var metaParts = new List<string>();
                            if (!string.IsNullOrEmpty(meta.ReleaseYear))                          metaParts.Add(meta.ReleaseYear);
                            if (meta.PlaytimeHours.HasValue && meta.PlaytimeHours.Value > 0)      metaParts.Add($"~{meta.PlaytimeHours.Value} hrs avg");
                            if (meta.MetacriticScore.HasValue && meta.MetacriticScore.Value > 0)  metaParts.Add($"{meta.MetacriticScore.Value} metacritic");

                            if (metaParts.Count > 0 && DetailRawgMeta != null)
                            {
                                DetailRawgMeta.Text      = string.Join("  •  ", metaParts);
                                DetailRawgMeta.IsVisible = true;
                            }

                            // Top-3 tag pills
                            if (meta.Tags != null && meta.Tags.Count > 0 && DetailTagsPanel != null)
                            {
                                DetailTagsPanel.Children.Clear();
                                foreach (var tag in meta.Tags.Take(3))
                                {
                                    var fmt    = _steamTagService.FormatTagWithIcon(tag);
                                    var border = new Border { Classes = { "tagPill" } };
                                    border.Child = new TextBlock { Text = fmt, Classes = { "tagPillText" } };
                                    DetailTagsPanel.Children.Add(border);
                                }
                                DetailTagsPanel.IsVisible = true;
                            }

                            // Screenshot gallery
                            if (meta.Screenshots != null && meta.Screenshots.Count > 0)
                            {
                                _currentScreenshots     = meta.Screenshots;
                                _currentScreenshotIndex = 0;

                                if (DetailGalleryIndexText  != null)
                                    DetailGalleryIndexText.Text = $"1 / {_currentScreenshots.Count}";
                                if (DetailGalleryControls != null)
                                    DetailGalleryControls.IsVisible = _currentScreenshots.Count > 1;

                                if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
                                {
                                    _screenshotAutoRotateTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settingAutoRotateIntervalSec));
                                    _screenshotAutoRotateTimer.Start();
                                }

                                if (DetailGameBackdrop.Source == null && !string.IsNullOrEmpty(meta.BackgroundImageUrl))
                                {
                                    var rawgBmp = await _rawgService.FetchBackdropBitmapAsync(meta.BackgroundImageUrl, appId, ct);
                                    if (rawgBmp != null && !ct.IsCancellationRequested)
                                    {
                                        DetailGameBackdrop.Source    = rawgBmp;
                                        DetailGameBackdrop.IsVisible = true;
                                    }
                                }
                            }
                        });
                    }
                }
                catch { }
            }, ct);
        }
    }

    // Screenshot gallery
    private void OnPrevScreenshotClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentScreenshots.Count <= 1) return;
        _currentScreenshotIndex = (_currentScreenshotIndex - 1 + _currentScreenshots.Count) % _currentScreenshots.Count;
        _ = ShowScreenshotAtIndexAsync(_currentScreenshotIndex);
        if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
        { _screenshotAutoRotateTimer.Stop(); _screenshotAutoRotateTimer.Start(); }
    }

    private void OnNextScreenshotClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentScreenshots.Count <= 1) return;
        _currentScreenshotIndex = (_currentScreenshotIndex + 1) % _currentScreenshots.Count;
        _ = ShowScreenshotAtIndexAsync(_currentScreenshotIndex);
        if (_settingAutoRotateScreenshots && _currentScreenshots.Count > 1)
        { _screenshotAutoRotateTimer.Stop(); _screenshotAutoRotateTimer.Start(); }
    }

    private async Task ShowScreenshotAtIndexAsync(int index)
    {
        if (index < 0 || index >= _currentScreenshots.Count) return;

        if (DetailGalleryIndexText != null)
            DetailGalleryIndexText.Text = $"{index + 1} / {_currentScreenshots.Count}";

        var appId = _selectedGame?.AppId ?? _selectedSearchResult?.AppId;
        if (string.IsNullOrEmpty(appId)) return;

        var bmp = await _rawgService.FetchScreenshotBitmapAsync(_currentScreenshots[index], appId, index);
        if (bmp != null && (_selectedGame?.AppId == appId || _selectedSearchResult?.AppId == appId))
        {
            DetailGameBackdrop.Source    = bmp;
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

    // Detail actions: mode switch, slsonline, netsock, eos proxy, steamless
    private async void OnToggleModeClicked(object? sender, RoutedEventArgs e)
    {
        // Adding uninstalled search result to plugin
        if (_selectedSearchResult != null && _selectedGame == null)
        {
            var appId = _selectedSearchResult.AppId;
            var name  = _selectedSearchResult.Name;

            DetailSwitchModeBtn.IsEnabled = false;
            if (DetailActionStatus != null) { DetailActionStatus.Text = "fetching keys and configuring slssteam..."; DetailActionStatus.IsVisible = true; }
            PlutoLogger.Info("Search", $"Adding {name} ({appId}) to plugin...");

            try
            {
                var keys   = await _hubcapSearchService.FetchAndCacheManifestKeysAsync(appId);
                var depots = keys.Keys.ToList();

                var newGame = new PluginGame
                {
                    AppId     = appId, Name = name, InstallDir = name,
                    IsAtom    = true,  IsAccela = false, Mode = "at0m",
                    Source    = "plugin_native", Depots = depots, Keys = keys,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await _slsService.SyncGameToConfigAsync(newGame);
                await _libraryService.SaveGameAsync(newGame);
                await ReloadLibraryAsync();

                _selectedGame = newGame;
                _selectedSearchResult.IsInstalled  = true;
                _selectedSearchResult.InstallMode  = "native";

                DetailGameSubtitle.Text            = $"{appId}  •  native";
                DetailSwitchModeBtn.Content        = "move to assella";
                DetailSwitchModeBtn.Foreground     = Avalonia.Media.Brushes.LightSkyBlue;
                DetailSwitchModeBtn.IsEnabled      = true;

                if (DetailActionStatus   != null) DetailActionStatus.Text    = "added to slssteam plugin";
                if (DetailOnlineTogglesRow != null) DetailOnlineTogglesRow.IsVisible = true;
                if (DetailSteamlessRow   != null) DetailSteamlessRow.IsVisible    = true;

                UpdateOnlineTogglesUi(_slsService.IsSlsOnline(appId), _slsService.IsNetsock(appId));
                PlutoLogger.Info("Search", $"Successfully added {name} ({appId}).");
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Search", $"Failed to add {name} ({appId})", ex);
                if (DetailActionStatus != null) DetailActionStatus.Text = $"failed: {ex.Message}";
                DetailSwitchModeBtn.IsEnabled = true;
            }
            return;
        }

        // Toggle existing installed game between native <-> assella
        if (_selectedGame != null)
        {
            if (_selectedGame.IsAccela)
            {
                PlutoLogger.Info("Transition", $"Converting {_selectedGame.Name} to plugin native");
                await _transitionService.ConvertToAtomPluginAsync(_selectedGame);
                _selectedGame.IsAccela = false;
            }
            else
            {
                PlutoLogger.Info("Transition", $"Reverting {_selectedGame.Name} to assella managed");
                await _transitionService.ConvertToAccelaManagedAsync(_selectedGame);
                _selectedGame.IsAccela = true;
            }

            DetailGameSubtitle.Text        = $"{_selectedGame.AppId}  •  {(_selectedGame.IsAccela ? "assella" : "native")}";
            DetailSwitchModeBtn.Content    = _selectedGame.IsAccela ? "add to plugin" : "move to assella";
            DetailSwitchModeBtn.Foreground = _selectedGame.IsAccela
                ? Avalonia.Media.Brushes.MediumSpringGreen
                : Avalonia.Media.Brushes.LightSkyBlue;

            await ReloadLibraryAsync();
        }
    }

    private async void OnToggleSlsOnlineClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null) return;
        bool cur = _slsService.IsSlsOnline(_selectedGame.AppId);
        await _slsService.SetSlsOnlineAsync(_selectedGame.AppId, _selectedGame.Name, !cur);
        UpdateOnlineTogglesUi(_slsService.IsSlsOnline(_selectedGame.AppId), _slsService.IsNetsock(_selectedGame.AppId));
    }

    private async void OnToggleNetsockClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null) return;
        bool cur = _slsService.IsNetsock(_selectedGame.AppId);
        await _slsService.SetNetsockAsync(_selectedGame.AppId, !cur);
        UpdateOnlineTogglesUi(_slsService.IsSlsOnline(_selectedGame.AppId), _slsService.IsNetsock(_selectedGame.AppId));
    }

    private void UpdateOnlineTogglesUi(bool isOnline, bool isNetsock)
    {
        if (isOnline)
        {
            DetailSlsOnlineBtn.Content    = "slsonline: active";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
            DetailNetsockBtn.IsVisible    = true;
            DetailNetsockBtn.Content      = isNetsock ? "netsock proxy: active" : "enable netsock proxy";
            DetailNetsockBtn.Foreground   = isNetsock ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        else
        {
            DetailSlsOnlineBtn.Content    = "enable slsonline";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.Gray;
            DetailNetsockBtn.IsVisible    = false;
        }
    }

    private async void OnApplySteamlessClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null) return;
        DetailSteamlessBtn.IsEnabled    = false;
        DetailSteamlessBtn.Content      = "processing steamless...";
        DetailSteamlessStatus.Text      = "scanning and unpacking executables...";
        DetailSteamlessStatus.Foreground = Avalonia.Media.Brushes.Gray;
        DetailSteamlessStatus.IsVisible  = true;

        var result = await _steamlessService.ProcessGameAsync(_selectedGame.InstallPath, _selectedGame.Name);

        DetailSteamlessBtn.IsEnabled    = true;
        DetailSteamlessBtn.Content      = "apply steamless";
        DetailSteamlessStatus.Text      = result.Message;
        DetailSteamlessStatus.Foreground = result.Success
            ? Avalonia.Media.Brushes.MediumSpringGreen
            : Avalonia.Media.Brushes.Gray;
    }

    // EOS Proxy
    private void UpdateEosProxyUi()
    {
        if (DetailEosProxyBtn == null) return;
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath))
        { DetailEosProxyBtn.IsVisible = false; return; }

        var status = _eosProxyService.GetProxyStatus(_selectedGame.InstallPath);
        switch (status)
        {
            case EosProxyStatus.Active:
                DetailEosProxyBtn.IsVisible    = true;
                DetailEosProxyBtn.Content      = "eos proxy: active";
                DetailEosProxyBtn.Foreground   = Avalonia.Media.Brushes.MediumSpringGreen;
                break;
            case EosProxyStatus.Inactive:
                DetailEosProxyBtn.IsVisible    = true;
                DetailEosProxyBtn.Content      = "enable eos proxy";
                DetailEosProxyBtn.Foreground   = Avalonia.Media.Brushes.Gray;
                break;
            case EosProxyStatus.Stale:
                DetailEosProxyBtn.IsVisible    = true;
                DetailEosProxyBtn.Content      = "reapply eos proxy";
                DetailEosProxyBtn.Foreground   = Avalonia.Media.Brushes.Goldenrod;
                break;
            default:
                DetailEosProxyBtn.IsVisible = false;
                break;
        }
    }

    private async void OnToggleEosProxyClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath)) return;
        var status = _eosProxyService.GetProxyStatus(_selectedGame.InstallPath);
        if (status == EosProxyStatus.Active) await _eosProxyService.RemoveProxyAsync(_selectedGame.InstallPath);
        else                                  await _eosProxyService.ApplyProxyAsync(_selectedGame.InstallPath);
        UpdateEosProxyUi();
    }

    // Gamepad focus for detail action buttons
    private List<Button> GetVisibleDetailActionButtons()
    {
        var list = new List<Button>();
        if (DetailSwitchModeBtn != null && DetailSwitchModeBtn.IsVisible && DetailSwitchModeBtn.IsEnabled) list.Add(DetailSwitchModeBtn);
        if (DetailSlsOnlineBtn  != null && DetailSlsOnlineBtn.IsVisible  && DetailSlsOnlineBtn.IsEnabled)  list.Add(DetailSlsOnlineBtn);
        if (DetailNetsockBtn    != null && DetailNetsockBtn.IsVisible    && DetailNetsockBtn.IsEnabled)    list.Add(DetailNetsockBtn);
        if (DetailEosProxyBtn   != null && DetailEosProxyBtn.IsVisible   && DetailEosProxyBtn.IsEnabled)   list.Add(DetailEosProxyBtn);
        if (DetailSteamlessBtn  != null && DetailSteamlessBtn.IsVisible  && DetailSteamlessBtn.IsEnabled)  list.Add(DetailSteamlessBtn);
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
            buttons[_detailActionIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void UpdateDetailActionHighlight()
    {
        var buttons = GetVisibleDetailActionButtons();
        for (int i = 0; i < buttons.Count; i++)
            buttons[i].Classes.Set("actionBtnFocused", i == _detailActionIndex);
    }
}
