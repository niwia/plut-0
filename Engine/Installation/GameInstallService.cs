using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pluto.Engine.DepotDownloader;
using Pluto.Engine.Steam;
using Pluto.Models;
using Pluto.Services;

namespace Pluto.Engine.Installation;

public readonly record struct InstallStepProgress(
    string Step,
    double OverallPercentage,
    double CurrentDepotPercentage,
    double SpeedMbPerSec,
    string Details
);

/// <summary>
/// High-level coordinator orchestrating the full game installation and update lifecycle:
/// SteamCMD/PICS -> Branch/Depot resolution -> Key decryption -> DepotDownloaderMod execution ->
/// Delta cache seeding -> ACF generation -> SLSsteam pipe notification.
/// </summary>
public sealed class GameInstallService
{
    private readonly DepotDownloaderService _ddmService;
    private readonly SlsSteamService _slsService;
    private readonly AccelaConfigService _configService;
    private readonly DepotKeyService _depotKeyService;
    private readonly HubcapManifestService _hubcapService;

    public GameInstallService(
        DepotDownloaderService ddmService,
        SlsSteamService slsService,
        AccelaConfigService configService,
        DepotKeyService depotKeyService,
        HubcapManifestService? hubcapService = null)
    {
        _ddmService = ddmService;
        _slsService = slsService;
        _configService = configService;
        _depotKeyService = depotKeyService;
        _hubcapService = hubcapService ?? new HubcapManifestService(configService, depotKeyService);
    }

