using System.Diagnostics;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>
/// 批量巡检：对一批设备跑**轻量**SNMP 采集（只取标量 + 资源/温度/风扇/电源 + 端口状态两列），
/// 然后按阈值判出"需关注"的清单。
///
/// 为什么必须是轻量：完整查询（<see cref="SnmpQueryService.QueryAsync(string,int,string,int,int,CancellationToken,bool)"/>
/// 的 includeLargeTables=true）会把接口表 12 列、VLAN、MAC、ARP 全部 WALK 一遍 —— 一台核心机就是
/// 十几万条 varbind；巡检 20 台时这个代价不可接受，而巡检根本不需要那些明细。
///
/// 异常判据只用三样（见 <see cref="InspectionRow"/> 的注释）：取不到 / CPU·内存超阈值 / 温度超设备自报阈值。
/// 端口 down 只作参考列。
/// </summary>
public sealed class SnmpInspectionService
{
    /// <summary>设备没有自报阈值时用的兜底判据（只是"值得看一眼"，不是故障判定）。</summary>
    private const int DefaultCpuPercent = 80;
    private const int DefaultMemoryPercent = 85;

    private readonly SnmpQueryService _snmp;
    private readonly ILogService? _log;

    public SnmpInspectionService(SnmpQueryService snmp, ILogService? log = null)
    {
        _snmp = snmp;
        _log = log;
    }

