using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RuijieNetworkAssistant.Resources;

/// <summary>
/// 最小 OOXML(.xlsx) 读取器：只用 System.IO.Compression + System.Xml，不引入 NPOI / ClosedXML /
/// OpenXml SDK 等大依赖，符合离线部署与体积约束。
/// 支持：共享字符串、内联字符串、公式缓存值、合并单元格；不读取图片（学校表的图片列会被忽略）。
/// 注意：只读打开，任何情况下都不修改原始 Excel。
/// </summary>
public static class XlsxWorkbookReader
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static XlsxWorkbook Read(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到 Excel 文件。", filePath);
        }

        var workbook = new XlsxWorkbook { FilePath = filePath };

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var sharedStrings = ReadSharedStrings(archive, cancellationToken);
        var relationships = ReadWorkbookRelationships(archive);

        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("不是有效的 xlsx 文件：缺少 xl/workbook.xml。");

        var settings = CreateSettings();
        using var workbookStream = workbookEntry.Open();
        using var reader = XmlReader.Create(workbookStream, settings);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
            {
                continue;
            }

            var name = reader.GetAttribute("name") ?? string.Empty;
            var relationshipId = reader.GetAttribute("id", RelationshipNs);
            if (string.IsNullOrEmpty(relationshipId) || !relationships.TryGetValue(relationshipId, out var target))
            {
                continue;
            }

            var sheet = new XlsxSheet { Name = name };
            var entry = FindEntry(archive, target);
            if (entry is null)
            {
                // 不静默吞掉（旧行为是留一张 0 行空表 → 被当成"说明/图片页已跳过"，
                // 交换机/VLAN 资源悄悄丢失，见 XlsxSheet.LoadError 的注释）。
                sheet.LoadError =
                    $"工作簿里找不到这张表的数据部件（关系目标“{target}”，已试过 {string.Join("、", CandidateEntryPaths(target))}）";
            }
            else
            {
                ReadSheet(entry, sheet, sharedStrings, settings, cancellationToken);
            }

            workbook.Sheets.Add(sheet);
        }

        return workbook;
    }

    private static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreWhitespace = false,
        CheckCharacters = false,
    };

    private static List<string> ReadSharedStrings(ZipArchive archive, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return result;
        }

        var settings = CreateSettings();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        var builder = new StringBuilder();
        var insideItem = false;
        var insideText = false;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "si")
                    {
                        builder.Clear();
                        insideItem = true;
                    }
                    else if (insideItem && reader.LocalName == "t")
                    {
                        insideText = true;
                    }

                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    if (insideText)
                    {
                        builder.Append(reader.Value);
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == "t")
                    {
                        insideText = false;
                    }
                    else if (reader.LocalName == "si")
                    {
                        result.Add(builder.ToString());
                        insideItem = false;
                    }

                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null)
        {
            return result;
        }

        var settings = CreateSettings();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
            {
                result[id] = target;
            }
        }

        return result;
    }

    private static void ReadSheet(
        ZipArchiveEntry entry,
        XlsxSheet sheet,
        List<string> sharedStrings,
        XmlReaderSettings settings,
        CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "row":
                    var rowIndex = ParseRowIndex(reader.GetAttribute("r"), sheet.Rows.Count);
                    var cells = ParseRow(reader, sharedStrings);
                    while (sheet.Rows.Count < rowIndex)
                    {
                        sheet.Rows.Add(Array.Empty<string?>());
                    }

                    if (rowIndex < sheet.Rows.Count)
                    {
                        sheet.Rows[rowIndex] = cells;
                    }
                    else
                    {
                        sheet.Rows.Add(cells);
                    }

                    break;
                case "mergeCell":
                    var reference = reader.GetAttribute("ref");
                    if (!string.IsNullOrWhiteSpace(reference))
                    {
                        sheet.MergedRanges.Add(reference);
                    }

                    break;
            }
        }
    }

    private static string?[] ParseRow(XmlReader rowReader, List<string> sharedStrings)
    {
        using var sub = rowReader.ReadSubtree();
        var values = new Dictionary<int, string?>();
        var maxColumn = -1;
        var currentColumn = -1;
        var cellType = string.Empty;
        var builder = new StringBuilder();
        var insideValue = false;
        var hasValue = false;

        while (sub.Read())
        {
            switch (sub.NodeType)
            {
                case XmlNodeType.Element:
                    switch (sub.LocalName)
                    {
                        case "c":
                            currentColumn = XlsxSheet.MergedRange.ColumnIndexFromLetters(ColumnLetters(sub.GetAttribute("r")));
                            cellType = sub.GetAttribute("t") ?? string.Empty;
                            builder.Clear();
                            insideValue = false;
                            hasValue = false;
                            break;
                        case "v":
                            insideValue = true;
                            break;
                        case "t":
                            if (string.Equals(cellType, "inlineStr", StringComparison.Ordinal))
                            {
                                insideValue = true;
                            }

                            break;
                    }

                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    if (insideValue)
                    {
                        builder.Append(sub.Value);
                        hasValue = true;
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (sub.LocalName is "v" or "t")
                    {
                        insideValue = false;
                    }
                    else if (sub.LocalName == "c" && currentColumn >= 0)
                    {
                        values[currentColumn] = ResolveValue(builder.ToString(), cellType, hasValue, sharedStrings);
                        if (currentColumn > maxColumn)
                        {
                            maxColumn = currentColumn;
                        }
                    }

                    break;
            }
        }

        if (maxColumn < 0)
        {
            return Array.Empty<string?>();
        }

        var row = new string?[maxColumn + 1];
        foreach (var pair in values)
        {
            row[pair.Key] = pair.Value;
        }

        return row;
    }

    private static string? ResolveValue(string raw, string cellType, bool hasValue, List<string> sharedStrings)
    {
        if (!hasValue)
        {
            return null;
        }

        switch (cellType)
        {
            case "s":
                return int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count
                    ? sharedStrings[index]
                    : raw;
            case "str":
            case "inlineStr":
            case "e":
            case "d":
                return raw;
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            default:
                return raw;
        }
    }

    private static int ParseRowIndex(string? text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value - 1 : fallback;

    private static string ColumnLetters(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return string.Empty;
        }

        var index = 0;
        while (index < cellReference.Length && char.IsLetter(cellReference[index]))
        {
            index++;
        }

        return cellReference[..index];
    }

    private static string NormalizeEntryPath(string target)
    {
        // 保留老口径（已经被别处引用），真正的多候选查找在 CandidateEntryPaths 里。
        foreach (var candidate in CandidateEntryPaths(target))
        {
            return candidate;
        }

        return target;
    }

    /// <summary>
    /// 在归档里找关系目标对应的部件：先按若干候选路径精确找，再退回一次**忽略大小写**的扫描。
    ///
    /// 为什么要多候选：不同生成器写 Target 的风格差别很大（`worksheets/sheet1.xml`、
    /// `/xl/worksheets/sheet1.xml`、`xl/worksheets/sheet1.xml`，甚至有带 `..` 的），
    /// 而 `ZipArchive.GetEntry` **既不解析 `..`、也区分大小写、还不认前导 `/`**。
    /// 旧实现只按一种口径拼路径，一旦对不上就返回 null → 整张表读成 0 行。
    /// </summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string target)
    {
        foreach (var candidate in CandidateEntryPaths(target))
        {
            var entry = archive.GetEntry(candidate);
            if (entry is not null)
            {
                return entry;
            }

            // 大小写不一致（`xl/Worksheets/Sheet1.xml`）时 GetEntry 找不到，做一次忽略大小写的扫描。
            var loose = archive.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, candidate, StringComparison.OrdinalIgnoreCase));
            if (loose is not null)
            {
                return loose;
            }
        }

        return null;
    }

    /// <summary>
    /// 由关系目标算出若干可能的包内路径（按可能性排序）。
    /// OPC 规范里 Target 是相对"持有该关系的部件"（这里是 `xl/workbook.xml`）解析的。
    /// </summary>
    internal static IEnumerable<string> CandidateEntryPaths(string target)
    {
        var normalized = (target ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        var absolute = normalized.StartsWith('/');
        var body = absolute ? normalized.TrimStart('/') : normalized;

        // 候选 1（保持历史行为）：已经是 xl/ 开头就用原样，否则补 xl/ 前缀。
        yield return ResolveSegments(
            body.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? body : "xl/" + body);

        // 候选 2（OPC 规范口径）：一律当作相对 xl/ 解析。
        if (!absolute && body.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
        {
            yield return ResolveSegments("xl/" + body);
        }
    }

    /// <summary>消掉路径里的 "." 与 ".."（`xl/../xl/worksheets/sheet1.xml` → `xl/worksheets/sheet1.xml`）。</summary>
    private static string ResolveSegments(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}
