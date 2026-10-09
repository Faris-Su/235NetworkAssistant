using System.Net;
using System.Net.Sockets;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>
/// 本端某个口上的 LLDP 邻居查询结果。
/// <see cref="Responded"/>=false 表示**根本没问到 LLDP**（设备没开 / SNMP 视图没放开 / 超时）——
/// 这时**不能**下"这个口没有邻居（是终端口）"的结论。
/// </summary>
internal sealed class LldpNeighbor
{
    public bool Responded { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string ManagementIp { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 用**标准 BRIDGE-MIB** 在三层/二层设备上定位一个 MAC：MAC → 桥口 → ifIndex → 端口名。
///
/// 为什么不用锐捷私有 MAC 表：那张表**没有端口列**（真机实测 `.4` 列恒定），
/// 命中也答不出"哪个口"；而标准转发表天生就是 MAC → 桥口，且实测更省请求（见 SnmpOidRepository 里那段实测记录）。
///
/// 这个类只负责**单台设备**的定位；跨设备的批量扫描由调用方（定位页）组织。
/// </summary>
public sealed class SnmpMacLocator
{
    private readonly SnmpV2cClient _client;
    private readonly ILogService? _log;

    public SnmpMacLocator(SnmpV2cClient client, ILogService? log = null)
    {
        _client = client;
        _log = log;
    }

    /// <summary>
    /// 在一台设备的 ARP 表里把 IP 解析成 MAC（顺带给出 VLAN）。
    ///
    /// 为什么必须先做这一步：**IP 只存在于有该网段 SVI 的三层设备上**，
    /// 接入交换机（纯二层）的 ARP 表基本是空的。所以"按 IP 查"的顺序一定是
    /// 先去三层设备问 ARP 拿到 MAC，再用转发表（FDB）去二层设备上找端口 —— 顺序反了就是白扫几百台接入交换机。
    ///
    /// ARP 表索引 = `&lt;VLAN&gt;.&lt;IP 4 字节&gt;`（真机实测），所以比索引末 4 段就知道是不是目标 IP，
    /// 不用去解析整行；值里能归一化成 MAC 的那条就是列 2（不写死列号，型号间更稳）。
    /// </summary>
    public async Task<IpToMacResult> ResolveIpToMacAsync(
        string host,
        int port,
        string community,
        string ip,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        if (!IpAddressHelper.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new IpToMacResult { Note = $"IP 格式无法识别：{ip}" };
        }

        var bytes = address.GetAddressBytes();
        var suffix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{bytes[3]}";

        var arp = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.ArpRoot, timeoutMs, retries,
                SnmpOidRepository.ArpMaxVarbinds, cancellationToken)
            .ConfigureAwait(false);
        if (arp.Rows.Count == 0)
        {
            // ⚠️ "没回应"和"回应了但表是空的"必须分开（DSH 复查挑出来的）：
            // 旧写法把 Community 错/不可达/一次丢包超时统统说成"这台设备没有 ARP 表"，
            // 最后汇成"这些设备里都没有这个 IP（已老化）"—— 方向全错的排障建议。
            return arp.Responded
                ? new IpToMacResult { NoArpTable = true, Note = "这台设备没有返回 ARP 表（纯二层设备本来就没有 ARP 表）" }
                : new IpToMacResult
                {
                    Unreachable = true,
                    Note = $"这台设备没有响应（{(arp.Error is { Length: > 0 } error ? error : "SNMP 请求超时")}）"
                           + "—— 检查 IP 是否可达、Community 是否正确、UDP 161 是否放通。",
                };
        }

        foreach (var row in arp.Rows)
        {
            var rest = row.Oid.StartsWith(SnmpOidRepository.ArpRoot + ".", StringComparison.Ordinal)
                ? row.Oid[(SnmpOidRepository.ArpRoot.Length + 1)..]
                : null;
            if (rest is null || !rest.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = rest.Split('.');
            var mac = row.Bytes is { Length: 6 } raw
                ? MacAddressHelper.FormatBytes(raw)
                : MacAddressHelper.Normalize(row.Display);
            if (mac is null)
            {
                continue;   // 同一索引下不是 MAC 的那几列（VLAN/状态/ifIndex）跳过
            }

            // 索引第二段在锐捷私有 ARP 表里是 **ifIndex**（不是 VLAN）——
            // 设备实测：ARP 索引中包含三层接口 ifIndex，可通过 ifName 映射到对应 VLAN SVI。
            // 所以这里再走一次 ifName 把它翻成接口名，VLAN 号从名字里抽。
            var entryIfIndex = parts.Length >= 2 ? parts[1] : string.Empty;
            var interfaceName = await ResolveIfNameAsync(host, port, community, entryIfIndex, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            return new IpToMacResult
            {
                Mac = mac,
                Interface = interfaceName,
                Vlan = ExtractVlanNumber(interfaceName),
            };
        }

        return new IpToMacResult
        {
            Note = $"这台设备的 ARP 表里没有 {ip}（表里共 {arp.Rows.Count} 条）"
                   + (arp.Truncated ? "；⚠ 表被行数上限截断，没命中不代表不存在" : string.Empty),
        };
    }

    /// <summary>
    /// 在一台设备上定位 MAC。
    /// 返回 <see cref="MacLocation"/>：<c>Found=false</c> 表示这台设备上没有这个 MAC
    /// （**注意**：也可能只是这台设备不支持标准转发表 / 被截断，<c>NotSupported</c> 会如实标出来，别当成"不存在"）。
    /// </summary>
    public async Task<MacLocation> LocateAsync(
        string host,
        int port,
        string community,
        string mac,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var normalized = MacAddressHelper.Normalize(mac);
        if (normalized is null)
        {
            return MacLocation.Invalid($"MAC 格式无法识别：{mac}");
        }

        // ① 转发表：索引是 6 字节 MAC，取值是桥口号
        var fdb = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.BridgeFdbPortRoot, timeoutMs, retries,
                SnmpOidRepository.BridgeFdbMaxRows, cancellationToken)
            .ConfigureAwait(false);

        if (fdb.Rows.Count == 0)
        {
            // ⚠️ "一个节点都没回"和"回了但表是空的"必须分开：
            // 前者是**连不上**（超时/不可达/Community 不对），报成"该设备不支持"会让用户查错方向。
            // 真机联测里就撞到过：追踪的下一跳不可达，结果标成了"该设备不支持"。
            return fdb.Responded
                ? MacLocation.Unsupported("这台设备没有返回标准转发表（dot1dTpFdbPort）—— 可能是二层表未开放 SNMP 视图。")
                : MacLocation.Unreachable(
                    $"这台设备没有响应（{(fdb.Error is { Length: > 0 } error ? error : "SNMP 请求超时")}）——"
                    + "检查 IP 是否可达、Community 是否正确、UDP 161 是否放通。");
        }

        var bridgePort = -1;
        foreach (var row in fdb.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.BridgeFdbPortRoot);
            if (index is null)
            {
                continue;
            }

            // 索引 = 6 个十进制字节（如 2.0.0.0.0.1）→ 12 位十六进制
            var bytes = index.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (bytes.Length != 6)
            {
                continue;
            }

            var text = string.Concat(bytes.Select(b => int.TryParse(b, out var value) ? value.ToString("x2") : string.Empty));
            if (!string.Equals(text, normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bridgePort = row.Number is { } value2
                ? (int)value2
                : int.TryParse(row.Display, out var parsed) ? parsed : -1;
            break;
        }

        if (bridgePort < 0)
        {
            return MacLocation.NotFound($"这台设备的转发表里没有该 MAC（共 {fdb.Rows.Count} 条）{TruncationNote(fdb)}");
        }

        // 桥口 0 / 超出映射表：那是"本机/非交换口"条目，不能编一个端口名出来
        var portMap = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.BridgePortIfIndexRoot, timeoutMs, retries,
                SnmpOidRepository.BridgePortMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var ifIndex = -1;
        foreach (var row in portMap.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.BridgePortIfIndexRoot);
            if (index is not null && int.TryParse(index, out var bridge) && bridge == bridgePort)
            {
                ifIndex = row.Number is { } value ? (int)value : -1;
                break;
            }
        }

        if (ifIndex <= 0)
        {
            // ⚠️ 这里也要分"没问到"和"问了确实没有"（DSH 复查挑出来的）：
            // 旧写法不管三七二十一就断言"这是设备自身/网关条目" —— 而映射表**一次超时**就会走到这里，
            // 于是一条真实路径被判死。只有"映射表回应了、但里面确实没有这个桥口"才是设备自身条目。
            return portMap.Responded
                ? new MacLocation
                {
                    Status = MacLocationStatus.FoundOnDeviceItself,
                    MacAddress = MacAddressHelper.Format(normalized),
                    BridgePort = bridgePort,
                    IfIndex = null,
                    PortName = string.Empty,
                    Note = $"转发表里命中桥口 {bridgePort}，但桥口→ifIndex 映射表里没有这个口："
                           + "这通常是**设备自身/网关**条目（不是接在物理口上的用户设备），所以不给端口名。",
                }
                : new MacLocation
                {
                    Status = MacLocationStatus.Found,
                    MacAddress = MacAddressHelper.Format(normalized),
                    BridgePort = bridgePort,
                    IfIndex = null,
                    PortName = string.Empty,
                    Note = $"转发表里命中桥口 {bridgePort}，但**桥口→ifIndex 映射表没问到**"
                           + $"（{(portMap.Error is { Length: > 0 } mapError ? mapError : "SNMP 请求超时")}）："
                           + "端口名未知 —— 这**不代表**它是设备自身条目，请重试或检查 SNMP 可达性。",
                };
        }

        // ③ ifIndex → 端口名
        var ifNames = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName), timeoutMs, retries,
                SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var portName = string.Empty;
        foreach (var row in ifNames.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName));
            if (index is not null && int.TryParse(index, out var candidate) && candidate == ifIndex)
            {
                portName = row.Display?.Trim() ?? string.Empty;
                break;
            }
        }

        // 命中在 VLAN 接口上 = 设备自身/网关条目，避免界面把它误认为终端接入物理端口
        var onVlanInterface = InterfaceNameHelper.TryParse(portName, out var portToken)
                              && portToken.IsLogical
                              && portToken.Prefix == "vl";

        return new MacLocation
        {
            Status = onVlanInterface ? MacLocationStatus.FoundOnDeviceItself : MacLocationStatus.Found,
            MacAddress = MacAddressHelper.Format(normalized),
            BridgePort = bridgePort,
            IfIndex = ifIndex,
            PortName = portName,
            Note = DescribePort(bridgePort, ifIndex, portName),
        };
    }

