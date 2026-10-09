namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 一个接口在某一时刻的计数器快照（都是**累计值**，单看它没有意义，必须两次求差）。
/// 取不到的那几项保持 null —— 和"设备真的返回 0"要分开。
/// </summary>
public sealed class InterfaceCounterSample
{
    public string IfIndex { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;

    /// <summary>接收字节（ifHCInOctets，64 位）；老设备没有 HC 计数时为 null。</summary>
    public long? InOctets { get; init; }

    /// <summary>发送字节（ifHCOutOctets）。</summary>
    public long? OutOctets { get; init; }

    public long? InErrors { get; init; }
    public long? OutErrors { get; init; }

    /// <summary>接口速率（bps），由 ifHighSpeed(Mbps) 或 ifSpeed(bps) 得来；取不到为 null。</summary>
    public long? SpeedBps { get; init; }
}

/// <summary>两次采样求差之后的流量结论（按利用率排序）。</summary>
public sealed class InterfaceTrafficResult
{
    public string Port { get; init; } = string.Empty;

    /// <summary>接收速率（bps）。</summary>
    public double? InBps { get; init; }

    /// <summary>发送速率（bps）。</summary>
    public double? OutBps { get; init; }

    /// <summary>
    /// 利用率（%）：取接收/发送里**较大**的那个 ÷ 接口速率。
    /// 全双工下收发各占一条方向，用较大值更接近"这个口有多忙"的直觉。
    /// </summary>
    public double? UtilizationPercent { get; init; }

    /// <summary>采样间隔内新增的错包数（两个方向合计）；取不到为 null。</summary>
    public long? ErrorDelta { get; init; }

    /// <summary>两次采样之间计数器**倒退**了（设备重启 / 计数器回绕）—— 这时速率无意义。</summary>
    public bool CounterReset { get; init; }

    public string InText => FormatBps(InBps);
    public string OutText => FormatBps(OutBps);
    public string UtilizationText => UtilizationPercent is { } value ? $"{value:F1}%" : "N/A";
    public string ErrorText => ErrorDelta is { } value ? (value > 0 ? $"+{value}" : "0") : "N/A";

    /// <summary>bps → 人能看懂的 Mbps/Gbps。</summary>
    public static string FormatBps(double? bps) => bps is not { } value
        ? "N/A"
        : value >= 1_000_000_000 ? $"{value / 1_000_000_000:F2} Gbps"
        : value >= 1_000_000 ? $"{value / 1_000_000:F1} Mbps"
        : value >= 1_000 ? $"{value / 1_000:F0} Kbps"
        : $"{value:F0} bps";
}
