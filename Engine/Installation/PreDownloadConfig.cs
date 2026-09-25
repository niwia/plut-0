using System.Collections.Generic;
using System.Linq;

namespace Pluto.Engine.Installation;

public class PreDownloadDepotItem
{
    public string DepotId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeString { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public bool IsRequired { get; set; }
    public string PlatformTag { get; set; } = "all";
}

public class PreDownloadConfig
{
    public uint AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SelectedLibrarySteamappsDir { get; set; } = string.Empty;
    public string SelectedBranch { get; set; } = "public";
    public List<PreDownloadDepotItem> Depots { get; set; } = new();

    public List<string> GetSelectedDepotIds() =>
        Depots.Where(d => d.IsSelected).Select(d => d.DepotId).ToList();

    public long GetTotalSelectedSize() =>
        Depots.Where(d => d.IsSelected).Sum(d => d.SizeBytes);
}
