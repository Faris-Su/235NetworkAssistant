using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 一张本机网卡的**只读快照**（概览页「本机网络」卡片用）。
/// 现场排障第一眼看的就是它：我这台电脑现在是什么地址、网关在哪、网线插没插。
/// </summary>
public sealed record LocalNetworkAdapter(
    string Name,
    string Description,
    bool IsVirtual,
    bool IsUp,
    string AddressText,
    string Gateway,
    string Mac)
{
    /// <summary>界面显示名：虚拟网卡带标记 —— 现场一堆 VMware / Hyper-V / TAP / VPN，不标就分不清该看哪个。</summary>
    public string DisplayName => IsVirtual ? $"{Name}（虚拟）" : Name;
}

/// <summary>一次采集的结果。</summary>
public sealed record LocalNetworkSnapshot(
    IReadOnlyList<LocalNetworkAdapter> Adapters,
    string DefaultGateway,
    string DnsServers,
    string SubnetNote,
    DateTimeOffset CollectedAt);

/// <summary>
/// 读本机网卡信息（只读、不依赖任何连接）。
///
/// 过滤规则（现场那台小电脑有 8 个网卡，不筛会把屏幕塞满）：
///   · 丢掉 Loopback / Tunnel；
///   · **虚拟网卡且没有 IPv4 的一律不显示**（VMware / Hyper-V / 各种 TAP 平时都这样）；
///   · **物理网卡即使没插线也留着**并标注「未插线」——"网线插没插"正是现场第一步要看的。
/// </summary>
public static class LocalNetworkInfoService
{
    /// <summary>
    /// 按网卡名/描述判断是不是虚拟网卡（VMware / Hyper-V / TAP / VPN / 蓝牙等）。
    /// 抽成独立方法是为了能单测 —— 判断错了会把真实网卡标成虚拟、或者反过来把一堆虚拟网卡当成真网卡。
    /// </summary>
    internal static bool LooksVirtual(string? nameOrDescription)
    {
        if (string.IsNullOrWhiteSpace(nameOrDescription))
        {
            return false;
        }

        string[] markers =
        {
            "virtual", "vmware", "hyper-v", "vethernet", "tap-", "tap ", "tun ", "tunnel",
            "vpn", "radmin", "loopback", "bluetooth", "蓝牙", "wan miniport", "npcap", "wintun",
            "docker", "wsl", "tailscale", "zerotier",
        };

        var text = nameOrDescription.ToLowerInvariant();
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>采集本机网卡。失败时返回空列表 + 一句说明，绝不抛异常（概览页不能被网卡信息拖崩）。</summary>
    public static LocalNetworkSnapshot Collect(string? targetHost = null)
    {
        var adapters = new List<LocalNetworkAdapter>();
        var physicalGateways = new List<string>();
        var virtualGateways = new List<string>();
        var physicalDns = new List<string>();
        var virtualDns = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;   // 个别网卡读属性会抛，跳过即可
                }

                var v4 = properties.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var nicGateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                var isVirtual = LooksVirtual(nic.Description) || LooksVirtual(nic.Name);

                // 169.254.x.x 是"DHCP 没拿到地址时自己编的"链路本地地址（APIPA），不是真地址：
                // 虚拟网卡出现它 → 整行没意义，直接不显示；物理网卡出现它 → 要提示（说明 DHCP 失败了）。
                var isLinkLocal = v4 is not null && IsLinkLocal(v4.Address);
                if (isVirtual && (v4 is null || isLinkLocal))
                {
                    continue;   // 虚拟网卡且没有地址：不显示（省屏）
                }

                var isUp = nic.OperationalStatus == OperationalStatus.Up;
                var addressText = v4 is not null && !isLinkLocal
                    ? $"{v4.Address}/{PrefixLength(v4.IPv4Mask)}"
                    : isLinkLocal
                        ? "—（169.254 自动地址：DHCP 没拿到地址）"
                        : isUp ? "—（没配 IP）" : "—（未插线）";

                adapters.Add(new LocalNetworkAdapter(
                    string.IsNullOrWhiteSpace(nic.Name) ? "（未命名网卡）" : nic.Name,
                    nic.Description ?? string.Empty,
                    isVirtual,
                    isUp,
                    addressText,
                    nicGateway?.ToString() ?? "—",
                    FormatMac(nic.GetPhysicalAddress())));

                // 网关 / DNS 先只从**物理网卡**收集（虚拟网卡那套 26.0.0.1 / VMnet 网关会把
                // "默认网关"带偏 —— 现场表现为"默认网关显示成 Radmin VPN 的地址"）。
                if (!isVirtual)
                {
                    if (nicGateway is not null && isUp)
                    {
                        physicalGateways.Add(nicGateway.ToString());
                    }

                    foreach (var server in properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var text = server.ToString();
                        if (!physicalDns.Contains(text, StringComparer.Ordinal))
                        {
                            physicalDns.Add(text);
                        }
                    }
                }
                else
                {
                    if (nicGateway is not null && isUp)
                    {
                        virtualGateways.Add(nicGateway.ToString());
                    }

                    foreach (var server in properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var text = server.ToString();
                        if (!virtualDns.Contains(text, StringComparer.Ordinal))
                        {
                            virtualDns.Add(text);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            return new LocalNetworkSnapshot(
                Array.Empty<LocalNetworkAdapter>(),
                "—",
                "—",
                "读取本机网卡信息失败（不影响其它功能）。",
                DateTimeOffset.Now);
        }

        // 排序：物理网卡在前、虚拟在后；同组里"已插线"优先，再按名字
        var ordered = adapters
            .OrderBy(a => a.IsVirtual)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 默认网关 / DNS：优先物理网卡，物理网卡没有才退回虚拟网卡（VPN 场景下有时只靠虚拟网卡上网）
        var gateway = physicalGateways.FirstOrDefault()
                      ?? virtualGateways.FirstOrDefault()
                      ?? "—";
        var dns = physicalDns.Count > 0 ? physicalDns : virtualDns;

        return new LocalNetworkSnapshot(
            ordered,
            gateway,
            dns.Count == 0 ? "—" : string.Join("、", dns.Take(3)),
            BuildSubnetNote(ordered, targetHost, gateway),
            DateTimeOffset.Now);
    }

    /// <summary>
    /// 「本机网卡 ↔ 目标地址」的一句话结论。
    ///
    /// ⚠️ 2026-09-25 用户反馈（校园网 Wi-Fi 连交换机，软件提示"不在同一网段"）后重新界定这句话的定位：
    ///   **同网段与否只是"走不走网关"的说明，绝不是"能不能连"的结论**。
    ///   Telnet 就是 TCP 连到 目标IP:23，跨网段只要能路由到、中间放行 TCP、设备开了服务就能连 ——
    ///   校园网/办公网里跨网段 Telnet 到交换机是常态。旧文案写成"（这是连不上的常见原因）"，
    ///   会被读成软件认定"不同网段 ⇒ 不能连"，属于**误导性诊断**，必须按真实排查顺序改写。
    /// </summary>
    private static string BuildSubnetNote(
        IReadOnlyList<LocalNetworkAdapter> adapters,
        string? targetHost,
        string gateway)
    {
        var withAddress = adapters.Where(a => a.AddressText.Contains('/')).ToList();
        if (withAddress.Count == 0)
        {
            return "本机没有可用的 IPv4 地址（网卡没插线或没配地址）——连设备前先解决这个。";
        }

        var targetText = IpAddressHelper.ExtractFirst(targetHost);
        if (targetText is null)
        {
            return "还没有目标地址：连上设备、或在【SNMP】页填了 IP 之后，这里会做同网段检查。";
        }

        if (!IPAddress.TryParse(targetText, out var target))
        {
            return "当前目标不是 IPv4 地址，跳过同网段检查。";
        }

        if (IPAddress.IsLoopback(target))
        {
            return "目标是本机回环地址（127.0.0.1），不存在网段问题。";
        }

        foreach (var adapter in withAddress)
        {
            var parts = adapter.AddressText.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var local)
                && int.TryParse(parts[1], out var prefix)
                && IsInSameSubnet(target, local, prefix))
            {
                return adapter.IsVirtual
                    ? $"与目标 {target} 同网段，但匹配到的是虚拟网卡（{adapter.Name} {adapter.AddressText}）——"
                      + "隧道/虚拟网络不一定真能到设备，连不上时优先看物理网卡。"
                    : $"与目标 {target} 同网段（{adapter.Name} {adapter.AddressText}）——二层直达，不经过网关。";
            }
        }

        var gatewayText = string.IsNullOrWhiteSpace(gateway) || gateway == "—"
            ? "没读到默认网关"
            : $"默认网关 {gateway}";
        return $"与目标 {target} 不同网段（{gatewayText}）。"
             + "跨网段本身不影响 Telnet —— 只要能路由到目标就能连；"
             + "连不上时按顺序查：① 先 ping 网关/目标 ② 中间防火墙或 ACL 是否放行 TCP 23 ③ 交换机是否开启了 Telnet。";
    }

    internal static int PrefixLength(IPAddress? mask)
    {
        if (mask is null)
        {
            return 32;
        }

        var bits = 0;
        foreach (var b in mask.GetAddressBytes())
        {
            for (var i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) == 0)
                {
                    return bits;
                }

                bits++;
            }
        }

        return bits;
    }

    /// <summary>169.254.0.0/16 = 链路本地地址（APIPA）—— DHCP 没拿到地址时系统自己编的，不能当真实地址用。</summary>
    internal static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    internal static bool IsInSameSubnet(IPAddress target, IPAddress local, int prefixLength)
    {
        var t = target.GetAddressBytes();
        var l = local.GetAddressBytes();
        if (t.Length != l.Length || prefixLength is < 0 or > 32)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (t[i] != l[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (t[fullBytes] & mask) == (l[fullBytes] & mask);
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "—" : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
