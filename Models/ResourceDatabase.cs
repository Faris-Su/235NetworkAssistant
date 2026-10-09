namespace RuijieNetworkAssistant.Models;

/// <summary>本地资源库快照。离线存储，导入 Excel 后写入一次，运行时不再读 Excel。</summary>
public sealed class ResourceDatabase
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset? LastImportedAt { get; set; }

    public List<string> SourceFiles { get; set; } = new();

    public List<SwitchRecord> Switches { get; set; } = new();

    public List<VlanRecord> Vlans { get; set; } = new();

    public List<LocationRecord> Locations { get; set; } = new();

    public int SwitchCount => Switches.Count;

    public int VlanCount => Vlans.Count;

    public int LocationCount => Locations.Count;
}
