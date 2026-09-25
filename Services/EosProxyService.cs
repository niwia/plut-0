using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Pluto.Services;

public enum EosProxyStatus
{
    None,     // Game has no EOS binaries
    Inactive, // Original EOS SDK present, proxy not active
    Active,   // Proxy is active (.yes exists and .dll matches proxy)
    Stale     // Proxy was applied earlier, but game was updated (.yes exists, .dll != proxy)
}

/// <summary>
/// Native C# port of ACCELA's eos_detector.py.
/// Detects Epic Online Services DLLs (EOSSDK-Win64-Shipping.dll) and manages
/// the community multiplayer proxy by swapping with .yes backup files.
/// </summary>
public class EosProxyService
{
    private static readonly string AccelaDepsProxyPath =
        "/home/aiwin/.local/share/ACCELA/squashfs-root/bin/src/deps/EOSSDK-Win64-Shipping.dll";

    private static readonly HashSet<string> PruneDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".depotdownloader", "__pycache__", "content", "paks", "pak", "assets",
        "sound", "sounds", "audio", "music", "media", "video", "videos", "movies",
        "textures", "cache", "saves", "screenshots", "shadercache", "streamingassets",
        "node_modules", ".git"
    };

    private string? _cachedProxyHash;

    public string? GetBundledProxyPath()
    {
        if (File.Exists(AccelaDepsProxyPath))
        {
            return AccelaDepsProxyPath;
        }

        var localDeps = Path.Combine(AppContext.BaseDirectory, "deps", "EOSSDK-Win64-Shipping.dll");
        if (File.Exists(localDeps))
        {
            return localDeps;
        }

        var cwdDeps = Path.Combine(Directory.GetCurrentDirectory(), "deps", "EOSSDK-Win64-Shipping.dll");
        if (File.Exists(cwdDeps))
        {
            return cwdDeps;
        }

        return null;
    }

    public string? GetProxyHash()
    {
        if (_cachedProxyHash != null) return _cachedProxyHash;
        var path = GetBundledProxyPath();
        if (path == null) return null;
        _cachedProxyHash = ComputeFileSha256(path);
        return _cachedProxyHash;
    }

    public static string? ComputeFileSha256(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds all EOS DLL or .yes instances in the game installation directory up to maxDepth.
    /// </summary>
    public List<string> FindEosFiles(string gameDirectory, int maxDepth = 4)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return found;
        }

        try
        {
            ScanDirectory(new DirectoryInfo(gameDirectory), 0, maxDepth, found);
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("EosProxy", $"Error scanning {gameDirectory}: {ex.Message}");
        }

        return found;
    }

    private void ScanDirectory(DirectoryInfo dir, int currentDepth, int maxDepth, List<string> found)
    {
        if (currentDepth >= maxDepth) return;

        try
        {
            foreach (var file in dir.GetFiles())
            {
                if (file.Name.Equals("EOSSDK-Win64-Shipping.dll", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.Equals("EOSSDK-Win64-Shipping.yes", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(file.FullName);
                }
            }

            foreach (var subDir in dir.GetDirectories())
            {
                if (subDir.Name.StartsWith(".") || PruneDirs.Contains(subDir.Name))
                {
                    continue;
                }
                ScanDirectory(subDir, currentDepth + 1, maxDepth, found);
            }
        }
        catch
        {
            // Ignore unauthorized access or IO issues
        }
    }

    /// <summary>
    /// Determines current EOS Proxy status for a game.
    /// </summary>
    public EosProxyStatus GetProxyStatus(string gameDirectory)
    {
        var files = FindEosFiles(gameDirectory);
        if (files.Count == 0) return EosProxyStatus.None;

        var proxyHash = GetProxyHash();

        var dirsWithEos = new Dictionary<string, (string? dll, string? yes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var pDir = Path.GetDirectoryName(f) ?? "";
            dirsWithEos.TryGetValue(pDir, out var entry);
            if (f.EndsWith(".yes", StringComparison.OrdinalIgnoreCase))
            {
                entry.yes = f;
            }
            else if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                entry.dll = f;
            }
            dirsWithEos[pDir] = entry;
        }

        bool hasAnyYes = false;
        bool hasAnyDll = false;
        foreach (var kvp in dirsWithEos)
        {
            if (kvp.Value.yes != null) hasAnyYes = true;
            if (kvp.Value.dll != null) hasAnyDll = true;
        }

        if (!hasAnyYes)
        {
            return hasAnyDll ? EosProxyStatus.Inactive : EosProxyStatus.None;
        }

        foreach (var kvp in dirsWithEos)
        {
            if (kvp.Value.yes != null)
            {
                if (kvp.Value.dll == null || !File.Exists(kvp.Value.dll))
                {
                    return EosProxyStatus.Stale;
                }
                if (proxyHash != null)
                {
                    var dllHash = ComputeFileSha256(kvp.Value.dll);
                    if (dllHash != proxyHash)
                    {
                        return EosProxyStatus.Stale;
                    }
                }
            }
        }

        return EosProxyStatus.Active;
    }

    /// <summary>
    /// Applies or reapplies the EOS proxy DLL.
    /// Renames original EOSSDK-Win64-Shipping.dll to EOSSDK-Win64-Shipping.yes and copies proxy.
    /// </summary>
    public async Task<bool> ApplyProxyAsync(string gameDirectory)
    {
        var proxySrc = GetBundledProxyPath();
        if (proxySrc == null || !File.Exists(proxySrc))
        {
            PlutoLogger.Error("EosProxy", "Bundled EOS proxy DLL not found");
            return false;
        }

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var files = FindEosFiles(gameDirectory);
                bool applied = false;

                var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var dir = Path.GetDirectoryName(f);
                    if (!string.IsNullOrEmpty(dir)) parentDirs.Add(dir);
                }

                foreach (var dir in parentDirs)
                {
                    var dllTarget = Path.Combine(dir, "EOSSDK-Win64-Shipping.dll");
                    var yesTarget = Path.Combine(dir, "EOSSDK-Win64-Shipping.yes");

                    if (File.Exists(dllTarget))
                    {
                        if (File.Exists(yesTarget))
                        {
                            try { File.Delete(yesTarget); } catch { }
                        }
                        File.Move(dllTarget, yesTarget);
                        File.Copy(proxySrc, dllTarget, true);
                        applied = true;
                    }
                    else if (File.Exists(yesTarget) && !File.Exists(dllTarget))
                    {
                        File.Copy(proxySrc, dllTarget, true);
                        applied = true;
                    }
                }

                PlutoLogger.Info("EosProxy", $"Applied EOS proxy in {gameDirectory}: result={applied}");
                return applied;
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("EosProxy", $"Failed applying EOS proxy in {gameDirectory}", ex);
                return false;
            }
        });
    }

    /// <summary>
    /// Removes the EOS proxy and restores the original DLL from .yes backup.
    /// </summary>
    public async Task<bool> RemoveProxyAsync(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var files = FindEosFiles(gameDirectory);
                bool removed = false;

                var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var dir = Path.GetDirectoryName(f);
                    if (!string.IsNullOrEmpty(dir)) parentDirs.Add(dir);
                }

                foreach (var dir in parentDirs)
                {
                    var dllTarget = Path.Combine(dir, "EOSSDK-Win64-Shipping.dll");
                    var yesTarget = Path.Combine(dir, "EOSSDK-Win64-Shipping.yes");

                    if (File.Exists(yesTarget))
                    {
                        if (File.Exists(dllTarget))
                        {
                            try { File.Delete(dllTarget); } catch { }
                        }
                        File.Move(yesTarget, dllTarget);
                        removed = true;
                    }
                }

                PlutoLogger.Info("EosProxy", $"Removed EOS proxy in {gameDirectory}: result={removed}");
                return removed;
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("EosProxy", $"Failed removing EOS proxy in {gameDirectory}", ex);
                return false;
            }
        });
    }
}
