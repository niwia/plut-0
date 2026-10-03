using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pluto.Engine.Steam;

namespace Pluto.Services;

/// <summary>
/// Decides whether a depot may be safely removed from SLSsteam's config.yaml.
///
/// Ported from ASSella's <c>is_depot_shared_with_other_games</c>, which Pluto never picked up.
/// The removal path used to consider a depot unshared whenever no other game in
/// <c>plugin_library.json</c> referenced it. That misses two cases, both of which break
/// games the user still has installed:
///
///   1. Shared redistributable depots (Steamworks common runtime, VC++, DirectX). These
///      belong to Steam rather than to any one game, and stripping them can break
///      unrelated titles.
///   2. Depots referenced by a game installed on disk but absent from the plugin library.
///      A vanilla Steam install that Pluto never registered is invisible to the
///      library-only check.
///
/// This service answers from all three sources before a depot is removed.
/// </summary>
public sealed class SharedDepotService
{
    /// <summary>
    /// Shared redistributable depot IDs owned by Steam rather than by any single game.
    /// Ported verbatim from ASSella's shared-redistributable set.
    /// </summary>
    private static readonly HashSet<string> SharedRedistributables = new(StringComparer.Ordinal)
    {
        "228980", "1034630",
        "228981", "228982", "228983", "228984", "228985",
        "228986", "228987", "228988", "228989", "228990",
        "229000", "229001", "229002", "229003", "229004",
        "229005", "229006", "229007",
        "229010", "229011", "229012",
        "229020", "229030", "229031", "229032"
    };

    /// <summary>Comment ASSella stamps onto redistributables so their purpose stays obvious.</summary>
    public const string SharedDepotComment = "Steamworks Shared";

    private readonly PluginLibraryService? _libraryService;

    public SharedDepotService(PluginLibraryService? libraryService = null)
    {
        _libraryService = libraryService;
    }

    /// <summary>True when the ID is a known shared redistributable.</summary>
    public static bool IsSharedRedistributable(string depotId) => SharedRedistributables.Contains(depotId);

    /// <summary>
    /// Returns true when the depot is referenced by something other than the game
    /// being removed, and must therefore be left in place.
    /// </summary>
    /// <param name="depotId">Depot ID to test.</param>
    /// <param name="excludingAppId">App whose own reference should be ignored.</param>
    public bool IsDepotShared(string depotId, string? excludingAppId = null)
    {
        var id = depotId?.Trim();
        if (string.IsNullOrEmpty(id)) return false;

        // 1. Steam-owned runtime depots are never removable.
        if (IsSharedRedistributable(id))
        {
            PlutoLogger.Debug("SharedDepot", $"Depot {id} is a known shared redistributable");
            return true;
        }

        // 2. Another game registered in the plugin library.
        if (ReferencedByRegisteredGame(id, excludingAppId, out _))
        {
            return true;
        }

        // 3. Another game's manifest on disk, including vanilla installs Pluto
        //    never registered.
        if (ReferencedByInstalledManifest(id, excludingAppId, out _))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the reason a depot is shared, for logging. Empty when it is not shared.
    /// </summary>
    public string DescribeSharing(string depotId, string? excludingAppId = null)
    {
        var id = depotId?.Trim();
        if (string.IsNullOrEmpty(id)) return string.Empty;

        if (IsSharedRedistributable(id)) return "shared redistributable";

        if (ReferencedByRegisteredGame(id, excludingAppId, out var registeredBy))
            return $"used by registered game '{registeredBy}'";

        if (ReferencedByInstalledManifest(id, excludingAppId, out var manifestName))
            return $"used by installed game '{manifestName}'";

        return string.Empty;
    }

    private bool ReferencedByRegisteredGame(string depotId, string? excludingAppId, out string gameName)
    {
        gameName = string.Empty;

        var library = _libraryService?.LoadPluginLibraryDictAsync().GetAwaiter().GetResult();
        if (library == null) return false;

        foreach (var (appId, game) in library)
        {
            if (!string.IsNullOrEmpty(excludingAppId) &&
                string.Equals(appId, excludingAppId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool usesDepot = game.Depots?.Any(d => string.Equals(d, depotId, StringComparison.OrdinalIgnoreCase)) == true;
            bool hasKey = game.Keys?.Keys.Any(k => string.Equals(k, depotId, StringComparison.OrdinalIgnoreCase)) == true;

            if (usesDepot || hasKey)
            {
                gameName = string.IsNullOrWhiteSpace(game.Name) ? appId : game.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans every Steam library's appmanifest files for the depot.
    ///
    /// This is the check Pluto was missing: a vanilla Steam game that was never
    /// registered with ASSella still needs its depots, and it appears nowhere in
    /// the plugin library.
    /// </summary>
    private bool ReferencedByInstalledManifest(string depotId, string? excludingAppId, out string manifestName)
    {
        manifestName = string.Empty;

        foreach (var library in SteamLibraryService.GetLibraryFolders())
        {
            if (string.IsNullOrEmpty(library.SteamappsPath) || !Directory.Exists(library.SteamappsPath))
                continue;

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(library.SteamappsPath, "appmanifest_*.acf");
            }
            catch (Exception ex)
            {
                // An unreadable library (unmounted card, permissions) must not
                // block removal outright; it just can't vouch for this depot.
                PlutoLogger.Warn("SharedDepot", $"Could not scan {library.SteamappsPath}: {ex.Message}");
                continue;
            }

            foreach (var acf in manifests)
            {
                var fileName = Path.GetFileName(acf);
                var manifestAppId = ExtractAppIdFromManifestName(fileName);

                if (!string.IsNullOrEmpty(excludingAppId) &&
                    string.Equals(manifestAppId, excludingAppId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var text = File.ReadAllText(acf);
                    if (!SteamAcfService.VdfParser.TryParse(text, out var root)) continue;

                    if (!root.Children.TryGetValue("AppState", out var state)) continue;
                    if (!state.Children.TryGetValue("InstalledDepots", out var depots)) continue;

                    if (depots.Children.Keys.Any(k => string.Equals(k, depotId, StringComparison.OrdinalIgnoreCase)))
                    {
                        manifestName = string.IsNullOrEmpty(manifestAppId) ? fileName : manifestAppId;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    PlutoLogger.Warn("SharedDepot", $"Could not read {fileName}: {ex.Message}");
                }
            }
        }

        return false;
    }

    private static string? ExtractAppIdFromManifestName(string fileName)
    {
        const string prefix = "appmanifest_";
        const string suffix = ".acf";

        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;

        var start = prefix.Length;
        var length = fileName.Length - prefix.Length - suffix.Length;
        return length > 0 ? fileName.Substring(start, length) : null;
    }
}