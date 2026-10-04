using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Pluto.Engine.Steam;
using Pluto.Models;

namespace Pluto.Services;

public enum UpdateStatus
{
    /// <summary>Not enough information to decide.</summary>
    Unknown,

    /// <summary>Installed build matches the latest known build.</summary>
    UpToDate,

    /// <summary>A newer build exists.</summary>
    Available
}

/// <summary>
/// Detects games that have a newer Steam build than the one installed.
///
/// There was no update detection in Pluto at all before this, so the filmstrip
/// had nothing to badge. The comparison is installed build ID (read from each
/// game's <c>appmanifest_&lt;appid&gt;.acf</c>) against the latest branch build ID
/// reported by the SteamCMD info API.
///
/// Two rules borrowed from ASSella, both learned the hard way there:
///
///   1. <b>Never downgrade a confirmed update.</b> A transient network failure
///      mid-refresh must not silently clear a real update badge; the user would
///      stop seeing it and miss the update entirely.
///   2. <b>Never report an update when the remote build is older.</b> Build IDs
///      increase monotonically, so a lower remote value means the upstream API
///      mirror is stale. Believing it would offer to "update" a game backwards.
///
/// Results are cached on disk because the API is a third-party mirror that must
/// not be polled per frame: <see cref="UpdateStatus.Available"/> persists until
/// an update is installed, <see cref="UpdateStatus.UpToDate"/> expires daily.
/// </summary>
public sealed class UpdateStatusService
{
    private static readonly string CachePath =
        Path.Combine(PlutoPaths.PlutoData, "update_status.json");

    /// <summary>How long an "up to date" verdict is trusted before rechecking.</summary>
    private static readonly TimeSpan UpToDateTtl = TimeSpan.FromHours(24);

    /// <summary>Concurrent requests to the third-party API.</summary>
    private const int MaxConcurrency = 4;

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private readonly object _cacheLock = new();
    private bool _loaded;

    private sealed class CacheEntry
    {
        [JsonPropertyName("status")]
        public UpdateStatus Status { get; set; }

        [JsonPropertyName("installed")]
        public string InstalledBuildId { get; set; } = string.Empty;

        [JsonPropertyName("latest")]
        public string LatestBuildId { get; set; } = string.Empty;

        [JsonPropertyName("checked")]
        public long CheckedAtUnix { get; set; }

        [JsonIgnore]
        public bool IsFresh =>
            Status == UpdateStatus.Available ||
            DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(CheckedAtUnix) < UpToDateTtl;

        /// <summary>Only a positive result may be downgraded back to UpToDate.</summary>
        [JsonIgnore]
        public bool IsConfirmed => Status == UpdateStatus.Available;
    }

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Last known status per AppID. Safe to call before any check has run.</summary>
    public UpdateStatus GetStatus(string appId) => GetEntry(appId).Status;

    /// <summary>True when this game's newest known build differs from the installed one.</summary>
    public bool HasUpdate(string appId) => GetEntry(appId).Status == UpdateStatus.Available;

    /// <summary>Human-readable build comparison, or empty when unknown.</summary>
    public string Describe(string appId)
    {
        var e = GetEntry(appId);
        if (e.Status != UpdateStatus.Available) return string.Empty;
        return $"{e.InstalledBuildId} -> {e.LatestBuildId}";
    }

    private CacheEntry GetEntry(string appId)
    {
        EnsureLoaded();
        lock (_cacheLock)
        {
            return _cache.TryGetValue(appId, out var e) ? e : new CacheEntry();
        }
    }

