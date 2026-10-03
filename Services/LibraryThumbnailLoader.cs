using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Fills in library row artwork lazily.
///
/// A naive implementation would fetch a capsule for all ~200 games on load, which
/// is a couple hundred network requests before the UI is usable. This instead:
///
///   1. Loads from the on-disk cache first, which costs no network at all. Most
///      games have already been seen in search results or the detail page, so this
///      is the common case.
///   2. Only reaches for the network when explicitly enabled, and then with bounded
///      concurrency.
///   3. Cancels cleanly when the list is re-filtered, so scrolling or typing
///      doesn't leave orphaned work running.
///
/// Failures are silent by design: a missing capsule degrades to a text row.
/// </summary>
public sealed class LibraryThumbnailLoader : IDisposable
{
    private const string CacheDirName = "image_cache";

    private static string CacheDir =>
        Path.Combine(PlutoPaths.AccelaData, CacheDirName);

    private const string FallbackUrlFormat =
        "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{0}/header.jpg";

    /// <summary>Never run more than this many downloads at once.</summary>
    private const int MaxConcurrency = 4;

    private readonly HttpClientLike _http;
    private readonly bool _allowNetwork;

    /// <summary>
    /// Marshals work onto the UI thread.
    ///
    /// <see cref="PluginGame.Thumbnail"/> is a bound Avalonia property, so it must be
    /// assigned on the UI thread - assigning from a threadpool thread raises
    /// PropertyChanged off-thread and the binding tries to set <c>Image.Source</c>
    /// from the wrong thread. Injecting the marshal step rather than reaching for a
    /// static Dispatcher keeps this testable outside a running Avalonia app.
    /// </summary>
    private readonly Action<Action> _postToUi;

    public LibraryThumbnailLoader(bool allowNetwork, Action<Action> postToUi)
    {
        _allowNetwork = allowNetwork;
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _http = new HttpClientLike();
    }

    /// <summary>
    /// Populates <see cref="PluginGame.Thumbnail"/> across the supplied games.
    /// Returns when every entry has been attempted or the token is cancelled.
    /// </summary>
    public async Task LoadAsync(IReadOnlyList<PluginGame> games, CancellationToken ct)
    {
        if (games.Count == 0) return;

        try
        {
            Directory.CreateDirectory(CacheDir);
        }
        catch (Exception ex)
        {
            PlutoLogger.Debug("Thumbnails", $"Cache dir unavailable: {ex.Message}");
        }

        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = new List<Task>(games.Count);

        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;
            if (game.Thumbnail != null) continue;

            tasks.Add(LoadOneAsync(game, gate, ct));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected when the user re-filters mid-load.
        }
        catch (Exception ex)
        {
            PlutoLogger.Debug("Thumbnails", $"Thumbnail batch ended early: {ex.Message}");
        }
    }

    private async Task LoadOneAsync(PluginGame game, SemaphoreSlim gate, CancellationToken ct)
    {
        // Acquire exactly once for the whole operation. Acquiring again before the
        // network call would hold a slot while waiting for another, and with every
        // slot doing that the batch deadlocks.
        await gate.WaitAsync(ct);
        try
        {
            if (ct.IsCancellationRequested || game.Thumbnail != null) return;

            byte[]? bytes = null;

            var cachedPath = Path.Combine(CacheDir, $"{game.AppId}.jpg");
            if (File.Exists(cachedPath))
            {
                try
                {
                    bytes = await File.ReadAllBytesAsync(cachedPath, ct);
                }
                catch (Exception ex)
                {
                    // Corrupt cache entry; fall through to a network fetch.
                    PlutoLogger.Debug("Thumbnails", $"Bad cache for {game.AppId}: {ex.Message}");
                }
            }

            if (bytes == null && _allowNetwork)
            {
                bytes = await _http.GetByteArrayAsync(
                    string.Format(FallbackUrlFormat, game.AppId), ct);

                if (bytes.Length > 0)
                {
                    try
                    {
                        await File.WriteAllBytesAsync(cachedPath, bytes, ct);
                    }
                    catch (Exception ex)
                    {
                        PlutoLogger.Debug("Thumbnails", $"Could not cache {game.AppId}: {ex.Message}");
                    }
                }
            }

            if (bytes == null || bytes.Length == 0) return;

            // Bitmap construction touches Avalonia's render interface, so the
            // decode and the assignment both belong on the UI thread.
            _postToUi(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested || game.Thumbnail != null) return;
                    using var ms = new MemoryStream(bytes);
                    game.Thumbnail = new Bitmap(ms);
                }
                catch (Exception ex)
                {
                    PlutoLogger.Debug("Thumbnails", $"Decode failed for {game.AppId}: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates to the batch.
        }
        catch (Exception ex)
        {
            PlutoLogger.Debug("Thumbnails", $"Load failed for {game.AppId}: {ex.Message}");
        }
        finally
        {
            try { gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Minimal HTTP wrapper so the loader owns exactly one client.</summary>
    private sealed class HttpClientLike : IDisposable
    {
        private readonly System.Net.Http.HttpClient _client =
            new() { Timeout = TimeSpan.FromSeconds(10) };

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct) =>
            _client.GetByteArrayAsync(url, ct);

        public void Dispose() => _client.Dispose();
    }
}