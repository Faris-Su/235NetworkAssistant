namespace RuijieNetworkAssistant.Resources;

/// <summary>
/// 资源库"楼栋筛选"的统一规则。
///
/// 交换机 / 场所 / VLAN-IP 三张表**必须用同一套判断**：2026-09-21 现场反馈"点楼栋筛选时
/// VLAN/IP 资源没有反应"，就是因为那张表漏了这一步（交换机、场所都筛了，VLAN 没筛）。
///
/// 单独抽成纯函数还有个好处：`ResourceLibraryViewModel` 依赖 WPF（MessageBox），
/// 自检工程编译不了它；把规则放在这里，自检就能直接锁死这条行为。
/// </summary>
public static class ResourceBuildingFilter
{
    /// <summary>"不筛选"的哨兵选项（下拉框第一项）。</summary>
    public const string AllBuildings = "全部";

    /// <summary>
    /// 楼栋名规范化：源表中同一位置可能有不同写法，统一去首尾空白并去掉末尾的 `Vlan`
    /// 尾缀（大小写不敏感）。不以该尾缀结尾的汇总表名保持原样。
    ///
    /// <para>
    /// 分类规则：
    /// <list type="bullet">
    ///   <item>移除由工作表名称拼接产生的 `Vlan` 尾缀。</item>
    ///   <item>不同位置名称保持分开，避免基于名称相似度擅自合并。</item>
    ///   <item>未分类汇总表保留独立筛选项，不并入具体位置。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static string Normalize(string? building)
    {
        var text = (building ?? string.Empty).Trim();
        if (text.Length > 4 && text.EndsWith("vlan", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4].TrimEnd(' ', '-', '_', '(', ')', '（', '）', '·');
        }

        return text.Trim();
    }

    /// <summary>是否处于"某个楼栋"的筛选状态（空值或"全部"= 不筛）。</summary>
    public static bool IsActive(string? selectedBuilding) =>
        !string.IsNullOrWhiteSpace(selectedBuilding) &&
        !string.Equals(selectedBuilding.Trim(), AllBuildings, StringComparison.Ordinal);

    /// <summary>
    /// 该记录的楼栋是否命中当前筛选；没有筛选时一律命中。
    /// 两侧都先 Trim、再按 OrdinalIgnoreCase 比，避免空格或大小写差异造成漏筛。
    /// </summary>
    public static bool Matches(string? building, string? selectedBuilding) =>
        !IsActive(selectedBuilding) ||
        string.Equals(
            Normalize(building),
            Normalize(selectedBuilding),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>按楼栋筛选一组记录（不筛选时原样返回）。</summary>
    public static IEnumerable<T> Apply<T>(
        IEnumerable<T> source,
        string? selectedBuilding,
        Func<T, string?> buildingOf) =>
        IsActive(selectedBuilding)
            ? source.Where(item => Matches(buildingOf(item), selectedBuilding))
            : source;
}
