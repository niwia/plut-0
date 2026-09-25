using System;

namespace Pluto;

/// <summary>
/// Manages the Pluto build and versioning string according to the repository rule:
/// Format: 0.0.8-xxday+month+26
/// </summary>
public static class PlutoVersion
{
    public const string BaseVersion = "0.0.8";

    // Build counter: increments per build
    public const int BuildNumber = 1;

    // Day, Month, Year according to the format rule
    public const string Day = "25";
    public const string Month = "09";
    public const string Year = "26";

    /// <summary>
    /// Evaluates to: 0.0.8-0125+09+26
    /// </summary>
    public static string FullVersion => $"{BaseVersion}-{BuildNumber:D2}{Day}+{Month}+{Year}";
}
