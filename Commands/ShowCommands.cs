namespace RuijieNetworkAssistant.Commands;

/// <summary>
/// 查看类命令集中定义。CLI 字符串禁止散落到 View / ViewModel / Button_Click。
/// 命令来源：项目资料《锐捷交换机指令》与项目提示词中列出的项目已有命令。
/// 未在资料中出现过的命令不得凭猜测加入，见 docs/commands.md。
/// </summary>
public static class ShowCommands
{
    public const string Version = "show version";
    public const string RunningConfig = "show running-config";

    /// <summary>
    /// `show startup-config`：设备**下次启动会加载**的配置。
    /// 用途：和 running-config 对比就能回答现场最常问的一句 —— "我改完是不是忘了保存（write）？"
    /// 真机实测（锐捷交换机 / RGOS 10.x，2026-09-22）该命令可用，输出格式与 running-config 一致。
    /// </summary>
    public const string StartupConfig = "show startup-config";
    public const string InterfaceStatus = "show interface status";
    public const string InterfaceBrief = "show ip interface brief";
    public const string Vlan = "show vlan";
    public const string MacAddressTable = "show mac-address-table";
    public const string MacAddressTableDynamic = "show mac-address-table dynamic";
    public const string Logging = "show logging";
    public const string Cpu = "show cpu";
    public const string Clock = "show clock";
    public const string Arp = "show arp";
    public const string AggregatePortSummary = "show aggregatePort summary";
    public const string InterfacesTrunk = "show int trunk";
    public const string LldpNeighbors = "show lldp neighbors";
    public const string DhcpSnoopingBinding = "show ip dhcp snooping binding";

    public static string VlanById(int vlanId) => $"show vlan id {vlanId}";

    public static string InterfaceDetail(string interfaceName) => $"show interface {interfaceName}";

    public static string InterfaceSwitchport(string interfaceName) => $"show interface {interfaceName} switchport";

    public static string LldpNeighborDetail(string interfaceName) =>
        $"show lldp neighbors interface {interfaceName} detail";

    public static string MacAddressTableFilter(string fragment) => $"show mac-address-table | in {fragment}";

    /// <summary>Show Center 固定条目：UI 只引用这里的顺序，不自己拼命令。</summary>
    public static IReadOnlyList<string> ShowCenterItems { get; } = new[]
    {
        Version,
        InterfaceStatus,
        Vlan,
        MacAddressTable,
        InterfaceBrief,
        Cpu,
        Logging,
        RunningConfig,
    };
}
