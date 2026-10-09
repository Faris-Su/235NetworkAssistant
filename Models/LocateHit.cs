namespace RuijieNetworkAssistant.Models;

/// <summary>定位结果里的一条（一台设备上的一次命中或一次"没找到"）。</summary>
public sealed class LocateHit
{
    /// <summary>这一步是从哪张表得到的：`ARP`（IP→MAC）还是 `FDB`（MAC→端口）。</summary>
    public string Kind { get; init; } = string.Empty;

    public string DeviceIp { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string Building { get; init; } = string.Empty;

    /// <summary>结论：命中 / 设备自身 / 未命中 / 该设备不支持 / 超时。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>端口名（只有 FDB 命中物理口时才有）。</summary>
    public string Port { get; init; } = string.Empty;

    public int BridgePort { get; init; }
    public int? IfIndex { get; init; }

    /// <summary>VLAN（ARP 命中时来自索引）。</summary>
    public string Vlan { get; init; } = string.Empty;

    /// <summary>补充说明：为什么没找到、为什么这个端口不能当结论用。</summary>
    public string Note { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }

    public static string CsvHeader => "步骤,设备IP,设备名,楼栋,结论,端口,桥口,ifIndex,VLAN,说明,耗时(ms)";

    public string ToCsvLine() => string.Join(
        ',',
        Csv(Kind), Csv(DeviceIp), Csv(DeviceName), Csv(Building), Csv(Status), Csv(Port),
        BridgePort.ToString(), IfIndex?.ToString() ?? string.Empty, Csv(Vlan), Csv(Note), ElapsedMs.ToString());

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? text
            : '"' + text.Replace("\"", "\"\"") + '"';
    }
}
