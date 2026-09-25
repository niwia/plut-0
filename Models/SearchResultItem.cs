using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace Pluto.Models;

public class SearchResultItem : INotifyPropertyChanged
{
    private Bitmap? _thumbnail;
    private bool _isInstalled;
    private string _installMode = string.Empty;
    private bool _isCached;

    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HeaderImageUrl { get; set; } = string.Empty;
    public bool ManifestAvailable { get; set; } = true;
    public int Score { get; set; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled != value)
            {
                _isInstalled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(TitleColorHex));
            }
        }
    }

    public string InstallMode
    {
        get => _installMode;
        set
        {
            if (_installMode != value)
            {
                _installMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsCached
    {
        get => _isCached;
        set
        {
            if (_isCached != value)
            {
                _isCached = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail != null;

    public string StatusText
    {
        get
        {
            if (IsInstalled)
            {
                return !string.IsNullOrEmpty(InstallMode) ? $"in library ({InstallMode})" : "in library";
            }
            if (IsCached)
            {
                return "cached keys";
            }
            return "online";
        }
    }

    public string StatusColorHex
    {
        get
        {
            if (IsInstalled) return "#81C784"; // Soft green
            if (IsCached) return "#64B5F6";    // Soft blue
            return "#666666";                  // Muted gray
        }
    }

    public string TitleColorHex
    {
        get
        {
            if (IsInstalled) return "#A5D6A7"; // Soft mint green for installed games
            return "#FFFFFF";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
