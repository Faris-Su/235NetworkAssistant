using System.Text.RegularExpressions;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// CLI 分页（More）处理。锐捷/类 Cisco 设备的规则：
///   空格 = 下一页，回车 = 下一行 —— 所以自动续页必须发空格，不能发回车。
/// 检测只看输出**末尾**一小段，避免把正常数据里出现的 more 误判成分页。
/// </summary>
public static partial class CliPager
{
    /// <summary>分页死循环保护：超过这个页数就停止并报错（文档要求）。</summary>
    public const int MaxPages = 1000;

    /// <summary>只看末尾多少个字符（分页提示一定在刚输出的尾部）。</summary>
    private const int TailLength = 160;

    /// <summary>续页时要发的字符：空格（不是回车）。</summary>
    public const string ContinueKey = " ";

    [GeneratedRegex(
        @"(?i)(-{2,}\s*more\s*-{2,}|<{1,}[^<>\r\n]{0,10}more[^<>\r\n]{0,10}>{1,}|\bmore\b\s*[:\-]?\s*$|--\s*more\s*--)",
        RegexOptions.Multiline)]
    private static partial Regex PagerPattern();

    /// <summary>输出末尾是否停在分页提示上。</summary>
    public static bool IsWaitingForPage(string? rawOutput, out string matched)
    {
        matched = string.Empty;
        if (string.IsNullOrEmpty(rawOutput))
        {
            return false;
        }

        // 注意：这里必须保留 --More--（归一化的默认行为会把它删掉），否则永远检测不到分页。
        var text = CliOutputNormalizer.Normalize(rawOutput, stripPagerPrompts: false);
        var tail = text.Length > TailLength ? text[^TailLength..] : text;

        var match = PagerPattern().Match(tail);
        if (!match.Success)
        {
            return false;
        }

        // 分页提示后面不应再有实质内容（否则说明早就翻过去了）
        var after = tail[(match.Index + match.Length)..].Trim('\n', ' ', '\t');
        if (after.Length > 0 && !LooksLikePagerTrailer(after))
        {
            return false;
        }

        matched = match.Value.Trim();
        return true;
    }

    /// <summary>
    /// `--More--` 后面的"说明性尾巴"：部分固件把它写成
    /// `--More--, next page: Space, next line: Enter, quit: Control-C`。
    /// 旧实现要求尾巴必须为空 → 这类固件**完全检测不到分页**，大表命令只拿到第一页就停在空闲超时。
    /// 只有明确的操作说明才放行；如果尾巴是下一屏的数据（含字母数字的正文），仍判为"已经翻过去了"。
    /// </summary>
    private static bool LooksLikePagerTrailer(string after)
    {
        if (after.Length > 120)
        {
            return false;
        }

        var text = after.ToLowerInvariant();
        string[] markers = { "next page", "next line", "quit", "space", "enter", "control-c", "ctrl-c", "q to quit" };
        if (markers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        // 纯标点/分隔符尾巴（例如只有一个逗号或冒号）也算说明
        return !text.Any(char.IsLetterOrDigit);
    }
}
