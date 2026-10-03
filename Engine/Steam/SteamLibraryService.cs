using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Pluto.Services;

namespace Pluto.Engine.Steam;

public readonly record struct SteamLibraryFolder(
    string Path,
    string SteamappsPath,
    string Label,
    long FreeSpaceBytes,
    string FreeSpaceString,
    int Index = 0
);

public static class SteamLibraryService
{
    public static List<SteamLibraryFolder> GetLibraryFolders()
    {
        var result = new List<SteamLibraryFolder>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultSteamapps = Path.Combine(home, ".local", "share", "Steam", "steamapps");
        var vdfPath = Path.Combine(defaultSteamapps, "libraryfolders.vdf");

        if (File.Exists(vdfPath))
        {
            try
            {
                foreach (var (libPath, rawLabel, libIndex) in ParseLibraryFolders(File.ReadAllText(vdfPath)))
                {
                    if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath)) continue;

                    var steamappsPath = Path.Combine(libPath, "steamapps");
                    var label = !string.IsNullOrWhiteSpace(rawLabel)
                        ? rawLabel
                        : GuessLabel(libPath);

                    long freeBytes = GetFreeSpace(libPath);
                    var freeStr = FormatBytes(freeBytes);

                    result.Add(new SteamLibraryFolder(libPath, steamappsPath, $"{label} ({freeStr} free)", freeBytes, freeStr, libIndex));
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Warn("SteamLibrary", $"Failed to parse libraryfolders.vdf: {ex.Message}");
            }
        }

        if (result.Count == 0)
        {
            PlutoLogger.Warn("SteamLibrary", "No Steam library folders discovered; defaulting to internal storage");
            var defaultPath = Path.Combine(home, ".local", "share", "Steam");
            long freeBytes = GetFreeSpace(defaultPath);
            var freeStr = FormatBytes(freeBytes);
            result.Add(new SteamLibraryFolder(defaultPath, defaultSteamapps, $"Internal Storage ({freeStr} free)", freeBytes, freeStr, 0));
        }

        return result;
    }

    /// <summary>
    /// Parses library blocks out of libraryfolders.vdf.
    ///
    /// This cannot be done with a regex: each library block contains a nested
    /// "apps" sub-block, so any `[^{}]+` character-class match fails to find a
    /// block body. On a real Steam install that returned zero libraries and
    /// silently fell back to internal storage, hiding every microSD and external
    /// drive. Brace counting handles the nesting correctly.
    /// </summary>
    private static List<(string Path, string Label, int Index)> ParseLibraryFolders(string content)
    {
        var found = new List<(string, string, int)>();
        CollectLibraryFolders(content, found, depth: 0);
        return found;
    }

    /// <summary>
    /// Recursively walks VDF blocks collecting numeric entries that carry a "path".
    ///
    /// The recursion is essential: the file is wrapped in a "libraryfolders" block,
    /// so a non-recursive scan consumes the entire document as one block body and
    /// never sees the library entries inside it.
    /// </summary>
    private static void CollectLibraryFolders(string content, List<(string Path, string Label, int Index)> found, int depth)
    {
        // Guard against pathological nesting in a malformed file.
        if (depth > 8) return;

        int pos = 0;
        while (pos < content.Length)
        {
            int keyStart = content.IndexOf('"', pos);
            if (keyStart < 0) break;

            int keyEnd = content.IndexOf('"', keyStart + 1);
            if (keyEnd < 0) break;

            string key = content[(keyStart + 1)..keyEnd];
            pos = keyEnd + 1;

            while (pos < content.Length && char.IsWhiteSpace(content[pos])) pos++;
            if (pos >= content.Length || content[pos] != '{') continue;

            // Walk to the matching close brace so nested blocks stay intact.
            int depthCounter = 0;
            int bodyStart = pos + 1;
            int i = pos;
            for (; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '{') depthCounter++;
                else if (c == '}')
                {
                    depthCounter--;
                    if (depthCounter == 0) break;
                }
            }

            if (i >= content.Length) break;
            string body = content[bodyStart..i];
            pos = i + 1;

            if (int.TryParse(key, out int libIndex))
            {
                string path = ExtractQuotedValue(body, "path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    found.Add((path.Trim(), ExtractQuotedValue(body, "label").Trim(), libIndex));
                }
            }
            else
            {
                // "libraryfolders" / "apps" and any other wrapper block.
                CollectLibraryFolders(body, found, depth + 1);
            }
        }
    }

    /// <summary>Returns the value of <paramref name="key"/> within a VDF block body, or empty.</summary>
    private static string ExtractQuotedValue(string blockBody, string key)
    {
        int idx = blockBody.IndexOf('"' + key + '"', StringComparison.Ordinal);
        if (idx < 0) return string.Empty;

        int openQuote = blockBody.IndexOf('"', idx + key.Length + 2);
        if (openQuote < 0) return string.Empty;

        int closeQuote = blockBody.IndexOf('"', openQuote + 1);
        if (closeQuote < 0) return string.Empty;

        return blockBody[(openQuote + 1)..closeQuote];
    }

    private static string GuessLabel(string libPath)
    {
        if (libPath.Contains("SDCARD", StringComparison.OrdinalIgnoreCase)) return "MicroSD Card";
        if (libPath.Contains("/run/media/", StringComparison.OrdinalIgnoreCase)) return "Removable Drive";
        if (libPath.Contains("/mnt/", StringComparison.OrdinalIgnoreCase)) return "Mounted Drive";
        return "Internal Storage";
    }

    private static long GetFreeSpace(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var drive = new DriveInfo(path);
                return drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex)
        {
            // Returning 0 blocks the download with a "no space" error, so log why.
            PlutoLogger.Warn("SteamLibrary", $"Could not query free space for {path}: {ex.Message}");
        }
        return 0;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int idx = 0;
        double d = bytes;
        while (d >= 1024.0 && idx < units.Length - 1)
        {
            d /= 1024.0;
            idx++;
        }
        return $"{d:0.#} {units[idx]}";
    }
}
