using System.Globalization;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>一次 SNMP 查询的总体状态。</summary>
public enum SnmpQueryStatus
{
    Success,
    Partial,
    Failed,
}

/// <summary>键值型结果（概览 / 系统信息用）。</summary>
public sealed record SnmpField(string Name, string Value, string? Note = null, SnmpOidConfidence Confidence = SnmpOidConfidence.Confirmed);

/// <summary>表格型结果（一个分类一张表）。</summary>
public sealed class SnmpSection
{
    public SnmpCategory Category { get; init; }

    public string Title { get; init; } = string.Empty;

    public string[] Columns { get; init; } = Array.Empty<string>();

    public List<string[]> Rows { get; } = new();

    /// <summary>该表是否被行数上限截断（MAC / ARP 大表）。</summary>
    public bool Truncated { get; set; }

    /// <summary>补充说明（含义未确认的列、截断提示等）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// **增量续拉的断点**：这张表还有没取完的数据时，存"最后一个取到的 OID"；
    /// 取完（或本来就没有数据）时为 null。界面据此决定要不要显示「继续拉取大表」。
    /// </summary>
    public string? ResumeOid { get; set; }

    /// <summary>
    /// 多轮拼接用的**原始列累积**：key = 行索引（如 MAC 表的 `&lt;vlan&gt;.&lt;mac6&gt;`），
    /// value = 列号 → 该列的原始取值（文本 + 原始字节）。
    ///
    /// 为什么需要（2026-09-23 真机踩到）：GETBULK 按 OID 顺序走，**同一行的不同列可能落在不同轮**里
    /// （列 1 的条目全部排在列 2 之前）。只按行 append 会出现两种坏结果：
    ///   ① 同一 `<vlan>.<mac>` 分成两行，后一行 VLAN 列是 N/A；
    ///   ② 同一 index 在两轮里各来一次 → **重复行**。
    /// 有了累积器就能"按 index 合并列 → 重新渲染整张表"，续拉后行数与内容都保持一致。
    /// 只在内存里用（设备信息库存的是渲染后的 Rows）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>? RawColumns { get; set; }

    /// <summary>累积器里 key 的**首次出现顺序**（渲染行时按它排，保证多次续拉后顺序稳定）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string>? RawColumnOrder { get; set; }

    /// <summary>
    /// **界面续拉时该"整体替换"还是"追加"**（2026-09-23 新增）。
    ///
    /// true：<see cref="Rows"/> 是"累积器重渲染后的**全量行**"，界面上要把旧行丢掉再灌（否则重复行）；
    /// false：<see cref="Rows"/> 只是本轮新增的部分（例如界面手里的分区没有累积器、
    /// 或者是 ARP 这种仍按追加处理的表），界面照旧 AddRange。
    /// 只在内存里用，不落盘。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RowsAreCumulative { get; set; }

    /// <summary>
    /// 被当作"半截行"丢掉的条数（只有列 1、没拿到这张表的身份列 MAC/IP）。
    ///
    /// 有它才能把 <see cref="Summary"/> 里那个"0 行"说清楚：**不是设备没有这张表**，
    /// 而是这几轮只走到了前面的列（大规模设备的 ARP 表可能出现这种情况，
    /// 界面若照旧写"N/A（设备没有返回该表，或该型号不支持）"会误导）。
    /// </summary>
    public int DroppedHalfRows { get; set; }

    public string Summary => Rows.Count == 0
        ? DroppedHalfRows > 0
            ? $"0 行（本轮只走到前几列，{DroppedHalfRows} 行没拿到身份列，已丢弃 —— 点[继续拉取大表]接着取）"
            : "N/A（设备没有返回该表，或该型号不支持）"
        : $"{Rows.Count} 行" + (Truncated
            ? ResumeOid is null ? "（已截断，仅显示前若干条）" : "（已截断，可点[继续拉取大表]接着取）"
            : string.Empty);
}

/// <summary>一次完整查询的结果（按分类组织，可直接给界面，也可落盘成设备快照）。</summary>
public sealed class SnmpQueryResult
{
    public string TargetIp { get; init; } = string.Empty;

    public int Port { get; init; } = 161;

    public DateTimeOffset QueriedAt { get; init; } = DateTimeOffset.Now;

    public SnmpQueryStatus Status { get; set; } = SnmpQueryStatus.Failed;

    public string StatusText { get; set; } = string.Empty;

    public List<SnmpField> Fields { get; } = new();

    public List<SnmpSection> Sections { get; } = new();

    /// <summary>本次实际成功取到的 OID 个数（用于"成功/部分成功"判断）。</summary>
    public int FetchedCount { get; set; }

    /// <summary>失败的类别说明（哪一类没取到）。</summary>
    public List<string> Failures { get; } = new();

    /// <summary>
    /// "该型号本来就不提供"的类别说明（现场 P2-8）：与 <see cref="Failures"/> 分开统计。
    /// 某些设备可能没有 HOST-RESOURCES-MIB 或 ENTITY-SENSOR-MIB —— 这是设备差异，
    /// 不是查询失败，状态仍然是"查询成功"。
    /// </summary>
    public List<string> Unsupported { get; } = new();

    /// <summary>
    /// 端口状态统计。**只在批量巡检路径填充**（`QueryAsync(..., includeLargeTables: false)`）——
    /// 完整查询时接口明细在 <see cref="Sections"/> 里，不需要这份汇总。
    /// 为 null 表示"设备没返回标准 IF-MIB"，界面上要如实标注，不能当成 0 个 down。
    /// </summary>
    public PortStatusCounts? PortStatus { get; set; }

    public string StatusLine => $"{QueriedAt:yyyy-MM-dd HH:mm:ss}｜{StatusText}";
}

/// <summary>
/// 端口状态计数。
/// <paramref name="AdminDown"/> 是人为关掉的（不算异常）；
/// <paramref name="LinkDown"/> 是"管理上开着、链路却没起来"的 —— 这才是巡检要盯的异常。
/// </summary>
public readonly record struct PortStatusCounts(
    int Total,
    int AdminDown,
    int LinkDown,
    bool AdminStatusKnown = true,
    bool Truncated = false);

/// <summary>
/// SNMP 查询编排：把"要查哪些 OID"与"怎么展示"分开。
/// - 标量与固定索引表（资源/温度/风扇/电源）走一次性 GET（多个 OID 打包，一个报文）；
/// - 变长表（接口 / VLAN / MAC / IP）走 WALK（GETNEXT），并设行数上限，避免把设备打爆；
/// - 单个类别失败不影响其它类别（结果里保留 N/A + 失败说明）。
/// 与 CLI 连接完全无关：只依赖 IP + Community + UDP 端口。
/// </summary>
public sealed class SnmpQueryService
{
    /// <summary>
    /// 大表（MAC / ARP）每次查询的**时间预算**：默认 60 秒，到点带着已取到的行收工并标注"已截断"。
    ///
    /// 为什么必须设预算（设备实测）：大型核心交换机的私有 MAC 表，
    /// 连续 GETBULK 走到 **62,475 条 / 2,499 次请求 / 231 秒**时，设备开始临时不回应
    /// （停几秒后单发又立刻能通 —— 是限速，不是不通）。而旧实现一旦中途超时就把整表
    /// 退回 GETNEXT 重走（一次请求一个节点），上万行的表等于永远跑不完，用户看到的就是"卡住/超时"。
    /// 现在：客户端会**限速 + 从断点续走**，并且到这里设的时间预算就收工（0 = 不限，用于"完整拉取"）。
    /// </summary>
    public const int DefaultLargeTableBudgetMs = 60_000;

    private readonly SnmpV2cClient _client;
    private readonly ILogService _log;

    public SnmpQueryService(ILogService log, SnmpV2cClient? client = null)
    {
        _log = log;
        _client = client ?? new SnmpV2cClient();
    }

