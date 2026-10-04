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

public class ThemeService
{
    private const string StyleConfigKey = "theme_style";

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

        ApplyStyle(app, CurrentStyle);
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
            app.Styles.Add(new Classic.Avalonia.Theme.ClassicTheme());
            // The Classic palette is light-surface; a dark variant would fight it.
            app.RequestedThemeVariant = ThemeVariant.Light;
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

        ApplyStyle(app, style);
    }
}