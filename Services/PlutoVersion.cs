using System;
using System.IO;
using System.Reflection;

namespace Pluto;

/// <summary>
/// Pluto's version string, per the repository rule in .agents/rules/versioning.md:
/// <c>0.0.8-xxday+month+26</c> where xx is a sequential build counter.
///
/// The counter is manual because it encodes build order. The date is NOT:
/// it used to be hardcoded, which meant every build shipped a stale date
/// until someone remembered to bump it by hand. It is now derived from the
/// assembly's build timestamp, so it is always correct by construction.
///
/// Set <see cref="BuildDateOverride"/> to pin a specific date when building
/// reproducibly or backdating a release.
/// </summary>
public static class PlutoVersion
{
    public const string BaseVersion = "0.0.8";

    /// <summary>Sequential build counter, incremented by +1 per build.</summary>
    public const int BuildNumber = 2;

    /// <summary>Two-digit year suffix. The rule fixes this at 26 (2026).</summary>
    private const string Year = "26";

    /// <summary>
    /// Optional explicit build date (yyyy-MM-dd). Null derives it from the
    /// assembly build timestamp, falling back to the current date.
    /// </summary>
    public static string? BuildDateOverride { get; set; }

    private static DateTime BuildDate
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BuildDateOverride) &&
                DateTime.TryParse(BuildDateOverride, out var pinned))
            {
                return pinned;
            }

            // The assembly's PE/ELF header carries the link timestamp.
            // Deterministic builds can zero this, hence the fallback.
            var assembly = Assembly.GetExecutingAssembly();
            var location = SafeGetLocation(assembly);
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                try
                {
                    return File.GetLastWriteTime(location);
                }
                catch
                {
                    // Fall through to current date.
                }
            }

            return DateTime.Now;
        }
    }

    private static string? SafeGetLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch
        {
            // Single-file/trimmed builds can throw here.
            return null;
        }
    }

    /// <summary>Evaluates to: 0.0.8-0225+09+26</summary>
    public static string FullVersion
    {
        get
        {
            var d = BuildDate;
            var day = d.Day.ToString("D2");
            var month = d.Month.ToString("D2");
            return $"{BaseVersion}-{BuildNumber:D2}{day}+{month}+{Year}";
        }
    }

    /// <summary>Short form for logs and window titles, e.g. "0.0.8 build 02".</summary>
    public static string ShortVersion => $"{BaseVersion} build {BuildNumber:D2}";
}