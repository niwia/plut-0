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
    public static readonly string SlsConfigPath = PlutoPaths.SlsConfig;

    public static readonly string SlsApiPipe = PlutoPaths.SlsApiPipe;

    private static readonly Regex TopLevelSectionPattern = new(@"^[A-Za-z0-9_]+[ \t]*:", RegexOptions.Multiline);

    /// <summary>
    /// Config path this instance operates on. Defaults to the live SLSsteam
    /// config; overridable so the write logic can be tested against a copy
    /// instead of mutating the real file.
    /// </summary>
    private readonly string _configPath;

    public SlsSteamService(string? configPathOverride = null)
    {
        _configPath = configPathOverride ?? SlsConfigPath;
    }

    /// <summary>Effective config path for this instance.</summary>
    public string ConfigPath => _configPath;

    public bool IsSlsConfigPresent => File.Exists(_configPath);
    public bool IsSlsPipePresent => File.Exists(SlsApiPipe);

    /// <summary>
    /// Checks whether an AppID is currently listed in SLSsteam AdditionalApps.
    /// </summary>
    public bool IsAppConfigured(string appId) => GetConfiguredAppIds().Contains(appId);

    /// <summary>
    /// Returns every AppID currently listed in SLSsteam's AdditionalApps.
    ///
    /// Callers that need to know the sync state of a whole library must use this
    /// rather than <see cref="IsAppConfigured"/> per game: that method reads the
    /// entire config file on every call, so checking 188 games meant 188 reads of
    /// a 30KB file on the UI thread. This reads once and returns a lookup set.
    /// </summary>
    public HashSet<string> GetConfiguredAppIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(_configPath)) return ids;

        try
        {
            var content = File.ReadAllText(_configPath);
            var bounds = GetSectionBounds(content, "AdditionalApps");
            if (!bounds.HasValue) return ids;

            var section = content.Substring(bounds.Value.contentStart,
                                            bounds.Value.sectionEnd - bounds.Value.contentStart);

            // One pass over the section rather than one regex per candidate ID.
            foreach (Match m in ListEntryPattern.Matches(section))
            {
                var id = m.Groups["id"].Value.Trim();
                if (id.Length > 0) ids.Add(id);
            }
        }
        catch (Exception ex)
        {
            // A malformed config.yaml means "unknown", not "nothing configured".
            // An empty result here would wrongly mark every game unsynced.
            PlutoLogger.Warn("SLS", $"Could not read AdditionalApps from {_configPath}: {ex.Message}");
        }

        return ids;
    }

    /// <summary>Matches "- 12345" or "- 12345 # comment" entries in a list section.</summary>
    private static readonly Regex ListEntryPattern = new(
        @"^[ \t]*-[ \t]*(?<id>[^#\r\n]+?)[ \t]*(?:#[^\r\n]*)?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// In-place sync of a game's AppID, Depots, and DecryptionKeys into config.yaml.
    /// Preserves file inode and triggers inotify immediately.
    /// </summary>
    public async Task<bool> SyncGameToConfigAsync(PluginGame game)
    {
        if (!File.Exists(_configPath)) return false;

        // An AppID or DepotID that is not purely numeric is not an identifier. It
        // would be written straight into the config as structure, so reject it
        // before touching the file rather than after.
        var appId = YamlGuard.SanitizeId(game.AppId);
        if (appId == null)
        {
            PlutoLogger.Error("SLS", $"Refusing to sync {game.Name}: AppID '{game.AppId}' is not numeric");
            return false;
        }

        try
        {
            var content = await File.ReadAllTextAsync(_configPath);

            // 1. Ensure AdditionalApps contains the AppID
            content = EnsureAdditionalApp(content, appId, game.Name);

            // 2. Ensure AdditionalDepots contains the game's depots
            foreach (var depot in game.Depots)
            {
                if (YamlGuard.SanitizeId(depot) == null)
                {
                    PlutoLogger.Warn("SLS", $"Skipping non-numeric depot id '{depot}' for {game.Name}");
                    continue;
                }

                // Never write a base game AppID into AdditionalDepots. SLSsteam
                // treats it as a depot and it can conflict with the app it names.
                // Real data hits this: plugin_library.json lists Project Zomboid
                // (108600) with 108600 among its own depots.
                if (depot == appId)
                {
                    PlutoLogger.Warn("SLS",
                        $"Refusing to add base AppID '{appId}' to AdditionalDepots for {game.Name}");
                    continue;
                }

                // An ID already listed in AdditionalApps belongs there, not in
                // AdditionalDepots - unless it is DLC, which legitimately appears
                // in both sections.
                if (IsBaseAppInAdditionalApps(content, depot, out var appComment))
                {
                    PlutoLogger.Warn("SLS",
                        $"Refusing to add '{depot}' to AdditionalDepots: already in AdditionalApps as '{appComment}'");
                    continue;
                }

                game.DepotNames.TryGetValue(depot, out var dName);

                // Tag redistributables so their shared nature stays legible in the file.
                var comment = SharedDepotService.IsSharedRedistributable(depot)
                    ? SharedDepotService.SharedDepotComment
                    : (string.IsNullOrWhiteSpace(dName) ? $"{game.Name} ({depot})" : $"{game.Name} - {dName} ({depot})");

                content = EnsureAdditionalDepot(content, depot, comment);
            }

            // 3. Ensure DecryptionKeys contains keys
            foreach (var kvp in game.Keys)
            {
                var depot = kvp.Key;
                var key = kvp.Value;

                if (YamlGuard.SanitizeId(depot) == null)
                {
                    PlutoLogger.Warn("SLS", $"Skipping key for non-numeric depot id '{depot}' on {game.Name}");
                    continue;
                }

                game.DepotNames.TryGetValue(depot, out var dName);
                var comment = string.IsNullOrWhiteSpace(dName) ? game.Name : $"{game.Name} - {dName}";
                content = EnsureDecryptionKey(content, depot, key, comment);
            }

            // 4. Validated in-place write: keeps the inode so SLSsteam's inotify
            //    watcher still fires, and refuses to commit content that failed checks.
            if (!WriteInPlace(_configPath, content, $"sync {appId}"))
            {
                return false;
            }

            PlutoLogger.Info("SLS", $"In-place updated config.yaml for game {appId} ({game.Name})");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("SLS", $"Error syncing game {appId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes the AppID and unshared depots/keys from SLS config.yaml.
    ///
    /// A depot is only removed when nothing else needs it: no other registered game
    /// references it, no installed game's manifest lists it, and it isn't one of
    /// Steam's shared redistributable runtimes. The previous implementation checked
    /// only registered games, which could strip a redistributable or a depot a
    /// vanilla Steam install still relies on.
    /// </summary>
    public async Task<bool> RemoveGameFromConfigAsync(
        PluginGame game,
        List<PluginGame> allOtherGames,
        SharedDepotService? sharedDepots = null)
    {
        if (!File.Exists(_configPath)) return false;

        try
        {
            var content = await File.ReadAllTextAsync(_configPath);

            // Removing entries shrinks the file, so a backup is worth taking before
            // we rewrite it. Keys in particular are not otherwise recoverable.
            YamlGuard.TryBackup(_configPath);

            // Remove AppId
            content = RemoveAdditionalApp(content, game.AppId);

            // Gather remaining depots & keys from all other registered games
            var remainingDepots = new HashSet<string>(allOtherGames.Where(g => g.AppId != game.AppId).SelectMany(g => g.Depots));
            var remainingKeys = new HashSet<string>(allOtherGames.Where(g => g.AppId != game.AppId).SelectMany(g => g.Keys.Keys));

            var sharing = sharedDepots ?? new SharedDepotService();

            foreach (var depot in game.Depots)
            {
                if (remainingDepots.Contains(depot)) continue;

                if (sharing.IsDepotShared(depot, game.AppId))
                {
                    PlutoLogger.Info("SLS",
                        $"Keeping depot {depot} for {game.Name}: {sharing.DescribeSharing(depot, game.AppId)}");
                    continue;
                }

                content = RemoveAdditionalDepot(content, depot);
            }

            foreach (var depot in game.Keys.Keys)
            {
                if (remainingKeys.Contains(depot)) continue;

                // A redistributable depot's key is shared just as the depot is.
                if (sharing.IsDepotShared(depot, game.AppId))
                {
                    PlutoLogger.Info("SLS",
                        $"Keeping key {depot} for {game.Name}: {sharing.DescribeSharing(depot, game.AppId)}");
                    continue;
                }

                content = RemoveDecryptionKey(content, depot);
            }

            if (!WriteInPlace(_configPath, content, $"remove {game.AppId}"))
            {
                return false;
            }

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

    // Validates then writes in place, preserving the inode so SLSsteam's inotify
    // watcher keeps firing. Validation happens first: this file is read by
    // SLSsteam at startup and by ASSella, so a bad write is expensive to undo.
    private static bool WriteInPlace(string filePath, string content, string operation)
    {
        return YamlGuard.Commit(filePath, content, operation);
    }

    /// <summary>
    /// Locates the byte range for a top-level section in YAML.
    /// Returns headerStart, contentStart, and sectionEnd.
    /// </summary>
    private static (int headerStart, int contentStart, int sectionEnd)? GetSectionBounds(string content, string sectionName)
    {
        var bounds = YamlSections.GetSectionBounds(content, sectionName);
        return bounds.HasValue
            ? (bounds.Value.HeaderStart, bounds.Value.ContentStart, bounds.Value.SectionEnd)
            : null;
    }

    /// <summary>
    /// Adds or updates a list entry ("  - id # comment") in a top-level section,
    /// preserving the section's existing contents and comments.
    /// </summary>
    /// <summary>
    /// True when an ID appears in AdditionalApps as a base game rather than as DLC.
    ///
    /// DLC entries legitimately live in both AdditionalApps and AdditionalDepots, so
    /// the trailing comment is what distinguishes the two cases. ASSella applies the
    /// same test.
    /// </summary>
    private static bool IsBaseAppInAdditionalApps(string content, string id, out string comment)
    {
        comment = string.Empty;

        var bounds = GetSectionBounds(content, "AdditionalApps");
        if (!bounds.HasValue) return false;

        var (_, contentStart, sectionEnd) = bounds.Value;
        var section = content[contentStart..sectionEnd];

        var match = Regex.Match(
            section,
            $@"^[ \t]*-[ \t]*{Regex.Escape(id)}[ \t]*(?:#[ \t]*(?<c>[^\r\n]*))?$",
            RegexOptions.Multiline);

        if (!match.Success) return false;

        comment = match.Groups["c"].Value.Trim();
        return !comment.Contains("dlc", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureListEntry(string content, string sectionName, string id, string comment)
    {
        // A flow-style header ("Apps: []") must become block form first, or the
        // append below creates a second top-level key of the same name. YAML
        // resolves duplicates to the last occurrence, so the original contents
        // would be silently discarded.
        content = YamlSections.ExpandFlowSection(content, sectionName);
        content = YamlGuard.FixListIndentation(content, sectionName);

        var cleanComment = YamlGuard.SanitizeComment(comment);
        var entry = string.IsNullOrEmpty(cleanComment) ? $"  - {id}\n" : $"  - {id} # {cleanComment}\n";

        var bounds = GetSectionBounds(content, sectionName);
        if (!bounds.HasValue)
        {
            return content.TrimEnd() + $"\n\n{sectionName}:\n{entry}";
        }

        var (_, contentStart, sectionEnd) = bounds.Value;
        var secContent = content[contentStart..sectionEnd];

        var itemRegex = new Regex(
            $@"^[ \t]*-[ \t]*['""]?{Regex.Escape(id)}['""]?[ \t]*(?:#[^\r\n]*)?$",
            RegexOptions.Multiline);

        var existing = itemRegex.Match(secContent);
        if (existing.Success)
        {
            // Refresh the comment when we have one, so a renamed game stays legible.
            if (string.IsNullOrEmpty(cleanComment)) return content;

            // Match offsets are relative to secContent, and the end is relative to
            // the match start - not to the section start. Adding Length straight to
            // contentStart yields an end before the start on any section large
            // enough for the match to sit past offset 0, which throws.
            var absStart = contentStart + existing.Index;
            var absEnd = absStart + existing.Length;

            if (absEnd > content.Length) return content;

            var desired = $"  - {id} # {cleanComment}";
            if (content[absStart..absEnd] == desired) return content;

            return content[..absStart] + desired + content[absEnd..];
        }

        int insertPos = YamlSections.FindInsertPosition(content, bounds.Value);
        if (insertPos > 0 && content[insertPos - 1] != '\n')
        {
            entry = "\n" + entry;
        }

        return content[..insertPos] + entry + content[insertPos..];
    }

    /// <summary>
    /// Adds or updates a map entry ("  key: value # comment") in a top-level section.
    /// </summary>
    private static string EnsureMapEntry(string content, string sectionName, string key, string value, string comment)
    {
        content = YamlSections.ExpandFlowSection(content, sectionName);

        var cleanComment = YamlGuard.SanitizeComment(comment);
        var suffix = string.IsNullOrEmpty(cleanComment) ? "" : $" # {cleanComment}";
        var entry = $"  {key}: {value}{suffix}\n";

        var bounds = GetSectionBounds(content, sectionName);
        if (!bounds.HasValue)
        {
            return content.TrimEnd() + $"\n\n{sectionName}:\n{entry}";
        }

        var (_, contentStart, sectionEnd) = bounds.Value;
        var secContent = content[contentStart..sectionEnd];

        var mapRegex = new Regex(
            $@"^[ \t]*['""]?{Regex.Escape(key)}['""]?[ \t]*:[ \t]*(?<val>[^\r\n#]+?)[ \t]*(?:#(?<comm>[^\r\n]*))?$",
            RegexOptions.Multiline);

        var existing = mapRegex.Match(secContent);
        if (existing.Success)
        {
            // End offset is relative to the match start, not the section start.
            var absStart = contentStart + existing.Index;
            var absEnd = absStart + existing.Length;

            if (absEnd > content.Length) return content;

            // Keep the existing comment when we weren't given one.
            var finalComment = cleanComment;
            if (string.IsNullOrEmpty(finalComment))
            {
                finalComment = existing.Groups["comm"].Value.Trim();
            }

            var finalSuffix = string.IsNullOrEmpty(finalComment) ? "" : $" # {finalComment}";
            var desired = $"  {key}: {value}{finalSuffix}";

            if (content[absStart..absEnd] == desired) return content;
            return content[..absStart] + desired + content[absEnd..];
        }

        int insertPos = YamlSections.FindInsertPosition(content, bounds.Value);
        if (insertPos > 0 && content[insertPos - 1] != '\n')
        {
            entry = "\n" + entry;
        }

        return content[..insertPos] + entry + content[insertPos..];
    }

    /// <summary>
    /// Removes every matching entry from a section, repeatedly until none remain.
    /// A single pass can leave duplicates behind, which would keep the game
    /// unlocked after the user asked to remove it.
    /// </summary>
    private static string RemoveEntry(string content, string sectionName, string pattern)
    {
        var removed = true;
        while (removed)
        {
            removed = false;
            var bounds = GetSectionBounds(content, sectionName);
            if (!bounds.HasValue) break;

            var (_, contentStart, sectionEnd) = bounds.Value;
            var secContent = content[contentStart..sectionEnd];

            var match = Regex.Match(secContent, pattern, RegexOptions.Multiline);
            if (!match.Success) break;

            int absStart = contentStart + match.Index;

            int lineStart = content.LastIndexOf('\n', absStart);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int lineEnd = content.IndexOf('\n', absStart);
            lineEnd = lineEnd < 0 ? content.Length : lineEnd + 1;

            content = content[..lineStart] + content[lineEnd..];
            removed = true;
        }

        return content;
    }

    private static string EnsureAdditionalApp(string content, string appId, string comment)
    {
        return EnsureListEntry(content, "AdditionalApps", appId, comment);
    }

    private static string RemoveAdditionalApp(string content, string appId)
    {
        return RemoveEntry(content, "AdditionalApps",
            $@"^[ \t]*-[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*(?:#[^\r\n]*)?(\r?\n|$)");
    }

    private static string EnsureAdditionalDepot(string content, string depotId, string comment)
    {
        return EnsureListEntry(content, "AdditionalDepots", depotId, comment);
    }

    private static string RemoveAdditionalDepot(string content, string depotId)
    {
        return RemoveEntry(content, "AdditionalDepots",
            $@"^[ \t]*-[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*(?:#[^\r\n]*)?(\r?\n|$)");
    }

    private static string EnsureDecryptionKey(string content, string depotId, string key, string comment)
    {
        // An AES key that is not 64 hex characters is not a key. Writing one would
        // make SLSsteam fail to decrypt the depot with no useful diagnostic, so
        // reject it here where the log can explain why.
        if (!YamlGuard.IsValidAesKey(key))
        {
            PlutoLogger.Warn("SLS", $"Refusing to write malformed AES key for depot {depotId}");
            return content;
        }

        return EnsureMapEntry(content, "DecryptionKeys", depotId, key.Trim(), comment);
    }

    private static string RemoveDecryptionKey(string content, string depotId)
    {
        // Match the whole line rather than stopping at '#': a key entry carries a
        // trailing comment, and a pattern that excluded '#' only consumed up to it,
        // leaving the orphaned " # ..." tail behind in the config.
        return RemoveEntry(content, "DecryptionKeys",
            $@"^[ \t]*['""]?{Regex.Escape(depotId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)");
    }

    /// <summary>
    /// Checks whether SLSonline (FakeAppId 480 Spacewar) is currently enabled for an AppID.
    /// </summary>
    public bool IsSlsOnline(string appId)
    {
        if (!File.Exists(_configPath)) return false;
        try
        {
            var content = File.ReadAllText(_configPath);
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
        if (!File.Exists(_configPath)) return false;
        try
        {
            var content = await File.ReadAllTextAsync(_configPath);
            if (enable)
            {
                content = EnsureFakeAppId(content, appId, "480", $"{gameName} -> Spacewar");
            }
            else
            {
                content = RemoveFakeAppId(content, appId);
                content = RemoveLaunchOption(content, appId); // Disable netsock if online disabled
            }

            if (!WriteInPlace(_configPath, content, $"sls-online {appId}"))
            {
                return false;
            }

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
        if (!File.Exists(_configPath)) return false;
        try
        {
            var content = File.ReadAllText(_configPath);
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
        if (!File.Exists(_configPath)) return false;
        try
        {
            var content = await File.ReadAllTextAsync(_configPath);
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

            if (!WriteInPlace(_configPath, content, $"netsock {appId}"))
            {
                return false;
            }

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
        return EnsureMapEntry(content, "FakeAppIds", appId, fakeAppId, comment);
    }

    private static string RemoveFakeAppId(string content, string appId)
    {
        return RemoveEntry(content, "FakeAppIds",
            $@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)");
    }

    private static string EnsureLaunchOption(string content, string appId, string command)
    {
        // Command contains quotes and backslashes; quoting it as a YAML scalar
        // keeps it a single value instead of terminating on the embedded quote.
        var quoted = "\"" + command.Replace("\"", "\\\"") + "\"";
        return EnsureMapEntry(content, "LaunchOptions", appId, quoted, "");
    }

    private static string RemoveLaunchOption(string content, string appId)
    {
        return RemoveEntry(content, "LaunchOptions",
            $@"^[ \t]*['""]?{Regex.Escape(appId)}['""]?[ \t]*:[^\r\n]*(\r?\n|$)");
    }
}
