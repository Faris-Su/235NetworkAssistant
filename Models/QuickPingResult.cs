namespace RuijieNetworkAssistant.Models;

public enum QuickPingStatus
{
    /// <summary>还没测（图形模式里 0..255 的格子初始都是这个状态）。</summary>
    Pending,

    /// <summary>通了。</summary>
    Ok,

    /// <summary>超时（大多数交换机会忽略 ICMP，超时**不等于**设备不在）。</summary>
    Timeout,

    /// <summary>目标是本机/网关以外的错误（DNS 解析失败、无路由等）。</summary>
    Error,
}

/// <summary>QuickPing 里一行结果。</summary>
public sealed class QuickPingResult
{
    public string Target { get; init; } = string.Empty;

    public QuickPingStatus Status { get; init; }

    /// <summary>往返延迟（ms）；没通为 null。</summary>
    public long? RttMs { get; init; }

    /// <summary>回复来自哪个地址（跨网段/DNS 场景下可能和输入不同）。</summary>
    public string ReplyAddress { get; init; } = string.Empty;

    /// <summary>对方的 MAC（从本机 ARP 表读的）。取不到就是空 —— 不编。</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>对方的主机名（反向 DNS 查的）。校园网里大多数地址查不到，空着是常态。</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>这一行的地址最后一段（图形模式按下标铺格子用）。</summary>
    public int LastOctet { get; init; }

    /// <summary>失败原因或补充说明（给用户看的原话）。</summary>
    public string Detail { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }

    public string StatusText => Status switch
    {
        QuickPingStatus.Pending => "未测",
        QuickPingStatus.Ok => "通",
        QuickPingStatus.Timeout => "超时",
        _ => "错误",
    };

    public string RttText => RttMs is { } value ? $"{value} ms" : "—";

    public const string CsvHeader = "IP地址,结果,延迟(ms),网卡地址,主机名,回复地址,说明,耗时(ms)";

    public string ToCsvLine() => string.Join(
        ',',
        Csv(Target), Csv(StatusText), RttMs?.ToString() ?? string.Empty, Csv(MacAddress), Csv(HostName),
        Csv(ReplyAddress), Csv(Detail), ElapsedMs.ToString());

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? text
            : '"' + text.Replace("\"", "\"\"") + '"';
    }
}
