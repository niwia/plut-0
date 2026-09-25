using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pluto.Services;

namespace Pluto.Engine.Steam;

public readonly record struct HubcapManifestResult(
    bool Success,
    Dictionary<string, string> DepotKeys,
    Dictionary<string, string> ManifestIds,
    Dictionary<string, string> ManifestFiles, // depotId -> local manifest file path
    string? AppToken,
    string? ErrorMessage
);

/// <summary>
/// Service responsible for fetching manifest ZIP bundles from Hubcap API,
/// extracting AES depot keys, AppTokens, and .manifest files, and persisting
/// keys into depot_keys.db in 100% ASSella parity.
/// </summary>
public sealed class HubcapManifestService
{
    private const string BaseUrl = "https://hubcapmanifest.com/api/v1";
    private readonly HttpClient _httpClient;
    private readonly AccelaConfigService _configService;
    private readonly DepotKeyService _depotKeyService;

    private static readonly string HubcapManifestsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "hubcap_manifests");

    private static readonly string AccelaManifestsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "manifests");

    private static readonly string TempManifestsDir = Path.Combine(
        Path.GetTempPath(), "mistwalker_manifests");

    private static readonly Regex AddAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)\s*(?:,\s*[^,]+)?\s*,\s*[""']([a-fA-F0-9]{64})[""']\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetManifestIdRegex = new(
        @"setManifestid\s*\(\s*(\d+)\s*,\s*[""'](\d+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AddTokenRegex = new(
        @"addtoken\s*\(\s*\d+\s*,\s*[""']([^""']+)[""']\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public HubcapManifestService(AccelaConfigService configService, DepotKeyService depotKeyService)
    {
        _configService = configService;
        _depotKeyService = depotKeyService;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    /// <summary>
    /// Fetches and resolves the manifest bundle and AES keys for an AppID.
    /// Checks local cache first; if missing or forceRefresh is true, queries Hubcap API.
    /// </summary>
    public async Task<HubcapManifestResult> ResolveManifestAndKeysAsync(
        uint appId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(HubcapManifestsDir);
        Directory.CreateDirectory(AccelaManifestsDir);
        Directory.CreateDirectory(TempManifestsDir);

        var zipPath = Path.Combine(HubcapManifestsDir, $"accela_fetch_{appId}.zip");

        // 1. If we don't have the zip or forceRefresh is requested, download from Hubcap
        if (forceRefresh || !File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
        {
            var apiKey = _configService.GetValue("morrenus_api_key", "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                PlutoLogger.Warn("HubcapManifest", "No Hubcap API key configured (morrenus_api_key in ACCELA.conf)");
                return new HubcapManifestResult(false, new(), new(), new(), null, "Hubcap API key not configured.");
            }

            bool downloaded = await DownloadBundleWithFallbackAsync(appId, apiKey, zipPath, cancellationToken);
            if (!downloaded)
            {
                PlutoLogger.Error("HubcapManifest", $"Failed to download manifest bundle for AppID {appId} from Hubcap.");
                return new HubcapManifestResult(false, new(), new(), new(), null, $"Could not download manifest for AppID {appId}.");
            }
        }

        // 2. Parse the ZIP bundle
        try
        {
            var depotKeys = new Dictionary<string, string>();
            var manifestIds = new Dictionary<string, string>();
            var manifestFiles = new Dictionary<string, string>();
            string? appToken = null;

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                // Look for Lua file
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = new StreamReader(entry.Open());
                        var luaContent = await reader.ReadToEndAsync(cancellationToken);

                        // Extract depot keys
                        foreach (Match m in AddAppIdRegex.Matches(luaContent))
                        {
                            var did = m.Groups[1].Value.Trim();
                            var key = m.Groups[2].Value.Trim().ToLowerInvariant();
                            depotKeys[did] = key;
                        }

                        // Extract manifest IDs
                        foreach (Match m in SetManifestIdRegex.Matches(luaContent))
                        {
                            var did = m.Groups[1].Value.Trim();
                            var mid = m.Groups[2].Value.Trim();
                            manifestIds[did] = mid;
                        }

                        // Extract AppToken
                        var tokenMatch = AddTokenRegex.Match(luaContent);
                        if (tokenMatch.Success)
                        {
                            appToken = tokenMatch.Groups[1].Value.Trim();
                        }
                    }
                }

                // Extract .manifest files to local cache
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetFile = Path.Combine(AccelaManifestsDir, entry.Name);
                        var tempFile = Path.Combine(TempManifestsDir, entry.Name);

                        entry.ExtractToFile(targetFile, overwrite: true);
                        entry.ExtractToFile(tempFile, overwrite: true);

                        // Filename pattern is usually {depotId}_{manifestId}.manifest
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                        var parts = nameWithoutExt.Split('_');
                        if (parts.Length >= 2 && long.TryParse(parts[0], out _))
                        {
                            var did = parts[0];
                            manifestFiles[did] = targetFile;
                        }
                    }
                }
            }

            // Persist keys & token to depot_keys.db
            if (depotKeys.Count > 0)
            {
                _depotKeyService.SaveDepotKeys(appId.ToString(), depotKeys, appToken);
            }

            PlutoLogger.Info("HubcapManifest",
                $"Processed Hubcap bundle for AppID {appId}: {depotKeys.Count} keys found, " +
                $"{manifestFiles.Count} manifests extracted, Token={(appToken != null ? "yes" : "no")}");

            return new HubcapManifestResult(true, depotKeys, manifestIds, manifestFiles, appToken, null);
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("HubcapManifest", $"Failed to process Hubcap manifest zip for AppID {appId}", ex);
            return new HubcapManifestResult(false, new(), new(), new(), null, ex.Message);
        }
    }

    private async Task<bool> DownloadBundleWithFallbackAsync(
        uint appId,
        string apiKey,
        string destinationZipPath,
        CancellationToken ct)
    {
        // Primary attempt: with ?force_update=true
        var primaryUrl = $"{BaseUrl}/manifest/{appId}?force_update=true";
        PlutoLogger.Info("HubcapManifest", $"Downloading manifest bundle from Hubcap: {primaryUrl}");

        if (await TryDownloadUrlAsync(primaryUrl, apiKey, destinationZipPath, ct))
        {
            return true;
        }

        // Fallback attempt: without force_update
        var fallbackUrl = $"{BaseUrl}/manifest/{appId}";
        PlutoLogger.Info("HubcapManifest", $"Retrying manifest bundle without force_update: {fallbackUrl}");
        return await TryDownloadUrlAsync(fallbackUrl, apiKey, destinationZipPath, ct);
    }

    private async Task<bool> TryDownloadUrlAsync(
        string url,
        string apiKey,
        string destinationZipPath,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                PlutoLogger.Warn("HubcapManifest", $"Hubcap request returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}) for {url}");
                return false;
            }

            var tempDownload = destinationZipPath + ".part";
            using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            using (var file = File.Create(tempDownload))
            {
                await stream.CopyToAsync(file, ct);
            }

            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            File.Move(tempDownload, destinationZipPath);
            PlutoLogger.Info("HubcapManifest", $"Saved manifest bundle to {destinationZipPath} ({new FileInfo(destinationZipPath).Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("HubcapManifest", $"Error downloading manifest from {url}: {ex.Message}");
            return false;
        }
    }
}
