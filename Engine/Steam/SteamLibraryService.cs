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
    private static readonly Regex PathRegex = new(@"\""path\""\s*\""([^\""]+)\""", RegexOptions.Compiled);
    private static readonly Regex LabelRegex = new(@"\""label\""\s*\""([^\""]*)\""", RegexOptions.Compiled);

    public static List<SteamLibraryFolder> GetLibraryFolders()
    {
        var result = new List<SteamLibraryFolder>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultSteamapps = System.IO.Path.Combine(home, ".local", "share", "Steam", "steamapps");
        var vdfPath = System.IO.Path.Combine(defaultSteamapps, "libraryfolders.vdf");

        if (File.Exists(vdfPath))
        {
            try
            {
                var content = File.ReadAllText(vdfPath);
                // Matches library blocks e.g. "1" { "path" "..." "label" "..." }
                var blockRegex = new Regex(@"\""(\d+)\""\s*\{([^{}]+)\}", RegexOptions.Compiled | RegexOptions.Singleline);
                var matches = blockRegex.Matches(content);

                foreach (Match m in matches)
                {
                    int libIndex = int.TryParse(m.Groups[1].Value, out var li) ? li : 0;
                    var blockText = m.Groups[2].Value;
                    var pathMatch = PathRegex.Match(blockText);
                    if (pathMatch.Success)
                    {
                        var libPath = pathMatch.Groups[1].Value.Trim();
                        var labelMatch = LabelRegex.Match(blockText);
                        var rawLabel = labelMatch.Success ? labelMatch.Groups[1].Value.Trim() : "";

                        var steamappsPath = System.IO.Path.Combine(libPath, "steamapps");
                        var label = !string.IsNullOrWhiteSpace(rawLabel)
                            ? rawLabel
                            : (libPath.Contains("SDCARD", StringComparison.OrdinalIgnoreCase) ? "MicroSD Card" : "Internal Storage");

                        long freeBytes = GetFreeSpace(libPath);
                        var freeStr = FormatBytes(freeBytes);

                        result.Add(new SteamLibraryFolder(libPath, steamappsPath, $"{label} ({freeStr} free)", freeBytes, freeStr, libIndex));
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Warn("SteamLibraryService", $"Failed to parse libraryfolders.vdf: {ex.Message}");
            }
        }

        // Fallback: if none found or vdf missing, provide default
        if (result.Count == 0)
        {
            var defaultPath = System.IO.Path.Combine(home, ".local", "share", "Steam");
            long freeBytes = GetFreeSpace(defaultPath);
            var freeStr = FormatBytes(freeBytes);
            result.Add(new SteamLibraryFolder(defaultPath, defaultSteamapps, $"Internal Storage ({freeStr} free)", freeBytes, freeStr, 0));
        }

        return result;
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
        catch { }
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
