namespace RuijieNetworkAssistant.Models;

/// <summary>`show vlan` 的一行 VLAN 信息（VLAN ID / Name / Status / Ports）。</summary>
public sealed class VlanInfoRecord
{
    public int VlanId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    /// <summary>端口列表。真机端口多时会换行，解析器需要把续行合并进来，因此允许 set。</summary>
    public string Ports { get; set; } = string.Empty;

    public int PortCount => Ports.Length == 0
        ? 0
        : Ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}
