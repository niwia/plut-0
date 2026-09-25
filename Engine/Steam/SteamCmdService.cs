using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Pluto.Services;

namespace Pluto.Engine.Steam;

public sealed record SteamDepotInfo(
    string DepotId,
    string Name,
    string? ManifestId,
    long Size,
    string OsList,
    string Architecture,
    string Language
);

public sealed record SteamAppPackageInfo(
    uint AppId,
    string Name,
    string InstallDir,
    string BuildId,
    Dictionary<string, string> Branches,
    List<SteamDepotInfo> Depots
);

/// <summary>
/// Service providing SteamCMD & PICS REST endpoint queries for app info, branches, depots, and manifests.
/// </summary>
public sealed class SteamCmdService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Fetches authoritative app info, branches, and depot definitions from SteamCMD REST API.
    /// </summary>
    public static async Task<SteamAppPackageInfo?> FetchAppInfoAsync(uint appId)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                PlutoLogger.Warn("SteamCmdService", $"SteamCMD API returned status {resp.StatusCode} for AppID {appId}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty(appId.ToString(), out var appEl))
            {
                return null;
            }

            // 1. Common info
            string name = $"App {appId}";
            string installDir = appId.ToString();
            if (appEl.TryGetProperty("common", out var commonEl))
            {
                if (commonEl.TryGetProperty("name", out var nameEl))
                    name = nameEl.GetString() ?? name;
            }
            if (appEl.TryGetProperty("config", out var configEl))
            {
                if (configEl.TryGetProperty("installdir", out var instEl))
                    installDir = instEl.GetString() ?? installDir;
            }

            // 2. Depots & Branches
            var depots = new List<SteamDepotInfo>();
            var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string buildId = "0";

            if (appEl.TryGetProperty("depots", out var depotsEl))
            {
                // Branches
                if (depotsEl.TryGetProperty("branches", out var branchesEl))
                {
                    foreach (var bProp in branchesEl.EnumerateObject())
                    {
                        var bName = bProp.Name;
                        string bBid = "0";
                        if (bProp.Value.TryGetProperty("buildid", out var bidEl))
                            bBid = bidEl.GetString() ?? "0";
                        branches[bName] = bBid;

                        if (string.Equals(bName, "public", StringComparison.OrdinalIgnoreCase))
                            buildId = bBid;
                    }
                }

                // Depots list
                foreach (var depotProp in depotsEl.EnumerateObject())
                {
                    var depId = depotProp.Name;
                    if (!uint.TryParse(depId, out _)) continue; // ignore non-numeric properties like 'branches'

                    var val = depotProp.Value;
                    string depName = depId;
                    if (val.TryGetProperty("name", out var nEl)) depName = nEl.GetString() ?? depId;

                    string? manifestGid = null;
                    if (val.TryGetProperty("manifests", out var manEl))
                    {
                        if (manEl.TryGetProperty("public", out var pubManEl))
                        {
                            if (pubManEl.ValueKind == JsonValueKind.Object && pubManEl.TryGetProperty("gid", out var gidEl))
                                manifestGid = gidEl.GetString();
                            else if (pubManEl.ValueKind == JsonValueKind.String)
                                manifestGid = pubManEl.GetString();
                        }
                    }

                    long maxsize = 0;
                    if (val.TryGetProperty("maxsize", out var sizeEl) && sizeEl.TryGetInt64(out var s))
                        maxsize = s;

                    string os = "all";
                    string arch = "all";
                    string lang = "all";
                    if (val.TryGetProperty("config", out var dConfig))
                    {
                        if (dConfig.TryGetProperty("oslist", out var osEl)) os = osEl.GetString() ?? "all";
                        if (dConfig.TryGetProperty("osarch", out var archEl)) arch = archEl.GetString() ?? "all";
                        if (dConfig.TryGetProperty("language", out var langEl)) lang = langEl.GetString() ?? "all";
                    }

                    depots.Add(new SteamDepotInfo(depId, depName, manifestGid, maxsize, os, arch, lang));
                }
            }

            return new SteamAppPackageInfo(appId, name, installDir, buildId, branches, depots);
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SteamCmdService", $"Error querying app info for {appId}", ex);
            return null;
        }
    }

    /// <summary>
    /// Filters depots applicable to Linux execution (native or Windows Proton compatible, English/all languages).
    /// </summary>
    public static List<SteamDepotInfo> FilterPlayableDepots(IEnumerable<SteamDepotInfo> depots, bool preferNativeLinux = true)
    {
        var result = new List<SteamDepotInfo>();

        foreach (var d in depots)
        {
            if (string.IsNullOrWhiteSpace(d.ManifestId)) continue;

            // Language filter
            if (!string.Equals(d.Language, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(d.Language, "english", StringComparison.OrdinalIgnoreCase))
                continue;

            // Architecture filter
            if (string.Equals(d.Architecture, "32", StringComparison.OrdinalIgnoreCase))
                continue;

            // OS filter
            var os = d.OsList.ToLowerInvariant();
            if (os == "all" || os == "")
            {
                result.Add(d);
            }
            else if (preferNativeLinux && os.Contains("linux"))
            {
                result.Add(d);
            }
            else if (os.Contains("windows"))
            {
                result.Add(d);
            }
        }

        return result;
    }
}
