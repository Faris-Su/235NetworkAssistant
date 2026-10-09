using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 配置对比（行级 diff）。
///
/// 两个必须做的事，否则结果没人敢信：
///   ① **忽略"每次都会变"的行**：运行时间、配置时间戳、`Current configuration : N bytes`、命令回显…
///      这些每次都不同，不忽略的话每份备份之间都是"满天差异"，真正的变更被淹掉；
///   ② 忽略的行要**单独计数并给样例** —— 用户需要知道"被跳过了什么"，而不是以为它们没变。
///
/// 性能：用两行的滚动 DP 求最长公共子序列（内存 O(n)），再借助决策位矩阵回溯。
/// 行数乘积超过 <see cref="MaxCells"/> 时退化成集合比对，并在结果里明确标注（不假装是行序对齐的 diff）。
/// </summary>
public sealed class ConfigDiffService
{
    /// <summary>超过这个规模就退化（3000×3000 ≈ 9M，实测毫秒级；再大就换策略，避免卡住界面）。</summary>
    private const long MaxCells = 12_000_000;

    /// <summary>被当成"每次都会变"的行前缀/片段（大小写不敏感）。</summary>
    private static readonly string[] VolatileMarkers =
    {
        "building configuration",
        "current configuration",
        "! last configuration change",
        "! nvram config last updated",
        "! startup-config last updated",
        "uptime is",
        "system start time",
        "system uptime",
        "time source is",
    };

    /// <summary>
    /// 抓取时混进来的控制痕迹，**必须在对比前清掉**（2026-09-22 真机实测踩到）：
    /// `show running-config` 分页时，设备会把 `--More--` 加一串空格**粘在下一行配置前面**，
    /// 而两次抓取粘的位置/次数不一定相同 —— 于是"两份完全一样的配置"会 diff 出十几行差异，
    /// 界面上就是假的"你有 13 行改动没保存"。这种误报会直接毁掉这个功能的可信度。
    /// 设备原文形态：` --More--          switchport access vlan 200`；另有退格符 \x08。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PagerArtifact =
        new(
            @"-{2,}\s*[Mm]ore\s*-{2,}\s*|\x08",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public ConfigDiffResult Compare(string? oldText, string? newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        // 先剔除"每次都会变"的行：注意**两边都要剔**，否则会凭空多出一堆增删。
        var oldKept = new List<DiffInputLine>();
        var newKept = new List<DiffInputLine>();
        var ignoredSamples = new List<string>();
        var ignored = 0;

        for (var i = 0; i < oldLines.Count; i++)
        {
            // 空行不参与比较（也不算"忽略"）：它不含任何配置语义，
            // 而设备两次输出的空行位置/个数会因为分页重画而不同 —— 真机实测这是最后一类假阳性
            //（running 与 startup 只差一处空行，却被报成"有 1 行改动没保存"）。
            if (oldLines[i].Raw.Trim().Length == 0)
            {
                continue;
            }

            if (IsVolatile(oldLines[i].Raw))
            {
                ignored++;
                AddSample(ignoredSamples, oldLines[i].Raw);
                continue;
            }

            oldKept.Add(oldLines[i] with { LineNo = oldLines[i].LineNo });
        }

        for (var i = 0; i < newLines.Count; i++)
        {
            if (newLines[i].Raw.Trim().Length == 0)
            {
                continue;
            }

            if (IsVolatile(newLines[i].Raw))
            {
                ignored++;
                AddSample(ignoredSamples, newLines[i].Raw);
                continue;
            }

            newKept.Add(newLines[i] with { LineNo = newLines[i].LineNo });
        }

        var cells = (long)oldKept.Count * newKept.Count;
        // ⚠️ 裁首尾空行必须放在**过滤易变行之后**（2026-09-22 真机实测）：
        // running 的表头是"命令回显 / 空行 / Building configuration... / Current configuration : N bytes / 空行 / 配置"，
        // 中间那两行是易变行、会被滤掉 —— 但它们在**过滤前**夹在中间，所以过滤前的"首尾"并不空。
        // 先裁后滤的结果就是：过滤完凭空多出两个"开头的空行"，表现为假的 2 行差异。
        oldKept = TrimBlankEdges(oldKept);
        newKept = TrimBlankEdges(newKept);
        cells = (long)oldKept.Count * newKept.Count;
        return cells <= MaxCells
            ? DiffByLcs(oldKept, newKept, ignored, ignoredSamples)
            : DiffBySet(oldKept, newKept, ignored, ignoredSamples);
    }

    /// <summary>行级 LCS diff（内存 O(n)，回溯靠决策位）。</summary>
    private static ConfigDiffResult DiffByLcs(
        List<DiffInputLine> oldLines,
        List<DiffInputLine> newLines,
        int ignored,
        List<string> ignoredSamples)
    {
        var n = oldLines.Count;
        var m = newLines.Count;

        // 决策矩阵：0=从左上（相同）、1=来自上方（删）、2=来自左方（增）
        var choice = new byte[(long)n * m > 0 ? n * m : 1];
        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                if (string.Equals(oldLines[i - 1].Key, newLines[j - 1].Key, StringComparison.Ordinal))
                {
                    current[j] = previous[j - 1] + 1;
                    choice[(i - 1) * m + (j - 1)] = 0;
                }
                else if (previous[j] >= current[j - 1])
                {
                    current[j] = previous[j];
                    choice[(i - 1) * m + (j - 1)] = 1;
                }
                else
                {
                    current[j] = current[j - 1];
                    choice[(i - 1) * m + (j - 1)] = 2;
                }
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        // 回溯：从右下往左上走，得到"从上往下"的行序
        var lines = new List<ConfigDiffLine>();
        var x = n;
        var y = m;
        var added = 0;
        var removed = 0;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && choice[(x - 1) * m + (y - 1)] == 0)
            {
                lines.Add(new ConfigDiffLine
                {
                    Kind = ConfigDiffKind.Same,
                    Text = oldLines[x - 1].Raw,
                    OldLineNumber = oldLines[x - 1].LineNo,
                    NewLineNumber = newLines[y - 1].LineNo,
                });
                x--;
                y--;
            }
            else if (y > 0 && (x == 0 || choice[(x - 1) * m + (y - 1)] == 2))
            {
                lines.Add(new ConfigDiffLine
                {
                    Kind = ConfigDiffKind.Added,
                    Text = newLines[y - 1].Raw,
                    NewLineNumber = newLines[y - 1].LineNo,
                });
                added++;
                y--;
            }
            else
            {
                lines.Add(new ConfigDiffLine
                {
                    Kind = ConfigDiffKind.Removed,
                    Text = oldLines[x - 1].Raw,
                    OldLineNumber = oldLines[x - 1].LineNo,
                });
                removed++;
                x--;
            }
        }

