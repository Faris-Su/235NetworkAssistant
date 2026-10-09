namespace RuijieNetworkAssistant.Models;

/// <summary>
/// Show 命令的解析结果。设计原则：
/// 1. 能可靠解析的内容 → 结构化行（Items）；
/// 2. 不能解析的行 → UnparsedLines（不丢弃）；
/// 3. 永远保留原始输出 RawOutput，解析失败时界面只显示原始文本。
/// </summary>
public sealed class ShowParseResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    public IReadOnlyList<string> UnparsedLines { get; init; } = Array.Empty<string>();

    public string RawOutput { get; init; } = string.Empty;

    /// <summary>解析提示（例如“输出格式与预期不符，仅显示原始输出”）。</summary>
    public string? ParserNote { get; init; }

    public bool HasStructuredData => Items.Count > 0;

    public string SummaryText => HasStructuredData
        ? $"解析 {Items.Count} 行" + (UnparsedLines.Count > 0 ? $"，{UnparsedLines.Count} 行未识别（见原始输出）" : string.Empty)
        : "未解析出结构化数据（请查看原始输出）";
}
