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
}
