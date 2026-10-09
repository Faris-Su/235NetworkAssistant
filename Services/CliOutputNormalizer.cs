using System.Text;
using System.Text.RegularExpressions;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// CLI 输出归一化。三层数据保持分离：
///   Raw Output（设备原始字节解码后的文本，会话留档保存的就是它）
///   → Normalized Output（去 ANSI / 处理退格 / 统一换行 / 去掉分页提示，解析器看这一层）
///   → Parsed Data（结构化结果）
/// 原始输出永远保留在 CommandOutput.RawOutput 与会话文件里，归一化只影响解析输入。
/// </summary>
public static partial class CliOutputNormalizer
{
    /// <summary>ANSI 转义序列（CSI / OSC / 单字符转义）。</summary>
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]")]
    private static partial Regex AnsiSequence();

    /// <summary>分页提示：--More-- / -- More -- / &lt;--- More ---&gt; / ---- More ----。</summary>
    [GeneratedRegex(
        @"(?i)(?:-{2,}\s*more\s*-{2,}|<{1,}[^<>\r\n]{0,10}more[^<>\r\n]{0,10}>{1,})")]
    private static partial Regex PagerPromptAnywhere();

    /// <summary>
    /// 把设备输出整理成“可直接解析”的文本。
    /// <paramref name="stripPagerPrompts"/> = false 时保留 --More--（分页检测必须先看到它才能续页）。
    /// </summary>
    public static string Normalize(string? raw, bool stripPagerPrompts = true)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = AnsiSequence().Replace(raw, string.Empty);
        text = ApplyBackspaces(text);
        if (stripPagerPrompts)
        {
            text = PagerPromptAnywhere().Replace(text, string.Empty);
        }

        // 统一换行：CRLF → LF；剩下的单个 CR 也当换行（锐捷部分命令只用 CR）
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // 去掉分页残留产生的“空白行堆叠”
        return CollapseBlankRuns(text);
    }

    /// <summary>处理退格：\b 删掉前一个字符（终端里的擦除效果）。</summary>
    private static string ApplyBackspaces(string text)
    {
        if (text.IndexOf('\b') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\b')
            {
                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Length--;
                }

                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>连续 3 个以上空行压成 2 个，避免分页擦除留下的空洞影响表格解析。</summary>
    private static string CollapseBlankRuns(string text)
    {
        if (!text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            return text;
        }

        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    /// <summary>去掉首尾空白行，供“原样展示”用。</summary>
    public static string TrimBlankEdges(string text) =>
        (text ?? string.Empty).Trim('\n', '\r', ' ', '\t');
}
