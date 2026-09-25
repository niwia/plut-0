using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Pluto.Engine.Installation;
using Pluto.Services;

namespace Pluto.Engine.Steam;

public static class DepotNameResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, string> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string AccelaDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "steam_headers.db");

    /// <summary>
    /// Resolves human-readable depot names using ASSella's SQLite cache, Steam Store DLC API,
    /// and SteamCMD architecture/OS heuristics without requiring SteamDB.
    /// </summary>
    public static async Task EnrichDepotNamesAsync(uint appId, string gameName, List<PreDownloadDepotItem> depots)
    {
        if (depots == null || depots.Count == 0) return;

        // 1. Try reading from ACCELA steam_headers.db depot_enrichments
        var cached = LoadFromAccelaDb(appId.ToString());

        // 2. Try fetching DLC names from Steam Store API for any unrecognized DLC depots
        var dlcNames = new Dictionary<string, string>();
        try
        {
            dlcNames = await FetchSteamStoreDlcNamesAsync(appId);
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("DepotNameResolver", $"Steam Store DLC query failed: {ex.Message}");
        }

        // 3. Assign rich names
        bool isFirstDepot = true;
        for (int i = 0; i < depots.Count; i++)
        {
            var item = depots[i];
            var depId = item.DepotId;

            // Priority 1: In-memory cache
            if (MemoryCache.TryGetValue(depId, out var memName) && !string.IsNullOrWhiteSpace(memName))
            {
                item.Name = memName;
                isFirstDepot = false;
                continue;
            }

            // Priority 2: ACCELA steam_headers.db
            if (cached.TryGetValue(depId, out var dbName) && !string.IsNullOrWhiteSpace(dbName))
            {
                item.Name = dbName;
                MemoryCache[depId] = dbName;
                isFirstDepot = false;
                continue;
            }

            // Priority 3: Steam Store DLC Name
            if (dlcNames.TryGetValue(depId, out var dlcName) && !string.IsNullOrWhiteSpace(dlcName))
            {
                item.Name = $"[DLC] {dlcName}";
                MemoryCache[depId] = item.Name;
                isFirstDepot = false;
                continue;
            }

            // Priority 4: Existing name if non-generic
            if (!string.IsNullOrWhiteSpace(item.Name) && !item.Name.Equals(depId, StringComparison.OrdinalIgnoreCase))
            {
                isFirstDepot = false;
                continue;
            }

            // Priority 5: Smart heuristic based on game name & platform tag
            string resolved;
            if (isFirstDepot)
            {
                resolved = $"[Windows] {gameName}";
                isFirstDepot = false;
            }
            else if (!string.IsNullOrWhiteSpace(item.PlatformTag) && !item.PlatformTag.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                resolved = $"[{item.PlatformTag}] Content";
            }
            else
            {
                resolved = $"Depot {depId}";
            }

            item.Name = resolved;
            MemoryCache[depId] = resolved;
        }
    }

    private static Dictionary<string, string> LoadFromAccelaDb(string appId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(AccelaDbPath)) return result;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = AccelaDbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT depot_id, name, is_dlc FROM depot_enrichments WHERE appid = @appid;";
            cmd.Parameters.AddWithValue("@appid", appId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var depotId = reader.GetString(0);
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var isDlc = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[depotId] = isDlc ? $"[DLC] {name}" : name;
                }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("DepotNameResolver", $"Error reading ACCELA db for {appId}: {ex.Message}");
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> FetchSteamStoreDlcNamesAsync(uint appId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";

        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return result;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appElem) ||
            !appElem.TryGetProperty("data", out var dataElem) ||
            !dataElem.TryGetProperty("dlc", out var dlcArray) ||
            dlcArray.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        // Fetch each DLC's name (limited to top 15 to stay fast)
        int count = 0;
        foreach (var dlcIdElem in dlcArray.EnumerateArray())
        {
            if (++count > 15) break;
            var dlcId = dlcIdElem.GetInt64().ToString();
            try
            {
                var dlcUrl = $"https://store.steampowered.com/api/appdetails?appids={dlcId}";
                using var dlcResp = await Http.GetAsync(dlcUrl);
                if (dlcResp.IsSuccessStatusCode)
                {
                    var dlcJson = await dlcResp.Content.ReadAsStringAsync();
                    using var dlcDoc = JsonDocument.Parse(dlcJson);
                    if (dlcDoc.RootElement.TryGetProperty(dlcId, out var subApp) &&
                        subApp.TryGetProperty("data", out var subData) &&
                        subData.TryGetProperty("name", out var nameProp))
                    {
                        var dlcTitle = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(dlcTitle))
                        {
                            result[dlcId] = dlcTitle;
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }
}
