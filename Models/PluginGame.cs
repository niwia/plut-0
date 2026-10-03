using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pluto.Models;

public class PluginGame : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("appid")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("installdir")]
    public string InstallDir { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "at0m";

    [JsonPropertyName("is_atom")]
    public bool IsAtom { get; set; } = true;

    [JsonPropertyName("is_accela")]
    public bool IsAccela { get; set; }

    [JsonPropertyName("install_path")]
    public string InstallPath { get; set; } = string.Empty;

    [JsonPropertyName("appmanifest_path")]
    public string AppmanifestPath { get; set; } = string.Empty;

    [JsonPropertyName("depots")]
    public List<string> Depots { get; set; } = new();

    [JsonPropertyName("keys")]
    public Dictionary<string, string> Keys { get; set; } = new();

    [JsonPropertyName("depot_names")]
    public Dictionary<string, string> DepotNames { get; set; } = new();

    [JsonPropertyName("source")]
    public string Source { get; set; } = "plugin_native";

    [JsonPropertyName("updated_at")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long UpdatedAt { get; set; }

    /// <summary>
    /// Whether this game's AppID is currently listed in SLSsteam's AdditionalApps.
    /// Refreshed on every library reload and shown in the list, so a game that is
    /// registered but not injected is visible at a glance.
    /// </summary>
    [JsonIgnore]
    public bool IsSlsSynced
    {
        get => _isSlsSynced;
        set
        {
            if (_isSlsSynced != value)
            {
                _isSlsSynced = value;
                OnPropertyChanged(nameof(IsSlsSynced));
                OnPropertyChanged(nameof(SyncBadgeText));
            }
        }
    }
    private bool _isSlsSynced;

    /// <summary>Short sync indicator shown on each library row.</summary>
    [JsonIgnore]
    public string SyncBadgeText => IsSlsSynced ? "synced" : "not synced";

    /// <summary>Accent colour for the sync badge, matching the detail page palette.</summary>
    [JsonIgnore]
    public string SyncBadgeColorHex => IsSlsSynced ? "#5EEAD4" : "#7A7A7A";

    private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    /// <summary>
    /// Capsule artwork for the library row. Populated lazily by
    /// <c>LibraryThumbnailLoader</c> - never fetched during library load, since a
    /// full library is hundreds of network requests.
    /// </summary>
    [JsonIgnore]
    public Avalonia.Media.Imaging.Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    private bool _isDownloading;
    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading != value)
            {
                _isDownloading = value;
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(NameOpacityMask));
            }
        }
    }

    private double _downloadPercentage;
    [JsonIgnore]
    public double DownloadPercentage
    {
        get => _downloadPercentage;
        set
        {
            if (Math.Abs(_downloadPercentage - value) > 0.05)
            {
                _downloadPercentage = value;
                OnPropertyChanged(nameof(DownloadPercentage));
                OnPropertyChanged(nameof(NameOpacityMask));
            }
        }
    }

    [JsonIgnore]
    public Avalonia.Media.IBrush? NameOpacityMask
    {
        get
        {
            if (!_isDownloading) return null;
            double progress = Math.Clamp(_downloadPercentage / 100.0, 0.0, 1.0);
            return new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
                GradientStops = new Avalonia.Media.GradientStops
                {
                    new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(255, 255, 255, 255), 0.0),
                    new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(255, 255, 255, 255), progress),
                    new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(51, 255, 255, 255), progress),
                    new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(51, 255, 255, 255), 1.0)
                }
            };
        }
    }

    private string _downloadStatusText = string.Empty;
    [JsonIgnore]
    public string DownloadStatusText
    {
        get => _downloadStatusText;
        set { if (_downloadStatusText != value) { _downloadStatusText = value; OnPropertyChanged(nameof(DownloadStatusText)); } }
    }

    private bool _isNew;
    [JsonIgnore]
    public bool IsNew
    {
        get => _isNew;
        set { if (_isNew != value) { _isNew = value; OnPropertyChanged(nameof(IsNew)); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