    public async Task<SnmpQueryResult> QueryAsync(
        string host,
        int port,
        string community,
        int timeoutMs = 1500,
        int retries = 1,
        CancellationToken cancellationToken = default,
        bool includeLargeTables = true,
        int largeTableBudgetMs = DefaultLargeTableBudgetMs)
    {
        var result = new SnmpQueryResult { TargetIp = host, Port = port };
        if (string.IsNullOrWhiteSpace(host))
        {
            return Fail(result, "没有填写设备 IP。");
        }

        // 1) 标量（设备基本信息）：一次 GET 打包全部
        var scalarResponse = await _client
            .GetWithStatusAsync(host, port, community, SnmpOidRepository.Scalars.Select(d => d.Oid), timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);

        if (!scalarResponse.Responded)
        {
            // 完全没回应：Community 错误 / IP 不可达 / UDP 161 被拦，都走这里
            return Fail(
                result,
                scalarResponse.Error is { Length: > 0 } error
                    ? $"设备没有响应：{error}"
                    : "设备没有响应：请检查 IP、Community（只读团体名）与 UDP 161 是否可达。");
        }

        foreach (var definition in SnmpOidRepository.Scalars)
        {
            var value = Lookup(scalarResponse.Values, definition.Oid);
            result.Fields.Add(new SnmpField(definition.Name, value, Note(definition), definition.Confidence));
            if (value != "N/A")
            {
                result.FetchedCount++;
            }
        }

        // 2) 设备名 / 系统描述 / 运行时间：**用标准 MIB-II**（私有分支里没有 SysName，
        //    早期误用私有分支里的 Community 当设备名 —— 已彻底移除）
        var stdScalars = await GetSafeAsync(
            host, port, community,
            new[] { SnmpOidRepository.StdSysName, SnmpOidRepository.StdSysDescr, SnmpOidRepository.StdSysUpTime },
            timeoutMs, retries, cancellationToken).ConfigureAwait(false);

        var sysName = stdScalars.TryGetValue(SnmpOidRepository.StdSysName, out var nameValue)
            ? Normalize(nameValue.Display)
            : "N/A";
        var sysDescr = stdScalars.TryGetValue(SnmpOidRepository.StdSysDescr, out var descrValue)
            ? Normalize(descrValue.Display)
            : "N/A";
        var sysUpTime = stdScalars.TryGetValue(SnmpOidRepository.StdSysUpTime, out var tickValue) && tickValue.Number is { } ticks
            ? FormatUptime(ticks / 100)      // sysUpTime 是 1/100 秒
            : "N/A";

        result.Fields.InsertRange(0, new[]
        {
            new SnmpField("设备名称", sysName, "来自标准 MIB-II sysName（1.3.6.1.2.1.1.5.0）；取不到就显示 N/A，绝不用 Community 代替"),
            new SnmpField("系统描述", sysDescr, "来自标准 MIB-II sysDescr"),
            new SnmpField("运行时间", sysUpTime, "来自标准 MIB-II sysUpTime（1/100 秒换算）"),
        });

        if (sysName != "N/A")
        {
            result.FetchedCount++;
        }

        // 3) 资源（内存 / CPU）
        //    内存：锐捷私有表 rgResource 的内存行（候选列 12/13/14，单位 MB，设备 Walk 样例确认）；
        //    CPU ：**标准 HOST-RESOURCES-MIB hrProcessorLoad**（RFC 2790），每核一行。
        //    本次纠错：不再用"三列凑等式"自动探测列号（会把 KB 列当 MB），
        //    也不再显示含义未确认的私有 CPU 百分比列（50/22/50）。
        var resourceWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.ResourceTableRoot, timeoutMs, retries, SnmpOidRepository.ResourceMaxRows, cancellationToken)
            .ConfigureAwait(false);
        // CPU 主路径 = 锐捷私有 CPU 表 `.36.1`（HOST-RESOURCES-MIB 在这几台设备上整棵不存在）；
        // 标准 hrProcessorLoad 只作为退化分支（私有表拿不到时才用）。
        var cpuWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.CpuRoot, timeoutMs, retries, SnmpOidRepository.CpuMaxRows, cancellationToken)
            .ConfigureAwait(false);
        var hrCpuWalk = await _client
            .WalkAsync(
                host,
                port,
                community,
                SnmpOidRepository.HrProcessorLoadRoot,
                timeoutMs,
                retries,
                SnmpOidRepository.HrProcessorLoadMaxRows,
                cancellationToken)
            .ConfigureAwait(false);
        result.Sections.Add(BuildResourceSection(
            result,
            resourceWalk.Rows,
            cpuWalk.Rows,
            hrCpuWalk.Rows,
            resourceWalk.Truncated || cpuWalk.Truncated || hrCpuWalk.Truncated));

        // 3) 温度 / 风扇 / 电源：**用 WALK 而不是"固定索引 GET"**
        //    原因（现场实测）：不同型号的传感器数量/索引写法不一样（.0.1 / .1 / 没有），
        //    固定索引 GET 一旦碰到设备不认识的 OID，多数代理会对整批请求回 noSuchName，
        //    结果整张表都变成空 —— 用户看到的就是"温度/风扇/电源都没取到"。
        //    WALK（GETNEXT）遇到不存在的子树只会正常结束，且自动适配任意索引。
        var temperatureWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.TemperatureTableRoot, timeoutMs, retries, SnmpOidRepository.TemperatureMaxRows, cancellationToken)
            .ConfigureAwait(false);
        var fanWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.FanTableRoot, timeoutMs, retries, SnmpOidRepository.FanMaxRows, cancellationToken)
            .ConfigureAwait(false);
        var powerWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.PowerTableRoot, timeoutMs, retries, SnmpOidRepository.PowerMaxRows, cancellationToken)
            .ConfigureAwait(false);
        result.Sections.Add(BuildTemperatureSection(result, temperatureWalk.Rows, temperatureWalk.Truncated));
        result.Sections.Add(BuildFanSection(result, fanWalk.Rows, fanWalk.Truncated));
        result.Sections.Add(BuildPowerSection(result, powerWalk.Rows, powerWalk.Truncated));

        if (includeLargeTables)
        {
            // 5) 接口：标准 IF-MIB（电口 + 光口都能拿到），锐捷光模块表作为补充
            result.Sections.Add(await BuildInterfaceSectionAsync(host, port, community, timeoutMs, retries, cancellationToken).ConfigureAwait(false));

            // 5) VLAN：WALK 整张表（4 列 × N 行，通常很小）
            result.Sections.Add(await BuildVlanSectionAsync(host, port, community, timeoutMs, retries, cancellationToken).ConfigureAwait(false));

            // 6) MAC / IP：大表，设行数上限并标注截断
            result.Sections.Add(await BuildMacSectionAsync(host, port, community, timeoutMs, retries, cancellationToken, largeTableBudgetMs).ConfigureAwait(false));
            result.Sections.Add(await BuildArpSectionAsync(host, port, community, timeoutMs, retries, cancellationToken, largeTableBudgetMs).ConfigureAwait(false));
        }
        else
        {
            // 批量巡检路径（2026-09-22 新增）：**不碰 MAC/ARP/VLAN**，接口也只取"状态"这一列。
            // 为什么：完整接口表是 12 次列 WALK（ifAlias/ifPhysAddress/ifAdminStatus/ifOperStatus/
            // ifHighSpeed/ifSpeed/计数器/错包列），大型设备上单是这一节就占查询时间的大头；
            // 而"每周巡检"真正要看的只有"有几个口 down"。MAC/ARP 上万行更是完全用不上。
            result.PortStatus = await BuildPortStatusCountsAsync(host, port, community, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            if (result.PortStatus is null)
            {
                result.Failures.Add("端口状态未取到（ifOperStatus/ifAdminStatus 都没有返回）");
            }
        }

        var totalRows = result.Sections.Sum(s => s.Rows.Count);
        result.Status = result.Failures.Count == 0
            ? SnmpQueryStatus.Success
            : SnmpQueryStatus.Partial;
        result.StatusText = result.Failures.Count == 0
            ? result.Unsupported.Count == 0
                ? $"查询成功：标量 {result.FetchedCount} 项｜表 {totalRows} 行"
                : $"查询成功：标量 {result.FetchedCount} 项｜表 {totalRows} 行｜" +
                  $"{result.Unsupported.Count} 项该型号未提供（{string.Join("；", result.Unsupported)}）"
            : $"部分成功：{string.Join("；", result.Failures)}";

        _log.Info($"SNMP 查询 {host}:{port} → {result.StatusText}（Community 不记录）");
        return result;
    }

    /// <summary>
    /// 只取接口**状态**两列（ifAdminStatus / ifOperStatus），数出"有几个口 down"。
    ///
    /// 为什么单独写：完整接口表要 WALK 12 列（见 BuildInterfaceSectionAsync），
    /// 批量巡检一台台跑根本扛不住；而巡检真正关心的只有
    /// "admin up 但 oper down" 的口 —— 那才是掉线/断链的信号。
    /// 管理性关闭（admin down）是人为的，不算异常，单独计数。
    /// 取不到（老设备没有标准 IF-MIB）返回 null，由调用方如实标注，不要臆造 0。
    /// </summary>
    private async Task<PortStatusCounts?> BuildPortStatusCountsAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var operWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfOperStatus), timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);
        if (operWalk.Rows.Count == 0)
        {
            return null;
        }

        var adminWalk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfAdminStatus), timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);

        // 用 ifIndex → 状态 建表，两边按同一个索引对齐（GETBULK 是列优先返回，不能按行号错位比较）
        var admin = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in adminWalk.Rows)
        {
            var index = LastIndex(row.Oid, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfAdminStatus));
            if (index is not null && row.Number is { } value)
            {
                admin[index] = (int)value;
            }
        }

        var total = 0;
        var adminDown = 0;
        var operDown = 0;
        foreach (var row in operWalk.Rows)
        {
            if (row.Number is not { } oper)
            {
                continue;
            }

            total++;
            var index = LastIndex(row.Oid, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfOperStatus));
            var isAdminDown = index is not null && admin.TryGetValue(index, out var adminValue) && adminValue == 2;
            if (isAdminDown)
            {
                adminDown++;
                continue;
            }

            // ifOperStatus: 1=up 2=down 3=testing 4=unknown 5=dormant 6=notPresent 7=lowerLayerDown
            // 除 up 之外都算"链路没起来"，但只有 admin up 的才计入异常。
            if (oper != 1)
            {
                operDown++;
            }
        }

        // ifAdminStatus 没回应时**不能**把 AdminDown 报成 0：那会让"人为关闭的口"全部被算成
        // "链路没起来"，把设备事实报反。这里如实标出来，由界面提示用户。
        return new PortStatusCounts(
            Total: total,
            AdminDown: adminDown,
            LinkDown: operDown,
            AdminStatusKnown: adminWalk.Responded,
            Truncated: operWalk.Truncated || adminWalk.Truncated);
    }

    /// <summary>
    /// 轻量查询：只取概览卡片需要的少数标量（型号/版本/序列号/运行时间/内存），
    /// 一次 GET 打包完成，适合概览页[刷新 SNMP] 与"打开页面时读一次"。
    /// </summary>
    public async Task<SnmpQueryResult> QueryCardAsync(
        string host,
        int port,
        string community,
        int timeoutMs = 1500,
        int retries = 1,
        CancellationToken cancellationToken = default)
    {
        var result = new SnmpQueryResult { TargetIp = host, Port = port };
        if (string.IsNullOrWhiteSpace(host))
        {
            return Fail(result, "没有填写设备 IP：可在 SNMP 页或设置页填写后重试。");
        }

        var response = await _client
            .GetWithStatusAsync(host, port, community, SnmpOidRepository.OverviewCardOids, timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Responded)
        {
            return Fail(result, response.Error is { Length: > 0 } error
                ? $"设备没有响应：{error}"
                : "设备没有响应：请检查 IP、Community 与 UDP 161。");
        }

        // 设备名走标准 MIB-II sysName；取不到就是 N/A（绝不用 Community 冒充）
        var cardSysName = response.Values.TryGetValue(SnmpOidRepository.StdSysName, out var cardName)
            ? Normalize(cardName.Display)
            : "N/A";
        result.Fields.Add(new SnmpField(
            "设备名称",
            cardSysName,
            "来自标准 MIB-II sysName（1.3.6.1.2.1.1.5.0）；取不到显示 N/A，不用 Community 代替"));

        foreach (var definition in SnmpOidRepository.Scalars.Where(d => SnmpOidRepository.OverviewCardOids.Contains(d.Oid)))
        {
            result.Fields.Add(new SnmpField(definition.Name, Lookup(response.Values, definition.Oid), Note(definition), definition.Confidence));
        }

        // 内存：与【资源】表**同一份解析**（铁律 5）——WALK 资源表后走 ResolveMemory，
        // 这样 MB / KB 两种列组都能自适应，不会出现"概览卡与资源表两个数字"。
        var cardResourceWalk = await _client
            // 与【资源】表用同一个上限常量（旧写法写死 128 varbind，截断了还会说成"设备没有返回"）
            .WalkAsync(host, port, community, SnmpOidRepository.ResourceTableRoot, timeoutMs, retries, SnmpOidRepository.ResourceMaxRows, cancellationToken)
            .ConfigureAwait(false);
        var cardMemory = ResolveMemory(cardResourceWalk.Rows);
        result.Fields.Add(new SnmpField(
            "内存",
            cardMemory is { } cardMem
                ? $"{cardMem.UsedMb} / {cardMem.TotalMb} MB 已用（剩余 {cardMem.FreeMb} MB）"
                : "N/A",
            cardMemory is { } sourceMem ? $"来自设备资源表 {sourceMem.Source}" : "设备没有返回已知的内存列组"));

        // 运行时间：只认标准 sysUpTime（私有 .1.1.27.0 不再单独显示，避免两个可能不一致的"运行时间"）。
        // 这里必须 ÷100：sysUpTime 是 TimeTicks（1/100 秒）。不除会把 57 天显示成 5764 天。
        var uptime = Number(response.Values, SnmpOidRepository.StdSysUpTimeTicks);
        result.Fields.Add(new SnmpField(
            "运行时间",
            uptime is > 0 ? FormatUptime(uptime.Value / 100) : "N/A",
            "标准 MIB-II sysUpTime（1.3.6.1.2.1.1.3.0）换算"));

        result.FetchedCount = result.Fields.Count(f => f.Value != "N/A");
        result.Status = result.FetchedCount > 0 ? SnmpQueryStatus.Success : SnmpQueryStatus.Partial;
        result.StatusText = result.FetchedCount > 0
            ? $"查询成功：取得 {result.FetchedCount} 项"
            : "已连上设备，但没有取到已知 OID（设备可能不支持这些私有 OID）";
        _log.Info($"SNMP 概览查询 {host}:{port} → {result.StatusText}（Community 不记录）");
        return result;
    }

    /// <summary>秒 → "57 天 15:31:24"。</summary>
    public static string FormatUptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.Days > 0
            ? $"{span.Days} 天 {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static SnmpQueryResult Fail(SnmpQueryResult result, string message)
    {
        result.Failures.Add(message);
        return new SnmpQueryResult
        {
            TargetIp = result.TargetIp,
            Port = result.Port,
            Status = SnmpQueryStatus.Failed,
            StatusText = message,
        };
    }

    private async Task<Dictionary<string, SnmpValue>> GetSafeAsync(
        string host,
        int port,
        string community,
        IReadOnlyList<string> oids,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetWithStatusAsync(host, port, community, oids, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            return new Dictionary<string, SnmpValue>(response.Values, StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("SNMP 分组查询失败", ex);
            return new Dictionary<string, SnmpValue>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 资源表（锐捷私有 .35.1）：WALK 结果按「行类型 + 列」组织。
    /// 行类型 1 = 内存行，行类型 2 = CPU 行；列号在不同行里可能有偏移，
    /// 所以内存按"最大且等于 1.1.37.0(totalMB)"的一列定位，避免写死列号。
    /// </summary>
    /// <param name="walkRows">锐捷私有 rgResource 表（内存行 + CPU 行）。</param>
    /// <param name="cpuRows">锐捷私有 CPU 表 `.36.1` 的 WALK 行（主路径）。</param>
    /// <param name="cpuLoadRows">标准 hrProcessorLoad 的行（退化分支）。</param>
    private static SnmpSection BuildResourceSection(
        SnmpQueryResult result,
        IReadOnlyList<SnmpValue> walkRows,
        IReadOnlyList<SnmpValue> cpuRows,
        IReadOnlyList<SnmpValue> cpuLoadRows,
        bool truncated = false)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Resource,
            Title = SnmpOidRepository.Title(SnmpCategory.Resource),
            Columns = new[] { "项目", "值", "说明" },
        };
        section.Truncated = truncated;

        // 把 WALK 行整理成 rows[行类型][列号] = 值
        var rows = new Dictionary<string, Dictionary<int, SnmpValue>>(StringComparer.Ordinal);
        foreach (var row in walkRows)
        {
            var rest = Suffix(row.Oid, SnmpOidRepository.ResourceTableRoot);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length < 3)
            {
                continue;
            }

            // OID 形如 .35.1.<行类型>.1.<列>.<索引>：去掉 WALK 根 .35.1 之后是 <行类型>.1.<列>.<索引>
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out var rowType) ||
                !int.TryParse(parts[2], out var column))
            {
                continue;
            }

            if (!rows.TryGetValue(rowType.ToString(), out var cells))
            {
                cells = new Dictionary<int, SnmpValue>();
                rows[rowType.ToString()] = cells;
            }

            cells[column] = row;
        }

        // 内存行：靠行名（walk 值 "memory"）定位，行内列号才有意义。
        var memoryRow = rows.Values.FirstOrDefault(cells => cells.Values
            .Any(v => v.Text?.Contains("memory", StringComparison.OrdinalIgnoreCase) == true));

        void Add(string name, string value, string note) => section.Rows.Add(new[] { name, value, note });

        // ---------- 内存：按**候选列组**择优（现场实测定下来的两组） ----------
        //   {12,13,14} = MB   —— 一类设备版本中的样例列号
        //   {6,7,8}    = KB   —— 另一类设备版本中的样例列号（.12.1 可能返回 noSuchObject）
        // 不再用"三列凑等式"自动探测（那会把 KB 当 MB），也不写死某一组列号。
        var memory = ResolveMemory(walkRows);
        if (memory is { } mem)
        {
            Add("内存总量", $"{mem.TotalMb} MB", $"设备上报（{mem.Source}）");
            Add("内存已用", $"{mem.UsedMb} MB", $"设备上报（{mem.Source}）");
            Add("内存剩余", $"{mem.FreeMb} MB", $"设备上报（{mem.Source}；已用 + 剩余 = 总量）");
            // 使用率：只认第 5 列，且必须与"已用÷总量"相差 ≤3 个百分点才采信
            //（第 11 列在 N18000 上是 32、与使用率无关，已删除，不再当候选）。
            var computed = mem.UsedMb * 100.0 / Math.Max(1, mem.TotalMb);
            var deviceUsage = ResolveUsagePercent(walkRows, mem, computed);
            Add(
                "内存使用率",
                $"{deviceUsage.Value:F0}%",
                deviceUsage.FromDevice
                    ? $"设备直接上报（{SnmpOidRepository.MemoryUsagePercent}），与已用÷总量（{computed:F1}%）一致"
                    : $"由「已用 ÷ 总量」计算得出（{computed:F1}%；设备没有给出可信的使用率列）");
        }
        else
        {
            Add("内存总量", "N/A", "设备没有返回已知的内存列组（.35.1.1.1.12/13/14 或 6/7/8）");
            Add("内存已用", "N/A", "同上；不用其它字段顶替，也不猜列号");
            Add("内存剩余", "N/A", "同上");
            Add("内存使用率", "N/A", "没有可信来源");
            result.Failures.Add("资源表未取到（内存）");
        }

        // ---------- CPU：私有表 `.36.1` 优先，标准 hrProcessorLoad 退化 ----------
        var cpu = ResolveCpu(cpuRows);
        if (cpu is { } cpuInfo)
        {
            Add(
                "CPU 利用率",
                string.Join(" / ", cpuInfo.SystemPercent.Select(v => $"{v}%")),
                "来自锐捷私有 CPU 表 .36.1.1.{1,2,3}.0，对应三个采集窗口；" +
                "窗口顺序（5 秒 / 1 分钟 / 5 分钟）是**按位置推断**的，设备未给出窗口名称");
            var warn = cpuInfo.WarningThreshold;
            var critical = cpuInfo.CriticalThreshold;
            if (warn is not null || critical is not null)
            {
                Add(
                    "CPU 阈值",
                    $"警告 {warn?.ToString() ?? "N/A"} / 严重 {critical?.ToString() ?? "N/A"}",
                    "来自 .36.1.1.{4,5}.0（设备直接上报）");
            }

            foreach (var card in cpuInfo.Cards)
            {
                var cardThreshold = card.Threshold;
                Add(
                    $"CPU · {card.Name}",
                    string.Join(" / ", card.Percent.Select(v => $"{v}%")),
                    cardThreshold is null
                        ? "来自 .36.1.2.1.{3,4,5}.<i>（每卡/每核一行）"
                        : $"来自 .36.1.2.1.{{3,4,5}}.<i>；阈值 警告 {cardThreshold.Value.Warning?.ToString() ?? "N/A"} / " +
                          $"严重 {cardThreshold.Value.Critical?.ToString() ?? "N/A"}");
            }
        }
        else if (cpuLoadRows.Any(v => !v.IsNoReading && v.Number is not null))
        {
            // 退化分支：设备没实现私有表，但有标准 hrProcessorLoad（哨兵值不算读数：不能显示成 "-255%"）
            var values = cpuLoadRows
                .Where(v => !v.IsNoReading && v.Number is not null)
                .OrderBy(v => v.Oid, StringComparer.Ordinal)
                .Select(v => v.Number!.Value)
                .ToList();
            Add(
                "CPU 利用率",
                string.Join(" / ", values.Select(v => $"{v}%")),
                $"私有 CPU 表没有返回数据，退回标准 HOST-RESOURCES-MIB hrProcessorLoad（共 {values.Count} 核，设备直接上报）");
        }
        else
        {
            Add(
                "CPU 利用率",
                "N/A",
                "私有 CPU 表（.36.1）与标准 hrProcessorLoad 都没有返回数据；需要 CPU 请看 CLI 的 show cpu");
            result.Unsupported.Add("CPU（私有表与标准表都没有数据）");
        }

        Add("CPU 型号", "见「设备基本信息」", "来自设备返回的 CPU 型号字段");

        if (section.Rows.All(r => r[1] == "N/A"))
        {
            result.Failures.Add("资源表未取到");
        }

        return section;
    }

    /// <summary>
    /// 温度表（锐捷私有 .44.1）：WALK 结果里同一索引的 4 个列都可能出现，
    /// 列号 → 含义按现场 walk 的结构确认：4=名称，5=当前温度，6=警告阈值，7=告警阈值。
    /// </summary>
    private static SnmpSection BuildTemperatureSection(SnmpQueryResult result, IReadOnlyList<SnmpValue> walkRows, bool truncated = false)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Temperature,
            Title = SnmpOidRepository.Title(SnmpCategory.Temperature),
            Columns = new[] { "传感器", "当前温度(°C)", "警告阈值(°C)", "告警阈值(°C)" },
        };
        section.Truncated = truncated;

        var byIndex = GroupSensorRows(walkRows, SnmpOidRepository.TemperatureTableRoot, new[] { 4, 5, 6, 7 }, out var mergedCells);
        foreach (var (index, cells) in byIndex.OrderBy(p => p.Key, SensorIndexOrder))
        {
            section.Rows.Add(new[]
            {
                Cell(cells, 4),
                Cell(cells, 5),
                Cell(cells, 6),
                Cell(cells, 7),
            });
        }

        section.Note = section.Rows.Count > 0
            ? "来自锐捷私有温度表：传感器名 + 三个数值列。列含义（当前值 / 警告 / 告警）是按数值结构推断的，"
              + "未在任何真机 CLI 上印证 —— 只作原始值参考"
            : "该型号未提供温度信息：锐捷私有温度表没有返回任何行（不是查询失败）";
        section.Note += MergedCellsWarning(mergedCells);
        if (section.Rows.Count == 0)
        {
            // 设备没有这张私有表 = 该型号不提供，不是查询失败（P2-8）
            result.Unsupported.Add("温度（该型号未提供私有温度表）");
        }

        return section;
    }

    private static SnmpSection BuildFanSection(SnmpQueryResult result, IReadOnlyList<SnmpValue> walkRows, bool truncated = false)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Fan,
            Title = SnmpOidRepository.Title(SnmpCategory.Fan),
            Columns = new[] { "风扇", "状态原始值", "说明" },
        };
        section.Truncated = truncated;

        // 设备 Walk 样例的风扇表：列 4 = 名称，列 3 = 状态枚举（含义未确认）
        var byIndex = GroupSensorRows(walkRows, SnmpOidRepository.FanTableRoot, new[] { 3, 4 }, out var mergedCells);
        foreach (var (_, cells) in byIndex.OrderBy(p => p.Key, SensorIndexOrder))
        {
            var name = Cell(cells, 4);
            var status = Cell(cells, 3);
            if (IsBlankCell(name) && IsBlankCell(status))
            {
                continue;
            }

            section.Rows.Add(new[]
            {
                name,
                IsBlankCell(status) ? status : $"{status}（原始值·未确认）",
                "锐捷私有表 .42.1 的状态枚举含义未确认（MIB 无说明、真机 CLI 未印证）——只报设备原文，不做任何结论",
            });
        }

        section.Note = section.Rows.Count == 0
            ? "该型号未提供风扇信息：私有表 .42.1 没有返回任何行（不是查询失败；已实测某些型号这里其实是别的表）"
            : "来源：设备私有表 .42.1；状态是原始枚举值，含义未确认，不参与任何判断（私有表含义随型号/版本变化）";
        section.Note += MergedCellsWarning(mergedCells);
        if (section.Rows.Count == 0)
        {
            result.Unsupported.Add("风扇（该型号未提供）");
        }

        return section;
    }

    private static SnmpSection BuildPowerSection(SnmpQueryResult result, IReadOnlyList<SnmpValue> walkRows, bool truncated = false)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Power,
            Title = SnmpOidRepository.Title(SnmpCategory.Power),
            Columns = new[] { "电源", "状态原始值", "说明" },
        };
        section.Truncated = truncated;

        // 设备 Walk 样例的电源表：列 4 = 名称，列 3 = 状态枚举（含义未确认）
        var byIndex = GroupSensorRows(walkRows, SnmpOidRepository.PowerTableRoot, new[] { 3, 4 }, out var mergedCells);
        foreach (var (_, cells) in byIndex.OrderBy(p => p.Key, SensorIndexOrder))
        {
            var name = Cell(cells, 4);
            var status = Cell(cells, 3);
            if (IsBlankCell(name) && IsBlankCell(status))
            {
                continue;
            }

            section.Rows.Add(new[]
            {
                name,
                IsBlankCell(status) ? status : $"{status}（原始值·未确认）",
                "锐捷私有表 .41.1 的状态枚举含义未确认（MIB 无说明、真机 CLI 未印证）——只报设备原文，不做任何结论",
            });
        }

        section.Note = section.Rows.Count == 0
            ? "该型号未提供电源信息：私有表 .41.1 没有返回任何行（不是查询失败；已实测某些型号这里其实是别的表）"
            : "来源：设备私有表 .41.1；状态是原始枚举值，含义未确认，不参与任何判断（私有表含义随型号/版本变化）";
        section.Note += MergedCellsWarning(mergedCells);
        if (section.Rows.Count == 0)
        {
            result.Unsupported.Add("电源（该型号未提供）");
        }

        return section;
    }

    /// <summary>
    /// 把"&lt;列&gt;.&lt;入口前缀&gt;.&lt;索引&gt;"这类传感器表按**索引的最后一个数字段**分组：
    /// 返回 索引 → (列号 → 值)。
    ///
    /// 为什么只取最后一段（而不是剩下的全部）：传感器表在不同型号/固件上的写法不同
    /// （`.&lt;列&gt;.1.N`、`.&lt;列&gt;.1.0.N`…），最后一段才是"第几个传感器"，取全部会把同一传感器拆成多行。
    /// 设备依据（docs/snmp.md §2.3~§2.5，Walk 样例）：
    ///   温度 `.44.1.{4,5,6,7}.1.0.N`（N=1..3）、风扇 `.42.1.{3,4}.1.N`、电源 `.41.1.{3,4}.1.N`。
    ///
    /// <paramref name="mergedCells"/> 回传"同一格被写了两次且值不同"的次数（≥1 次）。
    /// 一旦发生就说明该型号的索引结构不是上面这些写法，多个传感器被并成了一行 ——
    /// 必须让界面说出来，不能让用户拿着混行数据（A 传感器的名字配 B 传感器的温度）去下结论。
    /// </summary>
    private static Dictionary<string, Dictionary<int, string>> GroupSensorRows(
        IReadOnlyList<SnmpValue> walkRows,
        string root,
        IReadOnlyCollection<int> wantedColumns,
        out int mergedCells)
    {
        var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        mergedCells = 0;
        foreach (var row in walkRows)
        {
            var rest = Suffix(row.Oid, root);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var column))
            {
                continue;
            }

            if (!wantedColumns.Contains(column))
            {
                continue;
            }

            // 索引 = 最后一个数字段（见方法注释里的真机依据）；只有"列号.索引"两段时才退化成 1，
            // 那种写法里除了列号没有别的区分信息，只能当成同一个传感器。
            var index = parts.Length >= 3 ? string.Join('.', parts[^1]) : "1";
            if (!result.TryGetValue(index, out var cells))
            {
                cells = new Dictionary<int, string>();
                result[index] = cells;
            }

            var text = Normalize(row.Display);
            if (cells.TryGetValue(column, out var existing) &&
                !string.Equals(existing, text, StringComparison.Ordinal))
            {
                mergedCells++;
            }

            cells[column] = text;
        }

        return result;
    }

    /// <summary>
    /// 传感器表索引的顺序：按点分段**逐段按数值**比，避免 "10" 被排在 "2" 前面
    /// （字符串序："10" &lt; "2"）。非数字段回退到不区分大小写的文本比较，不会抛异常。
    /// </summary>
    internal static readonly IComparer<string> SensorIndexOrder = Comparer<string>.Create(CompareSensorIndex);

    private static int CompareSensorIndex(string? left, string? right)
    {
        var a = (left ?? string.Empty).Split('.');
        var b = (right ?? string.Empty).Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aIsNumber = int.TryParse(a[i], out var an);
            var bIsNumber = int.TryParse(b[i], out var bn);
            if (aIsNumber && bIsNumber)
            {
                var diff = an.CompareTo(bn);
                if (diff != 0)
                {
                    return diff;
                }
            }
            else
            {
                var diff = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (diff != 0)
                {
                    return diff;
                }
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>把"多个传感器被并成一行"这件事写进章节说明（没发生就返回空串）。</summary>
    private static string MergedCellsWarning(int mergedCells) =>
        mergedCells <= 0
            ? string.Empty
            : $" ⚠ 有 {mergedCells} 格被合并覆盖（本型号的索引写法与已实测型号不同，可能把不同传感器并成了一行），"
              + "这一列请当作参考、不要直接下结论；麻烦把设备型号和 `snmpwalk` 输出反馈给我们。";

    private static string Cell(IReadOnlyDictionary<int, string> cells, int column) =>
        cells.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "N/A";

    private async Task<SnmpSection> BuildInterfaceSectionAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Interface,
            Title = SnmpOidRepository.Title(SnmpCategory.Interface),
            Columns = new[] { "接口", "描述/别名", "管理状态", "链路状态", "速率", "MAC", "接收字节", "发送字节", "收/发错误" },
        };

        // 接口清单用标准 IF-MIB ifName / ifDescr（电口+光口、VLAN 接口都在里面），
        // 锐捷私有 .105 只是光模块 DDM 表——不能当接口清单用。
        var ifNameWalk = await _client
            // 上限走统一常量：曾经硬编码较小上限，大型设备接口表会被截断（MAC 端口/ARP VLAN 跟着翻不出来）
            .WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName), timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);
        var ifDescrWalk = ifNameWalk.Rows.Count == 0
            ? await _client
                .WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfDescr), timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
                .ConfigureAwait(false)
            : new SnmpWalkResponse(Array.Empty<SnmpValue>(), true, false, null);

        var nameRows = ifNameWalk.Rows.Count > 0 ? ifNameWalk.Rows : ifDescrWalk.Rows;
        // 接口清单被上限截断也要如实说（旧代码使用较小上限时会静默少一截）
        var interfaceTruncated = ifNameWalk.Truncated || ifDescrWalk.Truncated;
        if (nameRows.Count == 0)
        {
            section.Truncated = interfaceTruncated;
            section.Note = "设备没有返回标准 IF-MIB 接口表（ifName/ifDescr），可能 SNMP 未开放该视图";
            return section;
        }

        var nameByIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in nameRows)
        {
            var root = ifNameWalk.Rows.Count > 0
                ? SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName)
                : SnmpOidRepository.Std(SnmpOidRepository.StdIfDescr);
            var index = LastIndex(row.Oid, root);
            if (index is not null)
            {
                nameByIndex[index] = Normalize(row.Display);
            }
        }

        // 其余列：标准 IF-MIB 列直接 WALK（一次性拿到所有接口，不用按索引逐个 GET）
        var walkColumns = new (int Column, bool Extended, string Key)[]
        {
            (SnmpOidRepository.StdIfAlias, true, "alias"),
            (SnmpOidRepository.StdIfPhysAddress, false, "mac"),
            (SnmpOidRepository.StdIfAdminStatus, false, "admin"),
            (SnmpOidRepository.StdIfOperStatus, false, "oper"),
            (SnmpOidRepository.StdIfHighSpeed, true, "highSpeed"),
            (SnmpOidRepository.StdIfSpeed, false, "speed"),
            (SnmpOidRepository.StdIfHCOutOctets, true, "hcOut"),
            (SnmpOidRepository.StdIfHCInOctets, true, "hcIn"),
            (SnmpOidRepository.StdIfInOctets, false, "in"),
            (SnmpOidRepository.StdIfOutOctets, false, "out"),
            (SnmpOidRepository.StdIfInErrors, false, "inErr"),
            (SnmpOidRepository.StdIfOutErrors, false, "outErr"),
        };

        var columnValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (column, extended, key) in walkColumns)
        {
            var root = extended
                ? SnmpOidRepository.StdIfX(column)
                : SnmpOidRepository.StdIf(column);
            var walk = await _client
                .WalkAsync(host, port, community, root, timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
                .ConfigureAwait(false);
            interfaceTruncated |= walk.Truncated;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in walk.Rows)
            {
                var index = LastIndex(row.Oid, root);
                if (index is not null)
                {
                    // ifPhysAddress 必须走原始字节：58 69 6C 33 DF A8 是合法 UTF-8，
                    // 先按文本解码会得到 "Xil3…" 这种还原不回去的乱码（现场 P0-4）。
                    map[index] = key == "mac" && row.Bytes is { Length: > 0 } macBytes
                        ? MacAddressHelper.FormatBytes(macBytes)
                        : Normalize(row.Display);
                }
            }

            columnValues[key] = map;
        }

        string Read(string key, string index) =>
            columnValues.TryGetValue(key, out var map) && map.TryGetValue(index, out var value) ? value : "N/A";

        foreach (var pair in nameByIndex.OrderBy(p => int.TryParse(p.Key, out var n) ? n : int.MaxValue))
        {
            var index = pair.Key;
            // 逻辑接口（Null 0 / VLAN 100 这类）在 CLI 的 show interface status 里看不到，
            // 而且设备通常不填 ifAlias —— 直接把"逻辑接口"写进描述/别名列，差异一眼可解释。
            var alias = Read("alias", index);
            var logical = IsLogicalInterface(pair.Value);
            var descr = logical
                ? alias == "N/A" || alias.Length == 0 ? "逻辑接口" : $"{alias}（逻辑接口）"
                : alias;
            section.Rows.Add(new[]
            {
                pair.Value,
                descr,
                StatusText(Read("admin", index)),
                StatusText(Read("oper", index)),
                SpeedText(Read("highSpeed", index), Read("speed", index)),
                MacText(Read("mac", index)),
                ByteText(Read("hcIn", index), Read("in", index)),
                ByteText(Read("hcOut", index), Read("out", index)),
                $"{Read("inErr", index)} / {Read("outErr", index)}",
            });
        }

        section.Truncated = interfaceTruncated;
        section.Note = "来自标准 IF-MIB（ifName/ifDescr/ifOperStatus/ifHCInOctets…），电口与光口都会列出；"
                     + "速率优先取 ifHighSpeed(Mbps)，没有则用 ifSpeed(bps) 换算。";
        if (section.Rows.Count == 0)
        {
            // 现场遇到的"满屏 N/A"多半是这一类：设备（或它的 SNMP 视图）没有返回标准 IF-MIB。
            // 接口清单取不到时，MAC 表的「端口」列和 ARP 表的「VLAN」列也会跟着变成 N/A —— 必须说明白，
            // 否则用户会以为是软件坏了。
            section.Note = "设备没有返回标准 IF-MIB（ifName/ifDescr/ifOperStatus…），因此本表为空；"
                         + "MAC 表的「端口」列与 ARP 表的「VLAN」列也会因此显示 N/A。"
                         + "常见原因：设备侧限制了 SNMP 视图（只放开部分分支），或这次导出/查询没有包含 "
                         + "1.3.6.1.2.1 标准树。请在设备上确认 SNMP 视图，或把标准树一并 walk 出来对照。";
        }
        return section;
    }

    /// <summary>
    /// 逻辑接口（不是物理端口）：Null 0 / VLAN x / Loopback / Tunnel。
    /// 它们在 SNMP 的 IF-MIB 里有，但 CLI `show interface status` 不列出来 —— 这是"SNMP 接口数比 CLI 多"的原因。
    /// </summary>
    /// <remarks>
    /// 必须同时认**全称与缩写**：锐捷在 ifDescr 里可能给 "Null 0" / "VLAN 100"，
    /// 但在 ifName 里可能给 "Nu0" / "Vl100"（界面优先显示短名称）。
    /// </remarks>
    private static bool IsLogicalInterface(string name) =>
        name.StartsWith("VLAN", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Vl", StringComparison.Ordinal) ||          // Vl100 / Vlan100
        name.StartsWith("Null", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Nu", StringComparison.Ordinal) ||          // Nu0
        name.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Lo", StringComparison.Ordinal) ||          // Lo0
        name.StartsWith("Tunnel", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Tu", StringComparison.Ordinal);            // Tu0

    /// <summary>IF-MIB 状态枚举：1=up 2=down 3=testing 4=unknown 5=dormant 6=notPresent 7=lowerLayerDown。</summary>
    private static string StatusText(string value) => value switch
    {
        "1" => "up",
        "2" => "down",
        "3" => "testing",
        "4" => "unknown",
        "5" => "dormant",
        "6" => "notPresent",
        "7" => "lowerLayerDown",
        _ => value,
    };

    private static string SpeedText(string highSpeedMbps, string speedBps)
    {
        if (highSpeedMbps != "N/A" && long.TryParse(highSpeedMbps, out var mbps) && mbps > 0)
        {
            return mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps" : $"{mbps} Mbps";
        }

        if (speedBps != "N/A" && long.TryParse(speedBps, out var bps) && bps > 0)
        {
            return bps >= 1_000_000_000
                ? $"{bps / 1_000_000_000.0:0.##} Gbps"
                : bps >= 1_000_000 ? $"{bps / 1_000_000.0:0.##} Mbps" : $"{bps} bps";
        }

        return "N/A";
    }

    /// <summary>OctetString 形式的 MAC：标准 MIB 回的是 6 字节字符串，可能是不可打印字符 → 统一成 xx-xx-xx-xx-xx-xx。</summary>
    private static string MacText(string value)
    {
        if (value == "N/A")
        {
            return value;
        }

        // 已经是 02 00 00 00 00 01 这种十六进制串就直接用
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 12 && compact.All(Uri.IsHexDigit))
        {
            return string.Join("-", Enumerable.Range(0, 6).Select(i => compact.Substring(i * 2, 2)));
        }

        // 不可打印字节被解码成 "XX " 形式时同样能识别，其它情况原样返回
        return value;
    }

    private static string ByteText(string hc, string legacy)
    {
        var raw = hc != "N/A" ? hc : legacy;
        if (raw == "N/A" || !double.TryParse(raw, out var bytes) || bytes < 0)
        {
            return "N/A";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1)
        {
            bytes /= 1024;
            index++;
        }

        return $"{bytes:0.##} {units[index]}";
    }

    private async Task<SnmpSection> BuildVlanSectionAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Vlan,
            Title = SnmpOidRepository.Title(SnmpCategory.Vlan),
            Columns = new[] { "VLAN ID", "名称", "状态原始值" },
        };

        var walk = await _client
            .WalkAsync(host, port, community, SnmpOidRepository.VlanRoot, timeoutMs, retries, SnmpOidRepository.VlanMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var byIndex = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var row in walk.Rows)
        {
            var rest = Suffix(row.Oid, SnmpOidRepository.VlanRoot);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            var column = int.TryParse(parts[0], out var c) ? c : 0;
            var index = string.Join('.', parts.Skip(1));
            if (!byIndex.TryGetValue(index, out var rowValues))
            {
                rowValues = new Dictionary<int, string>();
                byIndex[index] = rowValues;
            }

            rowValues[column] = row.Display;
        }

        foreach (var pair in byIndex.OrderBy(p => int.TryParse(p.Key, out var n) ? n : int.MaxValue))
        {
            pair.Value.TryGetValue(3, out var name);
            pair.Value.TryGetValue(2, out var status);
            section.Rows.Add(new[] { pair.Key, name ?? "N/A", status ?? "N/A" });
        }

        section.Truncated = walk.Truncated;
        section.Note = "VLAN 名称来自设备。";
        return section;
    }

    private async Task<SnmpSection> BuildMacSectionAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken,
        int timeBudgetMs = SnmpQueryService.DefaultLargeTableBudgetMs,
        string? startAfterOid = null,
        Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>? previousRawColumns = null,
        List<string>? previousOrder = null)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Mac,
            Title = SnmpOidRepository.Title(SnmpCategory.Mac),
            Columns = new[] { "VLAN", "MAC", "端口", "说明", "老化(分钟)", "类型原始值" },
        };

        // ifIndex → 端口名 的映射（端口列能不能用见下面 portColumnUsable 的判断）
        var ifNames = await BuildIfIndexNameMapAsync(host, port, community, timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);

        // ⚠️ **顺序很重要**：先走标准 FDB（端口的唯一来源），再走私有 MAC 表。
        // 设备实测：对连续 GETBULK 可能有突发配额，累计大量请求后会被限速数秒。
        // 反过来的话（先私有表、后 FDB）私有大表会把配额吃掉，FDB 只取到几百条 → 端口列几乎全是"未提供"。
        // 先跑 FDB 能拿到大部分 MAC→端口；随后的私有表即使被限速，也可以靠[继续拉取大表]分轮补齐。
        // ⚠️ 本轮先不整表走标准 FDB（2026-09-23 实测结论，见 BuildMacPortLookupAsync 的注释）：
        //    这台核心的 dot1qTpFdbPort **按 VLAN 排序**（前 200 条全是 VLAN 2），
        //    固定时间预算只够走完最前面几个 VLAN → 端口覆盖率可能很低，
        //    却让每次查询多花 60 秒。等改成"按行精确 GET"（见下）再默认开启。
        //    —— 需要调试端口链路时把下面两行恢复即可。
        var portLookup = MacPortLookup.Empty;

        var walk = await _client
            .WalkAsync(
                host,
                port,
                community,
                SnmpOidRepository.MacRoot,
                timeoutMs,
                retries,
                SnmpOidRepository.MacMaxVarbinds,
                cancellationToken,
                timeBudgetMs,
                progress: null,
                startAfterOid: startAfterOid)
            .ConfigureAwait(false);

        // 索引 = <VLAN>.<MAC 6 字节> = **7 段**（设备 Walk 样例已确认）。
        // 以前传 6 → 丢掉 VLAN 那一段，同一个 MAC 出现在多个 VLAN 时会被合并成一行。
        var grouped = GroupByRowWithIndex(walk.Rows, SnmpOidRepository.MacRoot, 7);
        // 按 index 把本轮的列并进累积器（续拉时把上一轮的累积器传进来 → 跨轮补列、不重复行）
        var accumulator = previousRawColumns ?? new Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>(StringComparer.Ordinal);
        var order = previousOrder ?? new List<string>();
        MergeRawColumns(accumulator, order, grouped);
        section.RawColumns = accumulator;
        section.RawColumnOrder = order;
        // 带着上一轮的累积器进来 → 下面渲染出来的是"全量行"，界面要整体替换而不是追加
        section.RowsAreCumulative = previousRawColumns is not null;
        var addedThisRound = walk.Rows.Count;

        // ⚠️ **只保留有 MAC 列的行**（2026-09-23 真机实测后新增）：
        // GETBULK 是按 OID 顺序走的，而列 1（VLAN）的全部条目都排在列 2（MAC）之前 ——
        // 时间预算可能只走完列 1 就收工，那样每行只有 VLAN、没有 MAC（界面显示 N/A）。
        // 这种"半截行"既不是"设备没这个 MAC"，也不是"端口解析失败"，
        // 留着只会让人误判（设备样例中有大量这类记录）。
        // 丢掉它们并在说明里如实交代；想拿全就点[继续拉取大表]接着走（断点会继续往列 2 推进）。
        var rows = order.Select(index => accumulator[index]).Where(columns => columns.ContainsKey(2)).ToList();
        var droppedHalfRows = order.Count - rows.Count;
        section.DroppedHalfRows = droppedHalfRows;
        // "有 MAC、没 VLAN"的行：VLAN 那列在已走过的几轮里设备就没返回（不是我们丢的），如实说明
        var missingVlanRows = rows.Count(row => TextCell(row, 1) == "N/A");

        // ⚠️ 端口列不能无条件用 `.4` 当 ifIndex（2026-09-22 用真机 walk 复核后改）。
        // 实测证据：
        //   · 外部 Walk 样例：`.4` 列的值几乎恒定；
        //   · 设备 Walk 样例：相关列的值长期保持稳定；不同型号可能存在差异。
        // 也就是说 `.4` 是"类型/状态枚举"（docs/snmp.md §2.6 的定义），**不是每个条目各自的端口**。
        // 旧代码把它当 ifIndex 去 ifDescr 里翻名字，只要 ifIndex 1 能翻出名字，
        // **整张表每一行都会显示同一个端口** —— 比不显示更糟：用户会照着这个端口去排障。
        // 所以只有"这一列在整表里确实有变化"时才当端口用（真有型号这么干就照旧生效）。
        var portColumnUsable = HasVariedValues(rows, 4);
        foreach (var row in rows)
        {
            string portText;
            string portNote;
            if (portColumnUsable)
            {
                var portIndex = TextCell(row, 4);
                var resolved = ifNames.TryGetValue(portIndex, out var name) ? name : null;
                portText = resolved ?? portIndex;
                portNote = DescribeMacPort(portIndex, resolved);
            }
            else
            {
                // 私有表这列不能当端口用 → 改用标准 FDB 反查
                var macKey = MacAddressHelper.Normalize(MacCell(row, 2));
                if (macKey is not null && portLookup.PortsByMac.TryGetValue(macKey, out var resolvedPort))
                {
                    portText = resolvedPort;
                    portNote = $"端口来自标准 {portLookup.Source}（桥口→接口经 dot1dBasePortIfIndex + ifName 还原）";
                }
                else
                {
                    portText = NotProvidedText;
                    portNote = portLookup.FdbRows == 0
                        ? "SNMP MAC 表不含端口列（该型号 `.4` 全表同值），标准 FDB 表也没取到；端口请看【端口】页或 CLI"
                        : portLookup.Truncated
                            ? $"这次没查到该 MAC 的端口：标准 FDB 表本轮只取了一部分（时间预算）—— 勾选「完整拉取大表」或点[继续拉取大表]可覆盖更多"
                            : "标准 FDB 表里没有这个 MAC（可能是本机/CPU 条目，或该 MAC 只出现在上联口的老化条目里）";
                }
            }

            section.Rows.Add(new[]
            {
                TextCell(row, 1),
                MacCell(row, 2),
                portText,
                portNote,
                TextCell(row, 3),
                TextCell(row, 4),
            });
        }

        section.Truncated = walk.Truncated;
        // 断点游标：截断了就记下"取到哪了"，界面据此提供「继续拉取大表」
        section.ResumeOid = walk.Truncated ? walk.LastOid : null;
        if (droppedHalfRows > 0)
        {
            section.Note += $"⚠️ 本轮 WALK 在时间预算内主要走到了「VLAN」列：另有 {droppedHalfRows} 行"
                            + "只拿到 VLAN、没拿到 MAC，已按半截行丢弃（不是设备没有这些 MAC）。\n"
                            + "点[继续拉取大表]继续走，断点会往后面的列推进（每轮约 2 分钟）。\n";
        }

        if (missingVlanRows > 0)
        {
            section.Note += $"另有 {missingVlanRows} 行**有 MAC、没 VLAN**（这几轮设备没返回该列的 VLAN 值），VLAN 列如实显示 N/A。\n";
        }

        if (startAfterOid is not null)
        {
            section.Note = $"本轮为**增量续拉**（从断点 {ShortOid(startAfterOid)} 之后继续）。"
                           + (section.RowsAreCumulative
                               ? $"本表按 `VLAN.MAC` **累积重排**（本轮取回 {addedThisRound} 条 OID）：把各轮拿到的列合并后整表重渲染，"
                                 + "因此不会出现「同一个 MAC 分成两行」或重复行；同一 index 的列以最新一轮为准。\n"
                               : string.Empty)
                           + section.Note;
        }

        var truncationNote = walk.Truncated && !string.IsNullOrWhiteSpace(walk.Error)
            ? $"⚠️ {walk.Error}。大型设备的 MAC 表可能上万行，走完往往要几分钟且设备会限速 —— "
              + "点[继续拉取大表]可从断点接着取，几轮即可补齐；或勾选「完整拉取大表」一次拉全（慢）。\n"
            : string.Empty;
        section.Note = (section.Note ?? string.Empty) + (portColumnUsable
            ? truncationNote
              + "MAC 取自设备的**原始字节**（不再先解码成文本）；端口由 ifIndex 经标准 ifDescr 还原，"
              + "翻不出来就原样显示 ifIndex。与 CLI `show mac-address-table` 对不上时："
              + "CLI 默认不显示本机/CPU 条目（端口号 0 或 VLAN 接口），SNMP 表里可能会有。"
            : truncationNote
              + "MAC 取自设备的**原始字节**（不再先解码成文本）。"
              + "⚠️ 端口列显示「未提供」是**如实反映设备**：多个设备 Walk 样例中的 `.4` 列都是恒定枚举值，不是端口号 —— "
              + "旧版本拿它当端口号显示，每一行都会是同一个端口，反而误导排障。"
              + "要查某个 MAC 在哪个端口：用【端口】页或 CLI `show mac-address-table`。");
        if (section.Rows.Count == 0 && walk.Rows.Count > 0)
        {
            // 别让它显示成"该型号不支持"：原始数据是有的，只是行索引格式跟预期不一样
            section.Note = $"设备返回了 {walk.Rows.Count} 条 MAC 原始数据，但行索引格式与预期"
                         + "（`<VLAN>.<MAC 6 字节>`）不符，没能还原成行 —— 可能是新型号格式，请把设备型号反馈给我们。";
        }

        // 端口列：**按行精确 GET**（见 ResolveMacPortsAsync 的注释）。
        // 放在最后做：先保证 MAC 行本身已经拿到，端口只是"列补全"，失败也不影响表。
        try
        {
            var (portResolved, portPending) = await ResolveMacPortsAsync(
                    host, port, community, section, timeoutMs, retries, timeBudgetMs, cancellationToken)
                .ConfigureAwait(false);
            if (portPending > 0)
            {
                section.Note += $"\n端口列：本轮按行精确 GET 解出 {portResolved} / {portPending} 行"
                                + (portResolved < portPending
                                    ? "（其余受时间预算限制；点[继续拉取大表]或勾选「完整拉取大表」可继续补）"
                                    : "（全部解出）");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            section.Note += $"\n端口列解析失败（不影响 MAC 表本身）：{ex.Message}";
        }

        return section;
    }

    /// <summary>
    /// **增量续拉**：只重走被截断的大表（MAC / ARP），各从自己的断点继续，返回**本轮新增的行**。
    ///
    /// 为什么要它：大型核心设备的 MAC 表可能上万行、且连续请求过多会被设备限速，
    /// 一次拉全实测 16 分钟都跑不完。这里让界面"先出前 1.5 万行 → 点[继续拉取]再从断点补一轮"，
    /// 每轮 1~2 分钟、几轮补齐 —— 既不用盯着十几分钟，也不会因为一轮太长而被限速打断。
    /// </summary>
    public async Task<IReadOnlyList<SnmpSection>> ResumeBigTablesAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        string? macResumeOid,
        string? arpResumeOid,
        int timeBudgetMs = DefaultLargeTableBudgetMs,
        CancellationToken cancellationToken = default,
        SnmpSection? macSection = null,
        SnmpSection? arpSection = null)
    {
        var resumed = new List<SnmpSection>();
        if (!string.IsNullOrWhiteSpace(macResumeOid))
        {
            resumed.Add(await BuildMacSectionAsync(
                    host,
                    port,
                    community,
                    timeoutMs,
                    retries,
                    cancellationToken,
                    timeBudgetMs,
                    macResumeOid,
                    macSection?.RawColumns,
                    macSection?.RawColumnOrder)
                .ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(arpResumeOid))
        {
            resumed.Add(await BuildArpSectionAsync(
                    host,
                    port,
                    community,
                    timeoutMs,
                    retries,
                    cancellationToken,
                    timeBudgetMs,
                    arpResumeOid,
                    arpSection?.RawColumns,
                    arpSection?.RawColumnOrder)
                .ConfigureAwait(false));
        }

        return resumed;
    }

    /// <summary>OID 太长，界面提示里只显示末尾几段（前 20 个字符省略）。</summary>
    private static string ShortOid(string oid)
        => oid.Length <= 28 ? oid : "…" + oid[^24..];

    /// <summary>
    /// **按行精确 GET** 解析 MAC 表的端口列（2026-09-23 新增，取代"整表 WALK 标准 FDB"）。
    ///
    /// 为什么这样问：`dot1qTpFdbPort` 的索引就是 `&lt;VLAN&gt;.&lt;6 字节 MAC&gt;`，
    /// 而我们**手里已经有每一行的 VLAN + MAC** —— 直接按索引 GET 就能问到它所在的桥口
    /// （取不到再退无 VLAN 的 `dot1dTpFdbPort.&lt;MAC&gt;`），再经 `dot1dBasePortIfIndex` + `ifName` 还原成接口名。
    ///
    /// 请求数与"我们的行数"成正比（12 个 OID 一批，1.5 万行 ≈ 1250 次请求 ≈ 100 秒），
    /// 与整张 FDB 的大小无关；整表 WALK 可能只覆盖一小部分，这条可以显著提高覆盖率。
    /// 已经有端口的行会跳过 —— 所以[继续拉取大表]时只解新增行的端口，天然增量。
    /// </summary>
    public async Task<(int Resolved, int Total)> ResolveMacPortsAsync(
        string host,
        int port,
        string community,
        SnmpSection macSection,
        int timeoutMs,
        int retries,
        int timeBudgetMs,
        CancellationToken cancellationToken)
    {
        const int portColumn = 2;
        var pending = new List<(int RowIndex, string Vlan, string MacDotted)>();
        for (var i = 0; i < macSection.Rows.Count; i++)
        {
            var row = macSection.Rows[i];
            if (row.Length <= portColumn)
            {
                continue;
            }

            var current = row[portColumn];
            if (!string.IsNullOrWhiteSpace(current) && current != NotProvidedText)
            {
                continue;   // 已经有端口了（私有表那列可用，或上一轮已解出）
            }

            var vlanText = row.Length > 0 ? row[0] : string.Empty;
            var macText = row.Length > 1 ? row[1] : string.Empty;
            if (!int.TryParse(vlanText, out var vlan) || vlan is < 0 or > 4095)
            {
                continue;
            }

            var bytes = ParseMacBytes(macText);
            if (bytes is null)
            {
                continue;
            }

            pending.Add((i, vlan.ToString(CultureInfo.InvariantCulture), string.Join('.', bytes)));
        }

        if (pending.Count == 0)
        {
            return (0, 0);
        }

        var ifNames = await BuildIfIndexNameMapAsync(host, port, community, timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);
        var bridgeToName = await BuildBridgePortNameMapAsync(host, port, community, ifNames, timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var resolved = 0;

        // ① 带 VLAN 的 dot1qTpFdbPort.<vlan>.<mac>（首选，能区分同名 MAC 在多个 VLAN）
        var qbridge = pending.ToDictionary(
            p => $"{SnmpOidRepository.QbridgeFdbPortRoot}.{p.Vlan}.{p.MacDotted}",
            p => p.RowIndex,
            StringComparer.Ordinal);
        resolved += await ResolveByGetAsync(host, port, community, macSection, qbridge, bridgeToName, timeoutMs, retries, timeBudgetMs, watch, cancellationToken)
            .ConfigureAwait(false);

        // ② 剩下的用无 VLAN 的 dot1dTpFdbPort.<mac>（老设备 / 某些 VLAN 不在 FDB 里）
        if (watch.ElapsedMilliseconds < timeBudgetMs || timeBudgetMs == 0)
        {
            var bridge = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in pending)
            {
                var row = macSection.Rows[p.RowIndex];
                if (row.Length > portColumn && !string.IsNullOrWhiteSpace(row[portColumn]) && row[portColumn] != NotProvidedText)
                {
                    continue;   // ① 已经解出来了
                }

                bridge[$"{SnmpOidRepository.BridgeFdbPortRoot}.{p.MacDotted}"] = p.RowIndex;
            }

            if (bridge.Count > 0)
            {
                resolved += await ResolveByGetAsync(host, port, community, macSection, bridge, bridgeToName, timeoutMs, retries, timeBudgetMs, watch, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (resolved, pending.Count);
    }

    /// <summary>把一批 "OID → 行号" 用 GET 问回来（12 个一批、每 480 个歇 150 ms），命中的写回端口列。</summary>
    private async Task<int> ResolveByGetAsync(
        string host,
        int port,
        string community,
        SnmpSection macSection,
        Dictionary<string, int> oidToRow,
        Dictionary<int, string> bridgeToName,
        int timeoutMs,
        int retries,
        int timeBudgetMs,
        System.Diagnostics.Stopwatch watch,
        CancellationToken cancellationToken)
    {
        var resolved = 0;
        foreach (var chunk in oidToRow.Keys.Chunk(480))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeBudgetMs > 0 && watch.ElapsedMilliseconds >= timeBudgetMs)
            {
                break;
            }

            var response = await _client
                .GetWithStatusAsync(host, port, community, chunk, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            foreach (var oid in chunk)
            {
                if (!response.Values.TryGetValue(oid, out var value) || value.IsNoReading || value.Number is not { } bridgePort)
                {
                    continue;
                }

                var rowIndex = oidToRow[oid];
                var row = macSection.Rows[rowIndex];
                var portText = bridgeToName.TryGetValue((int)bridgePort, out var name) ? name : $"桥口 {bridgePort}";
                row[2] = portText;
                if (row.Length > 3)
                {
                    row[3] = "端口来自标准 dot1qTpFdbPort / dot1dTpFdbPort（按行精确 GET，桥口经 dot1dBasePortIfIndex + ifName 还原）";
                }

                resolved++;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);   // 主动降速，避开设备限速
        }

        return resolved;
    }

    /// <summary>桥口 → 接口名（dot1dBasePortIfIndex + 已取好的 ifIndex→ifName）。</summary>
    private async Task<Dictionary<int, string>> BuildBridgePortNameMapAsync(
        string host,
        int port,
        string community,
        Dictionary<string, string> ifNames,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, string>();
        var walk = await _client
            .WalkAsync(
                host,
                port,
                community,
                SnmpOidRepository.BridgeBasePortIfIndexRoot,
                timeoutMs,
                retries,
                SnmpOidRepository.BridgePortMaxRows,
                cancellationToken,
                0)
            .ConfigureAwait(false);
        foreach (var value in walk.Rows)
        {
            var rest = Suffix(value.Oid, SnmpOidRepository.BridgeBasePortIfIndexRoot);
            if (rest is null || value.Number is not { } ifIndex || !int.TryParse(rest, out var bridgePort))
            {
                continue;
            }

            var ifIndexText = ((long)ifIndex).ToString(CultureInfo.InvariantCulture);
            map[bridgePort] = ifNames.TryGetValue(ifIndexText, out var name) ? name : $"ifIndex {ifIndexText}";
        }

        return map;
    }

    /// <summary>把 `02:00:00:00:00:02` 这样的 MAC 文本转成 6 个字节（失败返回 null）。</summary>
    internal static byte[]? ParseMacBytes(string? text)
    {
        var normalized = MacAddressHelper.Normalize(text);
        if (normalized is null || normalized.Length != 12)
        {
            return null;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(normalized.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>标准 FDB 反查出来的端口信息。</summary>
    private sealed record MacPortLookup(
        Dictionary<string, string> PortsByMac,
        int FdbRows,
        bool Truncated,
        string Source)
    {
        public static MacPortLookup Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal), 0, false, string.Empty);
    }

    /// <summary>
    /// 用**标准表**把"MAC → 端口"补出来（私有表端口列不可用时依靠这条）。
    ///
    /// 数据链：dot1qTpFdbPort / dot1dTpFdbPort（索引带 MAC，取值是**桥口**）
    ///         → dot1dBasePortIfIndex（桥口 → ifIndex）
    ///         → 标准 ifName/ifDescr（ifIndex → 接口名，外面已经取过）。
    /// 设备 Walk 实测：dot1dBasePortIfIndex 表规模小，查询开销较低；
    /// dot1dTpFdbPort 22,079 行 / 94 s；dot1qTpFdbPort 同样可用且索引带 VLAN（优先用它）。
    /// 走表同样受时间预算约束：取到多少算多少，取不满时界面会说明"端口只覆盖了一部分"。
    ///
    /// ⚠️ **2026-09-23 实测：整表 WALK 这条路在当前版本**没有**默认开启**（调用点被注释掉），原因：
    ///   ① 部分设备的 dot1qTpFdbPort **按 VLAN 排序**，时间预算可能只够走完最前面几个 VLAN，
    ///      端口覆盖率有限，却要多花较长时间；
    ///   ② 正确做法是**按行精确 GET**：对我们已经拿到的每一行 MAC，
    ///      直接 GET `dot1qTpFdbPort.&lt;vlan&gt;.&lt;6 字节 MAC&gt;`（12 个一批），
    ///      请求数与"我们的行数"成正比，而不是与整张 FDB 的大小成正比 —— 覆盖率能到接近 100%。
    ///      这条留待下一轮实现（见 work/HANDOFF.md 第四十四轮待办）。
    /// </summary>
    private async Task<MacPortLookup> BuildMacPortLookupAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        Dictionary<string, string> ifNames,
        int timeBudgetMs,
        CancellationToken cancellationToken)
    {
        var byMac = new Dictionary<string, string>(StringComparer.Ordinal);

        // ① 桥口 → ifIndex（很小，不设预算）
        var bridgeToIfIndex = new Dictionary<int, string>();
        var baseWalk = await _client
            .WalkAsync(
                host,
                port,
                community,
                SnmpOidRepository.BridgeBasePortIfIndexRoot,
                timeoutMs,
                retries,
                SnmpOidRepository.BridgePortMaxRows,
                cancellationToken,
                0)
            .ConfigureAwait(false);
        foreach (var value in baseWalk.Rows)
        {
            var rest = Suffix(value.Oid, SnmpOidRepository.BridgeBasePortIfIndexRoot);
            if (rest is null || value.Number is not { } ifIndex || !int.TryParse(rest, out var bridgePort))
            {
                continue;
            }

            bridgeToIfIndex[bridgePort] = ((long)ifIndex).ToString();
        }

        // ② 标准 FDB：优先 Q-BRIDGE（带 VLAN），没有再退 BRIDGE-MIB
        foreach (var (root, macSegments, label) in new[]
                 {
                     (SnmpOidRepository.QbridgeFdbPortRoot, 6, "dot1qTpFdbPort"),
                     (SnmpOidRepository.BridgeFdbPortRoot, 6, "dot1dTpFdbPort"),
                 })
        {
            var fdbWalk = await _client
                .WalkAsync(
                    host,
                    port,
                    community,
                    root,
                    timeoutMs,
                    retries,
                    SnmpOidRepository.BridgeFdbMaxRows,
                    cancellationToken,
                    timeBudgetMs)
                .ConfigureAwait(false);
            if (fdbWalk.Rows.Count == 0)
            {
                continue;
            }

            foreach (var value in fdbWalk.Rows)
            {
                var rest = Suffix(value.Oid, root);
                if (rest is null || value.Number is not { } bridgePortNumber)
                {
                    continue;
                }

                var parts = rest.Split('.');
                if (parts.Length < macSegments)
                {
                    continue;
                }

                var macKey = string.Concat(parts.Skip(parts.Length - macSegments)
                    .Select(p => int.TryParse(p, out var b) && b is >= 0 and <= 255 ? b.ToString("x2") : null));
                if (macKey.Length != 12)
                {
                    continue;
                }

                var bridgePort = (int)bridgePortNumber;
                var portText = bridgeToIfIndex.TryGetValue(bridgePort, out var ifIndex)
                               && ifNames.TryGetValue(ifIndex, out var name)
                    ? name
                    : $"桥口 {bridgePort}";
                byMac[macKey] = portText;
            }

            return new MacPortLookup(byMac, fdbWalk.Rows.Count, fdbWalk.Truncated, label);
        }

        return new MacPortLookup(byMac, 0, false, "(两张标准 FDB 表都没有数据)");
    }

    private async Task<SnmpSection> BuildArpSectionAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken,
        int timeBudgetMs = SnmpQueryService.DefaultLargeTableBudgetMs,
        string? startAfterOid = null,
        Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>? previousRawColumns = null,
        List<string>? previousOrder = null)
    {
        var section = new SnmpSection
        {
            Category = SnmpCategory.Ip,
            Title = SnmpOidRepository.Title(SnmpCategory.Ip),
            Columns = new[] { "IP", "MAC", "VLAN", "状态原始值" },
        };

        var walk = await _client
            .WalkAsync(
                host,
                port,
                community,
                SnmpOidRepository.ArpRoot,
                timeoutMs,
                retries,
                SnmpOidRepository.ArpMaxVarbinds,
                cancellationToken,
                timeBudgetMs,
                progress: null,
                startAfterOid: startAfterOid)
            .ConfigureAwait(false);

        // ARP 表第 1 列是 **三层接口的 ifIndex**，不是 VLAN ID。
        // 用标准 ifDescr（"VLAN <id>"）把它还原成 VLAN ID；还原不了就原样显示并标注（P1-5）。
        var ifNames = await BuildIfIndexNameMapAsync(host, port, community, timeoutMs, retries, cancellationToken)
            .ConfigureAwait(false);
        var vlanIdByIfIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (index, text) in ifNames)
        {
            if (!text.StartsWith("VLAN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digits = text[4..].Trim();
            if (int.TryParse(digits, out var vlanId))
            {
                vlanIdByIfIndex[index] = vlanId.ToString();
            }
        }

        // 索引 = <三层接口 ifIndex>.<IP 4 字节> = **5 段**。
        // 与 MAC 表同理：GETBULK 按 OID 顺序走，**同一行的不同列会落在不同轮**里
        // （列 1 的条目全部排在列 2 之前），所以续拉必须按 index 合并列、整表重渲染，
        // 否则会出现"同一 IP 分成两行""后一行 MAC 是 N/A"以及重复行（2026-09-23 与 MAC 表一并修）。
        var grouped = GroupByRowWithIndex(walk.Rows, SnmpOidRepository.ArpRoot, 5);
        var accumulator = previousRawColumns ?? new Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>(StringComparer.Ordinal);
        var order = previousOrder ?? new List<string>();
        MergeRawColumns(accumulator, order, grouped);
        section.RawColumns = accumulator;
        section.RawColumnOrder = order;
        section.RowsAreCumulative = previousRawColumns is not null;

        // 只保留"有 MAC 或 IP"的行：只有列 1（ifIndex）的条目是时间预算用完时的半截行，保留只会显示满屏 N/A。
        var rows = order
            .Select(index => (Index: index, Columns: accumulator[index]))
            .Where(r => r.Columns.ContainsKey(2) || r.Columns.ContainsKey(3))
            .ToList();
        var droppedHalfRows = order.Count - rows.Count;
        section.DroppedHalfRows = droppedHalfRows;
        // "有 IP、没 MAC"的行：MAC 那列在已走过的几轮里设备没返回，如实说明
        var missingMacRows = rows.Count(r => TextCell(r.Columns, 2) == "N/A");

        foreach (var (index, row) in rows)
        {
            var rawVlan = TextCell(row, 1);
            var vlan = vlanIdByIfIndex.TryGetValue(rawVlan, out var resolved)
                ? resolved
                : rawVlan == "N/A" ? "N/A" : $"{rawVlan}（ifIndex）";
            var ip = TextCell(row, 3);
            if (ip == "N/A")
            {
                // 列 3 还没走到时，IP 本来就在索引里（`<ifIndex>.<a>.<b>.<c>.<d>`）—— 从索引还原，别显示 N/A
                ip = IpFromArpIndex(index) ?? "N/A";
            }

            section.Rows.Add(new[]
            {
                ip,
                MacCell(row, 2),
                vlan,
                TextCell(row, 5),
            });
        }

        section.Truncated = walk.Truncated;
        section.ResumeOid = walk.Truncated ? walk.LastOid : null;
        if (droppedHalfRows > 0)
        {
            section.Note += $"⚠️ 本轮 WALK 在时间预算内主要走到了「三层接口」列：另有 {droppedHalfRows} 行"
                            + "只拿到接口、没拿到 MAC/IP，已按半截行丢弃（点[继续拉取大表]可继续推进）。\n";
        }

        if (missingMacRows > 0)
        {
            section.Note += $"另有 {missingMacRows} 行**有 IP、没 MAC**（这几轮设备没返回该列的 MAC 值），MAC 列如实显示 N/A。\n";
        }

        section.Note = (walk.Truncated && !string.IsNullOrWhiteSpace(walk.Error)
                           ? $"⚠️ {walk.Error}（点[继续拉取大表]可从断点接着取；勾选「完整拉取大表」则一次拉全，慢）。\n"
                           : string.Empty)
                     + (startAfterOid is null
                         ? string.Empty
                         : $"本轮为增量续拉，从断点 {ShortOid(startAfterOid)} 之后继续。"
                           + (section.RowsAreCumulative
                               ? "本表按行索引**累积重排**（各轮的列合并后整表重渲染），不会出现重复行。\n"
                               : "\n"))
                     + section.Note
                     + "来自 ARP 表；VLAN 列是设备给的**三层接口 ifIndex**，已按标准 ifDescr（VLAN x）还原成 VLAN ID，"
                     + "还原不了的会标注（ifIndex）。MAC 取自原始字节。";
        return section;
    }

    /// <summary>
    /// 从 ARP 表的行索引还原 IP：索引 = `&lt;三层接口 ifIndex&gt;.&lt;a&gt;.&lt;b&gt;.&lt;c&gt;.&lt;d&gt;`，最后 4 段就是 IP。
    /// 用途：时间预算用完时"列 3（IP 值）"还没走到，但索引里本来就有 IP —— 不该显示 N/A。
    /// </summary>
    internal static string? IpFromArpIndex(string index)
    {
        var parts = index.Split('.');
        if (parts.Length < 4)
        {
            return null;
        }

        var bytes = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[parts.Length - 4 + i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])
                || bytes[i] is < 0 or > 255)
            {
                return null;
            }
        }

        return string.Join('.', bytes);
    }

    /// <summary>内存解析结果（统一按 MB 输出；KB 列自动 ÷1024）。</summary>
    public readonly record struct MemoryInfo(long TotalMb, long UsedMb, long FreeMb, string Source);

    /// <summary>
    /// 用标准 ifDescr（`1.3.6.1.2.1.2.2.1.2`）建 `ifIndex → 接口名` 映射 —— 全库的"坐标系"：
    /// ARP 的 VLAN 列、MAC 表的端口列都要靠它把裸 ifIndex 翻成人能看懂的名字。
    /// </summary>
    private async Task<Dictionary<string, string>> BuildIfIndexNameMapAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var root = SnmpOidRepository.StdIf(SnmpOidRepository.StdIfDescr);
        var walk = await _client
            .WalkAsync(host, port, community, root, timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in walk.Rows)
        {
            var index = LastIndex(row.Oid, root);
            if (index is not null)
            {
                map[index] = Normalize(row.Display);
            }
        }

        return map;
    }

    /// <summary>
    /// 内存使用率：只认第 5 列（真机实测 = 已用÷总量），且必须与计算值相差 ≤3 个百分点；
    /// 否则回退到"已用 ÷ 总量"自己算。第 11 列在 N18000 上是 32（与使用率无关），已不再当候选。
    /// </summary>
    private static (double Value, bool FromDevice) ResolveUsagePercent(
        IReadOnlyList<SnmpValue> resourceRows,
        MemoryInfo memory,
        double computed)
    {
        var reported = resourceRows
            .FirstOrDefault(v => string.Equals(v.Oid, SnmpOidRepository.MemoryUsagePercent, StringComparison.Ordinal))
            ?.Number;

        if (reported is { } value and >= 0 and <= 100 && Math.Abs(value - computed) <= 3)
        {
            return (value, true);
        }

        return (computed, false);
    }

    /// <summary>CPU 解析结果（私有表 .36.1）。</summary>
    public readonly record struct CpuCardInfo(string Name, IReadOnlyList<long> Percent, (long? Warning, long? Critical)? Threshold);

    public readonly record struct CpuInfo(
        IReadOnlyList<long> SystemPercent,
        long? WarningThreshold,
        long? CriticalThreshold,
        IReadOnlyList<CpuCardInfo> Cards);

    /// <summary>
    /// 解析锐捷私有 CPU 表 `.36.1`（现场实测的主路径）：
    ///   系统级 `.36.1.1.{1,2,3}.0` = 三个窗口利用率、`{4,5}.0` = 警告/严重阈值；
    ///   每卡 `​.36.1.2.1.2.<i>` = 名称、`{3,4,5}` = 三窗口、`{6,7}` = 阈值（行数上限 32）。
    /// 三个窗口与"5 秒 / 1 分钟 / 5 分钟"的对应关系是**按位置推断**的（设备不给窗口名）。
    /// </summary>
    public static CpuInfo? ResolveCpu(IReadOnlyList<SnmpValue> cpuRows)
    {
        long? Number(string oid) =>
            // 哨兵值（-255 = 设备明确"无读数"）不能当阈值/利用率用：
            // 否则界面上会出现"阈值 警告 -255"，正是用户反馈过的那类脏数据。
            cpuRows.FirstOrDefault(v => string.Equals(v.Oid, oid, StringComparison.Ordinal)) is { IsNoReading: false } row
                ? row.Number
                : null;

        var system = new List<long>();
        for (var window = 1; window <= 3; window++)
        {
            if (Number(SnmpOidRepository.CpuSystemPercent(window)) is { } value and >= 0)
            {
                system.Add(value);
            }
        }

        var warning = Number(SnmpOidRepository.CpuSystemThreshold(4));
        var critical = Number(SnmpOidRepository.CpuSystemThreshold(5));

        // 每卡/每核：按 .36.1.2.1.<列>.<索引> 分组
        var cards = new Dictionary<string, Dictionary<int, SnmpValue>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in cpuRows)
        {
            var rest = Suffix(row.Oid, SnmpOidRepository.CpuCardRoot);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var column))
            {
                continue;
            }

            var index = string.Join('.', parts.Skip(1));
            if (!cards.TryGetValue(index, out var cells))
            {
                cells = new Dictionary<int, SnmpValue>();
                cards[index] = cells;
            }

            cells[column] = row;
            if (column == 2 && !string.IsNullOrWhiteSpace(row.Text))
            {
                names[index] = row.Text!;
            }
        }

        var cardList = new List<CpuCardInfo>();
        foreach (var (index, cells) in cards.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var percent = new List<long>();
            for (var column = 3; column <= 5; column++)
            {
                if (cells.TryGetValue(column, out var cell) && cell.Number is { } value and >= 0)
                {
                    percent.Add(value);
                }
            }

            if (percent.Count == 0)
            {
                continue;
            }

            // 哨兵值（-255）不是阈值：直接取 .Number 会把它显示成"阈值 警告 -255"
            var cardWarning = cells.TryGetValue(6, out var warn) && !warn.IsNoReading ? warn.Number : null;
            var cardCritical = cells.TryGetValue(7, out var crit) && !crit.IsNoReading ? crit.Number : null;
            cardList.Add(new CpuCardInfo(
                names.TryGetValue(index, out var name) ? name : $"CPU {index}",
                percent,
                cardWarning is null && cardCritical is null ? null : (cardWarning, cardCritical)));

            if (cardList.Count >= SnmpOidRepository.CpuCardMaxRows)
            {
                break;
            }
        }

        if (system.Count == 0 && cardList.Count == 0)
        {
            return null;
        }

        return new CpuInfo(system, warning, critical, cardList);
    }

    /// <summary>
    /// 从资源表 WALK 结果解析内存 —— **概览卡与资源表共用这一份**（铁律 5：内存只有一个来源）。
    /// 先按行名（含 "memory"）定位内存行，再按候选列组择优：
    ///   `{12,13,14}` MB（某些版本） → `{6,7,8}` KB（另一些版本，÷1024）。
    /// 两组都凑不齐（或已用+剩余≠总量）就返回 null → 界面显示 N/A，绝不猜列号。
    /// </summary>
    public static MemoryInfo? ResolveMemory(IReadOnlyList<SnmpValue> resourceRows)
    {
        var groups = new (int Total, int Used, int Free, double Divisor, string Source)[]
        {
            (12, 13, 14, 1d, ".35.1.1.1.12/13/14.1（单位 MB）"),
            (6, 7, 8, 1024d, ".35.1.1.1.6/7/8.1（单位 KB，已换算为 MB）"),
        };

        var rows = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        var rowNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in resourceRows)
        {
            var rest = Suffix(row.Oid, SnmpOidRepository.ResourceTableRoot);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out var rowType) ||
                !int.TryParse(parts[2], out var column))
            {
                continue;
            }

            var key = rowType.ToString();
            if (!rows.TryGetValue(key, out var cells))
            {
                cells = new Dictionary<int, long>();
                rows[key] = cells;
            }

            if (row.Number is { } number && number > 0)
            {
                cells[column] = number;
            }

            if (column == 2 && !string.IsNullOrWhiteSpace(row.Text))
            {
                rowNames[key] = row.Text!;
            }
        }

        var memoryKey = rowNames
            .FirstOrDefault(p => p.Value.Contains("memory", StringComparison.OrdinalIgnoreCase)).Key
            ?? rows.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (memoryKey is null || !rows.TryGetValue(memoryKey, out var memoryCells))
        {
            return null;
        }

        foreach (var (totalColumn, usedColumn, freeColumn, divisor, source) in groups)
        {
            if (!memoryCells.TryGetValue(totalColumn, out var total) ||
                !memoryCells.TryGetValue(usedColumn, out var used) ||
                !memoryCells.TryGetValue(freeColumn, out var free) ||
                used + free != total)
            {
                continue;
            }

            return new MemoryInfo(
                // 向下取整，避免显示值超过设备返回的容量。
                (long)(total / divisor),
                (long)(used / divisor),
                (long)(free / divisor),
                source);
        }

        return null;
    }

    /// <summary>把 WALK 结果按"列 + 行索引"分组：索引 = 最后 indexLength 段。</summary>
    private static List<Dictionary<int, (string Text, byte[]? Bytes)>> GroupByRow(
        IReadOnlyList<SnmpValue> rows,
        string root,
        int indexLength)
        => GroupByRowWithIndex(rows, root, indexLength).Select(r => r.Columns).ToList();

    /// <summary>与 <see cref="GroupByRow"/> 相同，但**把行索引一起返回**（多轮拼接要按 index 合并列）。</summary>
    private static List<(string Index, Dictionary<int, (string Text, byte[]? Bytes)> Columns)> GroupByRowWithIndex(
        IReadOnlyList<SnmpValue> rows,
        string root,
        int indexLength)
    {
        var byIndex = new Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in rows)
        {
            var rest = Suffix(row.Oid, root);
            if (rest is null)
            {
                continue;
            }

            var parts = rest.Split('.');
            if (parts.Length <= indexLength || !int.TryParse(parts[0], out var column))
            {
                continue;
            }

            var index = string.Join('.', parts.Skip(parts.Length - indexLength));
            if (!byIndex.TryGetValue(index, out var values))
            {
                values = new Dictionary<int, (string Text, byte[]? Bytes)>();
                byIndex[index] = values;
                order.Add(index);
            }

            // 原始字节一并带下去：MAC 列（58 69 6C 33 DF A8 是合法 UTF-8）必须靠它还原（P0-4）
            values[column] = (row.Display, row.Bytes);
        }

        return order.Select(index => (index, byIndex[index])).ToList();
    }

    /// <summary>
    /// 把本轮分好组的列**并进累积器**（按 index 合并；同一列以**本轮**的值为准，缺的列保留原来的）。
    /// 这就是"跨轮次补列"的核心：列 1 在第 1 轮拿到、列 2 在第 2 轮拿到，合并后仍是**一行**。
    /// </summary>
    internal static void MergeRawColumns(
        Dictionary<string, Dictionary<int, (string Text, byte[]? Bytes)>> accumulator,
        List<string> order,
        IReadOnlyList<(string Index, Dictionary<int, (string Text, byte[]? Bytes)> Columns)> incoming)
    {
        foreach (var (index, columns) in incoming)
        {
            if (!accumulator.TryGetValue(index, out var existing))
            {
                accumulator[index] = new Dictionary<int, (string Text, byte[]? Bytes)>(columns);
                order.Add(index);
                continue;
            }

            foreach (var (column, value) in columns)
            {
                existing[column] = value;
            }
        }
    }

    /// <summary>取某一列的 MAC：有原始字节就用字节格式化，否则退回文本归一化。</summary>
    private static string MacCell(Dictionary<int, (string Text, byte[]? Bytes)> row, int column)
    {
        if (!row.TryGetValue(column, out var cell))
        {
            return "N/A";
        }

        return cell.Bytes is { Length: > 0 } bytes
            ? MacAddressHelper.FormatBytes(bytes)
            : Normalize(cell.Text);
    }

    private static string TextCell(Dictionary<int, (string Text, byte[]? Bytes)> row, int column) =>
        row.TryGetValue(column, out var cell) ? Normalize(cell.Text) : "N/A";

    /// <summary>
    /// 这一列在整表里是否出现过 ≥2 个不同取值。
    ///
    /// 用途：判断某列能不能当 ifIndex（端口号）用。整表恒为同一个值的列，
    /// 不可能是"每个条目各自的端口号"——它是枚举/标志位。
    /// 依据：设备 Walk 样例的 MAC 表 `.4` 列全表同值（见 BuildMacSectionAsync 里的注释）。
    /// 只有一行的表会返回 false（无法证明它"有变化"），这时宁可显示"未提供"也不猜。
    /// </summary>
    internal static bool HasVariedValues(
        IReadOnlyList<Dictionary<int, (string Text, byte[]? Bytes)>> rows,
        int column)
    {
        string? first = null;
        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var cell))
            {
                continue;
            }

            var text = Normalize(cell.Text);
            if (first is null)
            {
                first = text;
            }
            else if (!string.Equals(first, text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// MAC 表的「说明」列：告诉使用者这条目为什么 CLI 里可能看不到。
    /// CLI `show mac-address-table` 默认不列本机/CPU 条目与逻辑接口（VLAN SVI）上的条目。
    /// </summary>
    private static string DescribeMacPort(string portIndex, string? portName)
    {
        if (portIndex == "0")
        {
            return "本机/CPU 条目（CLI 默认不显示）";
        }

        if (portName is null)
        {
            return $"端口未识别（ifIndex {portIndex}）";
        }

        return IsLogicalInterface(portName)
            ? "逻辑接口条目（CLI 默认不显示）"
            : "物理端口（动态学习）";
    }

    private static string? Suffix(string oid, string root) =>
        oid.StartsWith(root + ".", StringComparison.Ordinal) ? oid[(root.Length + 1)..] : null;

    private static string? LastIndex(string oid, string root)
    {
        var rest = Suffix(oid, root);
        if (rest is null)
        {
            return null;
        }

        var parts = rest.Split('.');
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string Lookup(IReadOnlyDictionary<string, SnmpValue> values, string oid) =>
        values.TryGetValue(oid, out var value) ? Normalize(value.Display) : "N/A";

    private static string LookupBySuffix(IReadOnlyDictionary<string, SnmpValue> values, string oid) => Lookup(values, oid);

    private static string Text(IReadOnlyDictionary<string, SnmpValue> values, string oid, string unit) =>
        values.TryGetValue(oid, out var value) ? WithUnit(Normalize(value.Display), unit) : "N/A";

    private static long? Number(IReadOnlyDictionary<string, SnmpValue> values, string oid) =>
        // 哨兵值（-255 = 设备明确"无读数"）一律当"没有值"，绝不让它参与计算
        values.TryGetValue(oid, out var value) && !value.IsNoReading ? value.Number : null;

    private static string WithUnit(string text, string unit) =>
        string.IsNullOrWhiteSpace(unit) || text == "N/A" ? text : $"{text} {unit}";

    /// <summary>
    /// 设备用来表示"这个字段我没提供"的哨兵值：INTEGER `-255`、字符串 `"-"`、空串。
    /// 依据：外部 Walk 样例里风扇表 `.42.1.11`、电源表 `.41.1.16` 都是 -255，
    /// 风扇表 `.42.1.{6,7,8,9}` 是 `"-"`（见 docs/snmp.md §2.6 与《学会获取与读懂OID》§2.6）。
    /// 原样显示会让人把它当成真实测量值（例如把 -255 当成温度/状态），所以统一显示成"未提供"。
    /// </summary>
    public const string NotProvidedText = "未提供";

    private const string SentinelDash = "-";
    private const string SentinelMinus255 = "-255";

    /// <summary>设备确实返回了值，但值是"没提供"的哨兵占位。</summary>
    private static bool IsSentinelValue(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed == SentinelDash || trimmed == SentinelMinus255;
    }

    /// <summary>
    /// 表格里"这一格没有可用内容"：设备压根没返回这一格（N/A），
    /// 或者返回的是哨兵占位（未提供）。两者都不能拿来下结论，文案上分开显示。
    /// </summary>
    private static bool IsBlankCell(string text) =>
        string.Equals(text, "N/A", StringComparison.Ordinal) ||
        string.Equals(text, NotProvidedText, StringComparison.Ordinal);

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "N/A";
        }

        var trimmed = text.Trim();
        return IsSentinelValue(trimmed) ? NotProvidedText : trimmed;
    }

    private static string? Note(SnmpOidDefinition definition) =>
        definition.Confidence == SnmpOidConfidence.Confirmed ? definition.Description : $"{definition.Description}（{ConfidenceText(definition.Confidence)}）";

    public static string ConfidenceText(SnmpOidConfidence confidence) => confidence switch
    {
        SnmpOidConfidence.Confirmed => "现场 walk 已确认",
        SnmpOidConfidence.Inferred => "推断值",
        _ => "含义未确认",
    };
}
