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
            if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
            if (SearchStatusText     != null) SearchStatusText.IsVisible     = false;
            if (GamesListBox         != null) GamesListBox.IsVisible         = true;
            ApplyFilter(null);
            return;
        }

        ApplyFilter(text);

        if (text.Trim().Length >= 2)
        {
            if (SearchStatusText != null)
            {
                SearchStatusText.Text      = $"searching for \"{text.Trim()}\"...";
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
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2) return;

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
                        catch { }
                    }, ct);
                }
            }

            if (results.Count > 0)
            {
                if (SearchResultsListBox != null)
                {
                    SearchResultsListBox.IsVisible    = true;
                    SearchResultsListBox.SelectedIndex = 0;
                }
                if (GamesListBox    != null) GamesListBox.IsVisible    = false;
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
                    if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
                    if (GamesListBox         != null) GamesListBox.IsVisible         = true;
                    if (SearchStatusText     != null)
                    {
                        SearchStatusText.Text      = "no online matches (showing local library)";
                        SearchStatusText.IsVisible = true;
                    }
                }
                else
                {
                    if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
                    if (GamesListBox         != null) GamesListBox.IsVisible         = false;
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
            if (SearchStatusText != null) SearchStatusText.Text = "search error • press enter to retry";
        }
    }

    private void ClearSearchAndReset()
    {
        _liveSearchTimer.Stop();
        _searchCts?.Cancel();
        SearchBox.Text = string.Empty;
        if (SearchResultsListBox != null) SearchResultsListBox.IsVisible = false;
        if (SearchStatusText     != null) SearchStatusText.IsVisible     = false;
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
            else { NavigateList(1); GamesListBox.Focus(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _liveSearchTimer.Stop();
            if (SearchResultsListBox != null && SearchResultsListBox.IsVisible
                && SearchResultsListBox.SelectedItem is SearchResultItem sr)
                OpenSearchResultDetailPage(sr);
            else if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                await ExecuteSearchAsync(SearchBox.Text);
            else if (GamesListBox.SelectedItem is PluginGame local)
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
