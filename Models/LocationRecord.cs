namespace RuijieNetworkAssistant.Models;

/// <summary>场所资源（楼栋/楼层/房间/实验室），由资源表派生，用于按位置查询。</summary>
public sealed class LocationRecord
{
    public string Id { get; set; } = string.Empty;

    public string Building { get; set; } = string.Empty;

    public string Floor { get; set; } = string.Empty;

    public string Room { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public string SourceSheet { get; set; } = string.Empty;

    public string Display => string.Join(" ", new[] { Building, Floor, Room, LabName }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public LocationRecord Clone() => new()
    {
        Id = Id,
        Building = Building,
        Floor = Floor,
        Room = Room,
        LabName = LabName,
        Note = Note,
        SourceSheet = SourceSheet,
    };
}
