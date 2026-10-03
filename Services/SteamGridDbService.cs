using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Pluto.Services;

public class SteamGridDbService
{
    private readonly HttpClient _httpClient;
    private readonly AccelaConfigService _configService;
    private readonly Dictionary<string, Bitmap> _memoryLogoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _memoryHeroCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string CacheDir = Path.Combine(PlutoPaths.AccelaData, "sgdb_cache");

    private static readonly string PlutoKeyPath = PlutoPaths.SteamGridDbKey;

    public SteamGridDbService(AccelaConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        InitializeKey();
    }

    

    private void InitializeKey()
    {
        if (string.IsNullOrWhiteSpace(_configService.GetValue("steamgriddb_api_key")))
        {
            // GetApiKey() falls back to the key file and persists it into the
            // ACCELA config, so just calling it performs the migration.
            GetApiKey();
        }
    }

    public string? GetApiKey()
    {
        var key = _configService.GetValue("steamgriddb_api_key");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        if (!File.Exists(PlutoKeyPath)) return null;

        try
        {
            key = File.ReadAllText(PlutoKeyPath).Trim();
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("SteamGridDb", $"Could not read API key from {PlutoKeyPath}: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(key)) return null;

        SetApiKey(key);
        return key;
    }

    public void SetApiKey(string key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        _configService.SetValue("steamgriddb_api_key", trimmed);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlutoKeyPath)!);
            File.WriteAllText(PlutoKeyPath, trimmed);
        }
        catch (Exception ex)
        {
            // The key is already in the ACCELA config, so lookups still work.
            PlutoLogger.Warn("SteamGridDb", $"Could not persist key file {PlutoKeyPath}: {ex.Message}");
        }
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    /// <summary>
    /// Fetches official transparent PNG logo for the game, caching to disk and memory.
    /// </summary>
    public async Task<Bitmap?> FetchLogoBitmapAsync(string appId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        // 1. Check in-memory cache
        if (_memoryLogoCache.TryGetValue(appId, out var memBmp))
        {
            return memBmp;
        }

        // 2. Check disk cache
        var logosDir = Path.Combine(CacheDir, "logos");
        var localFile = Path.Combine(logosDir, $"{appId}.png");
        try
        {
            if (File.Exists(localFile))
            {
                using var fs = File.OpenRead(localFile);
                var bmp = new Bitmap(fs);
                _memoryLogoCache[appId] = bmp;
                return bmp;
            }
        }
        catch (Exception ex)
        {
            // Corrupted cache entry; we fall through and re-fetch from the API.
            PlutoLogger.Warn("SteamGridDb", $"Discarding unreadable logo cache for {appId}: {ex.Message}");
        }

        // 3. Query SteamGridDB API
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var uri = $"https://www.steamgriddb.com/api/v2/logos/steam/{appId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
            {
                string? bestUrl = null;
                int bestPriority = 999;

                foreach (var item in dataElem.EnumerateArray())
                {
                    bool nsfw = item.TryGetProperty("nsfw", out var n) && n.GetBoolean();
                    if (nsfw) continue;

                    string lang = item.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(lang) && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string style = item.TryGetProperty("style", out var s) ? s.GetString() ?? "" : "";
                    string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(url)) continue;

                    int priority = style.ToLowerInvariant() switch
                    {
                        "official" => 0,
                        "white" => 1,
                        "custom" => 2,
                        _ => 3
                    };

                    if (priority < bestPriority)
                    {
                        bestPriority = priority;
                        bestUrl = url;
                        if (priority == 0) break; // Optimal found
                    }
                }

                if (!string.IsNullOrEmpty(bestUrl))
                {
                    var bytes = await _httpClient.GetByteArrayAsync(bestUrl, ct);
                    if (bytes.Length > 0)
                    {
                        Directory.CreateDirectory(logosDir);
                        await File.WriteAllBytesAsync(localFile, bytes, ct);
                        using var ms = new MemoryStream(bytes);
                        var bmp = new Bitmap(ms);
                        _memoryLogoCache[appId] = bmp;
                        return bmp;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("SteamGridDb", $"Could not load logo for app {appId}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Fetches high-res cinematic hero backdrop image for the game.
    /// </summary>
    public async Task<Bitmap?> FetchHeroBitmapAsync(string appId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        // 1. Check in-memory cache
        if (_memoryHeroCache.TryGetValue(appId, out var memBmp))
        {
            return memBmp;
        }

        // 2. Check disk cache
        var heroesDir = Path.Combine(CacheDir, "heroes");
        var localFile = Path.Combine(heroesDir, $"{appId}.jpg");
        try
        {
            if (File.Exists(localFile))
            {
                using var fs = File.OpenRead(localFile);
                var bmp = new Bitmap(fs);
                _memoryHeroCache[appId] = bmp;
                return bmp;
            }
        }
        catch (Exception ex)
        {
            // Corrupted cache entry; re-fetch from the API below.
            PlutoLogger.Warn("SteamGridDb", $"Discarding unreadable hero cache for {appId}: {ex.Message}");
        }

        // 3. Query SteamGridDB API
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var uri = $"https://www.steamgriddb.com/api/v2/heroes/steam/{appId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
            {
                foreach (var item in dataElem.EnumerateArray())
                {
                    bool nsfw = item.TryGetProperty("nsfw", out var n) && n.GetBoolean();
                    if (nsfw) continue;

                    string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(url))
                    {
                        var bytes = await _httpClient.GetByteArrayAsync(url, ct);
                        if (bytes.Length > 0)
                        {
                            Directory.CreateDirectory(heroesDir);
                            await File.WriteAllBytesAsync(localFile, bytes, ct);
                            using var ms = new MemoryStream(bytes);
                            var bmp = new Bitmap(ms);
                            _memoryHeroCache[appId] = bmp;
                            return bmp;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("SteamGridDb", $"Could not load hero art for app {appId}: {ex.Message}");
        }

        return null;
    }
}
