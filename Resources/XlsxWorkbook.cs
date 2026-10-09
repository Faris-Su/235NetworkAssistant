namespace RuijieNetworkAssistant.Resources;

/// <summary>一个工作表的内容（按行保存为字符串，保留原始文本）。</summary>
public sealed class XlsxSheet
{
    private List<MergedRange>? _mergedIndex;

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 读取这张工作表时出的错（正常为 null）。
    ///
    /// 为什么必须有：旧实现里"取不到 xl/worksheets/sheetN.xml"是**静默 return 空表**，
    /// 上层只能看到 0 行 → 被归类成"说明/图片页，已跳过"，用户以为这份表本来就没数据
    /// （交换机/VLAN 资源悄悄丢失，预览的"新增/更新/跳过"计数也对不上）。
    /// 现在把失败原因挂在表上，让导入预览如实报出来。
    /// </summary>
    public string? LoadError { get; set; }

    public List<string?[]> Rows { get; } = new();

    public List<string> MergedRanges { get; } = new();

    public int RowCount => Rows.Count;

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);

    /// <summary>取单元格值（0 基）。fillMerged=true 时，合并单元格内向下/向右填充左上角的值。</summary>
    public string? GetValue(int row, int column, bool fillMerged = true)
    {
        if (row < 0 || row >= Rows.Count)
        {
            return null;
        }

        var line = Rows[row];
        var value = column >= 0 && column < line.Length ? line[column] : null;
        if (!string.IsNullOrWhiteSpace(value) || !fillMerged || MergedRanges.Count == 0)
        {
            return value;
        }

        _mergedIndex ??= BuildMergedIndex();
        foreach (var range in _mergedIndex)
        {
            if (row < range.MinRow || row > range.MaxRow || column < range.MinColumn || column > range.MaxColumn)
            {
                continue;
            }

            var line2 = range.MinRow < Rows.Count ? Rows[range.MinRow] : null;
            var topLeft = line2 is not null && range.MinColumn < line2.Length ? line2[range.MinColumn] : null;
            if (!string.IsNullOrWhiteSpace(topLeft))
            {
                return topLeft;
            }
        }

        return value;
    }

    /// <summary>取整行文本（末尾空列裁掉）。</summary>
    public IReadOnlyList<string> GetRowText(int row)
    {
        if (row < 0 || row >= Rows.Count)
        {
            return Array.Empty<string>();
        }

        return Rows[row].Select(v => v ?? string.Empty).ToList();
    }

    public string GetJoinedRowText(int row) =>
        string.Join(" ", GetRowText(row).Where(v => !string.IsNullOrWhiteSpace(v)));

    private List<MergedRange> BuildMergedIndex()
    {
        var list = new List<MergedRange>();
        foreach (var text in MergedRanges)
        {
            var range = MergedRange.TryParse(text);
            if (range is not null)
            {
                list.Add(range);
            }
        }

        return list;
    }

    internal sealed record MergedRange(int MinRow, int MinColumn, int MaxRow, int MaxColumn)
    {
        /// <summary>解析 A3:A18 这样的合并区域引用（0 基输出）。</summary>
        public static MergedRange? TryParse(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            var parts = reference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return null;
            }

            if (!TryParseCell(parts[0], out var minRow, out var minColumn) ||
                !TryParseCell(parts[1], out var maxRow, out var maxColumn))
            {
                return null;
            }

            return new MergedRange(
                Math.Min(minRow, maxRow),
                Math.Min(minColumn, maxColumn),
                Math.Max(minRow, maxRow),
                Math.Max(minColumn, maxColumn));
        }

        private static bool TryParseCell(string text, out int row, out int column)
        {
            row = -1;
            column = -1;
            var letters = 0;
            while (letters < text.Length && char.IsLetter(text[letters]))
            {
                letters++;
            }

            if (letters == 0 || letters == text.Length)
            {
                return false;
            }

            column = ColumnIndexFromLetters(text[..letters]);
            return int.TryParse(text[letters..], out var rowNumber) && rowNumber > 0 && (row = rowNumber - 1) >= 0;
        }

        internal static int ColumnIndexFromLetters(string letters)
        {
            var value = 0;
            foreach (var ch in letters.ToUpperInvariant())
            {
                if (ch is < 'A' or > 'Z')
                {
                    return -1;
                }

                value = (value * 26) + (ch - 'A' + 1);
            }

            return value - 1;
        }
    }
}

public sealed class XlsxWorkbook
{
    public string FilePath { get; init; } = string.Empty;

    public List<XlsxSheet> Sheets { get; } = new();
}
