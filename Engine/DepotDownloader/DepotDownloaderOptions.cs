using System;
using System.Collections.Generic;

namespace Pluto.Engine.DepotDownloader;

/// <summary>
/// Strongly-typed execution options for DepotDownloaderMod.
/// </summary>
public sealed class DepotDownloaderOptions
{
    public uint AppId { get; set; }
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }

    public string? ManifestFilePath { get; set; }
    public string? DepotKeysFilePath { get; set; }
    public string? AppToken { get; set; }
    public string? PackageToken { get; set; }

    public string InstallDirectory { get; set; } = string.Empty;
    public string Branch { get; set; } = "public";
    public string? BranchPassword { get; set; }

    public int MaxDownloads { get; set; } = 4;
    public bool Validate { get; set; } = true;
    public bool UseLanCache { get; set; } = true;
    public string? FileListPath { get; set; }

    public string OperatingSystem { get; set; } = "linux";
    public string Architecture { get; set; } = "64";
    public string Language { get; set; } = "english";

    public List<string> BuildArguments()
    {
        var args = new List<string>
        {
            "-app", AppId.ToString(),
            "-depot", DepotId.ToString()
        };

        if (ManifestId > 0)
        {
            args.Add("-manifest");
            args.Add(ManifestId.ToString());
        }

        if (!string.IsNullOrWhiteSpace(ManifestFilePath))
        {
            args.Add("-manifestfile");
            args.Add(ManifestFilePath);
        }

        if (!string.IsNullOrWhiteSpace(DepotKeysFilePath))
        {
            args.Add("-depotkeys");
            args.Add(DepotKeysFilePath);
        }

        if (!string.IsNullOrWhiteSpace(AppToken))
        {
            args.Add("-apptoken");
            args.Add(AppToken);
        }

        if (!string.IsNullOrWhiteSpace(PackageToken))
        {
            args.Add("-packagetoken");
            args.Add(PackageToken);
        }

        if (!string.IsNullOrWhiteSpace(InstallDirectory))
        {
            args.Add("-dir");
            args.Add(InstallDirectory);
        }

        if (!string.IsNullOrWhiteSpace(Branch) && !string.Equals(Branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-branch");
            args.Add(Branch);

            if (!string.IsNullOrWhiteSpace(BranchPassword))
            {
                args.Add("-branchpassword");
                args.Add(BranchPassword);
            }
        }

        if (MaxDownloads > 0)
        {
            args.Add("-max-downloads");
            args.Add(MaxDownloads.ToString());
        }

        if (Validate)
        {
            args.Add("-validate");
        }

        if (UseLanCache)
        {
            args.Add("-use-lancache");
        }

        if (!string.IsNullOrWhiteSpace(FileListPath))
        {
            args.Add("-filelist");
            args.Add(FileListPath);
        }

        // Isolated random 32-bit logon ID to prevent session collisions
        int logonId = Random.Shared.Next(1000000, int.MaxValue);
        args.Add("-loginid");
        args.Add(logonId.ToString());

        return args;
    }
}
