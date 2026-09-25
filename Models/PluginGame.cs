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

    [JsonIgnore]
    public bool IsSlsSynced { get; set; }

    [JsonIgnore]
    public int DepotCount => Depots?.Count ?? 0;

    [JsonIgnore]
    public int KeyCount => Keys?.Count ?? 0;

    [JsonIgnore]
    public string ModeBadgeText => IsAccela ? "assella" : "native";

    [JsonIgnore]
    public string DisplayColorHex => IsAccela ? "#4A6B8A" : "#444444";

    [JsonIgnore]
    public string SelectedColorHex => IsAccela ? "#60A5FA" : "#FFFFFF";

    [JsonIgnore]
    public string UpdatedAtString
    {
        get
        {
            if (UpdatedAt <= 0) return "unknown";
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(UpdatedAt).ToLocalTime();
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return UpdatedAt.ToString();
            }
        }
    }

    private bool _isDownloading;
    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set { if (_isDownloading != value) { _isDownloading = value; OnPropertyChanged(nameof(IsDownloading)); } }
    }

    private double _downloadPercentage;
    [JsonIgnore]
    public double DownloadPercentage
    {
        get => _downloadPercentage;
        set { if (Math.Abs(_downloadPercentage - value) > 0.05) { _downloadPercentage = value; OnPropertyChanged(nameof(DownloadPercentage)); } }
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
