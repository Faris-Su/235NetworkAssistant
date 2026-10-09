namespace RuijieNetworkAssistant.Models;

/// <summary>LLDP 邻居详情（原始输出始终保留）。</summary>
public sealed class LldpNeighborDetail
{
    public LldpNeighborRecord? Neighbor { get; init; }

    public string RawOutput { get; init; } = string.Empty;

    public string ParserNote { get; init; } = string.Empty;

    public string ToDisplayText()
    {
        var neighbor = Neighbor;
        if (neighbor is null)
        {
            return ParserNote;
        }

        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"邻居设备：{Text(neighbor.NeighborDevice)}",
                $"本地端口：{Text(neighbor.LocalPort)}",
                $"远端端口：{Text(neighbor.NeighborPort)}",
                $"管理 IP：{Text(neighbor.ManagementIp)}",
                $"Chassis ID：{Text(neighbor.ChassisId)}",
                $"System Description：{Text(neighbor.SystemDescription)}",
                $"Port Description：{Text(neighbor.PortDescription)}",
                $"Capability：{Text(neighbor.Capability)}",
                $"Aging Time：{Text(neighbor.AgingTime)}",
                $"更新时间：{Text(neighbor.UpdateTime)}",
                $"邻居序号：{Text(neighbor.NeighborIndex)}",
                $"Detail 状态：{neighbor.DetailStateDescription}",
            });
    }

    private static string Text(string? value) => LldpNeighborRecord.Or(value);
}
