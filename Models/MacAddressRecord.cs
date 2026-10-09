namespace RuijieNetworkAssistant.Models;

/// <summary>`show mac-address-table` 的一行（MAC → VLAN → Port → Status/Type）。</summary>
public sealed class MacAddressRecord
{
    public string MacAddress { get; init; } = string.Empty;

    public string Vlan { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;
}
