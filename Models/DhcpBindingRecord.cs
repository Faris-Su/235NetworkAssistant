namespace RuijieNetworkAssistant.Models;

/// <summary>`show ip dhcp snooping binding` 的一行（IP → MAC → VLAN → Port）。</summary>
public sealed class DhcpBindingRecord
{
    public string MacAddress { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public string LeaseSeconds { get; init; } = string.Empty;

    public string BindingType { get; init; } = string.Empty;

    public string Vlan { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;
}
