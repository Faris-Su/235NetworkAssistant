namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>OID 分类（与 SNMP 页面的 Tab 一一对应）。</summary>
public enum SnmpCategory
{
    DeviceInfo,
    Resource,
    Temperature,
    Fan,
    Power,
    Interface,
    Vlan,
    Mac,
    Ip,
    Other,
}

/// <summary>OID 含义的可信度。</summary>
public enum SnmpOidConfidence
{
    /// <summary>设备返回值及字段结构足以确认含义。</summary>
    Confirmed,

    /// <summary>依据 walk 的结构 + 数值关系推断（例如三档温度阈值、CPU 多窗口百分比）。</summary>
    Inferred,

    /// <summary>含义未确认：只原样展示，不做业务解释。</summary>
    Unknown,
}

/// <summary>
/// 一个 OID 的定义。OID 字符串只允许出现在本文件与 <see cref="SnmpQueryService"/>，
/// View / ViewModel / XAML 里禁止出现裸 OID（见 docs/snmp.md「OID 支持清单」）。
/// </summary>
public sealed record SnmpOidDefinition(
    string Name,
    string Oid,
    SnmpCategory Category,
    string DataType,
    string Unit,
    string Description,
    SnmpOidConfidence Confidence = SnmpOidConfidence.Confirmed,
    bool IsTable = false,
    string? TableRoot = null,
    int? Index = null);

/**
 * 锐捷 OID 库（OID 支持清单）。
 *
 * 依据：脱敏后的设备行为验证；原始 Walk 不随项目发布。
 *   - 私有字段样例位于企业号分支 1.3.6.1.4.1.4881.1.1.10.2.*
 *   - 设备型号与版本信息不记录在源码中
 *   - 该 walk **不包含标准 MIB-II**（1.3.6.1.2.1.* 一条都没有），所以设备名等信息只能取锐捷私有分支
 *
 * 规则：
 *   1. 只登记 walk 里真实存在的 OID；没有的类别不硬加（页面显示"设备不支持 / N/A"）。
 *   2. 含义靠结构 + 数值关系确认；不能确认的标 Inferred/Unknown，界面会明确标注，不冒充确认值。
 *   3. 每次新增 OID 都必须能在脱敏的验证样例或标准 MIB 中确认（见相关测试与 docs/snmp.md）。
 */
public static class SnmpOidRepository
{
    public const string EnterpriseBase = "1.3.6.1.4.1.4881.1.1.10.2";

    // ==================================================================
    // 标准 MIB-II / IF-MIB / IF-X-MIB / HOST-RESOURCES（RFC 1213 / RFC 2863 / RFC 2790）
    //
    // 为什么必须用它们（设备验证结论）：
    //   验证样例只包含厂商私有分支，里面
    //     · 没有可确认的 SysName（设备名）—— 私有分支字段不作为设备名使用
    //     · .105 是光模块 DDM 表，不能当作完整接口清单
    //   而这两项恰好是所有交换机都必须实现的标准 MIB，任何网管（Zabbix / SolarWinds…）都靠它取。
    //   所以：设备名、接口清单用标准 MIB；锐捷私有 OID 只用在标准 MIB 没有的信息
    //   （型号 / 序列号 / CPU·内存 / 温度 / 风扇 / 电源 / VLAN / MAC / ARP）。
    // ==================================================================
    public const string StdSysDescr = "1.3.6.1.2.1.1.1.0";
    public const string StdSysObjectId = "1.3.6.1.2.1.1.2.0";
    public const string StdSysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string StdSysContact = "1.3.6.1.2.1.1.4.0";
    public const string StdSysName = "1.3.6.1.2.1.1.5.0";
    public const string StdSysLocation = "1.3.6.1.2.1.1.6.0";

    /// <summary>
    /// 标准 sysUpTime —— **TimeTicks，单位 1/100 秒**，取值后必须 ÷100 才能当秒用。
    /// 旧名 `StdUpTimeSeconds` 会让人直接当秒用：2026-09-21 实测概览卡运行时间因此被放大 100 倍
    /// （真值 57 天显示成 5764 天），故改名。
    /// </summary>
    public const string StdSysUpTimeTicks = StdSysUpTime;

    // IF-MIB（接口清单/状态/计数）
    public const string StdIfTableRoot = "1.3.6.1.2.1.2.2.1";
    public const string StdIfXTableRoot = "1.3.6.1.2.1.31.1.1.1";

