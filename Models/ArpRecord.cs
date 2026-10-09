namespace RuijieNetworkAssistant.Models;

/// <summary>
/// `show arp` 的一行：IP → MAC → 接口。
///
/// 为什么需要它：MAC/IP 页的"IP 反查"原来只查 DHCP Snooping 绑定表，
/// 而**服务器、打印机、AP、教师机这类静态 IP 根本不在 DHCP 表里**，
/// 很多接入交换机也没开 DHCP Snooping —— 于是现场最常做的第一步（"这个 IP 在哪台交换机哪个口"）
/// 在软件里查不到，只能切到 Show Center 手敲 `show arp` 再肉眼比对。
/// ARP 表是每台三层设备都有的，正好补上这一类查询。
/// </summary>
public sealed class ArpRecord
{
    /// <summary>对端 IP（`Address` 列）。</summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>对端 MAC（`Hardware` 列，统一格式化成 02:00:00:00:00:01）。</summary>
    public string MacAddress { get; init; } = string.Empty;

    /// <summary>老化时间（分钟）；真机上静态/永久项显示为 `--`。</summary>
    public string Age { get; init; } = string.Empty;

    /// <summary>接口原文可能是 VLAN 类型的逻辑接口。</summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>
    /// 从 Interface 里抽出的 VLAN 号（抽不到就空）。
    /// 用途：和 MAC 地址表的 VLAN 列对上，判断"这个 IP 的 MAC 是不是从同一个 VLAN 学到的"。
    /// </summary>
    public string Vlan { get; init; } = string.Empty;
}
