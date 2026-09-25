using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Direct, high-performance C# manager for SLSsteam configuration and IPC.
/// Guarantees in-place writes to ~/.config/SLSsteam/config.yaml so Linux inotify file watchers
/// are never invalidated by inode changes.
/// </summary>
public class SlsSteamService
{
    public static readonly string SlsConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "SLSsteam", "config.yaml");

    public const string SlsApiPipe = "/tmp/SLSsteam.API";

    private static readonly Regex TopLevelSectionPattern = new(@"^[A-Za-z0-9_]+[ \t]*:", RegexOptions.Multiline);

    public bool IsSlsConfigPresent => File.Exists(SlsConfigPath);
    public bool IsSlsPipePresent => File.Exists(SlsApiPipe);

    /// <summary>
    /// Checks whether an AppID is currently listed in SLSsteam AdditionalApps.
    /// </summary>
    public bool IsAppConfigured(string appId)
    {
        if (!File.Exists(SlsConfigPath)) return false;

        try
        {
            var content = File.ReadAllText(SlsConfigPath);
            var bounds = GetSectionBounds(content, "AdditionalApps");
            if (!bounds.HasValue) return false;

            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var pattern = new Regex($@"^[ \t]*-[ \t]*['""]?{Regex.Escape(appId)}['""]?(?:[ \t]*#.*)?$", RegexOptions.Multiline);
            return pattern.IsMatch(secContent);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// In-place sync of a game's AppID, Depots, and DecryptionKeys into config.yaml.
    /// Preserves file inode and triggers inotify immediately.
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

            // 4. In-place write to keep existing inode (SLSsteam inotify FileWatcher automatically detects IN_CLOSE_WRITE)
            await WriteInPlaceAsync(SlsConfigPath, content);
            PlutoLogger.Info("SLS", $"In-place updated config.yaml for game {game.AppId} ({game.Name})");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error syncing game {game.AppId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes the AppID and unshared depots/keys from SLS config.yaml.
    /// </summary>
    public async Task<bool> RemoveGameFromConfigAsync(PluginGame game, List<PluginGame> allOtherGames)
    {
        if (!File.Exists(SlsConfigPath)) return false;

        try
        {
            var content = await File.ReadAllTextAsync(SlsConfigPath);

            // Remove AppId
            content = RemoveAdditionalApp(content, game.AppId);

            // Gather remaining depots & keys from all other registered games
            var remainingDepots = new HashSet<string>(allOtherGames.Where(g => g.AppId != game.AppId).SelectMany(g => g.Depots));
            var remainingKeys = new HashSet<string>(allOtherGames.Where(g => g.AppId != game.AppId).SelectMany(g => g.Keys.Keys));

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
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error removing game {game.AppId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Sends 'reloadlua\n' command directly into /tmp/SLSsteam.API pipe.
    /// Used only when specifically requested to re-run Lua plugin scripts.
    /// Normal config changes are automatically detected by SLSsteam's inotify FileWatcher.
    /// </summary>
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

    /// <summary>
    /// Launches a game via Steam URL scheme steam://rungameid/{appId}.
    /// </summary>
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

    // In-place atomic file stream writer to preserve Linux inotify inodes
    private static async Task WriteInPlaceAsync(string filePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.Begin);
        await fs.WriteAsync(bytes, 0, bytes.Length);
        fs.SetLength(bytes.Length);
        fs.Flush(true); // Commits to disk immediately so IN_CLOSE_WRITE triggers with full contents
    }

    /// <summary>
    /// Locates the byte range for a top-level section in YAML.
    /// Returns headerStart, contentStart, and sectionEnd.
    /// </summary>
    private static (int headerStart, int contentStart, int sectionEnd)? GetSectionBounds(string content, string sectionName)
    {
        var headerRegex = new Regex($@"^[ \t]*{Regex.Escape(sectionName)}[ \t]*:[ \t]*(?:#[^\r\n]*)?$", RegexOptions.Multiline);
        var match = headerRegex.Match(content);
        if (!match.Success) return null;

        int headerStart = match.Index;
        int contentStart = match.Index + match.Length;
        if (contentStart < content.Length && content[contentStart] == '\r') contentStart++;
        if (contentStart < content.Length && content[contentStart] == '\n') contentStart++;

        var afterSection = content.Substring(contentStart);
        var nextMatch = TopLevelSectionPattern.Match(afterSection);
        int sectionEnd = nextMatch.Success ? contentStart + nextMatch.Index : content.Length;

        return (headerStart, contentStart, sectionEnd);
    }

    private static string EnsureAdditionalApp(string content, string appId, string comment)
    {
        var bounds = GetSectionBounds(content, "AdditionalApps");
        var entry = string.IsNullOrWhiteSpace(comment) ? $"  - {appId}\n" : $"  - {appId} # {comment.Trim()}\n";

        if (bounds.HasValue)
        {
            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var itemRegex = new Regex($@"^[ \t]*-[ \t]*['""]?{Regex.Escape(appId)}['""]?(?:[ \t]*#.*)?$", RegexOptions.Multiline);
            if (itemRegex.IsMatch(secContent))
            {
                return content;
            }

            int insertPos = bounds.Value.sectionEnd;
            if (insertPos > 0 && content[insertPos - 1] != '\n')
            {
                entry = "\n" + entry;
            }
            return content.Insert(insertPos, entry);
        }
        else
        {
            return content.TrimEnd() + $"\n\nAdditionalApps:\n{entry}";
        }
    }

    private static string RemoveAdditionalApp(string content, string appId)
    {
        var bounds = GetSectionBounds(content, "AdditionalApps");
        if (!bounds.HasValue) return content;

        var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
        var itemRegex = new Regex($@"^[ \t]*-[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*(?:#.*)?(\r?\n|$)", RegexOptions.Multiline);
        var newSecContent = itemRegex.Replace(secContent, "");

        return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
    }

    private static string EnsureAdditionalDepot(string content, string depotId, string comment)
    {
        var bounds = GetSectionBounds(content, "AdditionalDepots");
        var entry = string.IsNullOrWhiteSpace(comment) ? $"  - {depotId}\n" : $"  - {depotId} # {comment.Trim()}\n";

        if (bounds.HasValue)
        {
            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var itemRegex = new Regex($@"^[ \t]*-[ \t]*['""]?{Regex.Escape(depotId)}['""]?(?:[ \t]*#.*)?$", RegexOptions.Multiline);
            if (itemRegex.IsMatch(secContent))
            {
                return content;
            }

            int insertPos = bounds.Value.sectionEnd;
            if (insertPos > 0 && content[insertPos - 1] != '\n')
            {
                entry = "\n" + entry;
            }
            return content.Insert(insertPos, entry);
        }
        else
        {
            return content.TrimEnd() + $"\n\nAdditionalDepots:\n{entry}";
        }
    }

    private static string RemoveAdditionalDepot(string content, string depotId)
    {
        var bounds = GetSectionBounds(content, "AdditionalDepots");
        if (!bounds.HasValue) return content;

        var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
        var itemRegex = new Regex($@"^[ \t]*-[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*(?:#.*)?(\r?\n|$)", RegexOptions.Multiline);
        var newSecContent = itemRegex.Replace(secContent, "");

        return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
    }

    private static string EnsureDecryptionKey(string content, string depotId, string key, string comment)
    {
        var bounds = GetSectionBounds(content, "DecryptionKeys");
        var entry = string.IsNullOrWhiteSpace(comment) 
            ? $"  {depotId}: {key}\n" 
            : $"  {depotId}: {key} # {comment.Trim()}\n";

        if (bounds.HasValue)
        {
            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*:[ \t]*", RegexOptions.Multiline);
            if (itemRegex.IsMatch(secContent))
            {
                return content;
            }

            int insertPos = bounds.Value.sectionEnd;
            if (insertPos > 0 && content[insertPos - 1] != '\n')
            {
                entry = "\n" + entry;
            }
            return content.Insert(insertPos, entry);
        }
        else
        {
            return content.TrimEnd() + $"\n\nDecryptionKeys:\n{entry}";
        }
    }

    private static string RemoveDecryptionKey(string content, string depotId)
    {
        var bounds = GetSectionBounds(content, "DecryptionKeys");
        if (!bounds.HasValue) return content;

        var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
        var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*:[ \t]*[a-fA-F0-9]+[ \t]*(?:#.*)?(\r?\n|$)", RegexOptions.Multiline);
        var newSecContent = itemRegex.Replace(secContent, "");

        return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
    }

    /// <summary>
    /// Checks whether SLSonline (FakeAppId 480 Spacewar) is currently enabled for an AppID.
    /// </summary>
    public bool IsSlsOnline(string appId)
    {
        if (!File.Exists(SlsConfigPath)) return false;
        try
        {
            var content = File.ReadAllText(SlsConfigPath);
            var bounds = GetSectionBounds(content, "FakeAppIds");
            if (!bounds.HasValue) return false;

            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var pattern = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[ \t]*", RegexOptions.Multiline);
            return pattern.IsMatch(secContent);
        }
        catch { return false; }
    }

    /// <summary>
    /// Toggles SLSonline (FakeAppId 480 -> Spacewar) in config.yaml and reloads SLSsteam.
    /// </summary>
    public async Task<bool> SetSlsOnlineAsync(string appId, string gameName, bool enable)
    {
        if (!File.Exists(SlsConfigPath)) return false;
        try
        {
            var content = await File.ReadAllTextAsync(SlsConfigPath);
            if (enable)
            {
                content = EnsureFakeAppId(content, appId, "480", $"{gameName} -> Spacewar");
            }
            else
            {
                content = RemoveFakeAppId(content, appId);
                content = RemoveLaunchOption(content, appId); // Disable netsock if online disabled
            }

            await WriteInPlaceAsync(SlsConfigPath, content);
            PlutoLogger.Info("SLS", $"Set SLSonline for {appId} ({gameName}) to {enable}");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error setting SLSonline for {appId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Checks whether Netsock LD_AUDIT multiplayer proxy is configured in LaunchOptions for an AppID.
    /// </summary>
    public bool IsNetsock(string appId)
    {
        if (!File.Exists(SlsConfigPath)) return false;
        try
        {
            var content = File.ReadAllText(SlsConfigPath);
            var bounds = GetSectionBounds(content, "LaunchOptions");
            if (!bounds.HasValue) return false;

            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var pattern = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:.*netsock\.so", RegexOptions.Multiline);
            return pattern.IsMatch(secContent);
        }
        catch { return false; }
    }

    /// <summary>
    /// Ensures that netsock.so is present in ~/.config/SLSsteam/tools/netsock/netsock.so,
    /// copying from existing ACCELA candidates or downloading from upstream GitHub release.
    /// </summary>
    public static async Task<bool> EnsureNetsockBinaryAsync()
    {
        var targetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "SLSsteam", "tools", "netsock", "netsock.so");

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            return true;
        }

        // Check local candidate paths
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, ".local", "share", "SLSsteam", "tools", "netsock", "netsock.so"),
            Path.Combine(home, ".local", "share", "ACCELA", "tools", "netsock", "netsock.so"),
            Path.Combine(home, ".local", "share", "ACCELA", "squashfs-root", "bin", "src", "tools", "netsock", "netsock.so"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".config", "SLSsteam", "tools", "netsock", "netsock.so")
        };

        foreach (var cand in candidates)
        {
            if (File.Exists(cand) && new FileInfo(cand).Length > 0)
            {
                try
                {
                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(cand, targetPath, true);
                    PlutoLogger.Info("SLS", $"Copied netsock.so from candidate {cand} to {targetPath}");
                    return true;
                }
                catch (Exception ex)
                {
                    PlutoLogger.Warn("SLS", $"Could not copy candidate netsock.so: {ex.Message}");
                }
            }
        }

        // If still missing, download from GitHub releases
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pluto/1.0");
            var url = "https://github.com/yesyes0649/steamnetsock-patch/releases/download/latest/fix.so";
            var bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(targetPath, bytes);
                PlutoLogger.Info("SLS", $"Downloaded latest netsock.so ({bytes.Length} bytes) to {targetPath}");
                return true;
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", "Failed to download netsock.so from GitHub", ex);
        }

        return false;
    }

    /// <summary>
    /// Toggles Netsock LD_AUDIT multiplayer proxy in LaunchOptions in config.yaml and reloads SLSsteam.
    /// </summary>
    public async Task<bool> SetNetsockAsync(string appId, bool enable)
    {
        if (!File.Exists(SlsConfigPath)) return false;
        try
        {
            var content = await File.ReadAllTextAsync(SlsConfigPath);
            if (enable)
            {
                await EnsureNetsockBinaryAsync();
                var netsockPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "SLSsteam", "tools", "netsock", "netsock.so");
                var cmd = $"\"env LD_AUDIT=\\\"{netsockPath}\\\" %command%\"";
                content = EnsureLaunchOption(content, appId, cmd);
            }
            else
            {
                content = RemoveLaunchOption(content, appId);
            }

            await WriteInPlaceAsync(SlsConfigPath, content);
            PlutoLogger.Info("SLS", $"Set Netsock for {appId} to {enable}");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error setting Netsock for {appId}", ex);
            return false;
        }
    }

    private static string EnsureFakeAppId(string content, string appId, string fakeAppId, string comment)
    {
        var bounds = GetSectionBounds(content, "FakeAppIds");
        var entry = string.IsNullOrWhiteSpace(comment)
            ? $"  {appId}: {fakeAppId}\n"
            : $"  {appId}: {fakeAppId} # {comment.Trim()}\n";

        if (bounds.HasValue)
        {
            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[ \t]*", RegexOptions.Multiline);
            if (itemRegex.IsMatch(secContent))
            {
                // Already present, replace it
                var replaceRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)", RegexOptions.Multiline);
                var newSecContent = replaceRegex.Replace(secContent, entry, 1);
                return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
            }

            int insertPos = bounds.Value.sectionEnd;
            if (insertPos > 0 && content[insertPos - 1] != '\n')
            {
                entry = "\n" + entry;
            }
            return content.Insert(insertPos, entry);
        }
        else
        {
            return content.TrimEnd() + $"\n\nFakeAppIds:\n{entry}";
        }
    }

    private static string RemoveFakeAppId(string content, string appId)
    {
        var bounds = GetSectionBounds(content, "FakeAppIds");
        if (!bounds.HasValue) return content;

        var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
        var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)", RegexOptions.Multiline);
        var newSecContent = itemRegex.Replace(secContent, "");

        return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
    }

    private static string EnsureLaunchOption(string content, string appId, string command)
    {
        var bounds = GetSectionBounds(content, "LaunchOptions");
        var entry = $"  {appId}: {command}\n";

        if (bounds.HasValue)
        {
            var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
            var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[ \t]*", RegexOptions.Multiline);
            if (itemRegex.IsMatch(secContent))
            {
                var replaceRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)", RegexOptions.Multiline);
                var newSecContent = replaceRegex.Replace(secContent, entry, 1);
                return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
            }

            int insertPos = bounds.Value.sectionEnd;
            if (insertPos > 0 && content[insertPos - 1] != '\n')
            {
                entry = "\n" + entry;
            }
            return content.Insert(insertPos, entry);
        }
        else
        {
            return content.TrimEnd() + $"\n\nLaunchOptions:\n{entry}";
        }
    }

    private static string RemoveLaunchOption(string content, string appId)
    {
        var bounds = GetSectionBounds(content, "LaunchOptions");
        if (!bounds.HasValue) return content;

        var secContent = content.Substring(bounds.Value.contentStart, bounds.Value.sectionEnd - bounds.Value.contentStart);
        var itemRegex = new Regex($@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)", RegexOptions.Multiline);
        var newSecContent = itemRegex.Replace(secContent, "");

        return content.Substring(0, bounds.Value.contentStart) + newSecContent + content.Substring(bounds.Value.sectionEnd);
    }
}
