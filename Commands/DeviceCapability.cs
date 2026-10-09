namespace RuijieNetworkAssistant.Commands;

/// <summary>
/// 设备能力标记。不同锐捷型号/固件存在 CLI 差异，架构上先预留能力开关，
/// 由命令生成器按能力决定是否允许生成某条命令（V0.1 全部按基础锐捷 CLI 打开）。
/// </summary>
public sealed class DeviceCapability
{
    public bool SupportsSwitchportTrunkNativeVlan { get; init; } = true;

    public bool SupportsSwitchportTrunkAllowedVlanAddRemove { get; init; } = true;

    public bool SupportsInterfaceRange { get; init; } = true;

    public bool SupportsMediumType { get; init; } = true;

    public bool SupportsLldp { get; init; } = true;

    public bool SupportsDhcpSnoopingBinding { get; init; } = true;

    public static DeviceCapability BasicRuijie { get; } = new();
}

/// <summary>命令配置档：型号 + 固件 + 能力集合，用于后续版本按设备切换命令格式。</summary>
public sealed class CommandProfile
{
    public string Name { get; init; } = "basic-ruijie";

    public string? DeviceModel { get; init; }

    public string? FirmwareVersion { get; init; }

    public DeviceCapability Capability { get; init; } = DeviceCapability.BasicRuijie;

    public static CommandProfile Default { get; } = new();
}
