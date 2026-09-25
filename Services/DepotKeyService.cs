using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Pluto.Services;

/// <summary>
/// Direct SQLite service for querying AES decryption keys and depots from ACCELA's depot_keys.db.
/// Eliminates python subprocess overhead and performs lookups in microseconds.
/// </summary>
public class DepotKeyService
{
    public static readonly string DefaultDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "db", "depot_keys.db");

    private readonly string _dbPath;

    public DepotKeyService(string? dbPath = null)
    {
        _dbPath = dbPath ?? DefaultDbPath;
    }

    public bool Exists => File.Exists(_dbPath);

    /// <summary>
    /// Retrieves AES decryption keys for a specific AppID from depot_keys.db.
    /// Returns dictionary mapping depot_id -> 64-character lowercase hex AES key.
    /// </summary>
    public Dictionary<string, string> GetKeysForApp(string appId)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(_dbPath))
        {
            return result;
        }

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT depot_id, aes_key FROM depot_keys WHERE appid = @appid;";
            cmd.Parameters.AddWithValue("@appid", appId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var depotId = reader.GetString(0).Trim();
                var key = reader.GetString(1).Trim().ToLowerInvariant();

                // Validate depot is numeric and key is 64-char hex
                if (long.TryParse(depotId, out _) && key.Length == 64)
                {
                    result[depotId] = key;
                }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("DepotKeys", $"Failed to query depot keys for app {appId}", ex);
        }

        return result;
    }

    /// <summary>
    /// Retrieves all depot IDs known for an AppID from depot_keys.db.
    /// If no keys are registered, returns an empty list.
    /// </summary>
    public List<string> GetDepotsForApp(string appId)
    {
        var keys = GetKeysForApp(appId);
        return new List<string>(keys.Keys);
    }

    /// <summary>
    /// Gets the AppToken for an appid if available.
    /// </summary>
    public string? GetAppToken(string appId)
    {
        if (!File.Exists(_dbPath)) return null;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT token FROM app_tokens WHERE appid = @appid LIMIT 1;";
            cmd.Parameters.AddWithValue("@appid", appId);

            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                return result.ToString()?.Trim();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Generates a temporary depot keys file formatted for DepotDownloaderMod (-depotkeys).
    /// </summary>
    public async System.Threading.Tasks.Task<string?> CreateTempDepotKeysFileAsync(string appId, IEnumerable<string> depotIds)
    {
        var keys = GetKeysForApp(appId);
        var lines = new List<string>();

        foreach (var depotId in depotIds)
        {
            if (keys.TryGetValue(depotId, out var key))
            {
                lines.Add($"{depotId};{key}");
            }
        }

        if (lines.Count == 0) return null;

        var tempPath = Path.Combine(Path.GetTempPath(), $"pluto_keys_{appId}_{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempPath, lines);
        return tempPath;
    }

    /// <summary>
    /// Persists AES decryption keys and optional AppToken into depot_keys.db.
    /// Compatible with ASSella's SQLite schema and tables.
    /// </summary>
    public void SaveDepotKeys(string appId, IDictionary<string, string> keys, string? appToken = null)
    {
        if (keys.Count == 0 && string.IsNullOrWhiteSpace(appToken)) return;

        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using (var initCmd = conn.CreateCommand())
            {
                initCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS depot_keys (
                        appid TEXT NOT NULL,
                        depot_id TEXT NOT NULL,
                        aes_key TEXT NOT NULL,
                        updated_at INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (appid, depot_id)
                    );
                    CREATE TABLE IF NOT EXISTS app_tokens (
                        appid TEXT PRIMARY KEY,
                        token TEXT NOT NULL,
                        updated_at INTEGER NOT NULL DEFAULT 0
                    );";
                initCmd.ExecuteNonQuery();
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var tx = conn.BeginTransaction();

            foreach (var kvp in keys)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value)) continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO depot_keys (appid, depot_id, aes_key, updated_at)
                    VALUES (@appid, @depot_id, @aes_key, @updated_at)
                    ON CONFLICT(appid, depot_id) DO UPDATE SET
                        aes_key = excluded.aes_key,
                        updated_at = excluded.updated_at;";
                cmd.Parameters.AddWithValue("@appid", appId);
                cmd.Parameters.AddWithValue("@depot_id", kvp.Key.Trim());
                cmd.Parameters.AddWithValue("@aes_key", kvp.Value.Trim().ToLowerInvariant());
                cmd.Parameters.AddWithValue("@updated_at", now);
                cmd.ExecuteNonQuery();
            }

            if (!string.IsNullOrWhiteSpace(appToken))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO app_tokens (appid, token, updated_at)
                    VALUES (@appid, @token, @updated_at)
                    ON CONFLICT(appid) DO UPDATE SET
                        token = excluded.token,
                        updated_at = excluded.updated_at;";
                cmd.Parameters.AddWithValue("@appid", appId);
                cmd.Parameters.AddWithValue("@token", appToken.Trim());
                cmd.Parameters.AddWithValue("@updated_at", now);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            PlutoLogger.Info("DepotKeys", $"Saved {keys.Count} keys and token={(appToken != null ? "yes" : "no")} for AppID {appId} into depot_keys.db");
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("DepotKeys", $"Failed to save depot keys for app {appId}", ex);
        }
    }
}