    public async Task<InspectionRow> InspectAsync(
        DeviceTarget target,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        SnmpQueryResult result;
        try
        {
            result = await _snmp
                .QueryAsync(target.Ip, 161, community, timeoutMs, retries, cancellationToken, includeLargeTables: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单台失败不能中断整轮巡检：如实记成"取不到"，把原因带回去。
            watch.Stop();
            _log?.Warn($"巡检 {target.Ip} 失败", ex);
            return new InspectionRow
            {
                Ip = target.Ip,
                Name = target.Name,
                Building = target.Building,
                Status = InspectionStatus.Failed,
                Anomaly = $"查询异常：{ex.Message}",
                StatusText = ex.Message,
                ElapsedMs = watch.ElapsedMilliseconds,
            };
        }

        watch.Stop();
        return Evaluate(target, result, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 把一次查询结果判成巡检行。**独立成静态方法是为了能在自检里直接喂构造好的结果**，
    /// 不需要真机也不需要模拟代理。
    /// </summary>
    internal static InspectionRow Evaluate(DeviceTarget target, SnmpQueryResult result, long elapsedMs)
    {
        var anomalies = new List<string>();

        if (result.Status == SnmpQueryStatus.Failed)
        {
            return new InspectionRow
            {
                Ip = target.Ip,
                Name = target.Name,
                Building = target.Building,
                Status = InspectionStatus.Failed,
                Anomaly = result.StatusText.Length > 0 ? result.StatusText : "查询失败",
                StatusText = result.StatusText,
                ElapsedMs = elapsedMs,
            };
        }

        string Field(string name) =>
            result.Fields.FirstOrDefault(f => f.Name == name)?.Value ?? "N/A";

        // ⚠️ "数据不完整"必须算"需关注"（2026-09-22 DSH 复查挑出来的最严重一条）：
        // `Failures` 里的东西是**没取到**（资源表 WALK 超时、端口状态没回来…），
        // 而旧写法只看"有没有命中阈值异常"，于是"CPU/内存全是 N/A"的巡检结果会被判成 **正常** ——
        // 巡检表全绿、导出的 CSV 里也没有原因，是最危险的假阳性。
        if (result.Failures.Count > 0)
        {
            anomalies.Add($"数据不完整：{string.Join("；", result.Failures)}");
        }

        // ⚠️ 内存/CPU 在 QueryAsync 里是**「资源表」的行**，不在 result.Fields 里
        // （Fields 只有设备型号/版本/序列号/设备名/运行时间这些标量）。
        // 第一版这里写成 Field("CPU 利用率") 去 Fields 里找，结果永远是 N/A —— 真机一跑就露馅。
        string Row(string rowName, int column = 1) =>
            result.Sections
                .FirstOrDefault(s => s.Category == SnmpCategory.Resource)?
                .Rows.FirstOrDefault(r => string.Equals(r.ElementAtOrDefault(0), rowName, StringComparison.Ordinal))?
                .ElementAtOrDefault(column) ?? "N/A";

        var cpu = Row("CPU 利用率");
        var memoryUsage = Row("内存使用率");
        var memoryTotal = Row("内存总量");
        var memoryUsed = Row("内存已用");
        var memory = memoryTotal == "N/A"
            ? "N/A"
            : $"{memoryUsed} / {memoryTotal}（{memoryUsage}）";

        // CPU：行里是 "0% / 0% / 0%"（三个采集窗口），巡检取**最大值**判阈值 —— 三个窗口里只要有一个高就值得看。
        var cpuPercent = MaxPercent(cpu);
        // 用设备自报的**警告**阈值（没有才退到严重阈值 / 兜底 80）：
        // 巡检的定位是"值得看一眼的挑出来"，不是"已经出故障的才算"。
        // 真机例子：目标交换机 自报"警告 90 / 严重 100"，CPU 91% 应该在巡检里被指出来。
        var cpuThreshold = ParseCpuThreshold(Row("CPU 阈值")) ?? DefaultCpuPercent;
        if (cpuPercent is { } cpuValue && cpuValue >= cpuThreshold)
        {
            anomalies.Add($"CPU {cpuValue}%（阈值 {cpuThreshold}）");
        }

        var memoryPercent = FirstPercent(memoryUsage);
        if (memoryPercent is { } memValue && memValue >= DefaultMemoryPercent)
        {
            anomalies.Add($"内存使用率 {memValue}%（阈值 {DefaultMemoryPercent}%）");
        }

        // 温度：设备自己的表里就带"警告/告警阈值"两列，直接拿设备给的阈值比，比我们拍脑袋定阈值准。
        var temperature = SummarizeTemperature(result, anomalies);
        var (fan, power) = SummarizeFanPower(result);

        var ports = "N/A";
        if (result.PortStatus is { } status)
        {
            ports = $"{status.Total} 口／down {status.LinkDown}（管理关闭 {status.AdminDown}）";
            // ifAdminStatus 没回来时不能把"管理关闭"报成 0 —— 那等于说"没人关过口"，与事实相反
            if (!status.AdminStatusKnown)
            {
                ports += "（⚠ ifAdminStatus 未取到，无法区分人为关闭）";
            }

            if (status.Truncated)
            {
                ports += "（⚠ 被行数上限截断，口数不是全量）";
            }
        }

        return new InspectionRow
        {
            Ip = target.Ip,
            Name = target.Name,
            Building = target.Building,
            DeviceName = Field("设备名称"),
            Model = Field("设备型号"),
            SoftwareVersion = Field("软件版本"),
            SerialNumber = Field("序列号"),
            Uptime = Field("运行时间"),
            Cpu = cpu,
            Memory = memory,
            Temperature = temperature,
            Fan = fan,
            Power = power,
            Ports = ports,
            Status = anomalies.Count == 0 ? InspectionStatus.Normal : InspectionStatus.Anomaly,
            Anomaly = string.Join("；", anomalies),
            StatusText = result.StatusText,
            ElapsedMs = elapsedMs,
        };
    }

    /// <summary>温度汇总：返回"最高读数"，并把超阈值的传感器写进异常清单。</summary>
    private static string SummarizeTemperature(SnmpQueryResult result, List<string> anomalies)
    {
        var section = result.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Temperature);
        if (section is null || section.Rows.Count == 0)
        {
            // 该型号没有私有温度表是**设备事实**，不是异常（目标交换机 实测就是没有）。
            return "N/A（该型号未提供）";
        }

        var readings = new List<int>();
        foreach (var row in section.Rows)
        {
            // 列定义：传感器 / 当前温度 / 警告阈值 / 告警阈值
            var current = ParseInt(row.ElementAtOrDefault(1));
            var warn = ParseInt(row.ElementAtOrDefault(2));
            var critical = ParseInt(row.ElementAtOrDefault(3));
            var sensor = row.ElementAtOrDefault(0) ?? "?";
            if (current is null)
            {
                continue;
            }

            readings.Add(current.Value);
            if (critical is { } criticalValue && current >= criticalValue)
            {
                anomalies.Add($"温度告警：{sensor} {current}°C ≥ 告警阈值 {criticalValue}°C");
            }
            else if (warn is { } warnValue && current >= warnValue)
            {
                anomalies.Add($"温度偏高：{sensor} {current}°C ≥ 警告阈值 {warnValue}°C");
            }
        }

        return readings.Count == 0 ? "N/A" : $"{readings.Max()} °C（{readings.Count} 个传感器）";
    }

    /// <summary>
    /// 风扇/电源：只报**原始枚举值**，不判好坏 —— 这些私有表的枚举含义至今未确认
    /// （MIB 无说明、真机 CLI 未印证），拿它当"正常/故障"下结论就是编数据。
    /// </summary>
    private static (string Fan, string Power) SummarizeFanPower(SnmpQueryResult result)
    {
        static string Describe(SnmpSection? section, string nameColumn)
        {
            if (section is null || section.Rows.Count == 0)
            {
                return "N/A（该型号未提供）";
            }

            var parts = section.Rows
                .Select(r => $"{r.ElementAtOrDefault(0)}={r.ElementAtOrDefault(1)}")
                .Take(4);
            return $"{section.Rows.Count} 项：{string.Join("，", parts)}";
        }

        return (
            Describe(result.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Fan), "风扇"),
            Describe(result.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Power), "电源"));
    }

    /// <summary>从 "0% / 25% / 12%" 里取最大值；解析不出返回 null。</summary>
    private static int? MaxPercent(string text)
    {
        var values = PercentValues(text).ToList();
        return values.Count == 0 ? null : values.Max();
    }

    /// <summary>从 "55%" 里取第一个百分比；解析不出返回 null。</summary>
    private static int? FirstPercent(string text) => PercentValues(text).Cast<int?>().FirstOrDefault();

    private static IEnumerable<int> PercentValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var raw in text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = raw.TrimEnd('%').Trim();
            if (int.TryParse(cleaned, out var value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// 从 "警告 90 / 严重 100" 里取阈值：优先"警告"，没有"警告"字样时退到"严重"。
    /// 两个都取不到返回 null（不要瞎猜一个数当设备阈值）。
    /// </summary>
    private static int? ParseCpuThreshold(string text)
    {
        foreach (var keyword in new[] { "警告", "严重" })
        {
            var at = text.IndexOf(keyword, StringComparison.Ordinal);
            if (at >= 0 && ParseInt(text[at..]) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.SkipWhile(c => !char.IsDigit(c) && c != '-').TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }
}
