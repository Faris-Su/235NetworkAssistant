namespace RuijieNetworkAssistant.Models;

// 目标设备的定义已挪到 Models/DeviceTarget.cs（批量备份也在用它，不能跟着巡检一起被排除出发布件）。

public enum InspectionStatus
{
    /// <summary>取到了数据、且没有命中任何异常判据。</summary>
    Normal,

    /// <summary>取到了数据，但 CPU / 内存 / 温度超阈值等需要有人看一眼。</summary>
    Anomaly,

    /// <summary>根本没取到（不可达 / Community 错 / 超时）。</summary>
    Failed,
}

/// <summary>
/// 一台设备的巡检结果。
///
/// ⚠️ 关于"端口 down"（2026-09-22 真机实测后定下的口径）：
/// 接入交换机上**没插线的空闲口本身就是 down**，一台 24 口交换机只插了 3 个设备就会报 21 个 down。
/// 所以**端口 down 数只作参考列，绝不参与异常判定** —— 否则每台设备都是"异常"，巡检就没意义了。
/// 真正的异常判据只用三样：① 取不到数据（不可达）；② CPU / 内存超阈值；
/// ③ 温度超过**设备自己上报的**警告/告警阈值。
/// </summary>
public sealed class InspectionRow
{
    public string Ip { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Building { get; init; } = string.Empty;

    public string DeviceName { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SoftwareVersion { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string Uptime { get; init; } = string.Empty;

    public string Cpu { get; init; } = "N/A";
    public string Memory { get; init; } = "N/A";
    public string Temperature { get; init; } = "N/A";
    public string Fan { get; init; } = "N/A";
    public string Power { get; init; } = "N/A";
    public string Ports { get; init; } = "N/A";

    public InspectionStatus Status { get; init; } = InspectionStatus.Failed;

    /// <summary>命中的异常说明（多条用「；」连起来）；正常时为空。</summary>
    public string Anomaly { get; init; } = string.Empty;

    /// <summary>结论原文（查询成功/部分成功/失败的原因），排查用。</summary>
    public string StatusText { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }
    public DateTimeOffset QueriedAt { get; init; } = DateTimeOffset.Now;

    public string StatusLabel => Status switch
    {
        InspectionStatus.Normal => "正常",
        InspectionStatus.Anomaly => "需关注",
        _ => "取不到",
    };

    /// <summary>CSV 表头（导出用；列顺序与 <see cref="ToCsvLine"/> 必须一致）。</summary>
    public const string CsvHeader =
        "IP,名称,楼栋,结论,异常说明,设备名,型号,软件版本,序列号,运行时间,CPU,内存,温度,风扇,电源,端口,耗时(ms),查询时间";

    public string ToCsvLine() => string.Join(
        ',',
        Csv(Ip), Csv(Name), Csv(Building), Csv(StatusLabel), Csv(Anomaly), Csv(DeviceName),
        Csv(Model), Csv(SoftwareVersion), Csv(SerialNumber), Csv(Uptime), Csv(Cpu), Csv(Memory),
        Csv(Temperature), Csv(Fan), Csv(Power), Csv(Ports), ElapsedMs.ToString(),
        Csv(QueriedAt.ToString("yyyy-MM-dd HH:mm:ss")));

    /// <summary>
    /// CSV 字段转义：含逗号/引号/换行时用双引号包起来并把内部引号翻倍（Excel 才认）。
    /// 设备名、型号、异常说明里出现逗号是常态，不转义整张表就错列了。
    /// </summary>
    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return text;
        }

        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
