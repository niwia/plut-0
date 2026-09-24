using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Implements mode transition protocols between ACCELA Managed mode
/// and AT0-M (Plugin Native) mode as specified in Section 4 &amp; 7B of HANDOVER.md.
/// </summary>
public class GameTransitionService
{
    private readonly SlsSteamService _slsService;
    private readonly PluginLibraryService _pluginService;

    public GameTransitionService(SlsSteamService slsService, PluginLibraryService pluginService)
    {
        _slsService = slsService;
        _pluginService = pluginService;
    }

    /// <summary>
    /// Converts a game to AT0-M Plugin Native mode:
    /// 1. Removes ACCELA marker folders (.ACCELA, .DepotDownloader)
    /// 2. In-place updates ~/.config/SLSsteam/config.yaml (preserving inode)
    /// 3. Registers in plugin_library.json
    /// 4. Ensures appmanifest_<appid>.acf has valid InstalledDepots and SizeOnDisk
    /// 5. Notifies SLSsteam to reload lua scripts
    /// </summary>
    public async Task<bool> ConvertToAtomPluginAsync(PluginGame game, string gameInstallPath, string acfPath)
    {
        try
        {
            // 1. Remove ACCELA markers
            string[] markers = { ".ACCELA", ".accela", ".DepotDownloader", ".depotdownloader" };
            foreach (var m in markers)
            {
                var p = Path.Combine(gameInstallPath, m);
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else if (File.Exists(p)) File.Delete(p);
            }

            // 2. Register in plugin_library.json
            game.Source = "plugin_native";
            game.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _pluginService.SaveGameAsync(game);

            // 3. Sync to SLS config.yaml (in-place)
            await _slsService.SyncGameToConfigAsync(game);

            // 4. Ensure appmanifest has InstalledDepots and non-zero SizeOnDisk
            long size = CalculateDirectorySize(gameInstallPath);
            SteamAcfService.EnsureAcfDepots(acfPath, game.AppId, game.Depots.ToArray(), Array.Empty<string>(), size);

            // 5. Reload SLS
            _slsService.NotifyReload();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameTransitionService] Error converting {game.AppId} to AT0-M: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Converts a game back to ACCELA Managed mode:
    /// 1. Re-creates .ACCELA marker folder
    /// 2. Unregisters from plugin_library.json
    /// 3. Cleans up non-shared depots/keys from SLS config.yaml
    /// 4. Notifies SLSsteam
    /// </summary>
    public async Task<bool> ConvertToAccelaManagedAsync(PluginGame game, string gameInstallPath)
    {
        try
        {
            // 1. Create .ACCELA marker
            var markerPath = Path.Combine(gameInstallPath, ".ACCELA");
            Directory.CreateDirectory(markerPath);

            // 2. Remove from plugin_library.json
            await _pluginService.RemoveGameAsync(game.AppId);

            // 3. Remove from SLS config
            var allGames = await _pluginService.LoadGamesAsync();
            await _slsService.RemoveGameFromConfigAsync(game, allGames);

            // 4. Reload SLS
            _slsService.NotifyReload();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameTransitionService] Error converting {game.AppId} to ACCELA: {ex.Message}");
            return false;
        }
    }

    private static long CalculateDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }
}
