using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pluto.Services;
using Avalonia.Threading;

namespace Pluto;

// MainWindow partial — settings page, themes, toggles, gamepad for settings
public partial class MainWindow
{
    // Load settings from AccelaConfigService
    private void LoadSettingsFromConfig()
    {
        try
        {
            _settingVaporEnabled    = _configService.GetBool("enable_vapor", true) || _configService.GetBool("enable_at0m", true);
            _settingDownloadAction  = _configService.GetValue("vapor_default_download_action", "native");
            _settingDisableUpdates  = _configService.GetBool("vapor_disable_updates", false) || _configService.GetBool("at0m_disable_updates", false);

            _settingAutoRotateScreenshots   = _configService.GetBool("screenshot_autorotate_enabled", true);
            _settingAutoRotateIntervalSec   = _configService.GetInt("screenshot_autorotate_interval_sec", 6);
            if (_settingAutoRotateIntervalSec < 2) _settingAutoRotateIntervalSec = 6;

            _settingDynamicMainBackdrop     = _configService.GetBool("main_dynamic_backdrop_enabled", true);
            _settingMainBackdropIntervalSec = _configService.GetInt("main_dynamic_backdrop_interval_sec", 15);
            if (_settingMainBackdropIntervalSec < 5) _settingMainBackdropIntervalSec = 15;

            _settingSearchThumbnailsEnabled = _configService.GetBool("search_thumbnails_enabled", true);

            UpdateSettingsUi();
            PlutoLogger.Info("Pluto",
                $"Settings: vapor={_settingVaporEnabled}, dl={_settingDownloadAction}, " +
                $"autoRotate={_settingAutoRotateScreenshots}@{_settingAutoRotateIntervalSec}s, " +
                $"mainBackdrop={_settingDynamicMainBackdrop}@{_settingMainBackdropIntervalSec}s, " +
                $"searchThumbs={_settingSearchThumbnailsEnabled}");
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
            ToggleVaporBtn.Content    = _settingVaporEnabled ? "enabled" : "disabled";
            ToggleVaporBtn.Foreground = _settingVaporEnabled
                ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        if (ToggleDownloadActionBtn != null)
            ToggleDownloadActionBtn.Content = _settingDownloadAction == "native" ? "native steam" : "accela downloader";
        if (ToggleUpdatesBtn != null)
            ToggleUpdatesBtn.Content = _settingDisableUpdates ? "disabled" : "normal (updates allowed)";

        if (ToggleAutoRotateBtn != null)
        {
            ToggleAutoRotateBtn.Content    = _settingAutoRotateScreenshots ? "enabled" : "disabled";
            ToggleAutoRotateBtn.Foreground = _settingAutoRotateScreenshots
                ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        if (ToggleAutoRotateIntervalBtn != null)
            ToggleAutoRotateIntervalBtn.Content = $"{_settingAutoRotateIntervalSec} seconds";

        if (ToggleMainBackdropBtn != null)
        {
            ToggleMainBackdropBtn.Content    = _settingDynamicMainBackdrop ? "enabled" : "disabled";
            ToggleMainBackdropBtn.Foreground = _settingDynamicMainBackdrop
                ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        if (ToggleMainBackdropIntervalBtn != null)
            ToggleMainBackdropIntervalBtn.Content = $"{_settingMainBackdropIntervalSec} seconds";

        if (ToggleSearchThumbnailsBtn != null)
        {
            ToggleSearchThumbnailsBtn.Content    = _settingSearchThumbnailsEnabled ? "enabled" : "disabled";
            ToggleSearchThumbnailsBtn.Foreground = _settingSearchThumbnailsEnabled
                ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
    }

    // Tab activation
    private void SetActiveSettingsTab(SettingsTab tab)
    {
        _activeSettingsTab = tab;

        if (SettingsTabThemeBtn   != null) SettingsTabThemeBtn.Classes.Set("active",   tab == SettingsTab.Theme);
        if (SettingsTabApiBtn     != null) SettingsTabApiBtn.Classes.Set("active",     tab == SettingsTab.Api);
        if (SettingsTabSlsBtn     != null) SettingsTabSlsBtn.Classes.Set("active",     tab == SettingsTab.Sls);
        if (SettingsTabVisualsBtn != null) SettingsTabVisualsBtn.Classes.Set("active", tab == SettingsTab.Visuals);
        if (SettingsTabHealthBtn  != null) SettingsTabHealthBtn.Classes.Set("active",  tab == SettingsTab.Health);

        if (SettingsThemePanel   != null) SettingsThemePanel.IsVisible   = tab == SettingsTab.Theme;
        if (SettingsApiPanel     != null) SettingsApiPanel.IsVisible     = tab == SettingsTab.Api;
        if (SettingsSlsPanel     != null) SettingsSlsPanel.IsVisible     = tab == SettingsTab.Sls;
        if (SettingsVisualsPanel != null) SettingsVisualsPanel.IsVisible = tab == SettingsTab.Visuals;
        if (SettingsHealthPanel  != null) SettingsHealthPanel.IsVisible  = tab == SettingsTab.Health;

        if (tab == SettingsTab.Health)
        {
            _ = RefreshVisorAsync();
        }

        _settingsOptionIndex = 0;

        // Only apply focus ring if we're actually in the settings view
        if (_currentView == ActiveView.Settings)
            RefreshSettingsFocus();
    }

    private void OnSettingsTabThemeClicked(object? sender, RoutedEventArgs e)   => SetActiveSettingsTab(SettingsTab.Theme);
    private void OnSettingsTabApiClicked(object? sender, RoutedEventArgs e)     => SetActiveSettingsTab(SettingsTab.Api);
    private void OnSettingsTabSlsClicked(object? sender, RoutedEventArgs e)     => SetActiveSettingsTab(SettingsTab.Sls);
    private void OnSettingsTabVisualsClicked(object? sender, RoutedEventArgs e) => SetActiveSettingsTab(SettingsTab.Visuals);
    private void OnSettingsTabHealthClicked(object? sender, RoutedEventArgs e)  => SetActiveSettingsTab(SettingsTab.Health);

    // SLS Config toggles
    private void OnToggleVaporClicked(object? sender, RoutedEventArgs e)
    {
        _settingVaporEnabled = !_settingVaporEnabled;
        UpdateSettingsUi();
        _configService.SetBool("enable_vapor",  _settingVaporEnabled);
        _configService.SetBool("enable_at0m",   _settingVaporEnabled);
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
        _configService.SetBool("at0m_disable_updates",  _settingDisableUpdates);
    }

    private void OnSyncAllClicked(object? sender, RoutedEventArgs e)   => SyncAllGames();
    private void OnReloadSlsClicked(object? sender, RoutedEventArgs e) => _slsService.NotifyReload();

    private async void SyncAllGames()
    {
        PlutoLogger.Info("Pluto", "Syncing all plugin games into config.yaml...");
        foreach (var g in _allGames)
        {
            if (!g.IsAccela)
                await _slsService.SyncGameToConfigAsync(g);
        }
    }

    // Visuals toggles
    private void OnToggleAutoRotateClicked(object? sender, RoutedEventArgs e)
    {
        _settingAutoRotateScreenshots = !_settingAutoRotateScreenshots;
        UpdateSettingsUi();
        _configService.SetBool("screenshot_autorotate_enabled", _settingAutoRotateScreenshots);
        if (!_settingAutoRotateScreenshots)
            _screenshotAutoRotateTimer.Stop();
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
            <= 3  => 6,
            <= 6  => 10,
            <= 10 => 15,
            _     => 3
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
            if (MainBackdropImage != null)
            {
                MainBackdropImage.Opacity   = 0;
                MainBackdropImage.IsVisible = false;
            }
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
            <= 5  => 10,
            <= 10 => 15,
            <= 15 => 30,
            _     => 5
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

    // Dynamic main backdrop rotation
    private void OnMainBackdropTimerTick(object? sender, EventArgs e)
    {
        if (_currentView == ActiveView.MainList && _settingDynamicMainBackdrop && _mainBackdropPool.Count > 0)
            TriggerNextMainBackdrop();
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
                using var http  = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var bytes = await http.GetByteArrayAsync(heroUrl);
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    bmp = new Bitmap(ms);
                }
            }

            if (bmp != null && _currentView == ActiveView.MainList && MainBackdropImage != null)
            {
                MainBackdropImage.Source    = bmp;
                MainBackdropImage.IsVisible = true;
                MainBackdropImage.Opacity   = 0.18;
            }
        }
        catch { }
    }

    // Theme colors
    private void ApplyThemeColors()
    {
        var native = _themeService.CurrentNative;
        var accela = _themeService.CurrentAccela;

        if (Color.TryParse(native.HighlightHex, out var nCol))
        {
            Resources["NativeGameColor"]          = new SolidColorBrush(nCol);
            Resources["NativeGameHoverColor"]     = new SolidColorBrush(Color.FromArgb(200, nCol.R, nCol.G, nCol.B));
        }
        if (Color.TryParse(native.DimmedHex, out var nDim))
            Resources["NativeGameUnselectedColor"] = new SolidColorBrush(nDim);

        if (Color.TryParse(accela.HighlightHex, out var aCol))
        {
            Resources["AccelaGameColor"]          = new SolidColorBrush(aCol);
            Resources["AccelaGameHoverColor"]     = new SolidColorBrush(Color.FromArgb(200, aCol.R, aCol.G, aCol.B));
        }
        if (Color.TryParse(accela.DimmedHex, out var aDim))
            Resources["AccelaGameUnselectedColor"] = new SolidColorBrush(aDim);

        if (ToggleNativeThemeBtn != null) { ToggleNativeThemeBtn.Content = native.Name; ToggleNativeThemeBtn.Foreground = new SolidColorBrush(nCol); }
        if (ToggleAccelaThemeBtn != null) { ToggleAccelaThemeBtn.Content = accela.Name; ToggleAccelaThemeBtn.Foreground = new SolidColorBrush(aCol); }

        if (ToggleSgdbApiBtn != null)
        {
            bool has = _sgdbService.HasApiKey;
            ToggleSgdbApiBtn.Content    = has ? "api active" : "key missing (add to ~/.config/pluto/steamgriddb_api.txt)";
            ToggleSgdbApiBtn.Foreground = has ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        if (ToggleRawgBtn != null)
        {
            bool has = _rawgService.HasApiKey;
            ToggleRawgBtn.Content    = has ? "api active" : "key missing (add to ~/.config/pluto/rawg_api.txt)";
            ToggleRawgBtn.Foreground = has ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
        if (ToggleHubcapApiBtn != null)
        {
            bool has = !string.IsNullOrWhiteSpace(_configService.GetValue("morrenus_api_key"));
            ToggleHubcapApiBtn.Content    = has ? "api configured" : "key not set (add to ACCELA.conf)";
            ToggleHubcapApiBtn.Foreground = has ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Gray;
        }
    }

    private void OnToggleNativeThemeClicked(object? sender, RoutedEventArgs e)  { _themeService.CycleNextNative();  ApplyThemeColors(); }
    private void OnToggleAccelaThemeClicked(object? sender, RoutedEventArgs e)  { _themeService.CycleNextAccela();  ApplyThemeColors(); }
    private void OnToggleSgdbApiClicked(object? sender, RoutedEventArgs e)      => ApplyThemeColors();
    private void OnToggleRawgClicked(object? sender, RoutedEventArgs e)         => ApplyThemeColors();
    private void OnToggleHubcapApiClicked(object? sender, RoutedEventArgs e)    => ApplyThemeColors();

    // ── Controller focus system for settings ──────────────────────────────────
    // Rule: Opacity-based highlighting (1.0 = focused, 0.4 = dim).
    // GetSettingsButtons() returns only the buttons in the *current* tab —
    // because the parent panel (SettingsThemePanel etc) is visible, its
    // children are always IsVisible=true; we filter by the active tab instead.

    private List<Button> GetSettingsButtons()
    {
        var list = new List<Button>();
        switch (_activeSettingsTab)
        {
            case SettingsTab.Theme:
                if (ToggleNativeThemeBtn != null) list.Add(ToggleNativeThemeBtn);
                if (ToggleAccelaThemeBtn != null) list.Add(ToggleAccelaThemeBtn);
                break;
            case SettingsTab.Api:
                if (ToggleSgdbApiBtn   != null) list.Add(ToggleSgdbApiBtn);
                if (ToggleRawgBtn      != null) list.Add(ToggleRawgBtn);
                if (ToggleHubcapApiBtn != null) list.Add(ToggleHubcapApiBtn);
                break;
            case SettingsTab.Sls:
                if (ToggleVaporBtn          != null) list.Add(ToggleVaporBtn);
                if (ToggleDownloadActionBtn != null) list.Add(ToggleDownloadActionBtn);
                if (ToggleUpdatesBtn        != null) list.Add(ToggleUpdatesBtn);
                break;
            case SettingsTab.Visuals:
                if (ToggleMainBackdropBtn        != null) list.Add(ToggleMainBackdropBtn);
                if (ToggleMainBackdropIntervalBtn != null) list.Add(ToggleMainBackdropIntervalBtn);
                if (ToggleSearchThumbnailsBtn    != null) list.Add(ToggleSearchThumbnailsBtn);
                if (ToggleAutoRotateBtn          != null) list.Add(ToggleAutoRotateBtn);
                if (ToggleAutoRotateIntervalBtn  != null) list.Add(ToggleAutoRotateIntervalBtn);
                break;
            case SettingsTab.Health:
                if (AssfixerCheckBtn != null)            list.Add(AssfixerCheckBtn);
                if (AssfixerRepairBtn != null)           list.Add(AssfixerRepairBtn);
                if (AssfixerRestoreBtn != null)          list.Add(AssfixerRestoreBtn);
                if (SanitationClearThumbnailsBtn != null) list.Add(SanitationClearThumbnailsBtn);
                if (SanitationResyncSlsBtn != null)       list.Add(SanitationResyncSlsBtn);
                if (HealthRefreshBtn != null)             list.Add(HealthRefreshBtn);
                break;
        }
        return list;
    }

    // All tab buttons (for resetting opacity when switching)
    private IEnumerable<Button?> AllSettingsTabPanelButtons() => new Button?[]
    {
        ToggleNativeThemeBtn, ToggleAccelaThemeBtn,
        ToggleSgdbApiBtn, ToggleRawgBtn, ToggleHubcapApiBtn,
        ToggleVaporBtn, ToggleDownloadActionBtn, ToggleUpdatesBtn,
        ToggleMainBackdropBtn, ToggleMainBackdropIntervalBtn,
        ToggleSearchThumbnailsBtn, ToggleAutoRotateBtn, ToggleAutoRotateIntervalBtn,
        AssfixerCheckBtn, AssfixerRepairBtn, AssfixerRestoreBtn,
        SanitationClearThumbnailsBtn, SanitationResyncSlsBtn, HealthRefreshBtn
    };

    // Full re-render of settings focus ring
    internal void RefreshSettingsFocus()
    {
        // Reset all settings buttons to full opacity first
        foreach (var btn in AllSettingsTabPanelButtons())
            if (btn != null) btn.Opacity = 1.0;

        // Then dim all in current tab except focused
        var buttons = GetSettingsButtons();
        if (buttons.Count == 0) return;
        _settingsOptionIndex = Math.Clamp(_settingsOptionIndex, 0, buttons.Count - 1);
        for (int i = 0; i < buttons.Count; i++)
            buttons[i].Opacity = i == _settingsOptionIndex ? 1.0 : 0.4;

        Dispatcher.UIThread.Post(() =>
        {
            if (_settingsOptionIndex >= 0 && _settingsOptionIndex < buttons.Count)
                buttons[_settingsOptionIndex].Focus();
        }, DispatcherPriority.Input);
    }

    internal void CycleSettingsTab(int offset)
    {
        const int count = 5;
        int next = (((int)_activeSettingsTab + offset) % count + count) % count;
        SetActiveSettingsTab((SettingsTab)next);
    }

    internal void NavigateSettingsOptions(int offset)
    {
        var buttons = GetSettingsButtons();
        if (buttons.Count == 0) return;
        _settingsOptionIndex = Math.Clamp(_settingsOptionIndex + offset, 0, buttons.Count - 1);
        RefreshSettingsFocus();
    }

    internal void TriggerSettingsOption()
    {
        var buttons = GetSettingsButtons();
        if (_settingsOptionIndex >= 0 && _settingsOptionIndex < buttons.Count)
            buttons[_settingsOptionIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // ── Health Tab Handlers ──────────────────────────────────────────────────

    private async void OnAssfixerCheckClicked(object? sender, RoutedEventArgs e)
    {
        if (AssfixerStatusText != null)
        {
            AssfixerStatusText.Text = "Running assfixer validation...";
            AssfixerStatusText.Foreground = Avalonia.Media.Brushes.DeepSkyBlue;
            AssfixerStatusText.IsVisible = true;
        }

        var (ok, msg) = await _healthService.RunAssfixerCheckAsync();
        if (AssfixerStatusText != null)
        {
            AssfixerStatusText.Text = ok ? $"● Config Healthy: {msg}" : $"● Check Notice: {msg}";
            AssfixerStatusText.Foreground = ok ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.Goldenrod;
        }
    }

    private async void OnAssfixerRepairClicked(object? sender, RoutedEventArgs e)
    {
        if (AssfixerStatusText != null)
        {
            AssfixerStatusText.Text = "Repairing and synchronizing config.yaml...";
            AssfixerStatusText.Foreground = Avalonia.Media.Brushes.DeepSkyBlue;
            AssfixerStatusText.IsVisible = true;
        }

        var (ok, msg) = await _healthService.RunAssfixerRepairAsync();
        if (AssfixerStatusText != null)
        {
            AssfixerStatusText.Text = ok ? $"● Repaired: {msg}" : $"● Repair Failed: {msg}";
            AssfixerStatusText.Foreground = ok ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.IndianRed;
        }
    }

    private void OnAssfixerRestoreClicked(object? sender, RoutedEventArgs e)
    {
        var (ok, msg) = _healthService.RestoreAssfixerBackup();
        if (AssfixerStatusText != null)
        {
            AssfixerStatusText.Text = ok ? $"● {msg}" : $"● {msg}";
            AssfixerStatusText.Foreground = ok ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.IndianRed;
            AssfixerStatusText.IsVisible = true;
        }
    }

    private void OnClearThumbnailCacheClicked(object? sender, RoutedEventArgs e)
    {
        var (count, mb) = _healthService.ClearThumbnailCache();
        if (SanitationStatusText != null)
        {
            SanitationStatusText.Text = $"Cleared {count} cached thumbnails ({mb:0.1} MB freed).";
            SanitationStatusText.Foreground = Avalonia.Media.Brushes.MediumSpringGreen;
            SanitationStatusText.IsVisible = true;
        }
    }

    private void OnResyncSlsClicked(object? sender, RoutedEventArgs e)
    {
        bool ok = _healthService.TouchSlsConfig();
        if (SanitationStatusText != null)
        {
            SanitationStatusText.Text = ok ? "Touched config.yaml — SLSsteam inotify reload triggered." : "config.yaml not found.";
            SanitationStatusText.Foreground = ok ? Avalonia.Media.Brushes.MediumSpringGreen : Avalonia.Media.Brushes.IndianRed;
            SanitationStatusText.IsVisible = true;
        }
    }

    private async void OnRefreshHealthClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshVisorAsync();
        if (SanitationStatusText != null)
        {
            SanitationStatusText.Text = "Health and API status updated.";
            SanitationStatusText.Foreground = Avalonia.Media.Brushes.DeepSkyBlue;
            SanitationStatusText.IsVisible = true;
        }
    }

    private void UpdateHealthTabUi(SystemHealthStatus health)
    {
        if (HealthSummaryStatusText != null)
        {
            HealthSummaryStatusText.Text = $"{health.OverallState} • Steam: {(health.SteamRunning ? "Online" : "Offline")} • SLS: {(health.SlsProcessActive ? "Active" : "Inactive")}";
            HealthSummaryStatusText.Foreground = health.IsOptimal 
                ? Avalonia.Media.Brushes.MediumSpringGreen 
                : (health.OverallState == "Attention" ? Avalonia.Media.Brushes.Goldenrod : Avalonia.Media.Brushes.IndianRed);
        }

        if (HealthIssuesListText != null)
        {
            if (health.Issues.Count > 0)
            {
                HealthIssuesListText.Text = "Status Notes:\n• " + string.Join("\n• ", health.Issues);
                HealthIssuesListText.IsVisible = true;
            }
            else
            {
                HealthIssuesListText.Text = "All critical services (SLSsteam, Steam process, Config) are running optimally.";
                HealthIssuesListText.IsVisible = true;
            }
        }

        if (HealthHubcapQuotaText != null)
        {
            if (health.Hubcap.IsConfigured)
            {
                HealthHubcapQuotaText.Text = $"User: {health.Hubcap.Username}  •  Daily Calls: {health.Hubcap.DailyUsage} / {health.Hubcap.DailyLimit}  •  Total Calls: {health.Hubcap.TotalCalls}";
            }
            else
            {
                HealthHubcapQuotaText.Text = "API key not configured in ACCELA.conf";
            }
        }
    }
}


