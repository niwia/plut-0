using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pluto.Models;
using Pluto.Services;

namespace Pluto;

// MainWindow partial — keyboard & gamepad navigation, view switching
public partial class MainWindow
{
    // View Switching
    private void ShowMainList()
    {
        _currentView = ActiveView.MainList;
        MainListPanel.IsVisible   = true;
        GameDetailPanel.IsVisible = false;
        SettingsPanel.IsVisible   = false;

        _detailCts?.Cancel();
        _screenshotAutoRotateTimer.Stop();

        // Show main backdrop
        if (MainBackdropImage != null)
        {
            MainBackdropImage.IsVisible = true;
            MainBackdropImage.Opacity   = 0.18;
        }

        if (_settingDynamicMainBackdrop && _mainBackdropPool.Count > 0)
        {
            _mainBackdropTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settingMainBackdropIntervalSec));
            _mainBackdropTimer.Start();
        }

        if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
            SearchResultsListBox.Focus();
        else
            GamesListBox.Focus();
    }

    private void OpenSettingsPage()
    {
        _currentView = ActiveView.Settings;
        _screenshotAutoRotateTimer.Stop();
        _mainBackdropTimer.Stop();

        // Hide main backdrop so it doesn't bleed through
        if (MainBackdropImage != null)
        {
            MainBackdropImage.Opacity   = 0;
            MainBackdropImage.IsVisible = false;
        }

        SetActiveSettingsTab(_activeSettingsTab);
        UpdateGameCountsText();
        UpdateControllerStatus(_gamepadService.ActiveControllerName, _gamepadService.HasConnectedController);

        if (SettingsLogPathText != null)
            SettingsLogPathText.Text = PlutoLogger.LogFilePath;

        MainListPanel.IsVisible   = false;
        GameDetailPanel.IsVisible = false;
        SettingsPanel.IsVisible   = true;
    }

    // Window-level keyboard handler
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_currentView == ActiveView.MainList)
        {
            bool searchActive = SearchResultsListBox != null
                             && SearchResultsListBox.IsVisible
                             && _searchResults.Count > 0;

            if (e.Key == Key.Up && !SearchBox.IsFocused)
            {
                if (searchActive) NavigateSearchResults(-1); else NavigateList(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && !SearchBox.IsFocused)
            {
                if (searchActive) NavigateSearchResults(1); else NavigateList(1);
                e.Handled = true;
            }
            else if (e.Key == Key.PageUp && !SearchBox.IsFocused)
            {
                if (searchActive) NavigateSearchResults(-5); else NavigateList(-5);
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown && !SearchBox.IsFocused)
            {
                if (searchActive) NavigateSearchResults(5); else NavigateList(5);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !SearchBox.IsFocused)
            {
                if (searchActive && SearchResultsListBox!.SelectedItem is SearchResultItem si)
                {
                    OpenSearchResultDetailPage(si);
                    e.Handled = true;
                }
                else if (GamesListBox.SelectedItem is PluginGame sel)
                {
                    OpenGameDetailPage(sel);
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

    // Gamepad handler — routes actions to per-view logic
    private void HandleGamepadAction(GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.NavigateUp:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                    {
                        if (SearchResultsListBox.SelectedIndex == 0) SearchBox.Focus();
                        else NavigateSearchResults(-1);
                    }
                    else NavigateList(-1);
                }
                else if (_currentView == ActiveView.GameDetail)  NavigateDetailActions(-1);
                else if (_currentView == ActiveView.Settings)    NavigateSettingsOptions(-1);
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
                        else { GamesListBox.Focus(); NavigateList(0); }
                    }
                    else if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        NavigateSearchResults(1);
                    else
                        NavigateList(1);
                }
                else if (_currentView == ActiveView.GameDetail) NavigateDetailActions(1);
                else if (_currentView == ActiveView.Settings)   NavigateSettingsOptions(1);
                break;

            case GamepadAction.NavigateLeft:
                // D-Pad Left: prev screenshot in detail, prev tab in settings, no-op in main list
                if (_currentView == ActiveView.GameDetail)
                    OnPrevScreenshotClicked(null, new RoutedEventArgs());
                else if (_currentView == ActiveView.Settings)
                    CycleSettingsTab(-1);
                break;

            case GamepadAction.NavigateRight:
                // D-Pad Right: next screenshot in detail, next tab in settings, no-op in main list
                if (_currentView == ActiveView.GameDetail)
                    OnNextScreenshotClicked(null, new RoutedEventArgs());
                else if (_currentView == ActiveView.Settings)
                    CycleSettingsTab(1);
                break;

            case GamepadAction.PageUp:
                // LB: fast scroll in main list; no-op in detail/settings (LB/RB now tab-cycle via NavigateLeft/Right via D-Pad)
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        NavigateSearchResults(-5);
                    else NavigateList(-5);
                }
                else if (_currentView == ActiveView.Settings)
                    CycleSettingsTab(-1);
                break;

            case GamepadAction.PageDown:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                        NavigateSearchResults(5);
                    else NavigateList(5);
                }
                else if (_currentView == ActiveView.Settings)
                    CycleSettingsTab(1);
                break;

            case GamepadAction.Confirm:
                if (_currentView == ActiveView.MainList)
                {
                    if (SearchResultsListBox != null && SearchResultsListBox.IsVisible && _searchResults.Count > 0)
                    {
                        var si = SearchResultsListBox.SelectedItem as SearchResultItem ?? _searchResults[0];
                        OpenSearchResultDetailPage(si);
                    }
                    else if (GamesListBox.SelectedItem is PluginGame sel)
                        OpenGameDetailPage(sel);
                }
                else if (_currentView == ActiveView.GameDetail) TriggerDetailAction();
                else if (_currentView == ActiveView.Settings)   TriggerSettingsOption();
                break;

            case GamepadAction.FocusSearch:
                if (_currentView == ActiveView.MainList)
                { SearchBox.Focus(); SearchBox.SelectAll(); }
                break;

            case GamepadAction.BackOrCancel:
                if (_currentView != ActiveView.MainList) ShowMainList();
                else ClearSearchAndReset();
                break;

            case GamepadAction.OpenSettings:
                if (_currentView == ActiveView.MainList) OpenSettingsPage();
                else ShowMainList();
                break;

            case GamepadAction.SyncAll:
                SyncAllGames();
                break;
        }
    }

    // List navigation helpers
    private void NavigateList(int offset)
    {
        if (_displayedGames.Count == 0) return;
        int cur  = GamesListBox.SelectedIndex;
        if (cur < 0) cur = 0;
        int next = (cur + offset) % _displayedGames.Count;
        if (next < 0) next += _displayedGames.Count;
        GamesListBox.SelectedIndex = next;
        if (GamesListBox.SelectedItem != null)
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
    }

    private void NavigateSearchResults(int offset)
    {
        if (_searchResults.Count == 0 || SearchResultsListBox == null) return;
        int cur  = SearchResultsListBox.SelectedIndex;
        if (cur < 0) cur = 0;
        int next = Math.Clamp(cur + offset, 0, _searchResults.Count - 1);
        SearchResultsListBox.SelectedIndex = next;
        if (SearchResultsListBox.SelectedItem != null)
            SearchResultsListBox.ScrollIntoView(SearchResultsListBox.SelectedItem);
    }

    // UI event handlers for list and back
    private void OnBackToMainClicked(object? sender, RoutedEventArgs e)   => ShowMainList();
    private void OnOpenSettingsClicked(object? sender, RoutedEventArgs e) => OpenSettingsPage();

    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GamesListBox.SelectedItem is PluginGame sel)
            OpenGameDetailPage(sel);
    }

    private void OnGameSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GamesListBox.SelectedItem != null)
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
    }

    private void OnGameListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GamesListBox.SelectedItem is PluginGame sel)
        { OpenGameDetailPage(sel); e.Handled = true; }
        else if (e.Key == Key.Up)   { NavigateList(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { NavigateList(1);  e.Handled = true; }
        else if (e.Key == Key.Tab || e.Key == Key.F1) { OpenSettingsPage(); e.Handled = true; }
        else if (e.Key == Key.Y || e.Key == Key.OemQuestion)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }
}