    /// <summary>
    /// 端口名的含义判读。这是整个定位功能里**最容易骗人**的一步：
    /// 真机实测过三种情况，不能都笼统报成"在这个口上"。
    ///   · `Vl100`（VLAN 接口）—— 交换机**自己的 SVI**，命中的是本机/网关 MAC，
    ///     不是接在物理口上的用户设备。不能把它报告成终端接入物理端口；
    ///   · `AggregatePort`/`Ag` —— 聚合口，实际出线在**成员口中的某一个**，得继续确认；
    ///   · `Gi0/1` 这类物理口 —— 那才是"接在这个口上"。
    /// </summary>
    private static string DescribePort(int bridgePort, int ifIndex, string portName)
    {
        if (portName.Length == 0)
        {
            return $"桥口 {bridgePort} → ifIndex {ifIndex}，但这台设备没返回该 ifIndex 的 ifName（端口名未知）";
        }

        if (!InterfaceNameHelper.TryParse(portName, out var token) || !token.IsLogical)
        {
            return string.Empty;
        }

        return token.Prefix == "vl"
            ? $"命中的是 VLAN 接口 {portName}：这是**设备自身/网关**的条目（或从该 SVI 学到的本机 MAC），"
              + "不是接在物理口上的用户设备 —— 请改用「按 IP 查」或到这台设备的 MAC 表上看物理口。"
            : $"命中的是聚合口 {portName}：实际出线在它的成员口中的某一个，需要到成员口继续确认。";
    }