    /// <summary>
    /// Executes a full, non-native game installation workflow with DepotDownloaderMod and delta cache sync.
    /// </summary>
    public async Task<bool> InstallGameAsync(
        uint appId,
        string? targetSteamappsDir = null,
        string branch = "public",
        List<string>? selectedDepotIds = null,
        IProgress<InstallStepProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PlutoLogger.Info("GameInstallService", $"=== Starting installation workflow for AppID {appId} (branch: '{branch}') ===");

        // Load DDM settings from config
        int maxDownloads = _configService.GetInt("ddm_max_downloads", 4);
        if (maxDownloads < 1) maxDownloads = 4;
        bool validate = _configService.GetBool("ddm_validate", true);
        bool useLanCache = _configService.GetBool("ddm_use_lancache", true);

        // 1. Resolve target library directory
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamapps = targetSteamappsDir ?? Path.Combine(home, ".local", "share", "Steam", "steamapps");
        var commonDir = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(commonDir);

        progress?.Report(new InstallStepProgress("Resolving App Info", 5, 0, 0, $"Querying SteamCMD & PICS for AppID {appId}..."));

        // 2. Query App Info, Branches & Depots via SteamCMD API
        var appInfo = await SteamCmdService.FetchAppInfoAsync(appId);
        if (appInfo == null)
        {
            PlutoLogger.Error("GameInstallService", $"Failed to fetch package info for AppID {appId}");
            progress?.Report(new InstallStepProgress("Failed", 0, 0, 0, "Could not resolve app information from Steam."));
            return false;
        }

        PlutoLogger.Info("GameInstallService", $"Resolved AppInfo: Name='{appInfo.Name}', InstallDir='{appInfo.InstallDir}', BuildId='{appInfo.BuildId}', DepotsCount={appInfo.Depots.Count}");
        progress?.Report(new InstallStepProgress("Selecting Depots", 10, 0, 0, $"Resolved '{appInfo.Name}'. Selecting playable depots..."));

        // 3. Filter depots for platform or user selection
        var playableDepots = SteamCmdService.FilterPlayableDepots(appInfo.Depots, preferNativeLinux: true);
        if (playableDepots.Count == 0)
        {
            // Fallback: try all depots with manifests
            playableDepots = appInfo.Depots.Where(d => !string.IsNullOrWhiteSpace(d.ManifestId)).ToList();
        }

        // Apply explicit user depot selections if provided from pre-download modal
        if (selectedDepotIds != null && selectedDepotIds.Count > 0)
        {
            var filtered = appInfo.Depots.Where(d => selectedDepotIds.Contains(d.DepotId)).ToList();
            if (filtered.Count > 0)
            {
                playableDepots = filtered;
            }
        }

        if (playableDepots.Count == 0)
        {
            PlutoLogger.Error("GameInstallService", $"No downloadable depots found for {appInfo.Name} ({appId})");
            progress?.Report(new InstallStepProgress("Failed", 0, 0, 0, "No playable depots available for this platform."));
            return false;
        }

        PlutoLogger.Info("GameInstallService", $"Selected {playableDepots.Count} playable depot(s): {string.Join(", ", playableDepots.Select(d => $"{d.DepotId} ({d.Name})"))}");

        var gameInstallDir = Path.Combine(commonDir, appInfo.InstallDir);
        Directory.CreateDirectory(gameInstallDir);

        // 4. Retrieve keys & generate temp depotkeys file
        progress?.Report(new InstallStepProgress("Resolving Keys", 15, 0, 0, "Retrieving AES decryption keys from SQLite store & Hubcap..."));
        var depotIds = playableDepots.Select(d => d.DepotId).ToList();

        var existingKeys = _depotKeyService.GetKeysForApp(appId.ToString());
        PlutoLogger.Info("GameInstallService", $"Existing keys in local DB for AppID {appId}: {existingKeys.Count} key(s)");

        HubcapManifestResult? hubcapResult = null;
        bool missingAnyKey = playableDepots.Any(d => !existingKeys.ContainsKey(d.DepotId));

        if (missingAnyKey || existingKeys.Count == 0)
        {
            PlutoLogger.Info("GameInstallService", $"Missing decryption keys for playable depots. Fetching manifest bundle from Hubcap for AppID {appId}...");
            progress?.Report(new InstallStepProgress("Resolving Keys", 17, 0, 0, "Fetching manifest bundle and depot keys from Hubcap..."));
            hubcapResult = await _hubcapService.ResolveManifestAndKeysAsync(appId, cancellationToken: cancellationToken);
            if (hubcapResult.Value.Success)
            {
                PlutoLogger.Info("GameInstallService", $"Successfully fetched Hubcap bundle: {hubcapResult.Value.DepotKeys.Count} keys, {hubcapResult.Value.ManifestFiles.Count} manifests.");
            }
            else
            {
                PlutoLogger.Warn("GameInstallService", $"Hubcap bundle fetch note: {hubcapResult.Value.ErrorMessage}");
            }
        }

        var keysFilePath = await _depotKeyService.CreateTempDepotKeysFileAsync(appId.ToString(), depotIds);
        var appToken = _depotKeyService.GetAppToken(appId.ToString()) ?? hubcapResult?.AppToken;

        PlutoLogger.Info("GameInstallService", $"Temp keys file: {keysFilePath ?? "(none)"}, AppToken: {(appToken != null ? "present" : "none")}");

        var installedDepotsMap = new Dictionary<string, string>();
        int totalDepots = playableDepots.Count;
        int completedDepots = 0;

        // 5. Download Depots using DepotDownloaderMod
        for (int i = 0; i < playableDepots.Count; i++)
        {
            var depot = playableDepots[i];
            if (cancellationToken.IsCancellationRequested) return false;

            ulong manifestGid = 0;
            if (!string.IsNullOrWhiteSpace(depot.ManifestId))
                ulong.TryParse(depot.ManifestId, out manifestGid);

            // Fallback: check if Hubcap provided manifest GID
            if (manifestGid == 0 && hubcapResult?.ManifestIds.TryGetValue(depot.DepotId, out var hmid) == true)
            {
                ulong.TryParse(hmid, out manifestGid);
            }

            // Resolve manifest file path
            string? manifestFilePath = null;
            if (hubcapResult?.ManifestFiles.TryGetValue(depot.DepotId, out var mfp) == true && File.Exists(mfp))
            {
                manifestFilePath = mfp;
            }
            else
            {
                var candidate1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ACCELA", "manifests", $"{depot.DepotId}_{manifestGid}.manifest");
                var candidate2 = Path.Combine(Path.GetTempPath(), "mistwalker_manifests", $"{depot.DepotId}_{manifestGid}.manifest");
                if (File.Exists(candidate1)) manifestFilePath = candidate1;
                else if (File.Exists(candidate2)) manifestFilePath = candidate2;
            }

            PlutoLogger.Info("GameInstallService",
                $"Starting download for Depot {depot.DepotId} ({depot.Name}) [Depot {i + 1}/{totalDepots}]: " +
                $"ManifestGID={manifestGid}, ManifestFile={manifestFilePath ?? "(none)"}, HasKey={keysFilePath != null}");

            progress?.Report(new InstallStepProgress(
                $"Downloading Depot {i + 1}/{totalDepots}",
                20 + (int)((double)completedDepots / totalDepots * 60),
                0,
                0,
                $"Depot {depot.DepotId}: {depot.Name}"));

            var options = new DepotDownloaderOptions
            {
                AppId = appId,
                DepotId = uint.Parse(depot.DepotId),
                ManifestId = manifestGid,
                ManifestFilePath = manifestFilePath,
                DepotKeysFilePath = keysFilePath,
                AppToken = appToken,
                InstallDirectory = gameInstallDir,
                Branch = branch,
                Validate = validate,
                MaxDownloads = maxDownloads,
                UseLanCache = useLanCache
            };

            var depotProg = new Progress<DepotDownloadProgress>(p =>
            {
                double overall = 20 + ((completedDepots + (p.Percentage / 100.0)) / totalDepots * 60.0);
                progress?.Report(new InstallStepProgress(
                    $"Downloading Depot {i + 1}/{totalDepots}",
                    overall,
                    p.Percentage,
                    p.SpeedMbPerSec,
                    $"{p.Status} ({p.SpeedMbPerSec:0.1} MB/s)"));
            });

            bool ok = await _ddmService.ExecuteDownloadAsync(options, depotProg, cancellationToken);
            PlutoLogger.Info("GameInstallService", $"DepotDownloader finished for depot {depot.DepotId}: result ok={ok}");

            if (!ok)
            {
                PlutoLogger.Warn("GameInstallService", $"DepotDownloader finished with warnings for depot {depot.DepotId}");
            }

            if (manifestGid > 0)
            {
                installedDepotsMap[depot.DepotId] = manifestGid.ToString();
            }

            completedDepots++;
        }

        // Clean up temp keys file
        if (keysFilePath != null && File.Exists(keysFilePath))
        {
            try { File.Delete(keysFilePath); } catch { }
        }

        // 6. Calculate total size on disk
        progress?.Report(new InstallStepProgress("Finalizing Installation", 85, 100, 0, "Generating manifests and delta cache..."));
        long sizeOnDisk = 0;
        try
        {
            var di = new DirectoryInfo(gameInstallDir);
            sizeOnDisk = di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
        }
        catch { }

        // 7. Seed .DepotDownloader delta cache & .ACCELA metadata
        await SteamAcfWriter.SeedDepotDownloaderDeltaCacheAsync(gameInstallDir, installedDepotsMap);
        await SteamAcfWriter.WriteAccelaMetadataAsync(gameInstallDir, appId, appInfo.Name, appInfo.BuildId, appInfo.InstallDir, installedDepotsMap);

        // 8. Copy manifests to Steam's central depotcache for native SLS ACF creation
        CopyManifestsToSteamDepotcache(installedDepotsMap);

        // 9. Register in SLS config.yaml & notify SLSsteam API pipe
        progress?.Report(new InstallStepProgress("Configuring SLSsteam", 90, 100, 0, "Registering in SLS config & notifying Steam via pipe..."));
        var pluginGame = new PluginGame
        {
            AppId = appId.ToString(),
            Name = appInfo.Name,
            IsAccela = true
        };
        await _slsService.SyncGameToConfigAsync(pluginGame);

        // Notify /tmp/SLSsteam.API with install command (install|appid|0)
        bool pipeSent = NotifySlsInstallPipe(appId);

        // 9. Smart ACF Verification: Wait for Steam to create the authentic ACF natively
        var acfPath = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        bool steamCreatedAcf = false;

        if (pipeSent)
        {
            progress?.Report(new InstallStepProgress("Waiting for Steam ACF", 95, 100, 0, "Waiting for Steam to create authentic appmanifest natively..."));
            steamCreatedAcf = await WaitForNativeSteamAcfAsync(acfPath, timeoutSeconds: 6, cancellationToken);
        }

        // 10. Fallback: If Steam is offline or watcher is inactive, write fallback ACF
        if (!steamCreatedAcf && !File.Exists(acfPath))
        {
            PlutoLogger.Info("GameInstallService", $"Steam did not create ACF natively (Steam may be closed). Writing fallback manifest to {acfPath}...");
            await SteamAcfWriter.WriteAcfManifestAsync(steamapps, appId, appInfo.Name, appInfo.InstallDir, appInfo.BuildId, sizeOnDisk, installedDepotsMap);
        }
        else if (steamCreatedAcf)
        {
            PlutoLogger.Info("GameInstallService", $"Confirmed: Steam natively created authentic ACF manifest at {acfPath}");
        }

        progress?.Report(new InstallStepProgress("Complete", 100, 100, 0, $"Successfully installed {appInfo.Name}!"));
        PlutoLogger.Info("GameInstallService", $"Installation workflow completed successfully for {appInfo.Name} ({appId})");
        return true;
    }

