using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Pluto.Services;

public record SteamlessResult(bool Success, string Message);

/// <summary>
/// Service to apply Steamless SteamStub DRM unpacking to game executables.
/// Runs atom0s's .NET Steamless unpacker directly on the host using .NET runtime.
/// </summary>
public class SteamlessService
{
    public static readonly string DefaultSteamlessDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "squashfs-root", "bin", "src", "deps", "Steamless", "Steamless.CLI.dll");

    public static readonly string FallbackSteamlessPy = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "ACCELA", "squashfs-root", "bin", "src", "deps", "steamless.py");

    private static readonly string[] ExcludedKeywords = new[]
    {
        "unins", "setup", "vcredist", "dxsetup", "crashreport", "unitycrashhandler", "dotnet", "easyanticheat"
    };

    public async Task<SteamlessResult> ProcessGameAsync(string? installPath, string gameName)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return new SteamlessResult(false, "game install folder not found");
        }

        return await Task.Run(() =>
        {
            try
            {
                var candidates = FindExecutableCandidates(installPath);
                if (candidates.Count == 0)
                {
                    return new SteamlessResult(false, "no suitable game executables found");
                }

                PlutoLogger.Info("Steamless", $"Evaluating {candidates.Count} executable(s) for {gameName}");

                foreach (var exePath in candidates)
                {
                    var exeName = Path.GetFileName(exePath);
                    PlutoLogger.Info("Steamless", $"Running Steamless on: {exeName}");

                    if (File.Exists(DefaultSteamlessDll))
                    {
                        var unpacked = RunDotnetSteamless(exePath);
                        if (unpacked)
                        {
                            return new SteamlessResult(true, $"drm unpacked: {exeName}");
                        }
                    }
                    else if (File.Exists(FallbackSteamlessPy))
                    {
                        var unpacked = RunPythonSteamless(exePath);
                        if (unpacked)
                        {
                            return new SteamlessResult(true, $"drm unpacked: {exeName}");
                        }
                    }
                }

                return new SteamlessResult(false, "no steam drm detected");
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Steamless", $"Error processing {gameName}", ex);
                return new SteamlessResult(false, $"steamless error: {ex.Message}");
            }
        });
    }

    private static List<string> FindExecutableCandidates(string installPath)
    {
        var result = new List<string>();
        try
        {
            var files = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (ExcludedKeywords.Any(k => name.Contains(k)))
                    continue;

                try
                {
                    var info = new FileInfo(f);
                    if (info.Length < 100 * 1024) // Skip tiny utilities (<100KB)
                        continue;

                    result.Add(f);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Steamless", $"Failed to scan directory {installPath}", ex);
        }

        // Sort candidates: largest executables first
        return result.OrderByDescending(f =>
        {
            try { return new FileInfo(f).Length; }
            catch { return 0L; }
        }).ToList();
    }

    private static bool RunDotnetSteamless(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{DefaultSteamlessDll}\" --quiet --realign \"{exePath}\"",
                WorkingDirectory = Path.GetDirectoryName(DefaultSteamlessDll)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(60000);

            var unpackedExe = exePath + ".unpacked.exe";
            if (File.Exists(unpackedExe) && new FileInfo(unpackedExe).Length > 0)
            {
                var bakFile = exePath + ".bak";
                if (File.Exists(bakFile)) File.Delete(bakFile);
                File.Move(exePath, bakFile);
                File.Move(unpackedExe, exePath);

                PlutoLogger.Info("Steamless", $"Replaced {exePath} with unpacked executable (backup saved to {bakFile})");
                return true;
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Steamless", $"Execution error on {exePath}", ex);
        }
        return false;
    }

    private static bool RunPythonSteamless(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{FallbackSteamlessPy}\" \"{exePath}\"",
                WorkingDirectory = Path.GetDirectoryName(FallbackSteamlessPy)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(60000);

            var originalBackup = exePath + ".original";
            if (File.Exists(originalBackup))
            {
                var bakFile = exePath + ".bak";
                if (File.Exists(bakFile)) File.Delete(bakFile);
                File.Move(originalBackup, bakFile);
                return true;
            }
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Steamless", $"Python execution error on {exePath}", ex);
        }
        return false;
    }
}
