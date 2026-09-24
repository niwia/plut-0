using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

public class PluginLibraryService : IDisposable
{
    public static readonly string DefaultDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "plugin_library.json");

    public static readonly string GamesCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "games_cache.json");

    private readonly string _dbPath;
    private FileSystemWatcher? _watcher;
    private readonly JsonSerializerOptions _jsonOptions;

    public event Action? LibraryChanged;

    public PluginLibraryService(string? customDbPath = null)
    {
        _dbPath = customDbPath ?? DefaultDbPath;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        SetupWatcher();
    }

    public string DbPath => _dbPath;

    public bool Exists => File.Exists(_dbPath);

    private void SetupWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(_dbPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += (_, _) => OnFileChanged();
                _watcher.Created += (_, _) => OnFileChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLibraryService] Warning: Failed to setup file watcher: {ex.Message}");
        }
    }

    private DateTime _lastNotification = DateTime.MinValue;
    private void OnFileChanged()
    {
        if ((DateTime.UtcNow - _lastNotification).TotalMilliseconds < 300)
            return;

        _lastNotification = DateTime.UtcNow;
        LibraryChanged?.Invoke();
    }

    public async Task<List<PluginGame>> LoadGamesAsync()
    {
        if (!File.Exists(_dbPath))
        {
            return new List<PluginGame>();
        }

        try
        {
            await using var stream = File.OpenRead(_dbPath);
            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, PluginGame>>(stream, _jsonOptions);
            if (dict == null) return new List<PluginGame>();

            var list = dict.Values.ToList();

            // Enrich with games_cache.json if available
            await EnrichWithGamesCacheAsync(list);

            return list
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLibraryService] Error reading {_dbPath}: {ex.Message}");
            return new List<PluginGame>();
        }
    }

    private async Task EnrichWithGamesCacheAsync(List<PluginGame> games)
    {
        if (!File.Exists(GamesCachePath)) return;

        try
        {
            await using var stream = File.OpenRead(GamesCachePath);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.TryGetProperty("games", out var gamesArray) && gamesArray.ValueKind == JsonValueKind.Array)
            {
                var cacheByAppId = new Dictionary<string, JsonElement>();
                foreach (var el in gamesArray.EnumerateArray())
                {
                    if (el.TryGetProperty("appid", out var appidProp))
                    {
                        cacheByAppId[appidProp.GetString() ?? ""] = el;
                    }
                }

                foreach (var game in games)
                {
                    if (cacheByAppId.TryGetValue(game.AppId, out var el))
                    {
                        if (el.TryGetProperty("install_path", out var ip))
                            game.InstallPath = ip.GetString() ?? "";

                        if (el.TryGetProperty("appmanifest_path", out var ap))
                            game.AppmanifestPath = ap.GetString() ?? "";

                        if (el.TryGetProperty("is_accela_install", out var ai))
                            game.IsAccelaManaged = ai.GetBoolean();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLibraryService] Note: Failed to enrich from games_cache.json: {ex.Message}");
        }
    }

    public async Task<bool> SaveLibraryAsync(Dictionary<string, PluginGame> games)
    {
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _dbPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, games, _jsonOptions);
            }

            File.Move(tempPath, _dbPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PluginLibraryService] Error saving {_dbPath}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SaveGameAsync(PluginGame game)
    {
        var games = await LoadGamesDictionaryAsync();
        games[game.AppId] = game;
        return await SaveLibraryAsync(games);
    }

    public async Task<bool> RemoveGameAsync(string appId)
    {
        var games = await LoadGamesDictionaryAsync();
        if (games.Remove(appId))
        {
            return await SaveLibraryAsync(games);
        }
        return false;
    }

    private async Task<Dictionary<string, PluginGame>> LoadGamesDictionaryAsync()
    {
        if (!File.Exists(_dbPath))
        {
            return new Dictionary<string, PluginGame>();
        }

        try
        {
            await using var stream = File.OpenRead(_dbPath);
            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, PluginGame>>(stream, _jsonOptions);
            return dict ?? new Dictionary<string, PluginGame>();
        }
        catch
        {
            return new Dictionary<string, PluginGame>();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
