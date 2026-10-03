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
/// Strongly-typed view of ACCELA's games_cache.json.
///
/// Previously this was walked with JsonDocument plus a family of GetXxxSafe
/// helpers. A typed model means a renamed or retyped field is a compile error
/// instead of a silently-defaulting field, and the retry wrapper can deserialize
/// the whole document at once.
/// </summary>
public sealed class GamesCacheFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("saved_at")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double SavedAt { get; set; }

    [JsonPropertyName("games")]
    public List<GamesCacheEntry> Games { get; set; } = new();
}

/// <summary>One entry in ACCELA's games_cache.json.</summary>
public sealed class GamesCacheEntry
{
    [JsonPropertyName("appid")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = string.Empty;

    [JsonPropertyName("install_dir")]
    public string InstallDir { get; set; } = string.Empty;

    [JsonPropertyName("install_path")]
    public string InstallPath { get; set; } = string.Empty;

    [JsonPropertyName("library_path")]
    public string LibraryPath { get; set; } = string.Empty;

    [JsonPropertyName("appmanifest_path")]
    public string AppmanifestPath { get; set; } = string.Empty;

    [JsonPropertyName("buildid")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("is_accela_install")]
    public bool IsAccelaInstall { get; set; }

    [JsonPropertyName("is_vapor")]
    public bool IsVapor { get; set; }

    [JsonPropertyName("is_atom")]
    public bool IsAtom { get; set; }

    [JsonPropertyName("is_plugin_game")]
    public bool IsPluginGame { get; set; }

    [JsonPropertyName("size_on_disk")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long SizeOnDisk { get; set; }

    [JsonPropertyName("last_updated")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long LastUpdated { get; set; }
}

/// <summary>
/// Dual-source game library manager for Pluto in native C#.
/// Reads both plugin_library.json and games_cache.json directly,
/// seamlessly merging AT0-M plugin native games and ACCELA managed games without python.
/// </summary>
public class PluginLibraryService : IDisposable
{
    public static readonly string DefaultDbPath = PlutoPaths.PluginLibraryDb;

    public static readonly string GamesCachePath = PlutoPaths.GamesCacheDb;

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
    /// Reads a JSON file, retrying briefly if it is caught mid-write.
    ///
    /// Both source files are rewritten by ASSella on every install/remove, and a
    /// watcher event can fire while the write is still in flight. A single failed
    /// read would otherwise log an error and silently drop the entire library from
    /// the UI until the next change.
    /// </summary>
    private static async Task<T> ReadJsonWithRetryAsync<T>(string path, JsonSerializerOptions options, string label)
        where T : class, new()
    {
        const int attempts = 4;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<T>(stream, options) ?? new T();
            }
            catch (JsonException) when (attempt < attempts)
            {
                // Truncated or partially flushed content; give the writer a moment.
                await Task.Delay(120 * attempt);
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(120 * attempt);
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Library", $"Error reading {path} ({label})", ex);
                return new T();
            }
        }

        PlutoLogger.Warn("Library", $"Gave up reading {path} ({label}) after {attempts} attempts");
        return new T();
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
            var dict = await ReadJsonWithRetryAsync<Dictionary<string, PluginGame>>(_dbPath, _jsonOptions, "plugin_library");
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

        // 2. Load ACCELA games from games_cache.json
        if (File.Exists(_cachePath))
        {
            var cache = await ReadJsonWithRetryAsync<GamesCacheFile>(_cachePath, _jsonOptions, "games_cache");

            if (cache.Games != null)
            {
                var gamesByAppId = result.ToDictionary(g => g.AppId, StringComparer.OrdinalIgnoreCase);

                foreach (var entry in cache.Games)
                {
                    var aid = entry.AppId;
                    if (string.IsNullOrWhiteSpace(aid)) continue;

                    bool isAccela = entry.IsAccelaInstall;
                    bool isAtom = entry.IsAtom || entry.IsVapor
                                  || string.Equals(entry.Source, "at0-m", StringComparison.OrdinalIgnoreCase);

                    if (gamesByAppId.TryGetValue(aid, out var existing))
                    {
                        // Enrich existing plugin game with filesystem paths
                        if (string.IsNullOrEmpty(existing.InstallPath) && !string.IsNullOrEmpty(entry.InstallPath))
                            existing.InstallPath = entry.InstallPath;

                        if (string.IsNullOrEmpty(existing.AppmanifestPath) && !string.IsNullOrEmpty(entry.AppmanifestPath))
                            existing.AppmanifestPath = entry.AppmanifestPath;

                        if (string.IsNullOrEmpty(existing.InstallDir) && !string.IsNullOrEmpty(entry.InstallDir))
                            existing.InstallDir = entry.InstallDir;
                    }
                    else if (isAccela || isAtom)
                    {
                        // Not in plugin_library.json: add it as an ACCELA or AT0-M managed game
                        seenAppIds.Add(aid);
                        result.Add(new PluginGame
                        {
                            AppId = aid,
                            Name = string.IsNullOrWhiteSpace(entry.GameName) ? $"App {aid}" : entry.GameName,
                            InstallDir = entry.InstallDir,
                            InstallPath = entry.InstallPath,
                            AppmanifestPath = entry.AppmanifestPath,
                            IsAccela = !isAtom && isAccela,
                            Source = isAtom ? "at0-m" : "ACCELA",
                            UpdatedAt = entry.LastUpdated
                        });
                    }
                }
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

    public void Dispose()
    {
        _dbWatcher?.Dispose();
        _cacheWatcher?.Dispose();
    }
}
