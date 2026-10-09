namespace RuijieNetworkAssistant.Models;

public enum ImportItemCategory
{
    New,
    Update,
    Skip,
    Error,
}

public static class ImportItemCategoryText
{
    public static string ToChinese(this ImportItemCategory category) => category switch
    {
        ImportItemCategory.New => "新增",
        ImportItemCategory.Update => "更新",
        ImportItemCategory.Skip => "跳过",
        ImportItemCategory.Error => "异常",
        _ => category.ToString(),
    };
}

public enum ImportTargetKind
{
    Switch,
    Vlan,
    Location,
}

public static class ImportTargetKindText
{
    public static string ToChinese(this ImportTargetKind kind) => kind switch
    {
        ImportTargetKind.Switch => "交换机",
        ImportTargetKind.Vlan => "VLAN",
        ImportTargetKind.Location => "场所",
        _ => kind.ToString(),
    };
}

/// <summary>导入预览的每一行。导入前用户必须能看到新增/更新/跳过/异常。</summary>
public sealed class ImportPreviewItem
{
    public ImportItemCategory Category { get; init; }

    public ImportTargetKind Target { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Display { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string SourceSheet { get; init; } = string.Empty;

    public int SourceRow { get; init; }
}

/// <summary>导入预览。预览阶段绝不修改原始 Excel，也不写入资源库。</summary>
public sealed class ImportPreview
{
    public string SourceFile { get; init; } = string.Empty;

    public List<string> Sheets { get; set; } = new();

    public List<ImportPreviewItem> Items { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// **真实**分类计数（不受 <see cref="Items"/> 明细上限影响）。导入器逐条累加；
    /// 手工构造预览（测试）时保持 -1，计数退回"数明细"。
    /// </summary>
    public int TotalNew { get; set; } = -1;
    public int TotalUpdate { get; set; } = -1;
    public int TotalSkip { get; set; } = -1;
    public int TotalError { get; set; } = -1;

    /// <summary>明细是否被上限截断（真值 = 屏幕上的表格只是前 N 条）。</summary>
    public bool ItemsTruncated { get; set; }

    public int NewCount => TotalNew >= 0 ? TotalNew : Items.Count(i => i.Category == ImportItemCategory.New);

    public int UpdateCount => TotalUpdate >= 0 ? TotalUpdate : Items.Count(i => i.Category == ImportItemCategory.Update);

    public int SkipCount => TotalSkip >= 0 ? TotalSkip : Items.Count(i => i.Category == ImportItemCategory.Skip);

    public int ErrorCount => TotalError >= 0 ? TotalError : Items.Count(i => i.Category == ImportItemCategory.Error);

    public string SummaryText =>
        $"新增 {NewCount} / 更新 {UpdateCount} / 跳过 {SkipCount} / 异常 {ErrorCount}" +
        (ItemsTruncated ? $"（明细只保留前 {Items.Count} 条）" : string.Empty);
}
