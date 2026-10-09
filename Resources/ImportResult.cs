using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Resources;

/// <summary>导入解析结果：预览信息 + 解析出的记录。写入资源库必须经过用户确认。</summary>
public sealed class ImportResult
{
    public ImportPreview Preview { get; init; } = new();

    public List<SwitchRecord> Switches { get; } = new();

    public List<VlanRecord> Vlans { get; } = new();

    public List<LocationRecord> Locations { get; } = new();

    public string SummaryText =>
        $"交换机 {Switches.Count} 条 / VLAN {Vlans.Count} 条 / 场所 {Locations.Count} 条";
}
