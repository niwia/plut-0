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
using Pluto.Engine.Installation;
using Pluto.Engine.Steam;

namespace Pluto;

// MainWindow partial — game detail page, artwork, screenshots, and detail actions
// Controller navigation rule: ALL navigable buttons are tracked individually,
// never via parent-panel visibility. Highlight = direct Opacity on the button.
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
        DetailSwitchModeBtn.IsVisible = true;
        if (DetailDownloadBtn != null) DetailDownloadBtn.IsVisible = false;

        if (DetailActionStatus != null) DetailActionStatus.IsVisible = false;

        // Online toggles: show row, set individual button states
        bool isOnline  = _slsService.IsSlsOnline(game.AppId);
        bool isNetsock = _slsService.IsNetsock(game.AppId);
        UpdateOnlineTogglesUi(isOnline, isNetsock);

        // Show steamless button
        DetailSteamlessBtn.IsVisible    = true;
        DetailSteamlessBtn.IsEnabled    = true;
        DetailSteamlessBtn.Content      = "apply steamless";
        DetailSteamlessStatus.IsVisible = false;

        UpdateEosProxyUi();

        // Keep main list visible in background: shifted left and dimmed
        MainListPanel.IsVisible   = true;
        MainListPanel.Opacity     = 0.22;
        MainListPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateX(-60px)");
        MainListPanel.IsHitTestVisible = false;

        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible   = false;

        _detailActionIndex = 0;
        RefreshDetailActionFocus();

        _ = LoadGameArtworkAndMetadataAsync(game.AppId, game.Name);
    }

    private void OpenSearchResultDetailPage(SearchResultItem item)
    {
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
        DetailSwitchModeBtn.IsVisible  = true;

        if (DetailDownloadBtn != null)
        {
            DetailDownloadBtn.Content   = "download game";
            DetailDownloadBtn.IsEnabled = true;
            DetailDownloadBtn.IsVisible = true;
        }

        if (DetailActionStatus != null) DetailActionStatus.IsVisible = false;

        // Online search result: hide SLS/netsock/steamless buttons individually
        DetailSlsOnlineBtn.IsVisible  = false;
        DetailNetsockBtn.IsVisible    = false;
        DetailEosProxyBtn.IsVisible   = false;
        DetailSteamlessBtn.IsVisible  = false;

        MainListPanel.IsVisible   = true;
        MainListPanel.Opacity     = 0.22;
        MainListPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateX(-60px)");
        MainListPanel.IsHitTestVisible = false;

        GameDetailPanel.IsVisible = true;
        SettingsPanel.IsVisible   = false;

        _detailActionIndex = 0;
        RefreshDetailActionFocus();

        _ = LoadGameArtworkAndMetadataAsync(item.AppId, item.Name);
    }

    // Reset all detail UI — hide backdrop, clear all data
    private void ResetDetailUi()
    {
        _currentScreenshots.Clear();
        _currentScreenshotIndex = 0;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        // Hide main backdrop so it doesn't bleed through
        if (MainBackdropImage != null)
        {
            MainBackdropImage.Opacity   = 0;
            MainBackdropImage.IsVisible = false;
        }

        if (DetailGameLogo         != null) { DetailGameLogo.Source  = null; DetailGameLogo.IsVisible = false; }
        if (DetailGameTitle        != null) DetailGameTitle.IsVisible  = false;
        if (DetailGameCredits      != null) { DetailGameCredits.Text   = string.Empty; DetailGameCredits.IsVisible = false; }
        if (DetailRatingRow        != null) DetailRatingRow.IsVisible   = false;
        if (DetailRawgMeta         != null) { DetailRawgMeta.Text       = string.Empty; DetailRawgMeta.IsVisible = false; }
        if (DetailTagsPanel        != null) { DetailTagsPanel.Children.Clear(); DetailTagsPanel.IsVisible = false; }
        if (DetailGalleryControls  != null) DetailGalleryControls.IsVisible = false;
        if (DetailGameBackdrop     != null) DetailGameBackdrop.IsVisible = false;

        // Reset all action buttons to hidden — they'll be explicitly shown as needed
        if (DetailSwitchModeBtn  != null) { DetailSwitchModeBtn.IsVisible  = false; DetailSwitchModeBtn.Opacity  = 1.0; DetailSwitchModeBtn.FontSize = 17; }
        if (DetailSlsOnlineBtn   != null) { DetailSlsOnlineBtn.IsVisible   = false; DetailSlsOnlineBtn.Opacity   = 1.0; DetailSlsOnlineBtn.FontSize = 14; }
        if (DetailNetsockBtn     != null) { DetailNetsockBtn.IsVisible     = false; DetailNetsockBtn.Opacity     = 1.0; DetailNetsockBtn.FontSize = 14; }
        if (DetailEosProxyBtn    != null) { DetailEosProxyBtn.IsVisible    = false; DetailEosProxyBtn.Opacity    = 1.0; DetailEosProxyBtn.FontSize = 14; }
        if (DetailSteamlessBtn   != null) { DetailSteamlessBtn.IsVisible   = false; DetailSteamlessBtn.Opacity   = 1.0; DetailSteamlessBtn.FontSize = 14; }
    }

    // ── Artwork + metadata loading ────────────────────────────────────────────
    private async Task LoadGameArtworkAndMetadataAsync(string appId, string gameName)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

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
                                DetailGameLogo.Source     = logoBmp;
                                DetailGameLogo.IsVisible  = true;
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

                            if (!string.IsNullOrEmpty(meta.CreditsLine) && DetailGameCredits != null)
                            {
                                DetailGameCredits.Text      = meta.CreditsLine;
                                DetailGameCredits.IsVisible = true;
                            }

                            if (meta.Rating.HasValue && meta.Rating.Value > 0 && DetailRatingRow != null)
                            {
                                DetailRatingScoreText.Text = $"{meta.Rating.Value:0.0} / 5";
                                var parts = new List<string>();
                                if (!string.IsNullOrEmpty(meta.FormattedReviewsCount)) parts.Add(meta.FormattedReviewsCount);
                                if (!string.IsNullOrEmpty(meta.Verdict))               parts.Add(meta.Verdict);
                                DetailRatingSubText.Text  = parts.Count > 0 ? string.Join("  •  ", parts) : "";
                                DetailRatingRow.IsVisible = true;
                            }

                            var metaParts = new List<string>();
                            if (!string.IsNullOrEmpty(meta.ReleaseYear))                         metaParts.Add(meta.ReleaseYear);
                            if (meta.PlaytimeHours.HasValue && meta.PlaytimeHours.Value > 0)     metaParts.Add($"~{meta.PlaytimeHours.Value} hrs avg");
                            if (meta.MetacriticScore.HasValue && meta.MetacriticScore.Value > 0) metaParts.Add($"{meta.MetacriticScore.Value} metacritic");

                            if (metaParts.Count > 0 && DetailRawgMeta != null)
                            {
                                DetailRawgMeta.Text      = string.Join("  •  ", metaParts);
                                DetailRawgMeta.IsVisible = true;
                            }

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

                            if (meta.Screenshots != null && meta.Screenshots.Count > 0)
                            {
                                _currentScreenshots     = meta.Screenshots;
                                _currentScreenshotIndex = 0;

                                if (DetailGalleryIndexText != null)
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

    // ── Screenshot gallery ────────────────────────────────────────────────────
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

    // ── Online toggle actions ─────────────────────────────────────────────────
    private void UpdateOnlineTogglesUi(bool isOnline, bool isNetsock)
    {
        if (isOnline)
        {
            DetailSlsOnlineBtn.IsVisible  = true;
            DetailSlsOnlineBtn.Content    = "slsonline: active";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
            DetailNetsockBtn.IsVisible    = true;
            DetailNetsockBtn.Content      = isNetsock ? "netsock proxy: active" : "enable netsock proxy";
            DetailNetsockBtn.Foreground   = isNetsock ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        else
        {
            DetailSlsOnlineBtn.IsVisible  = true;
            DetailSlsOnlineBtn.Content    = "enable slsonline";
            DetailSlsOnlineBtn.Foreground = Avalonia.Media.Brushes.Gray;
            DetailNetsockBtn.IsVisible    = false;
        }
        // Refresh focus ring since visibility changed
        RefreshDetailActionFocus();
    }

    // ── Main action button handlers ───────────────────────────────────────────
    private async void OnToggleModeClicked(object? sender, RoutedEventArgs e)
    {
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

                if (DetailActionStatus != null) DetailActionStatus.Text = "added to slssteam plugin";

                // Show previously hidden buttons
                DetailSlsOnlineBtn.IsVisible  = true;
                DetailSteamlessBtn.IsVisible  = true;
                bool isOnline  = _slsService.IsSlsOnline(appId);
                bool isNetsock = _slsService.IsNetsock(appId);
                UpdateOnlineTogglesUi(isOnline, isNetsock);
                UpdateEosProxyUi();

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

    private async void OnApplySteamlessClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null) return;
        DetailSteamlessBtn.IsEnabled     = false;
        DetailSteamlessBtn.Content       = "processing steamless...";
        DetailSteamlessStatus.Text       = "scanning and unpacking executables...";
        DetailSteamlessStatus.Foreground = Avalonia.Media.Brushes.Gray;
        DetailSteamlessStatus.IsVisible  = true;

        var result = await _steamlessService.ProcessGameAsync(_selectedGame.InstallPath, _selectedGame.Name);

        DetailSteamlessBtn.IsEnabled     = true;
        DetailSteamlessBtn.Content       = "apply steamless";
        DetailSteamlessStatus.Text       = result.Message;
        DetailSteamlessStatus.Foreground = result.Success
            ? Avalonia.Media.Brushes.MediumSpringGreen
            : Avalonia.Media.Brushes.Gray;
    }

    // ── EOS Proxy ─────────────────────────────────────────────────────────────
    private void UpdateEosProxyUi()
    {
        if (DetailEosProxyBtn == null) return;
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath))
        { DetailEosProxyBtn.IsVisible = false; RefreshDetailActionFocus(); return; }

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
        RefreshDetailActionFocus();
    }

    private async void OnToggleEosProxyClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedGame == null || string.IsNullOrWhiteSpace(_selectedGame.InstallPath)) return;
        var status = _eosProxyService.GetProxyStatus(_selectedGame.InstallPath);
        if (status == EosProxyStatus.Active) await _eosProxyService.RemoveProxyAsync(_selectedGame.InstallPath);
        else                                  await _eosProxyService.ApplyProxyAsync(_selectedGame.InstallPath);
        UpdateEosProxyUi();
    }

    // ── Controller focus system for detail page ───────────────────────────────
    // Rule: visibility is ALWAYS tracked on the button itself (never via parent panel).
    // Highlight = Opacity (1.0 = focused, 0.4 = unfocused). This works regardless
    // of local FontSize / Foreground set in AXAML — no CSS class conflicts.

    private List<Button> GetDetailActionButtons()
    {
        // Returns ALL buttons that have IsVisible=true on the button itself
        var list = new List<Button>();
        if (DetailSwitchModeBtn != null && DetailSwitchModeBtn.IsVisible && DetailSwitchModeBtn.IsEnabled) list.Add(DetailSwitchModeBtn);
        if (DetailDownloadBtn   != null && DetailDownloadBtn.IsVisible && DetailDownloadBtn.IsEnabled)     list.Add(DetailDownloadBtn);
        if (DetailSlsOnlineBtn  != null && DetailSlsOnlineBtn.IsVisible)  list.Add(DetailSlsOnlineBtn);
        if (DetailNetsockBtn    != null && DetailNetsockBtn.IsVisible)    list.Add(DetailNetsockBtn);
        if (DetailEosProxyBtn   != null && DetailEosProxyBtn.IsVisible)   list.Add(DetailEosProxyBtn);
        if (DetailSteamlessBtn  != null && DetailSteamlessBtn.IsVisible && DetailSteamlessBtn.IsEnabled) list.Add(DetailSteamlessBtn);
        return list;
    }

    private readonly Dictionary<Button, double> _detailButtonBaseFontSizes = new();

    private void EnsureDetailBaseFontSizes()
    {
        if (_detailButtonBaseFontSizes.Count > 0) return;
        if (DetailSwitchModeBtn != null) _detailButtonBaseFontSizes[DetailSwitchModeBtn] = 17;
        if (DetailDownloadBtn   != null) _detailButtonBaseFontSizes[DetailDownloadBtn]   = 16;
        if (DetailSlsOnlineBtn  != null) _detailButtonBaseFontSizes[DetailSlsOnlineBtn]  = 14;
        if (DetailNetsockBtn    != null) _detailButtonBaseFontSizes[DetailNetsockBtn]    = 14;
        if (DetailEosProxyBtn   != null) _detailButtonBaseFontSizes[DetailEosProxyBtn]   = 14;
        if (DetailSteamlessBtn  != null) _detailButtonBaseFontSizes[DetailSteamlessBtn]  = 14;
    }

    // Full re-render of the focus ring: dim all, brighten focused, subtle font size swell (+2px)
    private void RefreshDetailActionFocus()
    {
        EnsureDetailBaseFontSizes();
        var buttons = GetDetailActionButtons();
        if (buttons.Count == 0) return;
        _detailActionIndex = Math.Clamp(_detailActionIndex, 0, buttons.Count - 1);
        for (int i = 0; i < buttons.Count; i++)
        {
            bool isFocused = (i == _detailActionIndex);
            buttons[i].Opacity = isFocused ? 1.0 : 0.4;
            if (_detailButtonBaseFontSizes.TryGetValue(buttons[i], out double baseSize))
            {
                buttons[i].FontSize = isFocused ? (baseSize + 2) : baseSize;
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_detailActionIndex >= 0 && _detailActionIndex < buttons.Count)
                buttons[_detailActionIndex].Focus();
        }, DispatcherPriority.Input);
    }

    internal void NavigateDetailActions(int offset)
    {
        var buttons = GetDetailActionButtons();
        if (buttons.Count == 0) return;
        _detailActionIndex = Math.Clamp(_detailActionIndex + offset, 0, buttons.Count - 1);
        RefreshDetailActionFocus();
    }

    internal void TriggerDetailAction()
    {
        var buttons = GetDetailActionButtons();
        if (_detailActionIndex >= 0 && _detailActionIndex < buttons.Count)
            buttons[_detailActionIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // Clear all dim when leaving the detail page
    internal void ClearDetailActionFocus()
    {
        EnsureDetailBaseFontSizes();
        var all = new[] { DetailSwitchModeBtn, DetailDownloadBtn, DetailSlsOnlineBtn, DetailNetsockBtn, DetailEosProxyBtn, DetailSteamlessBtn };
        foreach (var btn in all)
        {
            if (btn != null)
            {
                btn.Opacity = 1.0;
                if (_detailButtonBaseFontSizes.TryGetValue(btn, out double baseSize))
                    btn.FontSize = baseSize;
            }
        }
    }

    // ── Pre-Download Configuration & Persistent Download Flow ───────────────
    private PreDownloadConfig? _currentPreDownloadConfig;
    private List<SteamLibraryFolder> _availableLibraries = new();
    private int _selectedLibraryIndex = 0;
    private List<string> _availableBranches = new();
    private int _selectedBranchIndex = 0;

    private async void OnDownloadGameClicked(object? sender, RoutedEventArgs e)
    {
        var appIdStr = _selectedSearchResult?.AppId ?? _selectedGame?.AppId;
        var name = _selectedSearchResult?.Name ?? _selectedGame?.Name ?? "Game";
        if (string.IsNullOrWhiteSpace(appIdStr) || !uint.TryParse(appIdStr, out var appId))
            return;

        if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = false;
        if (DetailActionStatus != null)
        {
            DetailActionStatus.IsVisible = true;
            DetailActionStatus.Foreground = Avalonia.Media.Brushes.DeepSkyBlue;
            DetailActionStatus.Text = "Querying branches & depots from Steam...";
        }

        try
        {
            var appInfo = await SteamCmdService.FetchAppInfoAsync(appId);
            if (appInfo == null)
            {
                if (DetailActionStatus != null)
                {
                    DetailActionStatus.Text = "Failed to resolve app details from Steam.";
                    DetailActionStatus.Foreground = Avalonia.Media.Brushes.IndianRed;
                }
                if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = true;
                return;
            }

            // 1. Storage Libraries
            _availableLibraries = SteamLibraryService.GetLibraryFolders();
            _selectedLibraryIndex = 0;

            // 2. Branches
            _availableBranches = new List<string> { "public" };
            if (appInfo.Branches != null)
            {
                foreach (var b in appInfo.Branches.Keys)
                {
                    if (!b.Equals("public", StringComparison.OrdinalIgnoreCase) && !_availableBranches.Contains(b))
                    {
                        _availableBranches.Add(b);
                    }
                }
            }
            _selectedBranchIndex = 0;

            // 3. Depots with DDM preferences
            var depotItems = new List<PreDownloadDepotItem>();
            foreach (var d in appInfo.Depots)
            {
                long sizeBytes = d.Size;
                var lowerName = d.Name.ToLowerInvariant();

                bool isOst = lowerName.Contains("soundtrack") || lowerName.Contains(" ost");
                bool isExtra = lowerName.Contains("artbook") || lowerName.Contains("wallpaper") || lowerName.Contains("bonus");
                bool isDemo = lowerName.Contains("demo") || lowerName.Contains("trial") || lowerName.Contains("sdk");

                // Check default selection
                bool shouldSelect = !string.IsNullOrWhiteSpace(d.ManifestId);
                if (_settingDdmFilterOst && isOst) shouldSelect = false;
                if (_settingDdmFilterExtras && isExtra) shouldSelect = false;
                if (isDemo) shouldSelect = false;

                depotItems.Add(new PreDownloadDepotItem
                {
                    DepotId = d.DepotId,
                    Name = d.Name,
                    SizeBytes = sizeBytes,
                    SizeString = SteamLibraryService.FormatBytes(sizeBytes),
                    IsSelected = shouldSelect,
                    IsRequired = false
                });
            }

            if (!depotItems.Any(d => d.IsSelected) && depotItems.Count > 0)
            {
                depotItems[0].IsSelected = true;
            }

            _currentPreDownloadConfig = new PreDownloadConfig
            {
                AppId = appId,
                GameName = name,
                SelectedLibrarySteamappsDir = _availableLibraries[0].SteamappsPath,
                SelectedBranch = "public",
                Depots = depotItems
            };

            // Populate UI Modal
            if (PreDownloadGameTitleText != null) PreDownloadGameTitleText.Text = name;
            if (PreDownloadStorageBtn != null) PreDownloadStorageBtn.Content = _availableLibraries[0].Label;
            if (PreDownloadBranchBtn != null) PreDownloadBranchBtn.Content = "public";
            if (PreDownloadDepotsItemsControl != null) PreDownloadDepotsItemsControl.ItemsSource = depotItems;
            if (PreDownloadTotalSizeText != null)
                PreDownloadTotalSizeText.Text = $"Selected: {SteamLibraryService.FormatBytes(_currentPreDownloadConfig.GetTotalSelectedSize())}";

            if (DetailActionStatus != null) DetailActionStatus.IsVisible = false;
            if (PreDownloadPanel != null) PreDownloadPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            if (DetailActionStatus != null)
            {
                DetailActionStatus.Text = $"Error: {ex.Message}";
                DetailActionStatus.Foreground = Avalonia.Media.Brushes.IndianRed;
            }
        }
        finally
        {
            if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = true;
        }
    }

    private void OnPreDownloadStorageCycleClicked(object? sender, RoutedEventArgs e)
    {
        if (_availableLibraries.Count == 0 || _currentPreDownloadConfig == null) return;
        _selectedLibraryIndex = (_selectedLibraryIndex + 1) % _availableLibraries.Count;
        var chosen = _availableLibraries[_selectedLibraryIndex];
        if (PreDownloadStorageBtn != null) PreDownloadStorageBtn.Content = chosen.Label;
        _currentPreDownloadConfig.SelectedLibrarySteamappsDir = chosen.SteamappsPath;
    }

    private void OnPreDownloadBranchCycleClicked(object? sender, RoutedEventArgs e)
    {
        if (_availableBranches.Count == 0 || _currentPreDownloadConfig == null) return;
        _selectedBranchIndex = (_selectedBranchIndex + 1) % _availableBranches.Count;
        var branch = _availableBranches[_selectedBranchIndex];
        if (PreDownloadBranchBtn != null) PreDownloadBranchBtn.Content = branch;
        _currentPreDownloadConfig.SelectedBranch = branch;
    }

    private void OnPreDownloadCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (PreDownloadPanel != null) PreDownloadPanel.IsVisible = false;
    }

    private async void OnPreDownloadConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentPreDownloadConfig == null) return;
        if (PreDownloadPanel != null) PreDownloadPanel.IsVisible = false;

        var appId = _currentPreDownloadConfig.AppId;
        var name = _currentPreDownloadConfig.GameName;
        var targetSteamappsDir = _currentPreDownloadConfig.SelectedLibrarySteamappsDir;
        var branch = _currentPreDownloadConfig.SelectedBranch;
        var selectedDepots = _currentPreDownloadConfig.GetSelectedDepotIds();

        // 1. Move or insert this game to the very top (index 0) of the game list with [— New] tag
        var targetGame = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (targetGame == null)
        {
            targetGame = new PluginGame
            {
                AppId = appId.ToString(),
                Name = name,
                IsAccela = true,
                IsNew = true
            };
            _allGames.Insert(0, targetGame);
        }
        else
        {
            _allGames.Remove(targetGame);
            _allGames.Insert(0, targetGame);
            targetGame.IsNew = true;
        }

        targetGame.IsDownloading = true;
        targetGame.DownloadPercentage = 0;
        targetGame.DownloadStatusText = "0%";

        // Refresh waterfall game list display
        _displayedGames.Clear();
        foreach (var g in _allGames) _displayedGames.Add(g);
        if (GamesListBox != null) GamesListBox.SelectedIndex = 0;

        // 2. Activate persistent global bottom progress bar
        if (GlobalDownloadBar != null) GlobalDownloadBar.IsVisible = true;
        if (GlobalDlGameTitle != null) GlobalDlGameTitle.Text = name;
        if (GlobalDlStepText != null) GlobalDlStepText.Text = "Initializing download...";
        if (GlobalDlSpeedText != null) GlobalDlSpeedText.Text = "0.0 MB/s";
        if (GlobalDlPercentText != null) GlobalDlPercentText.Text = "0%";
        if (GlobalDlProgressBar != null) GlobalDlProgressBar.Value = 0;

        if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = false;
        if (DetailSwitchModeBtn != null) DetailSwitchModeBtn.IsEnabled = false;

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<InstallStepProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Update persistent bottom bar
                if (GlobalDlProgressBar != null) GlobalDlProgressBar.Value = p.OverallPercentage;
                if (GlobalDlPercentText != null) GlobalDlPercentText.Text = $"{p.OverallPercentage:0}%";
                if (GlobalDlSpeedText != null) GlobalDlSpeedText.Text = p.SpeedMbPerSec > 0 ? $"{p.SpeedMbPerSec:0.1} MB/s" : "";
                if (GlobalDlStepText != null) GlobalDlStepText.Text = p.Step;

                // Update game list item at index 0
                targetGame.DownloadPercentage = p.OverallPercentage;
                targetGame.DownloadStatusText = p.SpeedMbPerSec > 0
                    ? $"{p.OverallPercentage:0}% ({p.SpeedMbPerSec:0.1} MB/s)"
                    : $"{p.OverallPercentage:0}%";

                // Update detail page status if user is currently viewing this game
                if (DetailActionStatus != null && (_selectedSearchResult?.AppId == appId.ToString() || _selectedGame?.AppId == appId.ToString()))
                {
                    DetailActionStatus.IsVisible = true;
                    DetailActionStatus.Text = $"{p.Step} ({p.OverallPercentage:0}%): {p.Details}";
                    DetailActionStatus.Foreground = Avalonia.Media.Brushes.DeepSkyBlue;
                }
            });
        });

        try
        {
            bool ok = await _installService.InstallGameAsync(
                appId,
                targetSteamappsDir: targetSteamappsDir,
                branch: branch,
                selectedDepotIds: selectedDepots,
                progress: progress,
                cancellationToken: _downloadCts.Token);

            targetGame.IsDownloading = false;
            targetGame.DownloadStatusText = string.Empty;
            targetGame.IsNew = true; // Stays New till next boot of Pluto!

            if (GlobalDownloadBar != null) GlobalDownloadBar.IsVisible = false;

            if (ok)
            {
                if (DetailActionStatus != null)
                {
                    DetailActionStatus.Text = $"Successfully installed {name}!";
                    DetailActionStatus.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
                }
                if (DetailDownloadBtn != null) DetailDownloadBtn.IsVisible = false;
                await ReloadLibraryAsync();
            }
            else
            {
                if (DetailActionStatus != null)
                {
                    DetailActionStatus.Text = "Installation failed or was cancelled.";
                    DetailActionStatus.Foreground = Avalonia.Media.Brushes.IndianRed;
                }
                if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            targetGame.IsDownloading = false;
            if (GlobalDownloadBar != null) GlobalDownloadBar.IsVisible = false;

            if (DetailActionStatus != null)
            {
                DetailActionStatus.Text = $"Install error: {ex.Message}";
                DetailActionStatus.Foreground = Avalonia.Media.Brushes.IndianRed;
            }
            if (DetailDownloadBtn != null) DetailDownloadBtn.IsEnabled = true;
        }
        finally
        {
            if (DetailSwitchModeBtn != null) DetailSwitchModeBtn.IsEnabled = true;
            RefreshDetailActionFocus();
        }
    }
}

