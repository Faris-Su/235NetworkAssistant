namespace RuijieNetworkAssistant.Models;

/// <summary>MAC 定位的四种结局。**必须区分开**：把"设备不支持"或"超时"当成"不存在"，是排障里最坑人的事。</summary>
public enum MacLocationStatus
{
    /// <summary>查到了，并且拿到了端口名。</summary>
    Found,

    /// <summary>查到了条目，但那是设备自身/网关（桥口 → ifIndex 映射里没有），不是接在物理口上的用户设备。</summary>
    FoundOnDeviceItself,

    /// <summary>这台设备的转发表里确实没有这个 MAC。</summary>
    NotFound,

    /// <summary>这台设备没有返回标准转发表（不支持 / SNMP 视图没放开）—— 不等于"不存在"。</summary>
    Unsupported,

    /// <summary>
    /// 这台设备**根本没回应**（超时 / 不可达 / Community 不对）。
    /// 必须和 Unsupported 分开：把"连不上"说成"该设备不支持"，用户会去查错方向。
    /// </summary>
    Unreachable,

    /// <summary>输入有问题（MAC 格式）。</summary>
    Invalid,
}

/// <summary>
/// 在一台设备的 ARP 表里把 IP 解析成 MAC 的结果。
/// <c>Mac</c> 为 null 表示这台设备没给出答案 —— **要区分"没这个 IP"和"这台设备没有 ARP 表"**（Note 里说明）。
/// </summary>
public sealed class IpToMacResult
{
    public string? Mac { get; init; }

    /// <summary>
    /// ARP 条目所在接口的名字（真机实测：ARP 索引第二段是 **ifIndex**，不是 VLAN ——
    /// 例如 ifIndex → `Vl100`，该接口是 VLAN SVI，而非连接终端的物理端口）。
    /// </summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>从接口名里抽出来的 VLAN 号（`Vl100` → `100`）；不是 VLAN 接口时为空。</summary>
    public string Vlan { get; init; } = string.Empty;

    /// <summary>没解析出来时的原因。</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>这台设备**根本没回应**（超时/不可达/Community 不对）—— 与"没有 ARP 表"是两回事。</summary>
    public bool Unreachable { get; init; }

    /// <summary>回应了、但这台设备确实没有 ARP 表（纯二层接入交换机就是这样）。</summary>
    public bool NoArpTable { get; init; }
}

/// <summary>
/// 一次逐跳追踪里的一跳：在哪台设备的哪个口上看到了目标 MAC，以及那个口对面是谁。
/// <c>NextHopIp</c> 为空 = 这一跳就是终点（该口没有 LLDP 邻居，说明接的是用户设备）。
/// </summary>
public sealed class MacTraceHop
{
    public string DeviceIp { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;

    public int BridgePort { get; init; }

    public int? IfIndex { get; init; }

    /// <summary>本跳的结论：命中（端口可信）/ 设备自身 / 未命中 / 该设备不支持 / 查询失败。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>这个口对面的邻居系统名（LLDP）；没有邻居就是空。</summary>
    public string NeighborName { get; init; } = string.Empty;

    /// <summary>这个口对面的邻居端口（LLDP）。</summary>
    public string NeighborPort { get; init; } = string.Empty;

    /// <summary>下一跳设备的管理 IP（LLDP 给的管理地址）；空 = 到终点了。</summary>
    public string NextHopIp { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }
}

/// <summary>单台设备上的 MAC 定位结果。</summary>
public sealed class MacLocation
{
    public MacLocationStatus Status { get; init; } = MacLocationStatus.NotFound;

    public bool Found => Status is MacLocationStatus.Found or MacLocationStatus.FoundOnDeviceItself;

    public string MacAddress { get; init; } = string.Empty;

    /// <summary>桥口号（BRIDGE-MIB 的 dot1dTpFdbPort 取值）。</summary>
    public int BridgePort { get; init; }

    /// <summary>ifIndex（端口名就是靠它查出来的）。</summary>
    public int? IfIndex { get; init; }

    /// <summary>端口名（真机是 `Gi0/1` 这种简写，来自 ifName）。</summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>补充说明 / 为什么没找到（给用户看的原话）。</summary>
    public string Note { get; init; } = string.Empty;

    public static MacLocation Invalid(string note) =>
        new() { Status = MacLocationStatus.Invalid, Note = note };

    public static MacLocation Unsupported(string note) =>
        new() { Status = MacLocationStatus.Unsupported, Note = note };

    public static MacLocation Unreachable(string note) =>
        new() { Status = MacLocationStatus.Unreachable, Note = note };

    public static MacLocation NotFound(string note) =>
        new() { Status = MacLocationStatus.NotFound, Note = note };
}