    public static string StdIf(int column) => $"{StdIfTableRoot}.{column}";
    public static string StdIfX(int column) => $"{StdIfXTableRoot}.{column}";

    /// <summary>构造标准 IF-MIB 的单元格 OID（列 + 索引）。</summary>
    public static string Std(int column) => StdIf(column);

    // 锐捷私有表的 WALK 根（表类信息统一用 WALK 取，避免"固定索引 GET"在别的型号上整批失败）
    public const string ResourceTableRoot = ResourceRoot;
    public const string TemperatureTableRoot = TemperatureRoot;
    public const string FanTableRoot = FanRoot;
    public const string PowerTableRoot = PowerRoot;

    public const int StdIfIndex = 1;
    public const int StdIfDescr = 2;
    public const int StdIfSpeed = 5;
    public const int StdIfPhysAddress = 6;
    public const int StdIfAdminStatus = 7;
    public const int StdIfOperStatus = 8;
    public const int StdIfInOctets = 10;
    public const int StdIfInErrors = 14;
    public const int StdIfOutOctets = 16;
    public const int StdIfOutErrors = 20;
    public const int StdIfName = 1;      // ifXTable
    public const int StdIfHighSpeed = 15;
    public const int StdIfAlias = 18;
    public const int StdIfHCInOctets = 6;
    public const int StdIfHCOutOctets = 10;

    // ---------- 标准网桥 MIB（BRIDGE-MIB）：MAC → 端口 的唯一可靠来源 ----------
    // 为什么需要：锐捷私有 MAC 表 `.22.1.1.5.1` **没有端口列**（真机实测 `.4` 列恒定，见 docs/snmp.md §2.6），
    // 所以"这个 MAC 接在哪个口"在私有表里根本答不出来。
    // 标准 BRIDGE-MIB 的转发表天然就是 MAC → 桥口，再经 dot1dBasePortIfIndex 换成 ifIndex，
    // 最后用 ifName 变成人能看懂的端口名 —— 三段拼起来就是"哪台的哪个口"。
    //
    // 2026-09-22 在真机 锐捷交换机 上实测确认（**不是照 MIB 文档猜的**）：
    //   · dot1dTpFdbPort 子树存在；索引就是 6 字节 MAC，取值是桥口号；
    //   · dot1dBasePortIfIndex 存在，40 条（桥口 1..40 → ifIndex 1..40）；
    //   · ifName 与 dot1dTpFdbPort 的索引映射
    //     → 与 CLI `show mac-address-table` 中的端口信息一致。
    //   成本对比：标准 FDB 表请求量显著低于设备私有 MAC 表，可减少大量 GETBULK 请求，
    //   而且只有它带端口。
    public const string BridgeFdbPortRoot = "1.3.6.1.2.1.17.4.3.1.2";      // dot1dTpFdbPort：MAC → 桥口

    /// <summary>
    /// dot1qTpFdbPort（Q-BRIDGE-MIB）：索引 = &lt;VLAN&gt;.&lt;6 字节 MAC&gt;，取值 = 桥口。
    /// 支持 VLAN 的设备优先用这张表 —— 它能区分"同一个 MAC 出现在多个 VLAN"的情况。
    /// </summary>
    public const string QbridgeFdbPortRoot = "1.3.6.1.2.1.17.7.1.2.2.1.2";

    /// <summary>dot1dBasePortIfIndex：桥口 → ifIndex。把 FDB 里的"桥口号"翻成接口名要靠它。</summary>
    public const string BridgeBasePortIfIndexRoot = "1.3.6.1.2.1.17.1.4.1.2";
    public const string BridgePortIfIndexRoot = "1.3.6.1.2.1.17.1.4.1.2";  // dot1dBasePortIfIndex：桥口 → ifIndex

    /// <summary>桥口 → ifIndex 表的行数上限（一台 48 口堆叠机也就几百条；给足余量防截断）。</summary>
    public const int BridgePortMaxRows = 4096;

    /// <summary>FDB 表的行数上限（varbind 条数）；为大规模设备保留充足余量。</summary>
    public const int BridgeFdbMaxRows = 80_000;

