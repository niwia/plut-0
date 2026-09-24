using System;
using System.IO;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Handles mode transitions between ACCELA Managed mode and AT0-M (Plugin Native) mode.
/// As confirmed: SLSsteam only reads AdditionalApps, AdditionalDepots, and DecryptionKeys
/// in ~/.config/SLSsteam/config.yaml. Once injected and reloadlua is sent, SLSsteam unlocks
/// licenses and game files immediately. No disk crawling or folder sizing is needed.
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
    /// 2. In-place injects into ~/.config/SLSsteam/config.yaml (preserving inode)
    /// 3. Registers in plugin_library.json
    /// 4. Sends reloadlua to /tmp/SLSsteam.API
    /// </summary>
    public async Task<bool> ConvertToAtomPluginAsync(PluginGame game, string? gameInstallPath = null)
    {
        try
        {
            // 1. Remove ACCELA markers if path is known
            var path = !string.IsNullOrWhiteSpace(gameInstallPath) ? gameInstallPath : game.InstallPath;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                string[] markers = { ".ACCELA", ".accela", ".DepotDownloader", ".depotdownloader" };
                foreach (var m in markers)
                {
                    var p = Path.Combine(path, m);
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                    else if (File.Exists(p)) File.Delete(p);
                }
            }

            // 2. Register in plugin_library.json
            game.Source = "plugin_native";
            game.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _pluginService.SaveGameAsync(game);

            // 3. Cleanly inject AdditionalApps, AdditionalDepots, and DecryptionKeys into SLS config.yaml
            await _slsService.SyncGameToConfigAsync(game);

            // 4. Reload SLSsteam
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
    /// 4. Sends reloadlua to /tmp/SLSsteam.API
    /// </summary>
    public async Task<bool> ConvertToAccelaManagedAsync(PluginGame game, string? gameInstallPath = null)
    {
        try
        {
            // 1. Create .ACCELA marker if path is known
            var path = !string.IsNullOrWhiteSpace(gameInstallPath) ? gameInstallPath : game.InstallPath;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                var markerPath = Path.Combine(path, ".ACCELA");
                Directory.CreateDirectory(markerPath);
            }

            // 2. Remove from plugin_library.json
            await _pluginService.RemoveGameAsync(game.AppId);

            // 3. Remove from SLS config
            var allGames = await _pluginService.LoadGamesAsync();
            await _slsService.RemoveGameFromConfigAsync(game, allGames);

            // 4. Reload SLSsteam
            _slsService.NotifyReload();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameTransitionService] Error converting {game.AppId} to ACCELA: {ex.Message}");
            return false;
        }
    }
}