        lines.Reverse();
        return new ConfigDiffResult
        {
            Lines = lines,
            AddedCount = added,
            RemovedCount = removed,
            IgnoredCount = ignored,
            IgnoredSamples = ignoredSamples,
        };
    }

    /// <summary>行数太大时的退化方案：只按"出现次数差"报增删，**不保证行序对齐**（结果里会标注）。</summary>
    private static ConfigDiffResult DiffBySet(
        List<DiffInputLine> oldLines,
        List<DiffInputLine> newLines,
        int ignored,
        List<string> ignoredSamples)
    {
        var oldCounts = CountByKey(oldLines, out var oldRaw);
        var newCounts = CountByKey(newLines, out var newRaw);
        var lines = new List<ConfigDiffLine>();
        var added = 0;
        var removed = 0;

        foreach (var (key, count) in newCounts)
        {
            oldCounts.TryGetValue(key, out var oldCount);
            for (var i = oldCount; i < count; i++)
            {
                lines.Add(new ConfigDiffLine { Kind = ConfigDiffKind.Added, Text = newRaw[key] });
                added++;
            }
        }

        foreach (var (key, count) in oldCounts)
        {
            newCounts.TryGetValue(key, out var newCount);
            for (var i = newCount; i < count; i++)
            {
                lines.Add(new ConfigDiffLine { Kind = ConfigDiffKind.Removed, Text = oldRaw[key] });
                removed++;
            }
        }

        return new ConfigDiffResult
        {
            Lines = lines,
            AddedCount = added,
            RemovedCount = removed,
            IgnoredCount = ignored,
            IgnoredSamples = ignoredSamples,
            Degraded = true,
        };
    }

    /// <summary>按**比较键**（已去掉前导空格）计数，另外回传 键→原文 供显示。</summary>
    private static Dictionary<string, int> CountByKey(List<DiffInputLine> lines, out Dictionary<string, string> rawByKey)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        rawByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            counts[line.Key] = counts.TryGetValue(line.Key, out var count) ? count + 1 : 1;
            rawByKey[line.Key] = line.Raw;
        }

        return counts;
    }

    /// <summary>
    /// 参与对比的一行：**比较用 <see cref="Key"/>（去掉前导空白）、显示用 <see cref="Raw"/>**。
    ///
    /// 为什么要分开（2026-09-22 真机实测踩到的最大一个假阳性）：
    /// `show running-config` 分页续页时设备会重画行，**两次抓取同一行留下的前导空格数不一样**
    /// （设备原文：`"            !"` vs `"!"`、`" switchport access vlan 200"` vs `"             switchport access vlan 200"`）。
    /// 直接比原文 → 两份完全一致的配置 diff 出 13+12 行差异，界面上就是假的"你有 13 行改动没保存"。
    /// 配置行的前导空格只是缩进、不含语义，所以比较时去掉、显示时保留。
    /// </summary>
    private readonly record struct DiffInputLine(int LineNo, string Raw, string Key);

    private static List<DiffInputLine> SplitLines(string? text)
    {
        var raw = string.IsNullOrEmpty(text)
            ? new List<string>()
            : PagerArtifact
                .Replace(text, " ")                       // 先摘掉 --More--/退格
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(l => l.TrimEnd())                 // 行尾空格：设备补齐用的，不算差异
                .ToList();

        // 这里**不裁空行**：先保留原始行号，等易变行过滤完再裁（见 Compare 里的注释）
        return raw
            .Select((line, index) => new DiffInputLine(index + 1, line, line.Trim()))
            .ToList();
    }

    /// <summary>
    /// 去掉**首尾**的空行：两份抓取的首尾空行数经常不一样（提示符、分页残留），
    /// 不裁的话每次都会多出几行假差异。**中间**的空行保留（配置里的空行位置也算结构）。
    /// </summary>
    /// <summary>去掉**首尾**的空行（`DiffInputLine` 版本；中间的空行保留，配置里空行位置也算结构）。</summary>
    private static List<DiffInputLine> TrimBlankEdges(List<DiffInputLine> lines)
    {
        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && lines[start].Raw.Trim().Length == 0)
        {
            start++;
        }

        while (end >= start && lines[end].Raw.Trim().Length == 0)
        {
            end--;
        }

        return start > end ? new List<DiffInputLine>() : lines.GetRange(start, end - start + 1);
    }

    private static List<string> TrimBlankEdges(List<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && lines[start].Trim().Length == 0)
        {
            start++;
        }

        while (end >= start && lines[end].Trim().Length == 0)
        {
            end--;
        }

        return start > end ? new List<string>() : lines.GetRange(start, end - start + 1);
    }

    /// <summary>这一行是不是"每次都会变"的（运行时间/时间戳/字节数/命令回显…）。</summary>
    private static bool IsVolatile(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;   // 空行本身不忽略（配置里的空行位置也算结构）
        }

        foreach (var marker in VolatileMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 命令回显不是配置内容：备份文件里我们自己加的 `# show xxx` 前缀，
        // 以及两次抓取里"这次是第一页、下次不是"的那条命令本身（真机实测：`show startup-config` 有时在、
        // 有时因为是从上一次的缓冲里截出来的而不在 → 又会变成一行假差异）。
        return trimmed.StartsWith("show running-config", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("show startup-config", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("# show ", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSample(List<string> samples, string line)
    {
        if (samples.Count < 5)
        {
            samples.Add(line.Trim());
        }
    }

    /// <summary>
    /// 把"running 与 startup 的差异"翻译成一句**能直接照做**的结论。
    ///
    /// 这是现场最常问的一句话："我改完是不是忘了保存（write）？"
    /// ⚠️ 调用约定：<paramref name="diff"/> 必须是 `Compare(running, startup)` 的结果
    /// （左 = running、右 = startup）。因此：
    ///   · `Removed` = 只在 running 里有 = **改动还没保存**（重启就丢）；
    ///   · `Added`   = 只在 startup 里有 = 保存之后会被抹掉的行（例如别人直接改过 startup）。
    /// 左右搞反的话结论会正好相反 —— 所以这里在文案里把"哪边多"写死，而不是只说"不一致"。
    /// </summary>
    public static string DescribeSaveState(ConfigDiffResult diff)
    {
        if (diff.Degraded)
        {
            return "⚠ 配置太大，只做了集合比对（不保证行序对齐）：请直接看下面的差异行自己判断是否已保存。";
        }

        if (!diff.HasDifferences)
        {
            return "✅ running 与 startup 一致：设备上的改动已经保存（重启不会丢）。";
        }

        if (diff.RemovedCount > 0 && diff.AddedCount == 0)
        {
            return $"⚠ 有 {diff.RemovedCount} 行只存在于 running-config —— **改动还没保存**，重启会丢。"
                   + "请到顶栏[保存配置]执行 write。";
        }

        if (diff.AddedCount > 0 && diff.RemovedCount == 0)
        {
            return $"⚠ 有 {diff.AddedCount} 行只存在于 startup-config（running 里没有）——"
                   + "这通常是有人直接改过启动配置，或上次保存后又被改回来了。"
                   + "**先看清楚下面这些行再决定要不要保存**，直接 write 会把这部分抹掉。";
        }

        return $"⚠ running 与 startup 双向不一致：running 里多 {diff.RemovedCount} 行（未保存的改动）、"
               + $"startup 里多 {diff.AddedCount} 行（保存后会消失）。**逐行看清楚再决定**。";
    }
}
