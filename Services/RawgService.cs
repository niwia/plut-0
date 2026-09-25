using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Pluto.Services;

public class RawgGameMetadata
{
    public string Name { get; set; } = string.Empty;
    public string ReleaseYear { get; set; } = string.Empty;
    public int? MetacriticScore { get; set; }
    public double? Rating { get; set; }
    public string Genres { get; set; } = string.Empty;
    public string BackgroundImageUrl { get; set; } = string.Empty;
    public string DescriptionSnippet { get; set; } = string.Empty;

    public string SummaryLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ReleaseYear)) parts.Add(ReleaseYear);
            if (MetacriticScore.HasValue && MetacriticScore.Value > 0) parts.Add($"{MetacriticScore.Value} metacritic");
            if (Rating.HasValue && Rating.Value > 0) parts.Add($"{Rating.Value:0.0} rating");
            if (!string.IsNullOrEmpty(Genres)) parts.Add(Genres);
            return string.Join("  •  ", parts);
        }
    }
}

public class RawgService
{
    private readonly HttpClient _httpClient;
    private readonly AccelaConfigService _configService;
    private readonly Dictionary<string, RawgGameMetadata> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "rawg_cache");

    private static readonly string ImageCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "image_cache");

    private static readonly string ExternalKeyPath = "/home/aiwin/Documents/Antigravity IDE/rawg_api.txt";
    private static readonly string PlutoKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "pluto", "rawg_api.txt");

    public RawgService(AccelaConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        InitializeKey();
    }

    private void InitializeKey()
    {
        var existing = _configService.GetValue("rawg_api_key");
        if (string.IsNullOrWhiteSpace(existing))
        {
            // Seed from external private key file if available
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
        var key = _configService.GetValue("rawg_api_key");
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
        _configService.SetValue("rawg_api_key", trimmed);

        try
        {
            var dir = Path.GetDirectoryName(PlutoKeyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(PlutoKeyPath, trimmed);
        }
        catch
        {
            // Ignore file write errors
        }
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    /// <summary>
    /// Fetches game metadata from RAWG API with disk and memory caching.
    /// </summary>
    public async Task<RawgGameMetadata?> FetchMetadataAsync(string rawGameName, CancellationToken ct = default)
    {
        var cleanName = CleanGameTitle(rawGameName);
        if (string.IsNullOrWhiteSpace(cleanName)) return null;

        // 1. Check in-memory cache
        if (_memoryCache.TryGetValue(cleanName, out var cached))
        {
            return cached;
        }

        // 2. Check disk cache
        try
        {
            Directory.CreateDirectory(CacheDir);
            var safeFilename = Regex.Replace(cleanName, @"[^a-zA-Z0-9_\-]", "_") + ".json";
            var cacheFile = Path.Combine(CacheDir, safeFilename);

            if (File.Exists(cacheFile))
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFile, ct);
                var meta = JsonSerializer.Deserialize<RawgGameMetadata>(cachedJson);
                if (meta != null)
                {
                    _memoryCache[cleanName] = meta;
                    return meta;
                }
            }
        }
        catch
        {
            // Ignore disk cache read errors
        }

        // 3. Query RAWG API
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var uri = $"https://api.rawg.io/api/games?key={apiKey}&search={Uri.EscapeDataString(cleanName)}&page_size=1";
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var resultsElem) &&
                resultsElem.ValueKind == JsonValueKind.Array &&
                resultsElem.GetArrayLength() > 0)
            {
                var first = resultsElem[0];

                string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? cleanName : cleanName;
                string released = first.TryGetProperty("released", out var rel) ? rel.GetString() ?? "" : "";
                string releaseYear = string.Empty;
                if (!string.IsNullOrEmpty(released) && released.Length >= 4)
                {
                    releaseYear = released.Substring(0, 4);
                }

                int? metacritic = null;
                if (first.TryGetProperty("metacritic", out var mc) && mc.ValueKind == JsonValueKind.Number)
                {
                    metacritic = mc.GetInt32();
                }

                double? rating = null;
                if (first.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number)
                {
                    rating = rt.GetDouble();
                }

                var genresList = new List<string>();
                if (first.TryGetProperty("genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in gArr.EnumerateArray())
                    {
                        if (g.TryGetProperty("name", out var gn))
                        {
                            var gName = gn.GetString();
                            if (!string.IsNullOrEmpty(gName)) genresList.Add(gName);
                        }
                    }
                }

                string bgImg = string.Empty;
                if (first.TryGetProperty("background_image", out var bi) && bi.ValueKind == JsonValueKind.String)
                {
                    bgImg = bi.GetString() ?? "";
                }

                var metadata = new RawgGameMetadata
                {
                    Name = name,
                    ReleaseYear = releaseYear,
                    MetacriticScore = metacritic,
                    Rating = rating,
                    Genres = string.Join(", ", genresList.Take(3)),
                    BackgroundImageUrl = bgImg
                };

                _memoryCache[cleanName] = metadata;

                // Save to disk cache
                try
                {
                    var safeFilename = Regex.Replace(cleanName, @"[^a-zA-Z0-9_\-]", "_") + ".json";
                    var cacheFile = Path.Combine(CacheDir, safeFilename);
                    await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(metadata), ct);
                }
                catch
                {
                    // Ignore disk cache write errors
                }

                return metadata;
            }
        }
        catch
        {
            // Silently fall through on network failure
        }

        return null;
    }

    /// <summary>
    /// Downloads high-res backdrop image from RAWG and returns Avalonia Bitmap.
    /// </summary>
    public async Task<Bitmap?> FetchBackdropBitmapAsync(string url, string appId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            Directory.CreateDirectory(ImageCacheDir);
            var localFile = Path.Combine(ImageCacheDir, $"rawg_{appId}.jpg");

            if (File.Exists(localFile))
            {
                using var fs = File.OpenRead(localFile);
                return new Bitmap(fs);
            }

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
            // Fall back to null
        }

        return null;
    }

    private static string CleanGameTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var clean = Regex.Replace(raw, @"\b(GOTY|Game of the Year|Deluxe Edition|Remastered|Definitive Edition|Standard Edition|Enhanced Edition)\b", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\p{Cs}|\p{So}", ""); // Remove emoji
        return clean.Trim();
    }
}