    /// <summary>
    /// Reads the build ID Steam recorded for the installed files.
    /// Empty when there is no manifest, which is normal for games that are
    /// registered but not installed on disk.
    /// </summary>
    public static string ReadInstalledBuildId(PluginGame game)
    {
        if (string.IsNullOrWhiteSpace(game.AppmanifestPath)) return string.Empty;
        if (!File.Exists(game.AppmanifestPath)) return string.Empty;

        try
        {
            var text = File.ReadAllText(game.AppmanifestPath);
            if (!SteamAcfService.VdfParser.TryParse(text, out var root)) return string.Empty;
            if (!root.Children.TryGetValue("AppState", out var state)) return string.Empty;

            var buildId = state.GetString("buildid") ?? string.Empty;
            return buildId == "0" ? string.Empty : buildId;
        }
        catch (Exception ex)
        {
            PlutoLogger.Debug("Updates", $"Could not read build id for {game.AppId}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Refreshes update status for the supplied games.
    ///
    /// Only games with an installed build are checked: without one there is
    /// nothing to compare, and skipping them avoids pointless API traffic on a
    /// library where most entries are registered rather than installed.
    /// </summary>
    /// <param name="onProgress">Called with (done, total) as each game resolves.</param>
    public async Task RefreshAsync(
        IReadOnlyList<PluginGame> games,
        bool force = false,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        var candidates = new List<(PluginGame Game, string Installed)>();
        lock (_cacheLock)
        {
            foreach (var g in games)
            {
                if (!force && _cache.TryGetValue(g.AppId, out var e) && e.IsFresh) continue;

                var installed = ReadInstalledBuildId(g);
                if (string.IsNullOrEmpty(installed)) continue;

                candidates.Add((g, installed));
            }
        }

        if (candidates.Count == 0)
        {
            onProgress?.Invoke(0, 0);
            return;
        }

        int done = 0;
        var tasks = candidates.Select(c => CheckOneAsync(c.Game, c.Installed, ct)
            .ContinueWith(_ => Interlocked.Increment(ref done), TaskScheduler.Default))
            .ToArray();

        var progressTask = ReportProgressAsync(onProgress, done, candidates.Count, ct);
        await Task.WhenAll(tasks.Append(progressTask));

        Save();
        PlutoLogger.Info("Updates", $"Checked {candidates.Count} installed games for updates");
    }

    private async Task ReportProgressAsync(Action<int, int>? onProgress, int done, int total, CancellationToken ct)
    {
        if (onProgress == null) return;

        int last = -1;
        while (Volatile.Read(ref done) < total && !ct.IsCancellationRequested)
        {
            int d = Volatile.Read(ref done);
            if (d != last)
            {
                last = d;
                onProgress(d, total);
            }
            await Task.Delay(120, ct);
        }
        onProgress(total, total);
    }

    private async Task CheckOneAsync(PluginGame game, string installedBuildId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!uint.TryParse(game.AppId, out var appId)) return;

            var info = await SteamCmdService.FetchAppInfoAsync(appId);
            if (info == null)
            {
                // Rule 1: a failed lookup must not erase a known update.
                return;
            }

            var latest = info.Branches.TryGetValue("public", out var b) ? b : info.BuildId;
            if (string.IsNullOrWhiteSpace(latest) || latest == "0")
            {
                return;
            }

            var status = Classify(installedBuildId, latest);

            lock (_cacheLock)
            {
                // Rule 1 again, at the point of write: keep a confirmed update
                // unless this run positively determined the game is current.
                if (_cache.TryGetValue(game.AppId, out var existing) && existing.IsConfirmed && status != UpdateStatus.UpToDate)
                {
                    return;
                }

                _cache[game.AppId] = new CacheEntry
                {
                    Status = status,
                    InstalledBuildId = installedBuildId,
                    LatestBuildId = latest,
                    CheckedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
            }

            PlutoLogger.Debug("Updates",
                $"{game.Name} ({game.AppId}): {status} {installedBuildId} -> {latest}");
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Updates", $"Update check failed for {game.AppId}: {ex.Message}");
        }
        finally
        {
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Decides whether the remote build represents an update.
    /// </summary>
    internal static UpdateStatus Classify(string installedBuildId, string latestBuildId)
    {
        if (string.IsNullOrWhiteSpace(installedBuildId) ||
            string.IsNullOrWhiteSpace(latestBuildId) ||
            installedBuildId == "0" || latestBuildId == "0")
        {
            return UpdateStatus.Unknown;
        }

        if (long.TryParse(installedBuildId, out var installed) &&
            long.TryParse(latestBuildId, out var latest))
        {
            // Rule 2: build IDs only increase. A lower remote value means the
            // mirror is stale, and trusting it would offer a downgrade.
            if (latest < installed) return UpdateStatus.UpToDate;
            return latest > installed ? UpdateStatus.Available : UpdateStatus.UpToDate;
        }

        // Non-numeric build IDs: fall back to inequality.
        return string.Equals(installedBuildId, latestBuildId, StringComparison.Ordinal)
            ? UpdateStatus.UpToDate
            : UpdateStatus.Available;
    }

    /// <summary>
    /// Clears the update badge after a successful install.
    /// Without this a downloaded update would keep advertising itself.
    /// </summary>
    public void MarkInstalled(string appId, string installedBuildId)
    {
        EnsureLoaded();
        lock (_cacheLock)
        {
            // An empty installed build means the caller does not know the exact
            // ID it just wrote, so keep whatever was last seen as latest rather
            // than blanking it and losing the record.
            var previous = _cache.TryGetValue(appId, out var existing) ? existing.LatestBuildId : string.Empty;

            _cache[appId] = new CacheEntry
            {
                Status = UpdateStatus.UpToDate,
                InstalledBuildId = installedBuildId,
                LatestBuildId = string.IsNullOrEmpty(installedBuildId) ? previous : installedBuildId,
                CheckedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }
        Save();
    }

    /// <summary>Forgets all cached verdicts so the next refresh rechecks everything.</summary>
    public void Invalidate()
    {
        EnsureLoaded();
        lock (_cacheLock) _cache.Clear();
        Save();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(CachePath)) return;

            var json = File.ReadAllText(CachePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, CacheOptions);
            if (parsed == null) return;

            lock (_cacheLock)
            {
                foreach (var kv in parsed) _cache[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Updates", $"Could not read update cache: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            PlutoPaths.EnsureDirectory(Path.GetDirectoryName(CachePath)!);
            lock (_cacheLock)
            {
                File.WriteAllText(CachePath,
                    JsonSerializer.Serialize(_cache, CacheOptions));
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Updates", $"Could not write update cache: {ex.Message}");
        }
    }
}