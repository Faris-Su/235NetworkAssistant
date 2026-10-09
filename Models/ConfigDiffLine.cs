namespace RuijieNetworkAssistant.Models;

public enum ConfigDiffKind
{
    /// <summary>两边都有、内容相同。</summary>
    Same,

    /// <summary>只在"新"里有（新增行）。</summary>
    Added,

    /// <summary>只在"旧"里有（删除行）。</summary>
    Removed,

    /// <summary>被当成"每次都会变"的行而忽略（运行时间、时间戳、字节数…）。</summary>
    Ignored,
}

/// <summary>配置对比里的一行。</summary>
public sealed class ConfigDiffLine
{
    public ConfigDiffKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>在"旧"里的行号（1 基；没有则为 null）。</summary>
    public int? OldLineNumber { get; init; }

    /// <summary>在"新"里的行号（1 基；没有则为 null）。</summary>
    public int? NewLineNumber { get; init; }

    public string KindLabel => Kind switch
    {
        ConfigDiffKind.Added => "新增",
        ConfigDiffKind.Removed => "删除",
        ConfigDiffKind.Ignored => "忽略",
        _ => "相同",
    };

    /// <summary>导出用的前缀：`+` 新增、`-` 删除，和常见 diff 一致。</summary>
    public string ToDiffText() => Kind switch
    {
        ConfigDiffKind.Added => "+ " + Text,
        ConfigDiffKind.Removed => "- " + Text,
        _ => "  " + Text,
    };
}

/// <summary>
/// 配置对比的一侧：一个本地备份文件，或者"当前连接设备的 running-config"。
/// </summary>
public sealed record ConfigDiffSource(string Label, string? FilePath, bool IsCurrentDevice)
{
    public override string ToString() => Label;
}

/// <summary>一次配置对比的结果。</summary>
public sealed class ConfigDiffResult
{
    public IReadOnlyList<ConfigDiffLine> Lines { get; init; } = Array.Empty<ConfigDiffLine>();

    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }

    /// <summary>
    /// 被忽略的"每次都会变"的行数，**两侧合计**（左边一条、右边一条就算 2）。
    /// 口径要写明白：只报一个数而不说清是单侧还是合计，用户会以为文件里真有这么多行变了。
    /// </summary>
    public int IgnoredCount { get; init; }

    /// <summary>被忽略的行（运行时间/时间戳这类必然变化的）——要让用户知道它们被跳过了，而不是真没变。</summary>
    public IReadOnlyList<string> IgnoredSamples { get; init; } = Array.Empty<string>();

    /// <summary>行数太大、退化成了"集合比对"（不保证行序对齐）时为 true，界面上要说明。</summary>
    public bool Degraded { get; init; }

    public bool HasDifferences => AddedCount > 0 || RemovedCount > 0;

    public string Summary => HasDifferences
        ? $"新增 {AddedCount} 行｜删除 {RemovedCount} 行｜忽略 {IgnoredCount} 行（两侧合计；"
          + "每次都会变的：运行时间/时间戳/字节数等。空行不参与比较）"
          + (Degraded ? "｜⚠ 行数太多，已退化为集合比对（不保证行序对齐）" : string.Empty)
        : $"没有差异（另有 {IgnoredCount} 行是每次都变的内容、两侧合计，已忽略；空行不参与比较）";
}
