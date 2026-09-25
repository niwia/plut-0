using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

public class SlsSteamService
{
    public static readonly string SlsConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "SLSsteam", "config.yaml");

    public const string SlsApiPipe = "/tmp/SLSsteam.API";

    public bool IsSlsConfigPresent => File.Exists(SlsConfigPath);
    public bool IsSlsPipePresent => File.Exists(SlsApiPipe);

    /// <summary>
    /// Checks whether an AppID is currently configured in SLSsteam AdditionalApps.
    /// </summary>
    public bool IsAppConfigured(string appId)
    {
        if (!File.Exists(SlsConfigPath)) return false;

        try
        {
            var content = File.ReadAllText(SlsConfigPath);
            // Look for AdditionalApps section and check if appId is listed
            var regex = new Regex($@"^\s*-\s*['""]?{Regex.Escape(appId)}['""]?\s*(?:#.*)?$", RegexOptions.Multiline);
            return regex.IsMatch(content);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// In-place sync of a game's AppID, Depots, and DecryptionKeys into ~/.config/SLSsteam/config.yaml,
    /// preserving the file inode so SLSsteam's inotify watcher triggers immediately.
    /// </summary>
    public async Task<bool> SyncGameToConfigAsync(PluginGame game)
    {
        if (!File.Exists(SlsConfigPath)) return false;

        try
        {
            var content = await File.ReadAllTextAsync(SlsConfigPath);

            // 1. Ensure AdditionalApps contains the AppID
            content = EnsureAdditionalApp(content, game.AppId, game.Name);

            // 2. Ensure AdditionalDepots contains the game's depots
            foreach (var depot in game.Depots)
            {
                game.DepotNames.TryGetValue(depot, out var dName);
                var comment = string.IsNullOrWhiteSpace(dName) ? $"{game.Name} ({depot})" : $"{game.Name} - {dName} ({depot})";
                content = EnsureAdditionalDepot(content, depot, comment);
            }

            // 3. Ensure DecryptionKeys contains keys
            foreach (var kvp in game.Keys)
            {
                var depot = kvp.Key;
                var key = kvp.Value;
                game.DepotNames.TryGetValue(depot, out var dName);
                var comment = string.IsNullOrWhiteSpace(dName) ? game.Name : $"{game.Name} - {dName}";
                content = EnsureDecryptionKey(content, depot, key, comment);
            }

            // 4. In-place write to keep existing inode
            await WriteInPlaceAsync(SlsConfigPath, content);
            PlutoLogger.Info("SLS", $"In-place updated config.yaml for game {game.AppId} ({game.Name})");

            // 5. Notify SLSsteam via API pipe
            NotifyReload();
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error syncing game {game.AppId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes the AppID and non-shared depots/keys from SLS config.yaml.
    /// </summary>
    public async Task<bool> RemoveGameFromConfigAsync(PluginGame game, List<PluginGame> allOtherGames)
    {
        if (!File.Exists(SlsConfigPath)) return false;

        try
        {
            var content = await File.ReadAllTextAsync(SlsConfigPath);

            // Remove AppId
            content = RemoveAdditionalApp(content, game.AppId);

            // Gather remaining depots & keys
            var remainingDepots = new HashSet<string>(allOtherGames.SelectMany(g => g.Depots));
            var remainingKeys = new HashSet<string>(allOtherGames.SelectMany(g => g.Keys.Keys));

            foreach (var depot in game.Depots)
            {
                if (!remainingDepots.Contains(depot))
                {
                    content = RemoveAdditionalDepot(content, depot);
                }
            }

            foreach (var depot in game.Keys.Keys)
            {
                if (!remainingKeys.Contains(depot))
                {
                    content = RemoveDecryptionKey(content, depot);
                }
            }

            await WriteInPlaceAsync(SlsConfigPath, content);
            PlutoLogger.Info("SLS", $"In-place removed game {game.AppId} ({game.Name}) from config.yaml");
            NotifyReload();
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error removing game {game.AppId}", ex);
            return false;
        }
    }

    public bool NotifyReload()
    {
        try
        {
            if (File.Exists(SlsApiPipe))
            {
                File.WriteAllText(SlsApiPipe, "reloadlua\n");
                PlutoLogger.Info("SLS", $"Sent 'reloadlua' to {SlsApiPipe}");
                return true;
            }
            else
            {
                PlutoLogger.Warn("SLS", $"API pipe {SlsApiPipe} does not exist");
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", "Failed to write reloadlua to API pipe", ex);
        }
        return false;
    }

    public bool TriggerInstallPipe(string appId, int libraryIndex = 0)
    {
        try
        {
            if (File.Exists(SlsApiPipe))
            {
                File.WriteAllText(SlsApiPipe, $"install|{appId}|{libraryIndex}\n");
                PlutoLogger.Info("SLS", $"Sent install command for {appId} (lib: {libraryIndex}) to {SlsApiPipe}");
                return true;
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Failed to send install command for {appId}", ex);
        }
        return false;
    }

    public bool LaunchGame(string appId)
    {
        try
        {
            PlutoLogger.Info("Launcher", $"Launching steam://rungameid/{appId}");
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"steam://rungameid/{appId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Launcher", $"Failed to launch steam://rungameid/{appId}", ex);
            return false;
        }
    }

    private static async Task WriteInPlaceAsync(string filePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.Begin);
        await fs.WriteAsync(bytes, 0, bytes.Length);
        fs.SetLength(bytes.Length);
        await fs.FlushAsync();
    }

    private static string EnsureAdditionalApp(string content, string appId, string comment)
    {
        var pattern = $@"^\s*-\s*['""]?{Regex.Escape(appId)}['""]?";
        if (Regex.IsMatch(content, pattern, RegexOptions.Multiline))
            return content;

        var sectionMatch = Regex.Match(content, @"^(AdditionalApps:\s*\n)", RegexOptions.Multiline);
        var entry = string.IsNullOrWhiteSpace(comment) ? $"  - {appId}\n" : $"  - {appId} # {comment}\n";

        if (sectionMatch.Success)
        {
            return content.Insert(sectionMatch.Index + sectionMatch.Length, entry);
        }
        else
        {
            return content + $"\nAdditionalApps:\n{entry}";
        }
    }

    private static string RemoveAdditionalApp(string content, string appId)
    {
        var pattern = $@"^[ \t]*-[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*(?:#.*)?\r?\n";
        return Regex.Replace(content, pattern, "", RegexOptions.Multiline);
    }

    private static string EnsureAdditionalDepot(string content, string depotId, string comment)
    {
        var pattern = $@"^\s*-\s*['""]?{Regex.Escape(depotId)}['""]?";
        if (Regex.IsMatch(content, pattern, RegexOptions.Multiline))
            return content;

        var sectionMatch = Regex.Match(content, @"^(AdditionalDepots:\s*\n)", RegexOptions.Multiline);
        var entry = string.IsNullOrWhiteSpace(comment) ? $"  - {depotId}\n" : $"  - {depotId} # {comment}\n";

        if (sectionMatch.Success)
        {
            return content.Insert(sectionMatch.Index + sectionMatch.Length, entry);
        }
        else
        {
            return content + $"\nAdditionalDepots:\n{entry}";
        }
    }

    private static string RemoveAdditionalDepot(string content, string depotId)
    {
        var pattern = $@"^[ \t]*-[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*(?:#.*)?\r?\n";
        return Regex.Replace(content, pattern, "", RegexOptions.Multiline);
    }

    private static string EnsureDecryptionKey(string content, string depotId, string key, string comment)
    {
        var pattern = $@"^\s*['""]?{Regex.Escape(depotId)}['""]?\s*:\s*";
        if (Regex.IsMatch(content, pattern, RegexOptions.Multiline))
            return content;

        var sectionMatch = Regex.Match(content, @"^(DecryptionKeys:\s*\n)", RegexOptions.Multiline);
        var entry = string.IsNullOrWhiteSpace(comment) 
            ? $"  {depotId}: {key}\n" 
            : $"  {depotId}: {key} # {comment}\n";

        if (sectionMatch.Success)
        {
            return content.Insert(sectionMatch.Index + sectionMatch.Length, entry);
        }
        else
        {
            return content + $"\nDecryptionKeys:\n{entry}";
        }
    }

    private static string RemoveDecryptionKey(string content, string depotId)
    {
        var pattern = $@"^[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*:[ \t]*[a-fA-F0-9]+[ \t]*(?:#.*)?\r?\n";
        return Regex.Replace(content, pattern, "", RegexOptions.Multiline);
    }
}
