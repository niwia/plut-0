using System;
using System.IO;
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

// MainWindow partial — search box, live search, search results
public partial class MainWindow
{
    /// <summary>
    /// Shows or hides the search results overlay.
    ///
    /// On the home screen the search field is the hero and results float above
    /// it, so the overlay panel is what actually toggles search mode - toggling
    /// the listbox alone would leave an invisible list swallowing input.
    /// </summary>
    private void SetSearchOverlayVisible(bool visible)
    {
        if (SearchOverlay != null) SearchOverlay.IsVisible = visible;
        if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = visible;
    }
    // Rotating placeholder
    private void OnPlaceholderTimerTick(object? sender, EventArgs e)
    {
        if (SearchBox != null && !SearchBox.IsFocused && string.IsNullOrEmpty(SearchBox.Text))
        {
            _placeholderIndex = (_placeholderIndex + 1) % SearchPlaceholders.Length;
            SearchBox.PlaceholderText = SearchPlaceholders[_placeholderIndex];
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = SearchBox?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _liveSearchTimer.Stop();
            _searchCts?.Cancel();
            SetSearchOverlayVisible(false);
            if (SearchStatusText     != null) SearchStatusText.IsVisible     = false;
            ApplyFilter(null);
            return;
        }

        ApplyFilter(text);

        if (text.Trim().Length >= 3)
        {
            if (SearchStatusText != null)
            {
                SearchStatusText.Text      = $"searching for \"{text.Trim()}\"...";
                SearchStatusText.IsVisible = true;
            }
            _liveSearchTimer.Stop();
            _liveSearchTimer.Start();
        }
        else
        {
            _liveSearchTimer.Stop();
            _searchCts?.Cancel();
            SetSearchOverlayVisible(false);
            if (SearchStatusText     != null) SearchStatusText.IsVisible     = false;
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
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 3) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        if (SearchStatusText != null)
        {
            SearchStatusText.Text      = $"searching online for \"{trimmed}\"...";
            SearchStatusText.IsVisible = true;
        }

        try
        {
            var results = await _hubcapSearchService.SearchAsync(trimmed, _allGames, ct);
            if (ct.IsCancellationRequested) return;

            _searchResults.Clear();
            foreach (var r in results) _searchResults.Add(r);

            // In-memory thumbnails (no disk writes)
            if (_settingSearchThumbnailsEnabled)
            {
                foreach (var r in results)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var url   = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{r.AppId}/header.jpg";
                            using var http  = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                            var bytes = await http.GetByteArrayAsync(url, ct);
                            if (bytes.Length > 0 && !ct.IsCancellationRequested)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    using var ms = new MemoryStream(bytes);
                                    r.Thumbnail  = new Bitmap(ms);
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            // Search results stay usable without thumbnails.
                            PlutoLogger.Warn("UI", $"Could not attach thumbnail for search result: {ex.Message}");
                        }
                    }, ct);
                }
            }

            // Check the rate-limit state before the results count. When rate limited,
            // SearchAsync falls back to local library matches, so results.Count is
            // non-zero and the "found N games" branch would report a successful
            // search that never actually reached the API.
            if (_hubcapSearchService.LastError == "rate_limit")
            {
                SetSearchOverlayVisible(false);
                if (EmptyStateText       != null) EmptyStateText.IsVisible       = false;
                if (SearchStatusText     != null)
                {
                    SearchStatusText.Text      = "search rate limited | press enter to retry";
                    SearchStatusText.IsVisible = true;
                }
            }
            else if (results.Count > 0)
            {
                if (SearchResultsListBox != null)
                {
                    SearchResultsListBox.IsVisible    = true;
                    SearchResultsListBox.SelectedIndex = 0;
                }
                if (EmptyStateText  != null) EmptyStateText.IsVisible  = false;
                if (SearchStatusText != null)
                {
                    SearchStatusText.Text      = $"found {results.Count} games";
                    SearchStatusText.IsVisible = true;
                }
            }
            else
            {
                if (_displayedGames.Count > 0)
                {
                    SetSearchOverlayVisible(false);
                    if (SearchStatusText     != null)
                    {
                        SearchStatusText.Text      = "no online matches (showing local library)";
                        SearchStatusText.IsVisible = true;
                    }
                }
                else
                {
                    SetSearchOverlayVisible(false);
                    if (EmptyStateText       != null)
                    {
                        EmptyStateText.Text      = $"no games found for \"{trimmed}\"";
                        EmptyStateText.IsVisible = true;
                    }
                    if (SearchStatusText != null) SearchStatusText.IsVisible = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Search", $"Search failed: {ex.Message}");
            if (SearchStatusText != null) SearchStatusText.Text = "search error | press enter to retry";
        }
    }

    private void ClearSearchAndReset()
    {
        _liveSearchTimer.Stop();
        _searchCts?.Cancel();
        SearchBox.Text = string.Empty;
        SetSearchOverlayVisible(false);
        if (SearchStatusText     != null) SearchStatusText.IsVisible     = false;
        if (Filmstrip != null)
        {
            Filmstrip.IsVisible = true;
            ApplyFilter(null);
            FocusLibrary();
        }
    }

    private void OnSearchBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        
        if (_settingSearchResetOnAccess && Filmstrip != null)
        {
            Filmstrip.FocusedIndex = 0;
        }
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
            else { NavigateList(1); FocusLibrary(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _liveSearchTimer.Stop();
            if (SearchResultsListBox != null && SearchResultsListBox.IsVisible
                && SearchResultsListBox.SelectedItem is SearchResultItem sr)
                OpenSearchResultDetailPage(sr);
            else if (!string.IsNullOrWhiteSpace(SearchBox.Text) && SearchBox.Text.Trim().Length >= 3)
                await ExecuteSearchAsync(SearchBox.Text);
            else if (SelectedGame is PluginGame local)
                OpenGameDetailPage(local);
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
        if (SearchResultsListBox.SelectedItem is SearchResultItem sel)
            OpenSearchResultDetailPage(sel);
    }

    private void OnSearchResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsListBox.SelectedItem != null)
            SearchResultsListBox.ScrollIntoView(SearchResultsListBox.SelectedItem);
    }

    private void OnSearchResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsListBox.SelectedItem is SearchResultItem sel)
        { OpenSearchResultDetailPage(sel); e.Handled = true; }
        else if (e.Key == Key.Up && SearchResultsListBox.SelectedIndex == 0)
        { SearchBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { ClearSearchAndReset(); e.Handled = true; }
    }
}
