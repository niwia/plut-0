using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Pluto.Models;

namespace Pluto.Services;

public class HubcapSearchService
{
    private const string BaseUrl = "https://hubcapmanifest.com/api/v1";
    private readonly HttpClient _httpClient;
    private readonly AccelaConfigService _configService;
    private readonly DepotKeyService _depotKeyService;

    public string? LastError { get; private set; }

    private static readonly string ImageCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "image_cache");

    // Blacklist patterns ported directly from ASSella search_ranking.py
    private static readonly string[] BlacklistPatterns =
    {
        @"soundtracks?", @"sound tracks?", @"\bost\b", @"original soundtrack",
        @"piano collections?", @"orchestras?", @"orchestral", @"world tour",
        @"concerts?", @"videos?", @"artbooks?", @"graphic novels?", @"dlcs?",
        @"demos?", @"dedicated server", @"servers?", @"tools?", @"sdks?",
        @"3d print model", @"wallpapers?", @"digital contents?", @"mod organizer",
        @"trailers?", @"shorts?", @"teasers?", @"season pass(es)?", @"content packs?",
        @"free editions?", @"upgrades?", @"beta(\s+test)?", @"benchmarks?"
    };

    private static readonly Regex BlacklistRegex = new(
        @"\b(?:" + string.Join("|", BlacklistPatterns) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public HubcapSearchService(AccelaConfigService configService, DepotKeyService depotKeyService)
    {
        _configService = configService;
        _depotKeyService = depotKeyService;

        // Custom HttpClientHandler with permissive SSL configuration for maximum reliability across ISP networks
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) Pluto/1.0");
    }

    private string? GetApiKey()
    {
        return _configService.GetValue("morrenus_api_key");
    }

    /// <summary>
    /// Searches for games on Hubcap API and enriches with local database awareness.
    /// Follows ASSella's dual-path search (exact AppID match first if numeric, then text search).
    /// </summary>
    public async Task<List<SearchResultItem>> SearchAsync(
        string query,
        IEnumerable<PluginGame> localLibrary,
        CancellationToken ct = default)
    {
        LastError = null;
        var trimmed = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 3)
        {
            return new List<SearchResultItem>();
        }

        var results = new List<SearchResultItem>();
        var localDict = localLibrary.ToDictionary(g => g.AppId, g => g);

        // Path 1: Check if API key is present
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            PlutoLogger.Warn("HubcapSearch", "Hubcap API key not set in ACCELA.conf; searching local depot keys only.");
            return SearchLocalDatabase(trimmed, localDict);
        }

        try
        {
            bool isNumeric = long.TryParse(trimmed, out _);

            // If numeric, try exact AppID match first
            if (isNumeric)
            {
                var appidResults = await QueryHubcapApiAsync(trimmed, appidOnly: true, apiKey, ct);
                if (appidResults.Count > 0)
                {
                    results.AddRange(appidResults);
                }
            }

            // If not numeric, or numeric yielded no results, query standard search
            if (results.Count == 0)
            {
                var textResults = await QueryHubcapApiAsync(trimmed, appidOnly: false, apiKey, ct);
                results.AddRange(textResults);
            }
        }
        catch (OperationCanceledException)
        {
            return results;
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("HubcapSearch", $"Online search error for '{trimmed}': {ex.Message}");
        }

        // Augment with local database results if few online results
        if (results.Count == 0)
        {
            var localResults = SearchLocalDatabase(trimmed, localDict);
            results.AddRange(localResults);
        }

        // Filter and Rank results using ASSella algorithm
        var ranked = FilterAndRankResults(results, trimmed);

        // Enrich with local library state and local cached key status
        foreach (var item in ranked)
        {
            if (localDict.TryGetValue(item.AppId, out var localGame))
            {
                item.IsInstalled = true;
                item.InstallMode = localGame.IsAccela ? "assella" : "native";
            }
            else
            {
                item.IsInstalled = false;
                item.InstallMode = string.Empty;
            }

            item.IsCached = _depotKeyService.GetKeysForApp(item.AppId).Count > 0;
        }

        return ranked;
    }

    private async Task<List<SearchResultItem>> QueryHubcapApiAsync(
        string query,
        bool appidOnly,
        string apiKey,
        CancellationToken ct)
    {
        var results = new List<SearchResultItem>();
        var uri = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit=100{(appidOnly ? "&appid=true" : "")}";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            LastError = "rate_limit";
            PlutoLogger.Warn("HubcapSearch", $"Hubcap rate limit hit for query '{query}', retrying after 1.1s delay...");
            try
            {
                await Task.Delay(1100, ct);
                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                retryRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
                using var retryResponse = await _httpClient.SendAsync(retryRequest, ct);
                if (retryResponse.IsSuccessStatusCode)
                {
                    LastError = null;
                    var retryJson = await retryResponse.Content.ReadAsStringAsync(ct);
                    return ParseSearchResults(retryJson);
                }

                string retryBody = string.Empty;
                try { retryBody = await retryResponse.Content.ReadAsStringAsync(ct); } catch { }
                PlutoLogger.Warn("HubcapSearch", $"Hubcap search retry failed with status {retryResponse.StatusCode} for query '{query}': {retryBody}");
            }
            catch (OperationCanceledException) { return results; }
            catch (Exception ex)
            {
                PlutoLogger.Warn("HubcapSearch", $"Hubcap retry error for '{query}': {ex.Message}");
            }
            return results;
        }

        if (!response.IsSuccessStatusCode)
        {
            LastError = response.StatusCode == HttpStatusCode.BadRequest ? "bad_request" : "api_error";
            string errBody = string.Empty;
            try { errBody = await response.Content.ReadAsStringAsync(ct); } catch { }
            PlutoLogger.Warn("HubcapSearch", $"Hubcap search returned status {response.StatusCode} for query '{query}': {errBody}");
            return results;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseSearchResults(json);
    }

    private static List<SearchResultItem> ParseSearchResults(string json)
    {
        var results = new List<SearchResultItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var itemElem in resultsElem.EnumerateArray())
            {
                string appId = string.Empty;
                if (itemElem.TryGetProperty("game_id", out var idProp))
                    appId = idProp.GetString() ?? string.Empty;
                else if (itemElem.TryGetProperty("appid", out var aidProp))
                    appId = aidProp.GetString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(appId)) continue;

                string gameName = "Unknown";
                if (itemElem.TryGetProperty("game_name", out var nameProp))
                    gameName = nameProp.GetString() ?? "Unknown";
                else if (itemElem.TryGetProperty("name", out var nProp))
                    gameName = nProp.GetString() ?? "Unknown";

                string headerUrl = string.Empty;
                if (itemElem.TryGetProperty("header_image", out var imgProp))
                    headerUrl = imgProp.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(headerUrl))
                {
                    headerUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
                }

                bool manifestAvail = true;
                if (itemElem.TryGetProperty("manifest_available", out var mProp) && mProp.ValueKind == JsonValueKind.False)
                {
                    manifestAvail = false;
                }

                results.Add(new SearchResultItem
                {
                    AppId = appId,
                    Name = CleanTitle(gameName),
                    HeaderImageUrl = headerUrl,
                    ManifestAvailable = manifestAvail
                });
            }
        }

        return results;
    }

    private List<SearchResultItem> SearchLocalDatabase(string query, Dictionary<string, PluginGame> localDict)
    {
        var items = new List<SearchResultItem>();
        if (!_depotKeyService.Exists) return items;

        try
        {
            var dbPath = DepotKeyService.DefaultDbPath;
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT appid FROM depot_keys WHERE appid LIKE @q LIMIT 50;";
            cmd.Parameters.AddWithValue("@q", $"%{query}%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var appId = reader.GetString(0);
                string name = $"App {appId}";
                if (localDict.TryGetValue(appId, out var local))
                {
                    name = local.Name;
                }

                items.Add(new SearchResultItem
                {
                    AppId = appId,
                    Name = name,
                    HeaderImageUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    IsCached = true,
                    ManifestAvailable = true
                });
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("HubcapSearch", $"Local database search error: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Fetches and caches thumbnail bitmap on-demand only when a game is selected from the list.
    /// </summary>
    public async Task<Bitmap?> FetchAndCacheGameThumbnailAsync(string appId, string? fallbackUrl = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(ImageCacheDir);
            var localFile = Path.Combine(ImageCacheDir, $"{appId}.jpg");

            if (File.Exists(localFile))
            {
                using var fs = File.OpenRead(localFile);
                return new Bitmap(fs);
            }

            var url = !string.IsNullOrEmpty(fallbackUrl)
                ? fallbackUrl
                : $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

            var bytes = await _httpClient.GetByteArrayAsync(url, ct);
            if (bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(localFile, bytes, ct);
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            }
        }
        catch
        {
            // Silently return null on network error
        }

        return null;
    }

    /// <summary>
    /// Fetches manifest zip from Hubcap (/manifest/{appid}), parses {appid}.lua for depot keys,
    /// and persists them into depot_keys.db.
    /// </summary>
    public async Task<Dictionary<string, string>> FetchAndCacheManifestKeysAsync(string appId, CancellationToken ct = default)
    {
        // 1. Check if keys already exist locally
        var existing = _depotKeyService.GetKeysForApp(appId);
        if (existing.Count > 0)
        {
            return existing;
        }

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Hubcap API key not configured in ACCELA.conf.");
        }

        PlutoLogger.Info("HubcapSearch", $"Fetching manifest bundle for AppID {appId} from Hubcap...");

        var uri = $"{BaseUrl}/manifest/{appId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Hubcap returned status {response.StatusCode} fetching manifest for AppID {appId}.");
        }

        var zipBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var parsedKeys = new Dictionary<string, string>();

        using (var zipMs = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Read))
        {
            var luaEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry != null)
            {
                using var reader = new StreamReader(luaEntry.Open());
                var luaText = await reader.ReadToEndAsync(ct);

                // Regex match addappid(depot_id, 1, "aes_key")
                var matches = Regex.Matches(luaText, @"addappid\(\s*(\d+)\s*,\s*\d+\s*,\s*""([a-fA-F0-9]{64})""\)");
                foreach (Match m in matches)
                {
                    if (m.Groups.Count >= 3)
                    {
                        var did = m.Groups[1].Value.Trim();
                        var key = m.Groups[2].Value.Trim().ToLowerInvariant();
                        parsedKeys[did] = key;
                    }
                }
            }
        }

        if (parsedKeys.Count > 0)
        {
            // Persist keys to depot_keys.db
            SaveKeysToDatabase(appId, parsedKeys);
            PlutoLogger.Info("HubcapSearch", $"Successfully retrieved and cached {parsedKeys.Count} depot key(s) for AppID {appId}.");
        }

        return parsedKeys;
    }

    private void SaveKeysToDatabase(string appId, Dictionary<string, string> keys)
    {
        try
        {
            var dbPath = DepotKeyService.DefaultDbPath;
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Ensure table exists
            using var initCmd = conn.CreateCommand();
            initCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS depot_keys (
                    appid TEXT NOT NULL,
                    depot_id TEXT NOT NULL,
                    aes_key TEXT NOT NULL,
                    updated_at INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (appid, depot_id)
                );";
            initCmd.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO depot_keys (appid, depot_id, aes_key, updated_at)
                VALUES (@appid, @depot_id, @aes_key, @updated_at);";

            var pAppId = cmd.Parameters.Add("@appid", SqliteType.Text);
            var pDepotId = cmd.Parameters.Add("@depot_id", SqliteType.Text);
            var pKey = cmd.Parameters.Add("@aes_key", SqliteType.Text);
            var pTime = cmd.Parameters.Add("@updated_at", SqliteType.Integer);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var kvp in keys)
            {
                pAppId.Value = appId;
                pDepotId.Value = kvp.Key;
                pKey.Value = kvp.Value;
                pTime.Value = now;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("HubcapSearch", $"Failed to persist depot keys to database for AppID {appId}", ex);
        }
    }

    // ── Scoring, Filtering & Deduplication ───────────────────────────────────

    private static List<SearchResultItem> FilterAndRankResults(List<SearchResultItem> items, string query)
    {
        bool queryContainsBlacklist = BlacklistRegex.IsMatch(query);

        var filtered = items.Where(item =>
        {
            if (!item.ManifestAvailable) return false;
            // Only filter blacklist if the user didn't explicitly search for soundtrack, dlc, etc.
            if (!queryContainsBlacklist && BlacklistRegex.IsMatch(item.Name))
            {
                return false;
            }
            return true;
        }).ToList();

        // Calculate score
        foreach (var item in filtered)
        {
            item.Score = ComputeRelevanceScore(item.Name, query);
        }

        // Sort descending by score, then ascending by name length
        var ranked = filtered
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Name.Length)
            .ToList();

        // Deduplicate by normalized name
        var deduped = new List<SearchResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ranked)
        {
            var norm = NormalizeForMatch(item.Name);
            if (string.IsNullOrEmpty(norm) || seen.Contains(norm))
            {
                continue;
            }
            seen.Add(norm);
            deduped.Add(item);
        }

        return deduped;
    }

    private static int ComputeRelevanceScore(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(name)) return -10000;

        var normName = NormalizeForMatch(name);
        var normQuery = NormalizeForMatch(query);
        if (string.IsNullOrWhiteSpace(normQuery)) return 0;

        int score = 0;
        if (normName == normQuery) score += 5000;
        if (normName.StartsWith(normQuery)) score += 2500;
        if (normName.Contains($" {normQuery} ") || normName.EndsWith($" {normQuery}")) score += 1500;

        var nameTokens = normName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var queryTokens = normQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int tokenHits = 0;
        int prefixHits = 0;
        foreach (var qToken in queryTokens)
        {
            if (nameTokens.Contains(qToken))
            {
                tokenHits++;
            }
            else if (nameTokens.Any(t => t.StartsWith(qToken)))
            {
                prefixHits++;
            }
        }

        score += tokenHits * 350;
        score += prefixHits * 120;
        score -= Math.Min(700, Math.Max(0, normName.Length - normQuery.Length) * 5);

        return score;
    }

    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var lowered = value.Trim().ToLowerInvariant();
        return Regex.Replace(lowered, @"[^a-z0-9]+", " ").Trim();
    }

    private static string CleanTitle(string raw)
    {
        // Strip out any emoji or stray control characters
        return Regex.Replace(raw, @"\p{Cs}|\p{So}", "").Trim();
    }
}
