using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pluto.Services;

public class SteamTagInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

public class SteamTagService
{
    private readonly Dictionary<string, SteamTagInfo> _tags = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "tags_cache.json");

    private static readonly string LocalHtmlPath = "/home/aiwin/Documents/pluto/Steam Game Tags · SteamDB.html";

    public SteamTagService()
    {
        InitializeTags();
    }

    private void InitializeTags()
    {
        // 1. Try loading from cache files
        string[] cacheLocations =
        {
            CacheFile,
            Path.Combine(AppContext.BaseDirectory, "tags_cache.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "tags_cache.json")
        };

        foreach (var cachePath in cacheLocations)
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    var json = File.ReadAllText(cachePath);
                    var list = JsonSerializer.Deserialize<List<SteamTagInfo>>(json);
                    if (list != null && list.Count > 0)
                    {
                        foreach (var tag in list)
                        {
                            if (!string.IsNullOrWhiteSpace(tag.Name))
                            {
                                _tags[tag.Name] = tag;
                            }
                        }
                        PlutoLogger.Info("SteamTagService", $"Loaded {_tags.Count} tags from cache {cachePath}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Warn("SteamTagService", $"Failed loading cache {cachePath}: {ex.Message}");
            }
        }

        // 2. Parse from SteamDB HTML file if cache doesn't exist yet
        string[] htmlLocations =
        {
            LocalHtmlPath,
            Path.Combine(AppContext.BaseDirectory, "Steam Game Tags · SteamDB.html"),
            Path.Combine(Directory.GetCurrentDirectory(), "Steam Game Tags · SteamDB.html")
        };

        foreach (var htmlPath in htmlLocations)
        {
            try
            {
                if (File.Exists(htmlPath))
                {
                    var content = File.ReadAllText(htmlPath);
                    var pattern = new Regex(@"<a[^>]+href=[""']/tag/(\d+)/[^""']*[""'][^>]*>(?:<span[^>]*>([^<]+)</span>)?\s*([^<]+)</a>");
                    var matches = pattern.Matches(content);

                    var list = new List<SteamTagInfo>();
                    foreach (Match m in matches)
                    {
                        var id = m.Groups[1].Value.Trim();
                        var icon = m.Groups[2].Value.Trim();
                        var name = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value.Trim());

                        if (!string.IsNullOrEmpty(name) && !_tags.ContainsKey(name))
                        {
                            var info = new SteamTagInfo { Id = id, Icon = icon, Name = name };
                            _tags[name] = info;
                            list.Add(info);
                        }
                    }

                    if (list.Count > 0)
                    {
                        try
                        {
                            var dir = Path.GetDirectoryName(CacheFile);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            var serialized = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(CacheFile, serialized);
                            PlutoLogger.Info("SteamTagService", $"Parsed {list.Count} tags from {htmlPath} and cached to {CacheFile}");
                        }
                        catch { }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Warn("SteamTagService", $"Failed to parse tags HTML from {htmlPath}: {ex.Message}");
            }
        }
    }

    public SteamTagInfo? GetTagInfo(string rawTagName)
    {
        if (string.IsNullOrWhiteSpace(rawTagName)) return null;
        if (_tags.TryGetValue(rawTagName.Trim(), out var info))
        {
            return info;
        }
        return null;
    }

    public string FormatTagWithIcon(string rawTagName)
    {
        var info = GetTagInfo(rawTagName);
        if (info != null && !string.IsNullOrEmpty(info.Icon))
        {
            return $"{info.Icon} {info.Name}";
        }
        return rawTagName.Trim();
    }
}
