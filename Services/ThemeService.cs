using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Pluto.Services;

public record ThemeColorPreset(string Name, string HighlightHex, string DimmedHex);

/// <summary>Which control theme the application renders with.</summary>
public enum ThemeStyle
{
    /// <summary>Avalonia's Fluent theme. The default.</summary>
    Modern,

    /// <summary>Classic Windows-style controls, via Classic.Avalonia.Theme.</summary>
    Classic
}

/// <summary>
/// Authentic Windows 9x desktop colour schemes, as provided by
/// Classic.Avalonia.Theme. Only meaningful while the Classic theme is active;
/// Modern always uses Avalonia's Dark variant.
/// </summary>
public static class ClassicSchemes
{
    /// <summary>
    /// Scheme variants in cycle order, matched by name against the package's
    /// static ThemeVariant properties.
    /// </summary>
    public static readonly string[] Names =
    {
        "Classic", "Standard", "Brick", "Wheat", "Marine", "Sprouce",
        "Plum", "Rose", "Storm", "Desert", "Eggplant", "StarsAndStripes", "Pumpkin"
    };

    public static ThemeVariant Resolve(string name)
    {
        // Reflect rather than hard-reference each property, so a rename upstream
        // degrades to the default scheme instead of failing to compile.
        foreach (var prop in typeof(Classic.Avalonia.Theme.ClassicTheme)
                 .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (prop.GetValue(null) is ThemeVariant variant) return variant;
        }

        return Classic.Avalonia.Theme.ClassicTheme.Classic;
    }

    public static string Next(string current)
    {
        int idx = Array.IndexOf(Names, current);
        if (idx < 0) idx = 0;
        return Names[(idx + 1) % Names.Length];
    }
}

public class ThemeService
{
    private const string StyleConfigKey = "theme_style";
    private const string SchemeConfigKey = "theme_classic_scheme";

    private readonly AccelaConfigService _configService;

    public static readonly List<ThemeColorPreset> NativePresets = new()
    {
        new("classic white", "#FFFFFF", "#555555"),
        new("mint green", "#4ADE80", "#265C38"),
        new("amber gold", "#FBBF24", "#6B5014"),
        new("ice blue", "#93C5FD", "#385473"),
        new("lavender", "#C084FC", "#543373"),
        new("rose coral", "#FB7185", "#6B2B38")
    };

    public static readonly List<ThemeColorPreset> AccelaPresets = new()
    {
        new("classic white", "#FFFFFF", "#555555"),
        new("slate blue", "#60A5FA", "#385473"),
        new("deep cyan", "#22D3EE", "#155E75"),
        new("royal indigo", "#818CF8", "#3730A3"),
        new("emerald blue", "#38BDF8", "#0369A1"),
        new("teal accent", "#2DD4BF", "#0F766E"),
        new("sunset amber", "#F59E0B", "#78350F")
    };

    private int _nativeIndex = 0;
    private int _accelaIndex = 0;

    public ThemeService(AccelaConfigService configService)
    {
        _configService = configService;
        LoadFromConfig();
    }

    public ThemeColorPreset CurrentNative => NativePresets[_nativeIndex];
    public ThemeColorPreset CurrentAccela => AccelaPresets[_accelaIndex];

    /// <summary>Currently selected control theme.</summary>
    public ThemeStyle CurrentStyle { get; private set; } = ThemeStyle.Modern;

    /// <summary>
    /// Active Windows 9x colour scheme name. Only applied when
    /// <see cref="CurrentStyle"/> is <see cref="ThemeStyle.Classic"/>.
    /// </summary>
    public string CurrentScheme { get; private set; } = "Classic";

    public void LoadFromConfig()
    {
        var savedNative = _configService.GetValue("theme_native_color", "#FFFFFF");
        var savedAccela = _configService.GetValue("theme_accela_color", "#FFFFFF");

        int nIdx = NativePresets.FindIndex(p => p.HighlightHex.Equals(savedNative, StringComparison.OrdinalIgnoreCase));
        _nativeIndex = nIdx >= 0 ? nIdx : 0;

        int aIdx = AccelaPresets.FindIndex(p => p.HighlightHex.Equals(savedAccela, StringComparison.OrdinalIgnoreCase));
        _accelaIndex = aIdx >= 0 ? aIdx : 0;

        var savedStyle = _configService.GetValue(StyleConfigKey, "modern");
        CurrentStyle = savedStyle.Equals("classic", StringComparison.OrdinalIgnoreCase)
            ? ThemeStyle.Classic
            : ThemeStyle.Modern;

        var savedScheme = _configService.GetValue(SchemeConfigKey, "Classic");
        CurrentScheme = Array.IndexOf(ClassicSchemes.Names, savedScheme) >= 0 ? savedScheme : "Classic";
    }