    private static string TruncationNote(SnmpWalkResponse walk) =>
        walk.Truncated ? "（⚠ 转发表被行数上限截断，没命中不代表不存在）" : string.Empty;

    private static string? LastIndex(string oid, string root) =>
        oid.StartsWith(root + ".", StringComparison.Ordinal) ? oid[(root.Length + 1)..] : null;

    /// <summary>
    /// 逐跳下行追踪：从 <paramref name="startHost"/> 开始，顺着「转发表 + LLDP 邻居」一路往下走，
    /// 直到算出"这个口接的就是用户设备"。
    ///
    /// 为什么它比"扫全部设备"强（DSH 咨询 + 真机数据都指向同一结论）：
    /// 核心/汇聚的转发表里**必然包含全部下游 MAC**，所以"命中即停"很容易停在核心那一跳；
    /// 而 411 台全扫还要等核心自己串行几十秒。跟着 LLDP 走一条路径（通常 2~5 跳）才是量级上的差别。
    ///
    /// 终止条件：① 转发表里没这个 MAC；② 命中的口没有 LLDP 邻居（= 接的是终端，这就是终点）；
    /// ③ 邻居没给管理地址；④ 成环（查过的 IP 不再进）；⑤ 到达 <paramref name="maxHops"/>。
    /// </summary>
    public async Task<List<MacTraceHop>> TraceAsync(
        string startHost,
        string community,
        string mac,
        int timeoutMs,
        int retries,
        int maxHops,
        CancellationToken cancellationToken)
    {
        var hops = new List<MacTraceHop>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = startHost;

        for (var hop = 0; hop < Math.Clamp(maxHops, 1, 16); hop++)
        {
            if (!visited.Add(current))
            {
                hops.Add(new MacTraceHop
                {
                    DeviceIp = current,
                    Status = "成环",
                    Note = "这个 IP 已经查过了，停止（避免在环路里死循环）",
                });
                break;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var location = await LocateAsync(current, 161, community, mac, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();

            if (!location.Found)
            {
                hops.Add(new MacTraceHop
                {
                    DeviceIp = current,
                    Status = location.Status switch
                    {
                        MacLocationStatus.Unsupported => "该设备不支持",
                        MacLocationStatus.Unreachable => "连不上",
                        MacLocationStatus.Invalid => "输入有误",
                        _ => "未命中",
                    },
                    Note = location.Note,
                    ElapsedMs = watch.ElapsedMilliseconds,
                });
                break;
            }

            var neighbor = location.IfIndex is { } ifIndex
                ? await FindNeighborOnPortAsync(current, 161, community, ifIndex, timeoutMs, retries, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var onDeviceItself = location.Status == MacLocationStatus.FoundOnDeviceItself;
            // 没问到 LLDP 时：端口命中了，但**不能断言**这里是终端口 —— 单独一种状态说清楚。
            var lldpUnknown = neighbor is { Responded: false };
            var hasNeighbor = neighbor is { Responded: true, ManagementIp.Length: > 0 };
            hops.Add(new MacTraceHop
            {
                DeviceIp = current,
                Port = location.PortName,
                BridgePort = location.BridgePort,
                IfIndex = location.IfIndex,
                Status = onDeviceItself
                    ? "设备自身"
                    : lldpUnknown
                        ? "命中（对端未知）"
                        : neighbor is { Responded: true, Name.Length: 0 }
                            ? "命中（终端口）"
                            : neighbor is null
                                ? "命中（端口未知）"
                                : "命中（下挂交换机）",
                NeighborName = neighbor is { } n1 ? n1.Name : string.Empty,
                NeighborPort = neighbor is { } n2 ? n2.Port : string.Empty,
                NextHopIp = hasNeighbor && neighbor is { } n3 ? n3.ManagementIp : string.Empty,
                Note = lldpUnknown
                    ? location.Note
                      + $"｜⚠ LLDP 没问到（{(neighbor is { } u && u.Error.Length > 0 ? u.Error : "SNMP 请求超时或设备未开 LLDP")}）："
                      + "**不能断定这个口接的是终端**，请重试或到设备上确认对端。"
                    : location.Note,
                ElapsedMs = watch.ElapsedMilliseconds,
            });

            if (onDeviceItself || lldpUnknown || !hasNeighbor || neighbor is not { } next)
            {
                break;   // 没有下一跳 → 链路到头了
            }

            current = next.ManagementIp;
        }

        return hops;
    }

    /// <summary>
    /// 本端 ifIndex 上挂着哪个 LLDP 邻居（含邻居的管理 IP）。没有邻居返回 null。
    ///
    /// LLDP-MIB 的索引是 `&lt;timeMark&gt;.&lt;本端 ifIndex&gt;.&lt;远端条目号&gt;`，
    /// 三段要一起用才拼得回同一个远端条目 —— 只按 ifIndex 找会在"同一口挂多台"时串行。
    /// 管理 IPv4 地址写在 lldpRemManAddrTable 的**索引**里（列里是 subtype/ifIndex 之类），别去读列值。
    /// </summary>
    private async Task<LldpNeighbor> FindNeighborOnPortAsync(
        string host,
        int port,
        string community,
        int ifIndex,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var names = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.LldpRemSysNameRoot, timeoutMs, retries,
                SnmpOidRepository.LldpRemMaxRows, cancellationToken)
            .ConfigureAwait(false);
        if (names.Rows.Count == 0)
        {
            // ⚠️ 不能直接返回"没邻居"：**没问到 LLDP** 和"这个口确实没有邻居"是两回事。
            // DSH 复查指出：旧写法把"设备没开 LLDP / SNMP 视图没放开 / 一次丢包超时"当成
            // "这个口没有邻居，是终端口"，于是追踪会给出一个**自信但是错的**答案 ——
            // 那个口上面往往还挂着一台交换机。Responded 如实带出去，由调用方决定怎么表述。
            return new LldpNeighbor
            {
                Responded = names.Responded,
                Error = names.Error ?? string.Empty,
            };
        }

        string? key = null;
        var name = string.Empty;
        foreach (var row in names.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.LldpRemSysNameRoot);
            var parts = index?.Split('.');
            if (parts is not { Length: >= 3 }
                || !int.TryParse(parts[1], out var localPort)
                || localPort != ifIndex)
            {
                continue;
            }

            key = $"{parts[0]}.{parts[1]}.{parts[2]}";
            name = row.Display?.Trim() ?? string.Empty;
            break;
        }

        if (key is null)
        {
            return new LldpNeighbor { Responded = true };
        }

        var remotePort = string.Empty;
        var ports = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.LldpRemPortIdRoot, timeoutMs, retries,
                SnmpOidRepository.LldpRemMaxRows, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in ports.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.LldpRemPortIdRoot);
            if (index is not null && string.Equals(index, key, StringComparison.Ordinal))
            {
                remotePort = row.Display?.Trim() ?? string.Empty;
                break;
            }
        }

