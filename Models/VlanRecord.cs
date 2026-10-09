namespace RuijieNetworkAssistant.Models;

/// <summary>
/// VLAN / IP 资源记录。由用户导入的本地工作簿解析而来；工作簿内容不随项目分发。
/// 这是规划数据，不是交换机实时状态。
/// </summary>
public sealed class VlanRecord
{
    public string Id { get; set; } = string.Empty;

    public int VlanId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string Mask { get; set; } = string.Empty;

    public string IpRange { get; set; } = string.Empty;

    public string Building { get; set; } = string.Empty;

    public string Floor { get; set; } = string.Empty;

    public string Room { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public string College { get; set; } = string.Empty;

    public string PortInfo { get; set; } = string.Empty;

    public string ComputerCount { get; set; } = string.Empty;

    public string IpCount { get; set; } = string.Empty;

    public string Dns { get; set; } = string.Empty;

    public string Owner { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public string SourceSheet { get; set; } = string.Empty;

    public int SourceRow { get; set; }

    public string LocationText => string.Join(" ", new[] { Building, Floor, Room, LabName }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>表格显示用：原表未标注 VLAN（如“自动获取”）时显示占位文本。</summary>
    public string VlanIdText => VlanId > 0 ? VlanId.ToString() : "(未标注)";

    /// <summary>用于界面编辑：返回副本，避免未保存的修改直接影响资源库内容。</summary>
    public VlanRecord Clone() => new()
    {
        Id = Id,
        VlanId = VlanId,
        Name = Name,
        Gateway = Gateway,
        Mask = Mask,
        IpRange = IpRange,
        Building = Building,
        Floor = Floor,
        Room = Room,
        LabName = LabName,
        College = College,
        PortInfo = PortInfo,
        ComputerCount = ComputerCount,
        IpCount = IpCount,
        Dns = Dns,
        Owner = Owner,
        Phone = Phone,
        Note = Note,
        SourceSheet = SourceSheet,
        SourceRow = SourceRow,
    };
}
