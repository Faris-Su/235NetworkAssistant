namespace RuijieNetworkAssistant.Models;

/// <summary>`show int trunk` 解析出的 Trunk 端口信息。</summary>
public sealed class TrunkPortRecord
{
    public string Port { get; init; } = string.Empty;

    public string Mode { get; set; } = string.Empty;

    public string Encapsulation { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string NativeVlan { get; set; } = string.Empty;

    public string AllowedVlans { get; set; } = string.Empty;

    public string ActiveVlans { get; set; } = string.Empty;

    public string ForwardingVlans { get; set; } = string.Empty;
}
