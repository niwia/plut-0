using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Pluto.Services;

/// <summary>
/// Handles verification and repair of Steam appmanifest_<appid>.acf files
/// in accordance with Section 5 &amp; 7A of HANDOVER.md.
/// </summary>
public static class SteamAcfService
{
    public static void EnsureAcfDepots(string acfPath, string appid, string[] depotIds, string[] manifests, long sizeOnDisk)
    {
        if (!File.Exists(acfPath)) return;

        try
        {
            var content = File.ReadAllText(acfPath);

            // Check if InstalledDepots is already populated
            bool hasDepots = Regex.IsMatch(content, @"\""InstalledDepots\""\s*\{[^}]*\""\d+\""", RegexOptions.Singleline);
            bool needsSize = Regex.IsMatch(content, @"\""SizeOnDisk\""\s*\""0\""");

            if (hasDepots && !needsSize) return;

            // Build InstalledDepots block if missing
            if (!hasDepots && depotIds.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("\t\"InstalledDepots\"");
                sb.AppendLine("\t{");
                for (int i = 0; i < depotIds.Length; i++)
                {
                    var did = depotIds[i];
                    if (did == appid) continue;
                    var mgid = (i < manifests.Length && !string.IsNullOrEmpty(manifests[i])) ? manifests[i] : "0";
                    long dSize = depotIds.Length == 1 ? sizeOnDisk : (sizeOnDisk / depotIds.Length);

                    sb.AppendLine($"\t\t\"{did}\"");
                    sb.AppendLine("\t\t{");
                    sb.AppendLine($"\t\t\t\"manifest\"\t\t\"{mgid}\"");
                    sb.AppendLine($"\t\t\t\"size\"\t\t\"{dSize}\"");
                    sb.AppendLine("\t\t}");
                }
                sb.Append("\t}");

                content = Regex.Replace(content, @"\""InstalledDepots\""\s*\{\s*\}", sb.ToString());
            }

            // Ensure SizeOnDisk is greater than 0
            if (needsSize && sizeOnDisk > 0)
            {
                content = Regex.Replace(content, @"\""SizeOnDisk\""\s*\""\d+\""", $"\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"");
            }

            // Ensure StateFlags is 4 (STATE_FULLY_INSTALLED)
            content = Regex.Replace(content, @"\""StateFlags\""\s*\""[^\""]+\""", "\"StateFlags\"\t\t\"4\"");

            File.WriteAllText(acfPath, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SteamAcfService] Error updating {acfPath}: {ex.Message}");
        }
    }
}