    private static bool NotifySlsInstallPipe(uint appId)
    {
        const string pipePath = "/tmp/SLSsteam.API";
        if (!File.Exists(pipePath)) return false;

        try
        {
            // Flow for install: send install|appid|0 to /tmp/SLSsteam.API
            File.WriteAllText(pipePath, $"install|{appId}|0\n");
            PlutoLogger.Info("GameInstallService", $"Sent install|{appId}|0 to {pipePath}");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("GameInstallService", $"Could not write to {pipePath}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForNativeSteamAcfAsync(string acfPath, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (File.Exists(acfPath))
            {
                return true;
            }
            await Task.Delay(350, ct);
        }
        return File.Exists(acfPath);
    }

    private static void CopyManifestsToSteamDepotcache(Dictionary<string, string> installedDepotsMap)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var centralDepotcache = Path.Combine(home, ".local", "share", "Steam", "depotcache");
        try
        {
            Directory.CreateDirectory(centralDepotcache);
            var accelaManifests = Path.Combine(home, ".local", "share", "ACCELA", "manifests");
            var tempManifests = Path.Combine(Path.GetTempPath(), "mistwalker_manifests");

            foreach (var kvp in installedDepotsMap)
            {
                var filename = $"{kvp.Key}_{kvp.Value}.manifest";
                var src1 = Path.Combine(accelaManifests, filename);
                var src2 = Path.Combine(tempManifests, filename);
                var src = File.Exists(src1) ? src1 : (File.Exists(src2) ? src2 : null);

                if (src != null)
                {
                    var dest = Path.Combine(centralDepotcache, filename);
                    File.Copy(src, dest, overwrite: true);
                    PlutoLogger.Info("GameInstallService", $"Copied {filename} to Steam's central depotcache for native ACF creation.");
                }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("GameInstallService", $"Could not copy manifests to Steam depotcache: {ex.Message}");
        }
    }
}
