using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pluto.Services;

namespace Pluto.Engine.DepotDownloader;

public readonly record struct DepotDownloadProgress(
    double Percentage,
    string Status,
    double SpeedMbPerSec,
    long BytesDownloaded,
    long TotalBytes
);

/// <summary>
/// Service managing the execution and progress streaming of DepotDownloaderMod.
/// </summary>
public sealed class DepotDownloaderService
{
    private static readonly Regex PercentRegex = new(
        @"(\d+(?:\.\d+)?)%",
        RegexOptions.Compiled);

    private static readonly Regex SpeedRegex = new(
        @"(\d+(?:\.\d+)?)\s*(MB|KB|B)/s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public event Action<DepotDownloadProgress>? ProgressChanged;
    public event Action<string>? LogMessageReceived;

    /// <summary>
    /// Resolves the absolute path to DepotDownloader.dll bundled in Pluto.
    /// </summary>
    public static string? ResolveBinaryPath()
    {
        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appBase, "Engine", "DepotDownloader", "Bin", "DepotDownloader.dll"),
            Path.Combine(appBase, "DepotDownloader.dll"),
            "/home/aiwin/Documents/pluto/Engine/DepotDownloader/Bin/DepotDownloader.dll",
            "/home/aiwin/ASSella_git/src/deps/DepotDownloader.dll"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    /// <summary>
    /// Executes a download operation using DepotDownloaderMod with full progress tracking.
    /// </summary>
    public async Task<bool> ExecuteDownloadAsync(
        DepotDownloaderOptions options,
        IProgress<DepotDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dllPath = ResolveBinaryPath();
        if (dllPath == null)
        {
            PlutoLogger.Error("DepotDownloader", "DepotDownloader.dll not found in Pluto Engine/Bin or fallback paths.");
            return false;
        }

        var args = options.BuildArguments();
        var argumentsString = $"\"{dllPath}\" " + string.Join(" ", args.ConvertAll(EscapeArgument));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argumentsString,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        PlutoLogger.Info("DepotDownloader", $"Starting download: AppID {options.AppId}, Depot {options.DepotId} -> {options.InstallDirectory}");

        using var process = new Process { StartInfo = psi };

        double currentPercent = 0;
        double currentSpeedMb = 0;
        string currentStatus = "Starting download...";

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            var line = e.Data.Trim();
            LogMessageReceived?.Invoke(line);

            // Parse Percentage
            var mPercent = PercentRegex.Match(line);
            if (mPercent.Success && double.TryParse(mPercent.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            {
                currentPercent = p;
            }

            // Parse Download Speed
            var mSpeed = SpeedRegex.Match(line);
            if (mSpeed.Success && double.TryParse(mSpeed.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
            {
                var unit = mSpeed.Groups[2].Value.ToUpperInvariant();
                currentSpeedMb = unit switch
                {
                    "MB" => s,
                    "KB" => s / 1024.0,
                    "B"  => s / (1024.0 * 1024.0),
                    _    => s
                };
            }

            if (line.Contains("Validating", StringComparison.OrdinalIgnoreCase))
                currentStatus = "Validating files...";
            else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                currentStatus = $"Downloading: {currentPercent:0.0}%";
            else if (line.Contains("Pre-allocating", StringComparison.OrdinalIgnoreCase))
                currentStatus = "Pre-allocating space...";
            else if (line.Contains("Done", StringComparison.OrdinalIgnoreCase))
                currentStatus = "Complete";

            var progUpdate = new DepotDownloadProgress(currentPercent, currentStatus, currentSpeedMb, 0, 0);
            progress?.Report(progUpdate);
            ProgressChanged?.Invoke(progUpdate);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                PlutoLogger.Error("DepotDownloader", e.Data);
                LogMessageReceived?.Invoke($"[Error] {e.Data}");
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        PlutoLogger.Info("DepotDownloader", "Cancelling download process tree...");
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);

            bool success = process.ExitCode == 0;
            PlutoLogger.Info("DepotDownloader", $"Download completed with exit code {process.ExitCode}");
            return success;
        }
        catch (OperationCanceledException)
        {
            PlutoLogger.Info("DepotDownloader", "Download operation was cancelled by user.");
            return false;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("DepotDownloader", "Exception running DepotDownloader", ex);
            return false;
        }
    }

    private static string EscapeArgument(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }
}