    // ---------- 标准 LLDP-MIB（IEEE 802.1AB）：逐跳追踪靠它找"下一台" ----------
    // 用途：在 A 台的转发表里查到某个 MAC 落在 Gi0/24，那这个口到底是"下挂了一台交换机"
    // 还是"直接接的用户设备"？看这个口上有没有 LLDP 邻居就知道 —— 有邻居就跳到那台继续查，
    // 没有（或对端不是交换机）才是终点。**这一步把"扫 411 台"降成"扫一条路径上的几台"。**
    //
    // 设备实测确认该列存在（通过 Walk 验证，不是只依据 MIB 文档推测）：
    //   lldpRemSysName.<index> = <neighbor-name>   ← 邻居系统名
    //   lldpRemPortId.<index> = <remote-port>      ← 邻居侧端口
    //   lldpRemManAddr 索引包含 IPv4 邻居管理地址
    //   索引三段 = <timeMark>.<本端 ifIndex>.<远端条目号>
    public const string LldpRemSysNameRoot = "1.0.8802.1.1.2.1.4.1.1.9";
    public const string LldpRemPortIdRoot = "1.0.8802.1.1.2.1.4.1.1.7";
    public const string LldpRemManAddrRoot = "1.0.8802.1.1.2.1.4.2.1";

    /// <summary>LLDP 邻居表的行数上限（一台 48 口满配也就百来条，给足余量）。</summary>
    public const int LldpRemMaxRows = 8192;

    /// <summary>标准接口表要读的列（标准 MIB 一定存在，读不到只是这台设备没实现该列）。</summary>
    public static IReadOnlyList<(int Column, string Title, string Unit)> StdInterfaceColumns { get; } = new[]
    {
        (StdIfDescr, "描述", ""),
        (StdIfName, "名称", ""),
        (StdIfAlias, "别名", ""),
        (StdIfPhysAddress, "MAC", ""),
        (StdIfAdminStatus, "管理状态", ""),
        (StdIfOperStatus, "链路状态", ""),
        (StdIfSpeed, "速率", "bps"),
        (StdIfHighSpeed, "高速率", "Mbps"),
        (StdIfInOctets, "接收字节", ""),
        (StdIfOutOctets, "发送字节", ""),
        (StdIfInErrors, "接收错误", ""),
        (StdIfOutErrors, "发送错误", ""),
        (StdIfHCInOctets, "接收字节(64)", ""),
        (StdIfHCOutOctets, "发送字节(64)", ""),
    };

    /// <summary>CPU 利用率的通用来源（HOST-RESOURCES-MIB，交换机不一定实现）。</summary>
    public const string StdHrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2";

    // ---------- 设备基本信息（Scalar，全部来自 walk，值可确认） ----------
    public const string SoftwareVersion = EnterpriseBase + ".1.1.2.0";
    public const string BootVersion = EnterpriseBase + ".1.1.3.0";
    public const string InternalVersion = EnterpriseBase + ".1.1.1.0";
    public const string SerialNumber = EnterpriseBase + ".1.1.24.0";
    public const string Model = EnterpriseBase + ".1.1.26.0";
    public const string Vendor = EnterpriseBase + ".1.1.30.0";
    public const string SystemType = EnterpriseBase + ".1.1.33.0";
    public const string CpuModel = EnterpriseBase + ".1.1.35.0";
    public const string MemoryType = EnterpriseBase + ".1.1.36.0";
    // 已删除的死定义（本次纠错）：
    //   .1.1.37.0 内存总量  → 与资源表 .35.1.1.1.12.1 是同一个量，按铁律 5 只保留后者
    //   .1.1.27.0 运行时间(秒) / .1.1.45.0(ticks) → 与标准 sysUpTime 重复，只保留标准
    public const string TimeMark = EnterpriseBase + ".1.1.31.0";
    public const string SlotSoftwareVersion = EnterpriseBase + ".1.1.25.1.5.1";
    public const string SlotSerialNumber = EnterpriseBase + ".1.1.25.1.7.1";

