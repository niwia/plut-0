using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Pluto.Engine.Installation;

public class PreDownloadDepotItem : INotifyPropertyChanged
{
    public string DepotId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeString { get; set; } = string.Empty;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRequired { get; set; }
    public string PlatformTag { get; set; } = "all";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class PreDownloadConfig
{
    public uint AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SelectedLibrarySteamappsDir { get; set; } = string.Empty;
    public int SelectedLibraryIndex { get; set; } = 0;
    public string SelectedBranch { get; set; } = "public";
    public List<PreDownloadDepotItem> Depots { get; set; } = new();

    public List<string> GetSelectedDepotIds() =>
        Depots.Where(d => d.IsSelected).Select(d => d.DepotId).ToList();

    public long GetTotalSelectedSize() =>
        Depots.Where(d => d.IsSelected).Sum(d => d.SizeBytes);
}