    /// <summary>
    /// Advances to the next Windows 9x colour scheme and reapplies it.
    ///
    /// Schemes only exist inside the Classic theme; calling this while Modern is
    /// active still persists the choice so it takes effect on the next switch.
    /// </summary>
    public string CycleClassicScheme(Application app)
    {
        CurrentScheme = ClassicSchemes.Next(CurrentScheme);
        _configService.SetValue(SchemeConfigKey, CurrentScheme);

        if (CurrentStyle == ThemeStyle.Classic)
        {
            ApplyStyle(app, CurrentStyle);
        }

        PlutoLogger.Info("Theme", $"Classic scheme set to {CurrentScheme}");
        return CurrentScheme;
    }

    public ThemeColorPreset CycleNextNative()
    {
        _nativeIndex = (_nativeIndex + 1) % NativePresets.Count;
        _configService.SetValue("theme_native_color", CurrentNative.HighlightHex);
        return CurrentNative;
    }

    public ThemeColorPreset CycleNextAccela()
    {
        _accelaIndex = (_accelaIndex + 1) % AccelaPresets.Count;
        _configService.SetValue("theme_accela_color", CurrentAccela.HighlightHex);
        return CurrentAccela;
    }

    /// <summary>Switches the control theme and persists the choice.</summary>
    public ThemeStyle CycleStyle(Application app)
    {
        CurrentStyle = CurrentStyle == ThemeStyle.Modern
            ? ThemeStyle.Classic
            : ThemeStyle.Modern;

        _configService.SetValue(StyleConfigKey,
            CurrentStyle == ThemeStyle.Classic ? "classic" : "modern");

        ApplyStyle(app, CurrentStyle, CurrentScheme);
        PlutoLogger.Info("Theme", $"Control theme switched to {CurrentStyle}");
        return CurrentStyle;
    }

    /// <summary>
    /// Replaces the application's control theme.
    ///
    /// Application.Styles holds exactly one IGlobalStyles at a time, so the swap
    /// removes the previous entry and inserts the new one. Doing it at runtime
    /// rather than via ThemeVariantScope keeps the choice a single persisted
    /// value instead of a per-control-tree setting.
    /// </summary>
    public static void ApplyStyle(Application app, ThemeStyle style)
    {
        ApplyStyle(app, style, "Classic");
    }

    public static void ApplyStyle(Application app, ThemeStyle style, string schemeName)
    {
        if (app is null) return;

        // Drop any theme we previously installed. FluentTheme is matched by type
        // name so we do not need a compile-time reference to it.
        for (int i = app.Styles.Count - 1; i >= 0; i--)
        {
            var s = app.Styles[i];
            var name = s.GetType().FullName ?? string.Empty;

            if (name.Contains("FluentTheme") || name.Contains("Classic.Avalonia"))
            {
                app.Styles.RemoveAt(i);
            }
        }

        if (style == ThemeStyle.Classic)
        {
            // FontAliasing stays on: the theme disables antialiasing by default for
            // the authentic bitmap look, which is unreadable at TV resolution.
            app.Styles.Add(new Classic.Avalonia.Theme.ClassicTheme { FontAliasing = true });
            app.RequestedThemeVariant = ClassicSchemes.Resolve(schemeName);
        }
        else
        {
            app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            app.RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    /// <summary>Applies the persisted style during startup, before the window exists.</summary>
    public static void ApplyPersistedStyle(Application app)
    {
        var config = new AccelaConfigService();

        var saved = config.GetValue(StyleConfigKey, "modern");
        var style = saved.Equals("classic", StringComparison.OrdinalIgnoreCase)
            ? ThemeStyle.Classic
            : ThemeStyle.Modern;

        var savedScheme = config.GetValue(SchemeConfigKey, "Classic");
        if (Array.IndexOf(ClassicSchemes.Names, savedScheme) < 0) savedScheme = "Classic";

        ApplyStyle(app, style, savedScheme);
    }
}