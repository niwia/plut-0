using System;
using System.Collections.Generic;
using System.IO;

namespace Pluto.Services;

/// <summary>
/// Single source of truth for every filesystem path Pluto touches.
///
/// Before this existed, paths were scattered across services as either
/// <c>Environment.SpecialFolder.UserProfile</c> concatenations or - worse -
/// hardcoded /home/aiwin/... literals that made the project non-portable.
/// Everything now resolves through here so a fresh clone on another machine,
/// another user, or a different install location works without edits.
/// </summary>
public static class PlutoPaths
{
    /// <summary>Current user's home directory, independent of the app's install location.</summary>
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Directory containing the running binary. Works for both `dotnet run` and publish output.</summary>
    public static string AppBase => AppContext.BaseDirectory;

    /// <summary>
    /// ACCELA shared data root (<c>~/.local/share/ACCELA</c>).
    /// Pluto reads ACCELA's databases, caches and bundled assets from here.
    /// </summary>
    public static string AccelaData =>
        Path.Combine(Home, ".local", "share", "ACCELA");

    /// <summary>Pluto's own shared data root (<c>~/.local/share/pluto</c>).</summary>
    public static string PlutoData =>
        Path.Combine(Home, ".local", "share", "pluto");

    /// <summary>Pluto's config directory (<c>~/.config/pluto</c>). Holds API keys.</summary>
    public static string PlutoConfig =>
        Path.Combine(Home, ".config", "pluto");

    /// <summary>Log directory (<c>~/.local/share/pluto/logs</c>).</summary>
    public static string Logs =>
        Path.Combine(PlutoData, "logs");

    // ---- ACCELA database files -------------------------------------------------

    /// <summary>Pluto/ASSella plugin library registry.</summary>
    public static string PluginLibraryDb =>
        Path.Combine(AccelaData, "db", "plugin_library.json");

    /// <summary>ACCELA's pre-parsed snapshot of every discovered game.</summary>
    public static string GamesCacheDb =>
        Path.Combine(AccelaData, "db", "games_cache.json");

    /// <summary>SQLite store of AES decryption keys keyed by AppID/DepotID.</summary>
    public static string DepotKeysDb =>
        Path.Combine(AccelaData, "db", "depot_keys.db");

    /// <summary>ACCELA's INI config (AT0-M / Vapor settings).</summary>
    public static string AccelaConf =>
        Path.Combine(Home, ".config", "Tachibana Labs", "ACCELA.conf");

    /// <summary>SLSsteam's live config. Rewritten in place to preserve the inode.</summary>
    public static string SlsConfig =>
        Path.Combine(Home, ".config", "SLSsteam", "config.yaml");

    // ---- IPC endpoints ---------------------------------------------------------

    /// <summary>
    /// SLSsteam command pipe. Not a real FIFO - a regular file SLSsteam polls.
    ///
    /// This is the only channel Pluto uses. The two-way "bridge" socket and
    /// command file (/tmp/assella_ipc.sock, /tmp/assella_cmd.json) described in
    /// earlier revisions were removed: assella_bridge.lua was never implemented
    /// on either side, so nothing bound the socket or read the file. SLSsteam
    /// detects config.yaml changes through its own inotify watcher instead.
    /// </summary>
    public static string SlsApiPipe => "/tmp/SLSsteam.API";

    // ---- API keys --------------------------------------------------------------

    public static string RawgKey => Path.Combine(PlutoConfig, "rawg_api.txt");
    public static string SteamGridDbKey => Path.Combine(PlutoConfig, "steamgriddb_api.txt");

    // ---- Bundled engine binaries ------------------------------------------------

    /// <summary>
    /// Locates the bundled DepotDownloaderMod assembly.
    /// Prefers the copy shipped next to the binary, then walks up looking for the
    /// source tree layout (so `dotnet run` from the repo root still works).
    /// </summary>
    public static string? DepotDownloaderDll
    {
        get
        {
            foreach (var candidate in DepotDownloaderCandidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }

    private static IEnumerable<string?> DepotDownloaderCandidates
    {
        get
        {
            // Published layout: Engine/DepotDownloader/Bin/DepotDownloader.dll
            yield return Path.Combine(AppBase, "Engine", "DepotDownloader", "Bin", "DepotDownloader.dll");
            // Flat publish fallback.
            yield return Path.Combine(AppBase, "DepotDownloader.dll");

            // `dotnet run` sets BaseDirectory to bin/Debug/net10.0/, so the source
            // tree is three levels up. Walk up looking for the Engine folder rather
            // than hardcoding a depth.
            var dir = new DirectoryInfo(AppBase);
            for (int i = 0; i < 6 && dir?.Parent != null; i++)
            {
                dir = dir.Parent;
                yield return Path.Combine(dir.FullName, "Engine", "DepotDownloader", "Bin", "DepotDownloader.dll");
            }
        }
    }

    /// <summary>
    /// Resolves a bundled resource, preferring the running app's directory and
    /// falling back to the current working directory.
    /// Replaces hardcoded absolute source-tree paths.
    /// </summary>
    public static string BundledResource(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppBase, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), fileName)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // Return the primary candidate so callers can produce a meaningful error message.
        return candidates[0];
    }

    /// <summary>Creates a directory if missing, swallowing IO errors.</summary>
    public static bool EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Paths", $"Could not create directory {path}: {ex.Message}");
            return false;
        }
    }
}