        // 管理地址表是走**表根**（不是某一列），所以 LastIndex 拿到的是
        // `<列号>.<timeMark>.<本端 ifIndex>.<远端条目号>.<地址类型>.<长度>.<IPv4 四段>`
        // —— 第一段是列号，必须先剥掉；真机实测就是这里少算了一段，导致"下一跳"一直是空的。
        var managementIp = string.Empty;
        var addresses = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.LldpRemManAddrRoot, timeoutMs, retries,
                SnmpOidRepository.LldpRemMaxRows, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in addresses.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.LldpRemManAddrRoot);
            var parts = index?.Split('.');
            if (parts is not { Length: >= 10 })
            {
                continue;
            }

            var entryKey = $"{parts[1]}.{parts[2]}.{parts[3]}";
            if (!string.Equals(entryKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            // addrSubtype 1 = IPv4（写在索引的倒数第 6 段）
            //   ⚠️ 必须同时校验**长度段**（倒数第 5 段）等于 4：邻居只上报 IPv6 时索引会长得多
            //   （subtype=2、len=16），只按"从末尾数第 6 段"取就会拿地址字节当 subtype，
            //   万一那个字节恰好是 1，就会把 IPv6 的末 4 字节拼成一个假 IPv4 当下一跳。
            if (parts[^6] != "1" || parts[^5] != "4")
            {
                continue;
            }

            managementIp = $"{parts[^4]}.{parts[^3]}.{parts[^2]}.{parts[^1]}";
            break;
        }

        return new LldpNeighbor
        {
            Responded = true,
            Name = name,
            Port = remotePort,
            ManagementIp = managementIp,
        };
    }

    /// <summary>ifIndex → 接口名（走标准 ifName 表，通常几十条，很便宜）。取不到返回空串。</summary>
    private async Task<string> ResolveIfNameAsync(
        string host,
        int port,
        string community,
        string ifIndex,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(ifIndex, out var wanted))
        {
            return string.Empty;
        }

        var names = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName), timeoutMs, retries,
                SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in names.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName));
            if (index is not null && int.TryParse(index, out var candidate) && candidate == wanted)
            {
                return row.Display?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>`Vl100` / `VLAN 100` → `100`；普通物理口返回空串（不要把它当 VLAN）。</summary>
    private static string ExtractVlanNumber(string interfaceName) =>
        InterfaceNameHelper.TryParse(interfaceName, out var token) && token.IsLogical && token.Prefix == "vl"
            ? token.LogicalIndex.ToString()
            : string.Empty;
}
