namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 交换机地址簿记录，由用户导入的本地设备清单解析而来。
/// 字段名对应原表实际表头，不用猜测。
/// </summary>
public sealed class SwitchRecord
{
    /// <summary>稳定主键：管理 IP；没有 IP 时退化为“表名+行号”。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>原表位置类列，或工作表名称（如“区域A”）。</summary>
    public string Building { get; set; } = string.Empty;

    /// <summary>原表“交换机所在楼层”。</summary>
    public string Floor { get; set; } = string.Empty;

    /// <summary>原表“名称”（部分 Sheet 有）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>原表“交换机IP”。可能包含 VLAN 等附加文字，原始文本保留在 Remark。</summary>
    public string ManagementIp { get; set; } = string.Empty;

    /// <summary>原表“交换机物理地址”。</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>原表“交换机序列号”。</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>原表“交换机型号”。</summary>
    public string DeviceModel { get; set; } = string.Empty;

    /// <summary>原表“其它设备型号”等信息。</summary>
    public string Remark { get; set; } = string.Empty;

    public string SourceSheet { get; set; } = string.Empty;

    public int SourceRow { get; set; }

    public string LocationText =>
        string.IsNullOrWhiteSpace(Floor) ? Building : $"{Building} {Floor}".Trim();

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name)
            ? Name
            : !string.IsNullOrWhiteSpace(ManagementIp) ? ManagementIp : $"({SourceSheet} 第{SourceRow}行)";

    /// <summary>用于界面编辑：返回副本，避免未保存的修改直接影响资源库内容。</summary>
    public SwitchRecord Clone() => new()
    {
        Id = Id,
        Building = Building,
        Floor = Floor,
        Name = Name,
        ManagementIp = ManagementIp,
        MacAddress = MacAddress,
        SerialNumber = SerialNumber,
        DeviceModel = DeviceModel,
        Remark = Remark,
        SourceSheet = SourceSheet,
        SourceRow = SourceRow,
    };
}
