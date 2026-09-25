using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Pluto.Services;

namespace Pluto.Engine.Steam;

/// <summary>
/// Service managing Steam ACF manifests, DepotDownloader delta cache seeding (.DepotDownloader),
/// and ACCELA interoperability metadata (.ACCELA/metadata.json).
/// </summary>
public sealed class SteamAcfWriter
{
    /// <summary>
    /// Writes a standard Steam appmanifest_<appid>.acf file so Steam and SLSsteam recognize the game as installed.
    /// </summary>
    public static async Task WriteAcfManifestAsync(
        string steamappsDir,
        uint appId,
        string gameName,
        string installDirName,
        string buildId,
        long sizeOnDisk,
        Dictionary<string, string> installedDepots)
    {
        var acfPath = Path.Combine(steamappsDir, $"appmanifest_{appId}.acf");
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sb = new StringBuilder();
        sb.AppendLine("\"AppState\"");
        sb.AppendLine("{");
        sb.AppendLine($"\t\"appid\"\t\t\"{appId}\"");
        sb.AppendLine("\t\"Universe\"\t\t\"1\"");
        sb.AppendLine($"\t\"name\"\t\t\"{gameName}\"");
        sb.AppendLine("\t\"StateFlags\"\t\t\"4\"");
        sb.AppendLine($"\t\"installdir\"\t\t\"{installDirName}\"");
        sb.AppendLine($"\t\"LastUpdated\"\t\t\"{nowSec}\"");
        sb.AppendLine($"\t\"UpdateResult\"\t\t\"0\"");
        sb.AppendLine($"\t\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"");
        sb.AppendLine($"\t\"buildid\"\t\t\"{buildId}\"");
        sb.AppendLine($"\t\"LastOwner\"\t\t\"0\"");
        sb.AppendLine($"\t\"BytesToDownload\"\t\t\"{sizeOnDisk}\"");
        sb.AppendLine($"\t\"BytesDownloaded\"\t\t\"{sizeOnDisk}\"");
        sb.AppendLine("\t\"AutoUpdateBehavior\"\t\t\"1\"");

        sb.AppendLine("\t\"InstalledDepots\"");
        sb.AppendLine("\t{");
        foreach (var (depotId, manifestId) in installedDepots)
        {
            sb.AppendLine($"\t\t\"{depotId}\"");
            sb.AppendLine("\t\t{");
            sb.AppendLine($"\t\t\t\"manifest\"\t\t\"{manifestId}\"");
            sb.AppendLine("\t\t\t\"size\"\t\t\"0\"");
            sb.AppendLine("\t\t}");
        }
        sb.AppendLine("\t}");
        sb.AppendLine("}");

        await File.WriteAllTextAsync(acfPath, sb.ToString());
        PlutoLogger.Info("SteamAcfWriter", $"Generated Steam ACF manifest: {acfPath}");
    }

    /// <summary>
    /// Seeds the .DepotDownloader/ hidden delta cache folder with manifests and .sha sidecars.
    /// This enables subsequent DepotDownloaderMod invocations to perform incremental delta patching.
    /// </summary>
    public static async Task SeedDepotDownloaderDeltaCacheAsync(
        string gameInstallDir,
        Dictionary<string, string> installedDepots,
        string? sourceManifestsDir = null)
    {
        var ddmDir = Path.Combine(gameInstallDir, ".DepotDownloader");
        Directory.CreateDirectory(ddmDir);

        var steamBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam");
        var depotcacheDir = Path.Combine(steamBase, "depotcache");

        foreach (var (depotId, manifestId) in installedDepots)
        {
            var manifestFileName = $"{depotId}_{manifestId}.manifest";
            var targetManifestPath = Path.Combine(ddmDir, manifestFileName);
            var targetShaPath = Path.Combine(ddmDir, $"{manifestFileName}.sha");

            // Look for existing manifest in source dir or Steam depotcache
            string? foundSource = null;
            if (!string.IsNullOrWhiteSpace(sourceManifestsDir))
            {
                var p = Path.Combine(sourceManifestsDir, manifestFileName);
                if (File.Exists(p)) foundSource = p;
            }
            if (foundSource == null)
            {
                var p = Path.Combine(depotcacheDir, manifestFileName);
                if (File.Exists(p)) foundSource = p;
            }

            if (foundSource != null && !File.Exists(targetManifestPath))
            {
                try
                {
                    File.Copy(foundSource, targetManifestPath, overwrite: true);
                }
                catch { }
            }

            // Write or generate .sha sidecar file (raw 20-byte SHA-1 hash)
            if (File.Exists(targetManifestPath) && !File.Exists(targetShaPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(targetManifestPath);
                    var hash = SHA1.HashData(bytes);
                    await File.WriteAllBytesAsync(targetShaPath, hash);
                    PlutoLogger.Info("SteamAcfWriter", $"Seeded delta SHA sidecar: {targetShaPath}");
                }
                catch (Exception ex)
                {
                    PlutoLogger.Warn("SteamAcfWriter", $"Failed writing SHA sidecar for {manifestFileName}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Writes .ACCELA/metadata.json inside the game directory for complete 2-way interoperability with ASSella.
    /// </summary>
    public static async Task WriteAccelaMetadataAsync(
        string gameInstallDir,
        uint appId,
        string gameName,
        string buildId,
        string installDirName,
        Dictionary<string, string> installedDepots)
    {
        var accelaDir = Path.Combine(gameInstallDir, ".ACCELA");
        Directory.CreateDirectory(accelaDir);

        var metaPath = Path.Combine(accelaDir, "metadata.json");
        var meta = new Dictionary<string, object>
        {
            ["appid"] = appId.ToString(),
            ["game_name"] = gameName,
            ["installdir"] = installDirName,
            ["buildid"] = buildId,
            ["installed_branch"] = "public",
            ["install_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["manifests"] = installedDepots
        };

        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metaPath, json);
        PlutoLogger.Info("SteamAcfWriter", $"Wrote ASSella metadata: {metaPath}");
    }
}
