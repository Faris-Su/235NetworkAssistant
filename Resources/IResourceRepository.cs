using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Resources;

/// <summary>
/// 本地资源库。完全离线工作，运行期不读 Excel。
/// V0.1 采用 JSON + 内存索引（数据量为数百条，索引有意义但 SQLite 的依赖成本更高）；
/// 该接口保证后续可无缝替换为 SQLite 实现，见 docs/architecture.md。
/// </summary>
public interface IResourceRepository
{
    ResourceDatabase Database { get; }

    bool IsLoaded { get; }

    /// <summary>资源库内容发生变化（导入/编辑/删除/清空）。调用发生在 UI 线程。</summary>
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    Task ApplyImportAsync(ImportResult result, CancellationToken cancellationToken = default);

    /// <summary>保存对单条交换机记录的修改（按 Id 覆盖）。</summary>
    Task<bool> UpdateSwitchAsync(SwitchRecord record, CancellationToken cancellationToken = default);

    /// <summary>删除一条交换机记录。</summary>
    Task<bool> RemoveSwitchAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>保存对单条 VLAN 记录的修改（按 Id 覆盖）。</summary>
    Task<bool> UpdateVlanAsync(VlanRecord record, CancellationToken cancellationToken = default);

    /// <summary>删除一条 VLAN 记录。</summary>
    Task<bool> RemoveVlanAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>清空资源库（导入的学校资源数据；不影响设置与备份）。</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>导出资源库为 CSV（UTF-8 BOM，Excel 可直接打开），返回生成的文件列表。</summary>
    Task<IReadOnlyList<string>> ExportCsvAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// 导出**指定的一批记录**为 CSV（界面按楼栋/查询筛过之后导出当前结果用）。
    /// 不传这一批就等于导出全库 —— 两者都不能把"屏幕上看到的是子集、导出的却是全库"混在一起。
    /// </summary>
    Task<IReadOnlyList<string>> ExportCsvAsync(
        string directory,
        IReadOnlyList<SwitchRecord> switches,
        IReadOnlyList<VlanRecord> vlans,
        IReadOnlyList<LocationRecord> locations,
        CancellationToken cancellationToken = default);

    IReadOnlyList<SwitchRecord> SearchSwitches(string? query);

    IReadOnlyList<VlanRecord> SearchVlans(string? query);

    IReadOnlyList<LocationRecord> SearchLocations(string? query);

    /// <summary>资源库中出现的楼栋/位置列表（用于筛选）。</summary>
    IReadOnlyList<string> GetBuildings();

    /// <summary>按 IP 反查 VLAN（本地规划数据，不代表设备实时状态）。</summary>
    VlanIpMatch? FindVlanByIp(string ip);
}