    /// <summary>设备基本信息的标量表（GET）。</summary>
    public static IReadOnlyList<SnmpOidDefinition> Scalars { get; } = new[]
    {
        new SnmpOidDefinition("设备型号", Model, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的型号信息"),
        new SnmpOidDefinition("软件版本", SoftwareVersion, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的软件版本"),
        new SnmpOidDefinition("Boot 版本", BootVersion, SnmpCategory.DeviceInfo, "STRING", "", "主/备 Boot 版本"),
        new SnmpOidDefinition("内部版本号", InternalVersion, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的内部版本信息"),
        new SnmpOidDefinition("序列号", SerialNumber, SnmpCategory.DeviceInfo, "STRING", "", "设备序列号（辅助识别设备）"),
        new SnmpOidDefinition("厂商", Vendor, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的厂商信息"),
        new SnmpOidDefinition("系统类型", SystemType, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的系统类型"),
        new SnmpOidDefinition("CPU 型号", CpuModel, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的 CPU 型号"),
        new SnmpOidDefinition("内存类型", MemoryType, SnmpCategory.DeviceInfo, "STRING", "", "设备返回的内存类型"),
        // 铁律 5（同一个量只能有一个来源）：
        //  1) 内存总量改读资源表那一列（.35.1.1.1.12.1），不再同时显示私有标量 .1.1.37.0；
        //  2) 运行时间只保留标准 sysUpTime（见 StandardTable），私有 .1.1.27.0（秒）不再单独显示
        //     —— 两者在 Walk 样例上互证。
        // 内存总量只在【资源】表显示一次：列组随型号不同（MB 或 KB），
        // 静态标量列表没法跟着设备变，硬留一行就会出现"资源表有数、这里 N/A"的矛盾（现场 P0-1）。
        new SnmpOidDefinition("板卡软件版本", SlotSoftwareVersion, SnmpCategory.DeviceInfo, "STRING", "", "slot 0"),
        new SnmpOidDefinition("板卡序列号", SlotSerialNumber, SnmpCategory.DeviceInfo, "STRING", "", "slot 0"),
        new SnmpOidDefinition(
            "时间标记",
            TimeMark,
            SnmpCategory.Other,
            "STRING",
            "",
            "Walk 时间字段含义未确认，仅原样展示",
            SnmpOidConfidence.Unknown),
    };

    // ---------- 系统资源（表 35.1：两行 —— memory / CPU） ----------
    private const string ResourceRoot = EnterpriseBase + ".35.1";

    public const string ResourceMemoryRoot = ResourceRoot + ".1";
    public const string ResourceCpuRoot = ResourceRoot + ".2";

    /// <summary>资源行名称（memory / Slot 0: … Cpu 0）。</summary>
    public static string ResourceName(bool cpu) => (cpu ? ResourceCpuRoot : ResourceMemoryRoot) + ".1.2.1";

    /// <summary>
    /// 内存三列（**固定列号，不再自动探测**）。
    ///
    /// 依据：设备 Walk 样例的资源表字段关系；同一张表存在 MB 与 KB 两组列，且已用+剩余=总量。
    /// KB 组是 MB 组乘以 1024，
    /// **V0.2.1 之前用"三列满足 已用+剩余=总量"的自动探测，会先命中 6/7/8 把 KB 当 MB 显示**。
    /// 现在写死 12/13/14 并标注单位 MB；取不到就 N/A，不再猜列号、不再用 KB 列顶替。
    /// </summary>
    public static string MemoryMbColumn(int column) => ResourceMemoryRoot + $".1.{column}.1";

    /// <summary>内存总量（MB）—— 设备基本信息、资源表、概览卡**三处同源**（铁律 5）。</summary>
    public static string ResourceMemoryTotalMb => MemoryMbColumn(12);

    /// <summary>内存已用（MB）。</summary>
    public static string ResourceMemoryUsedMb => MemoryMbColumn(13);

    /// <summary>内存剩余（MB）。</summary>
    public static string ResourceMemoryFreeMb => MemoryMbColumn(14);

    /// <summary>
    /// CPU 利用率：**标准 HOST-RESOURCES-MIB hrProcessorLoad**（RFC 2790），WALK 整表，多核多行。
    /// 这是本次纠错的核心之一：不再显示锐捷私有表里含义未确认的 50/22/50。
    /// </summary>
    public const string HrProcessorLoadRoot = "1.3.6.1.2.1.25.3.3.1.2";

    /// <summary>标准 hrProcessorLoad 的行数上限（多核 CPU 可能有多行）。</summary>
    public const int HrProcessorLoadMaxRows = 1024;

    /// <summary>内存使用率列（第 5 列）：真机实测 = 已用 ÷ 总量 × 100（±1pp）。</summary>
    public static string MemoryUsagePercent => ResourceMemoryRoot + ".1.5.1";

    // ---------- CPU（表 36.1，真机实测） ----------
    //  系统级：.36.1.1.{1,2,3}.0 = 三个窗口的利用率；{4,5}.0 = 警告 / 严重阈值
    //  每卡/每核：.36.1.2.1.2.<i> = 名称；{3,4,5} = 三个窗口；{6,7} = 阈值
    // 注意：HOST-RESOURCES-MIB 在这三台锐捷设备上整棵不存在，所以私有表才是主路径。
    public const string CpuRoot = EnterpriseBase + ".36.1";

    /// <summary>系统级 CPU 标量组根：`.36.1.1`（利用率 `.1/.2/.3.0`、阈值 `.4/.5.0`）。</summary>
    public const string CpuSystemRoot = CpuRoot + ".1";

    public const string CpuCardRoot = CpuRoot + ".2.1";

    public const int CpuCardMaxRows = 256;

    /// <summary>系统级 CPU 利用率窗口（window = 1/2/3）。</summary>
    public static string CpuSystemPercent(int window) => $"{CpuSystemRoot}.{window}.0";

    /// <summary>系统级 CPU 阈值（kind：4 = 警告，5 = 严重）。</summary>
    public static string CpuSystemThreshold(int kind) => $"{CpuSystemRoot}.{kind}.0";

    /// <summary>每卡/每核字段（column：2 名称、3/4/5 三个窗口、6/7 阈值）。</summary>
    public static string CpuCardField(int column, string index) => $"{CpuCardRoot}.{column}.{index}";

    public static IReadOnlyList<SnmpOidDefinition> ResourceTable { get; } = new[]
    {
        new SnmpOidDefinition("资源行名称", ResourceName(false), SnmpCategory.Resource, "STRING", "", "memory / CPU 行标识", IsTable: true, TableRoot: ResourceMemoryRoot),
        new SnmpOidDefinition("内存总量", ResourceMemoryTotalMb, SnmpCategory.Resource, "Gauge32", "MB", "Walk 样例中的 MB 列（与 1.1.37.0 一致）", IsTable: true, TableRoot: ResourceMemoryRoot),
        new SnmpOidDefinition("内存已用", ResourceMemoryUsedMb, SnmpCategory.Resource, "Gauge32", "MB", "Walk 样例中的已用内存列", IsTable: true, TableRoot: ResourceMemoryRoot),
        new SnmpOidDefinition("内存剩余", ResourceMemoryFreeMb, SnmpCategory.Resource, "Gauge32", "MB", "Walk 样例中的剩余内存列（已用+剩余=总量）", IsTable: true, TableRoot: ResourceMemoryRoot),
        new SnmpOidDefinition("CPU 利用率", HrProcessorLoadRoot, SnmpCategory.Resource, "INTEGER", "%", "标准 HOST-RESOURCES-MIB（RFC 2790），每核一行；设备不实现则 N/A", IsTable: true),
    };

    // ---------- 温度 / 风扇 / 电源：都在**系统信息组** `1.1` 下面 ----------
    // 设备 Walk 样例确认：风扇在 `.10.2.1.1.42.1`、电源在 `.10.2.1.1.41.1`、温度在 `.10.2.1.1.44.1`。
    // 旧代码少了 `.1.1` 两级（写成 `.10.2.42.1`），结果在任何设备上都取不到；
    // 而 `.10.2.42.1` 恰好是 ifIndex→端口名映射表，表现为"有表但一行也对不上"。
    private const string SysInfoRoot = EnterpriseBase + ".1.1";

    private const string TemperatureRoot = SysInfoRoot + ".44.1";

    public static string TemperatureField(int column, int index) => $"{TemperatureRoot}.{column}.1.0.{index}";

    public static IReadOnlyList<SnmpOidDefinition> TemperatureTable { get; } = new[]
    {
        new SnmpOidDefinition("传感器名称", TemperatureField(4, 1), SnmpCategory.Temperature, "STRING", "", "设备返回的传感器名称", IsTable: true, TableRoot: TemperatureRoot),
        new SnmpOidDefinition("当前温度", TemperatureField(5, 1), SnmpCategory.Temperature, "INTEGER", "°C", "设备返回的温度值", IsTable: true, TableRoot: TemperatureRoot),
        new SnmpOidDefinition("警告阈值", TemperatureField(6, 1), SnmpCategory.Temperature, "INTEGER", "°C", "设备返回的警告阈值", IsTable: true, TableRoot: TemperatureRoot),
        new SnmpOidDefinition("告警阈值", TemperatureField(7, 1), SnmpCategory.Temperature, "INTEGER", "°C", "设备返回的告警阈值", IsTable: true, TableRoot: TemperatureRoot),
    };

    // ---------- 风扇 / 电源（表 42.1 / 41.1，索引 .1.N） ----------
    private const string FanRoot = SysInfoRoot + ".42.1";
    private const string PowerRoot = SysInfoRoot + ".41.1";

    public static string FanField(int column, int index) => $"{FanRoot}.{column}.1.{index}";
    public static string PowerField(int column, int index) => $"{PowerRoot}.{column}.1.{index}";

    public const int FanCount = 1;      // walk 中出现 1 个风扇
    public const int PowerCount = 1;    // walk 中出现 1 个电源

    public static IReadOnlyList<SnmpOidDefinition> FanTable { get; } = new[]
    {
        new SnmpOidDefinition("风扇名称", FanField(4, 1), SnmpCategory.Fan, "STRING", "", "设备返回的风扇名称", IsTable: true, TableRoot: FanRoot),
        new SnmpOidDefinition("风扇状态原始值", FanField(3, 1), SnmpCategory.Fan, "INTEGER", "", "walk 值 4（枚举含义未确认，原样展示）", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: FanRoot),
    };

    public static IReadOnlyList<SnmpOidDefinition> PowerTable { get; } = new[]
    {
        new SnmpOidDefinition("电源名称", PowerField(4, 1), SnmpCategory.Power, "STRING", "", "设备返回的电源名称", IsTable: true, TableRoot: PowerRoot),
        new SnmpOidDefinition("电源状态原始值", PowerField(3, 1), SnmpCategory.Power, "INTEGER", "", "walk 值 4（枚举含义未确认，原样展示）", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: PowerRoot),
    };

    // ---------- 接口（表 10.1.1.1 + 105.1.1.1 / 105.1.2.1，索引 = ifIndex） ----------
    public const string InterfaceRoot = EnterpriseBase + ".10.1.1.1";
    public const string InterfaceDetailRoot = EnterpriseBase + ".105.1.1.1";
    public const string InterfaceTransceiverRoot = EnterpriseBase + ".105.1.2.1";

    /// <summary>
    /// 标准 IF-MIB 每列 WALK 的上限，单位是"**接口条目数**"（一列 walk 就是每个接口一条）。
    /// 大型设备的私有接口表可能超过数百个条目。
    /// **这个常量以前是死的**——接口代码曾使用过较小的硬编码上限，结果大型设备只翻得出部分
    /// ifIndex 名称，MAC 的"端口"列、ARP 的"VLAN"列也可能无法完整映射（已改为引用本常量）。
    /// 取 8192：给多槽位框式机留足余量，同时一列 walk 最多也就几千个条目，代价可忽略。
    /// </summary>
    public const int InterfaceMaxRows = 8192;

    public static IReadOnlyList<SnmpOidDefinition> InterfaceTable { get; } = new[]
    {
        new SnmpOidDefinition("接口名", InterfaceDetailRoot + ".2", SnmpCategory.Interface, "STRING", "", "物理或逻辑接口名称", IsTable: true, TableRoot: InterfaceDetailRoot),
        new SnmpOidDefinition("MAC 地址", InterfaceRoot + ".21", SnmpCategory.Interface, "Hex-STRING", "", "接口 MAC", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("速率", InterfaceRoot + ".25", SnmpCategory.Interface, "Gauge32", "Mbps", "接口速率（Mbps）", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("接收字节", InterfaceRoot + ".17", SnmpCategory.Interface, "Counter64", "字节", "累计接收字节", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("发送字节", InterfaceRoot + ".18", SnmpCategory.Interface, "Counter64", "字节", "累计发送字节", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("接收带宽利用率", InterfaceRoot + ".38", SnmpCategory.Interface, "STRING", "%", "接收方向的带宽利用率", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("发送带宽利用率", InterfaceRoot + ".43", SnmpCategory.Interface, "STRING", "%", "发送方向的带宽利用率", IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("状态字段 1/2", InterfaceRoot + ".3", SnmpCategory.Interface, "INTEGER", "", "枚举含义未确认，原样展示", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: InterfaceRoot),
        new SnmpOidDefinition("光模块型号", InterfaceTransceiverRoot + ".4", SnmpCategory.Interface, "STRING", "", "设备返回的光模块型号", IsTable: true, TableRoot: InterfaceTransceiverRoot),
        new SnmpOidDefinition("光模块温度", InterfaceDetailRoot + ".17", SnmpCategory.Interface, "INTEGER", "°C", "设备返回的温度（含义按数值范围推断）", SnmpOidConfidence.Inferred, IsTable: true, TableRoot: InterfaceDetailRoot),
        new SnmpOidDefinition("光模块波长", InterfaceDetailRoot + ".86", SnmpCategory.Interface, "STRING", "nm", "设备返回的波长", IsTable: true, TableRoot: InterfaceDetailRoot),
    };

    // ---------- VLAN（表 9.1.7.1，索引 = VLAN ID） ----------
    public const string VlanRoot = EnterpriseBase + ".9.1.7.1";
    /// <summary>
    /// VLAN 表 WALK 上限（**varbind 条数**，理由见 <see cref="MacMaxVarbinds"/>）。
    /// VLAN 子树每行多列 → 旧值 4096 可能无法覆盖大型 VLAN 表。
    /// 这里为数千个 VLAN 的设备留出余量。
    /// </summary>
    public const int VlanMaxRows = 16_000;

    public static IReadOnlyList<SnmpOidDefinition> VlanTable { get; } = new[]
    {
        new SnmpOidDefinition("VLAN ID", VlanRoot + ".1", SnmpCategory.Vlan, "INTEGER", "", "VLAN 编号", IsTable: true, TableRoot: VlanRoot),
        new SnmpOidDefinition("VLAN 状态", VlanRoot + ".2", SnmpCategory.Vlan, "INTEGER", "", "枚举含义未确认，原样展示", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: VlanRoot),
        new SnmpOidDefinition("VLAN 名称", VlanRoot + ".3", SnmpCategory.Vlan, "STRING", "", "设备返回的 VLAN 名称", IsTable: true, TableRoot: VlanRoot),
    };

    // ---------- MAC（表 22.1.1.5.1，索引 = <VLAN>.<MAC 6 字节>） ----------
    public const string MacRoot = EnterpriseBase + ".22.1.1.5.1";
    /// <summary>
    /// MAC / ARP 这类大表的 WALK 上限，单位是 **varbind 条数（不是要显示的行数）**：
    /// SNMP 表在设备上是**列优先**存的（`&lt;表根&gt;.&lt;列&gt;.&lt;索引&gt;`，一整列走完才进下一列），
    /// 所以按"行数"卡上限会砍在某一列中间 —— 后面的列整列丢失，界面上就是"一大堆 N/A"。
    /// 外部 Walk 样例显示：较低的 varbind 上限可能只覆盖到 ARP 表的前几列，
    /// 使 IP / MAC / 状态列显示 N/A。这里为大型表留出余量。
    /// </summary>
    public const int MacMaxVarbinds = 200_000;      // 按大型 MAC 表留有余量

    public static IReadOnlyList<SnmpOidDefinition> MacTable { get; } = new[]
    {
        new SnmpOidDefinition("VLAN", MacRoot + ".1", SnmpCategory.Mac, "Gauge32", "", "该 MAC 所属 VLAN", IsTable: true, TableRoot: MacRoot),
        new SnmpOidDefinition("MAC 地址", MacRoot + ".2", SnmpCategory.Mac, "Hex-STRING", "", "MAC 地址", IsTable: true, TableRoot: MacRoot),
        new SnmpOidDefinition("老化时间", MacRoot + ".3", SnmpCategory.Mac, "INTEGER", "分钟", "设备返回的老化时间", IsTable: true, TableRoot: MacRoot),
        new SnmpOidDefinition("类型/状态原始值", MacRoot + ".4", SnmpCategory.Mac, "INTEGER", "", "枚举含义未确认，原样展示", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: MacRoot),
    };

    // ---------- IP（ARP 表 2.1.1.1，索引 = <VLAN>.<IP 4 字节>） ----------
    public const string ArpRoot = EnterpriseBase + ".2.1.1.1";
    /// <summary>ARP 表 WALK 上限（varbind 条数，理由见 <see cref="MacMaxVarbinds"/>）。</summary>
    public const int ArpMaxVarbinds = 260_000;      // 按大型 ARP 表留有余量

    /// <summary>资源表（内存 / CPU 行）单次 WALK 上限（varbind 条数）。</summary>
    public const int ResourceMaxRows = 512;

    /// <summary>私有 CPU 表上限，为多卡多核设备留有余量。</summary>
    public const int CpuMaxRows = 512;

    /// <summary>
    /// 温度表上限（**varbind 条数**）：为多传感器设备留有余量。
    /// 旧值 2048 余量不足时，设备表增加一列就可能导致尾部列（告警阈值）整列变 N/A。
    /// </summary>
    public const int TemperatureMaxRows = 8192;

    /// <summary>风扇表上限（varbind 条数）。</summary>
    public const int FanMaxRows = 1024;

    /// <summary>电源表上限（varbind 条数）；表列数随型号变化，留足余量。</summary>
    public const int PowerMaxRows = 2048;

    public static IReadOnlyList<SnmpOidDefinition> IpTable { get; } = new[]
    {
        new SnmpOidDefinition("VLAN", ArpRoot + ".1", SnmpCategory.Ip, "INTEGER", "", "该 IP 所属 VLAN", IsTable: true, TableRoot: ArpRoot),
        new SnmpOidDefinition("MAC 地址", ArpRoot + ".2", SnmpCategory.Ip, "Hex-STRING", "", "对应 MAC", IsTable: true, TableRoot: ArpRoot),
        new SnmpOidDefinition("IP 地址", ArpRoot + ".3", SnmpCategory.Ip, "IpAddress", "", "ARP 表中的 IP", IsTable: true, TableRoot: ArpRoot),
        new SnmpOidDefinition("状态原始值", ArpRoot + ".5", SnmpCategory.Ip, "INTEGER", "", "枚举含义未确认，原样展示", SnmpOidConfidence.Unknown, IsTable: true, TableRoot: ArpRoot),
    };

    /// <summary>概览卡片读取的标量（少而稳，避免一次点开就发几十个包）。</summary>
    public static IReadOnlyList<string> OverviewCardOids { get; } = new[]
    {
        StdSysName,
        StdSysDescr,
        StdSysUpTimeTicks,
        Model,
        SoftwareVersion,
        SerialNumber,
        Vendor,
        // 运行时间用标准 sysUpTime（私有 .1.1.27.0 已按铁律 5 删除，避免两个"运行时间"打架）
        ResourceMemoryTotalMb,
        ResourceMemoryUsedMb,
        ResourceMemoryFreeMb,
    };

    /// <summary>全部分类（界面 Tab 顺序）。</summary>
    public static IReadOnlyList<SnmpCategory> Categories { get; } = new[]
    {
        SnmpCategory.DeviceInfo,
        SnmpCategory.Resource,
        SnmpCategory.Temperature,
        SnmpCategory.Fan,
        SnmpCategory.Power,
        SnmpCategory.Interface,
        SnmpCategory.Vlan,
        SnmpCategory.Mac,
        SnmpCategory.Ip,
        SnmpCategory.Other,
    };

    public static string Title(SnmpCategory category) => category switch
    {
        SnmpCategory.DeviceInfo => "概览",
        SnmpCategory.Resource => "CPU / 内存",
        SnmpCategory.Temperature => "温度",
        SnmpCategory.Fan => "风扇",
        SnmpCategory.Power => "电源",
        SnmpCategory.Interface => "接口",
        SnmpCategory.Vlan => "VLAN",
        SnmpCategory.Mac => "MAC",
        SnmpCategory.Ip => "IP",
        _ => "其他",
    };

    /// <summary>某个分类下登记的所有 OID（用于"OID 支持清单"展示与测试）。</summary>
    public static IReadOnlyList<SnmpOidDefinition> Definitions(SnmpCategory category) =>
        Scalars.Concat(ResourceTable).Concat(TemperatureTable).Concat(FanTable)
            .Concat(PowerTable).Concat(InterfaceTable).Concat(VlanTable).Concat(MacTable).Concat(IpTable)
            .Where(d => d.Category == category)
            .ToList();
}
