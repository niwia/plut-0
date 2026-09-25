using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Handles mode transitions between ACCELA Managed mode and AT0-M (Plugin Native) mode.
/// Pure C# implementation with zero Python dependency.
/// Note: SLSsteam only reads AdditionalApps, AdditionalDepots, and DecryptionKeys in
/// ~/.config/SLSsteam/config.yaml. Once injected and reloadlua is sent, SLSsteam unlocks
/// licenses and game files immediately. No disk crawling or folder sizing is needed.
/// </summary>
public class GameTransitionService
{
    private readonly SlsSteamService _slsService;
    private readonly PluginLibraryService _pluginService;
    private readonly DepotKeyService _depotKeyService;

    public GameTransitionService(
        SlsSteamService slsService,
        PluginLibraryService pluginService,
        DepotKeyService depotKeyService)
    {
        _slsService = slsService;
        _pluginService = pluginService;
        _depotKeyService = depotKeyService;
    }

    /// <summary>
    /// Converts a game to AT0-M Plugin Native mode:
    /// 1. Removes ACCELA marker folders (.ACCELA, .DepotDownloader)
    /// 2. Resolves AES decryption keys and depots from depot_keys.db
    /// 3. In-place injects into ~/.config/SLSsteam/config.yaml (preserving inode)
    /// 4. Registers in plugin_library.json
    /// 5. Validates Steam ACF manifest if present
    /// 6. Sends reloadlua to /tmp/SLSsteam.API
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

            // 2. Query AES keys & depots from depot_keys.db
            var dbKeys = _depotKeyService.GetKeysForApp(game.AppId);
            if (game.Keys == null || game.Keys.Count == 0)
            {
                game.Keys = dbKeys;
            }
            else
            {
                // Merge any newly found keys
                foreach (var (k, v) in dbKeys)
                {
                    game.Keys[k] = v;
                }
            }

            if (game.Depots == null || game.Depots.Count == 0)
            {
                game.Depots = game.Keys.Count > 0
                    ? game.Keys.Keys.OrderBy(d => d).ToList()
                    : new List<string> { game.AppId };
            }

            // 3. Register in plugin_library.json
            game.Source = "plugin_native";
            game.IsAccela = false;
            game.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _pluginService.SaveGameAsync(game);

            // 4. Cleanly inject AdditionalApps, AdditionalDepots, and DecryptionKeys into SLS config.yaml
            await _slsService.SyncGameToConfigAsync(game);

            // 5. Ensure ACF manifest has valid StateFlags & InstalledDepots (without disk crawling)
            if (!string.IsNullOrWhiteSpace(game.AppmanifestPath) && File.Exists(game.AppmanifestPath))
            {
                SteamAcfService.EnsureAcfDepots(game.AppmanifestPath, game.AppId, game.Depots.ToArray(), Array.Empty<string>(), 0);
            }

            PlutoLogger.Info("Transition", $"Successfully converted {game.AppId} ({game.Name}) to plugin native");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Transition", $"Error converting {game.AppId} to plugin native", ex);
            return false;
        }
    }

    /// <summary>
    /// Converts a game back to ACCELA Managed mode:
    /// 1. Re-creates .ACCELA marker folder
    /// 2. Unregisters from plugin_library.json
    /// 3. Cleans up non-shared depots/keys from SLS config.yaml (inotify FileWatcher handles reload automatically)
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
            game.IsAccela = true;
            game.Source = "ACCELA";

            // 3. Remove from SLS config
            var pluginDict = await _pluginService.LoadPluginLibraryDictAsync();
            var allOtherGames = pluginDict.Values.Where(g => g.AppId != game.AppId).ToList();
            await _slsService.RemoveGameFromConfigAsync(game, allOtherGames);

            PlutoLogger.Info("Transition", $"Successfully reverted {game.AppId} ({game.Name}) to assella managed mode");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Transition", $"Error converting {game.AppId} to ACCELA", ex);
            return false;
        }
    }
}
