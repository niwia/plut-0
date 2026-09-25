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

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "sgdb_cache");

    private static readonly string ExternalKeyPath = "/home/aiwin/Documents/Antigravity IDE/steamgriddb_api.txt";
    private static readonly string PlutoKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "pluto", "steamgriddb_api.txt");

    public SteamGridDbService(AccelaConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        InitializeKey();
    }

    private void InitializeKey()
    {
        var existing = _configService.GetValue("steamgriddb_api_key");
        if (string.IsNullOrWhiteSpace(existing))
        {
            string? key = null;
            if (File.Exists(ExternalKeyPath))
            {
                key = File.ReadAllText(ExternalKeyPath).Trim();
            }
            else if (File.Exists(PlutoKeyPath))
            {
                key = File.ReadAllText(PlutoKeyPath).Trim();
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                SetApiKey(key);
            }
        }
    }

    public string? GetApiKey()
    {
        var key = _configService.GetValue("steamgriddb_api_key");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        if (File.Exists(ExternalKeyPath))
        {
            key = File.ReadAllText(ExternalKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                SetApiKey(key);
                return key;
            }
        }

        if (File.Exists(PlutoKeyPath))
        {
            key = File.ReadAllText(PlutoKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                SetApiKey(key);
                return key;
            }
        }

        return null;
    }

    public void SetApiKey(string key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        _configService.SetValue("steamgriddb_api_key", trimmed);

        try
        {
            var dir = Path.GetDirectoryName(PlutoKeyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(PlutoKeyPath, trimmed);
        }
        catch
        {
            // Ignore file write issues
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
        catch
        {
            // Ignore corrupted disk cache and re-fetch
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
        catch
        {
            // Silently return null on network/API failure
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
        catch
        {
            // Fall through
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
        catch
        {
            // Fall through
        }

        return null;
    }
}
