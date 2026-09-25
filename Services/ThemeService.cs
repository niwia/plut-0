using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Pluto.Services;

public record ThemeColorPreset(string Name, string HighlightHex, string DimmedHex);

public class ThemeService
{
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

    public void LoadFromConfig()
    {
        var savedNative = _configService.GetValue("theme_native_color", "#FFFFFF");
        var savedAccela = _configService.GetValue("theme_accela_color", "#60A5FA");

        int nIdx = NativePresets.FindIndex(p => p.HighlightHex.Equals(savedNative, StringComparison.OrdinalIgnoreCase));
        _nativeIndex = nIdx >= 0 ? nIdx : 0;

        int aIdx = AccelaPresets.FindIndex(p => p.HighlightHex.Equals(savedAccela, StringComparison.OrdinalIgnoreCase));
        _accelaIndex = aIdx >= 0 ? aIdx : 0;
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
}
