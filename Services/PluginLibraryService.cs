using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Dual-source game library manager for Pluto in native C#.
/// Reads both plugin_library.json and games_cache.json directly,
/// seamlessly merging AT0-M plugin native games and ACCELA managed games without python.
/// </summary>
public class PluginLibraryService : IDisposable
{
    public static readonly string DefaultDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "plugin_library.json");

    public static readonly string GamesCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "games_cache.json");

    private readonly string _dbPath;
    private readonly string _cachePath;
    private FileSystemWatcher? _dbWatcher;
    private FileSystemWatcher? _cacheWatcher;
    private readonly JsonSerializerOptions _jsonOptions;

    public event Action? LibraryChanged;

    public PluginLibraryService(string? customDbPath = null, string? customCachePath = null)
    {
        _dbPath = customDbPath ?? DefaultDbPath;
        _cachePath = customCachePath ?? GamesCachePath;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
        };

        SetupWatchers();
    }

    public string DbPath => _dbPath;
    public string CachePath => _cachePath;
    public bool Exists => File.Exists(_dbPath) || File.Exists(_cachePath);

    private void SetupWatchers()
    {
        try
        {
            var dbDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dbDir) && Directory.Exists(dbDir))
            {
                _dbWatcher = new FileSystemWatcher(dbDir, Path.GetFileName(_dbPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _dbWatcher.Changed += (_, _) => OnFileChanged();
                _dbWatcher.Created += (_, _) => OnFileChanged();
            }

            var cacheDir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
            {
                _cacheWatcher = new FileSystemWatcher(cacheDir, Path.GetFileName(_cachePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _cacheWatcher.Changed += (_, _) => OnFileChanged();
                _cacheWatcher.Created += (_, _) => OnFileChanged();
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Library", $"Failed to setup file watcher: {ex.Message}");
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

    /// <summary>
    /// Dual-source loads all AT0-M and ACCELA games natively.
    /// </summary>
    public async Task<List<PluginGame>> LoadGamesAsync()
    {
        var result = new List<PluginGame>();
        var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Load AT0-M plugin native games from plugin_library.json
        if (File.Exists(_dbPath))
        {
            try
            {
                await using var stream = File.OpenRead(_dbPath);
                var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, PluginGame>>(stream, _jsonOptions);
                if (dict != null)
                {
                    foreach (var (appId, game) in dict)
                    {
                        var aid = !string.IsNullOrWhiteSpace(game.AppId) ? game.AppId : appId;
                        game.AppId = aid;
                        game.IsAccela = false;
                        if (string.IsNullOrWhiteSpace(game.Name))
                        {
                            game.Name = $"App {aid}";
                        }
                        seenAppIds.Add(aid);
                        result.Add(game);
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Library", $"Error reading {_dbPath}", ex);
            }
        }

        // 2. Load ACCELA games from games_cache.json
        if (File.Exists(_cachePath))
        {
            try
            {
                await using var stream = File.OpenRead(_cachePath);
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("games", out var gamesArray) && gamesArray.ValueKind == JsonValueKind.Array)
                {
                    var gamesByAppId = result.ToDictionary(g => g.AppId, StringComparer.OrdinalIgnoreCase);

                    foreach (var el in gamesArray.EnumerateArray())
                    {
                        var aid = GetStringSafe(el, "appid");
                        if (string.IsNullOrWhiteSpace(aid)) continue;

                        string gameName = GetStringSafe(el, "game_name", $"App {aid}");
                        string installDir = GetStringSafe(el, "install_dir");
                        string installPath = GetStringSafe(el, "install_path");
                        string appmanifestPath = GetStringSafe(el, "appmanifest_path");
                        bool isAccela = GetBoolSafe(el, "is_accela_install");
                        bool isAtom = GetBoolSafe(el, "is_atom") || GetBoolSafe(el, "is_vapor") || GetStringSafe(el, "source").Equals("at0-m", StringComparison.OrdinalIgnoreCase);
                        long lastUpdated = GetLongSafe(el, "last_updated");

                        if (gamesByAppId.TryGetValue(aid, out var existing))
                        {
                            // Enrich existing plugin game with filesystem paths
                            if (string.IsNullOrEmpty(existing.InstallPath) && !string.IsNullOrEmpty(installPath))
                                existing.InstallPath = installPath;

                            if (string.IsNullOrEmpty(existing.AppmanifestPath) && !string.IsNullOrEmpty(appmanifestPath))
                                existing.AppmanifestPath = appmanifestPath;

                            if (string.IsNullOrEmpty(existing.InstallDir) && !string.IsNullOrEmpty(installDir))
                                existing.InstallDir = installDir;
                        }
                        else
                        {
                            // Not in plugin_library.json: Add if it's an ACCELA or AT0-M managed game
                            if (isAccela || isAtom)
                            {
                                seenAppIds.Add(aid);
                                var newGame = new PluginGame
                                {
                                    AppId = aid,
                                    Name = gameName,
                                    InstallDir = installDir,
                                    InstallPath = installPath,
                                    AppmanifestPath = appmanifestPath,
                                    IsAccela = !isAtom && isAccela,
                                    Source = isAtom ? "at0-m" : "ACCELA",
                                    UpdatedAt = lastUpdated
                                };
                                result.Add(newGame);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Library", $"Error enriching from {_cachePath}", ex);
            }
        }

        return result
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Loads only the games registered in plugin_library.json as a dictionary.
    /// </summary>
    public async Task<Dictionary<string, PluginGame>> LoadPluginLibraryDictAsync()
    {
        if (!File.Exists(_dbPath))
        {
            return new Dictionary<string, PluginGame>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(_dbPath);
            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, PluginGame>>(stream, _jsonOptions);
            return dict != null
                ? new Dictionary<string, PluginGame>(dict, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PluginGame>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Library", $"Error reading dictionary from {_dbPath}", ex);
            return new Dictionary<string, PluginGame>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Saves the plugin library dictionary to disk atomically.
    /// </summary>
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
                await stream.FlushAsync();
            }

            File.Move(tempPath, _dbPath, overwrite: true);
            PlutoLogger.Info("Library", $"Saved {games.Count} games to {_dbPath}");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Library", $"Error saving {_dbPath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Adds or updates a game record in plugin_library.json.
    /// </summary>
    public async Task<bool> SaveGameAsync(PluginGame game)
    {
        var games = await LoadPluginLibraryDictAsync();
        games[game.AppId] = game;
        return await SaveLibraryAsync(games);
    }

    /// <summary>
    /// Removes a game record from plugin_library.json.
    /// </summary>
    public async Task<bool> RemoveGameAsync(string appId)
    {
        var games = await LoadPluginLibraryDictAsync();
        if (games.Remove(appId))
        {
            return await SaveLibraryAsync(games);
        }
        return false;
    }

    private static string GetStringSafe(JsonElement el, string propName, string defaultVal = "")
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? defaultVal;
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetRawText();
        }
        return defaultVal;
    }

    private static bool GetBoolSafe(JsonElement el, string propName, bool defaultVal = false)
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return prop.GetBoolean();
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                return bool.TryParse(str, out var b) ? b : defaultVal;
            }
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.TryGetInt64(out var n) && n != 0;
            }
        }
        return defaultVal;
    }

    private static long GetLongSafe(JsonElement el, string propName, long defaultVal = 0)
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var numVal))
                return numVal;
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                return long.TryParse(str, out var strVal) ? strVal : defaultVal;
            }
        }
        return defaultVal;
    }

    public void Dispose()
    {
        _dbWatcher?.Dispose();
        _cacheWatcher?.Dispose();
    }
}
