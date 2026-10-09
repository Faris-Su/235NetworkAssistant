using System.Text;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// MAC 地址工具。锐捷输出里可能写成 0200.0000.0001，也会写成 02:00:00:00:00:01 或 0200-0000-0001，
/// 比较时必须归一化，展示时统一成 02:00:00:00:00:01。
/// </summary>
public static class MacAddressHelper
{
    /// <summary>提取 12 位十六进制字符（小写）；不是 MAC 时返回 null。</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(12);
        foreach (var ch in text)
        {
            if (Uri.IsHexDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '.' or ':' or '-' or ' ' or '\t')
            {
                continue;
            }
            else
            {
                return null;
            }

            if (builder.Length > 12)
            {
                return null;
            }
        }

        return builder.Length == 12 ? builder.ToString() : null;
    }

    /// <summary>格式化为 02:00:00:00:00:01。</summary>
    public static string Format(string? text)
    {
        var normalized = Normalize(text);
        if (normalized is null)
        {
            return text?.Trim() ?? string.Empty;
        }

        // 12 个十六进制字符 + 5 个冒号 = 17，一次成型。
        // 旧写法每行要分配 6 个小字符串再 string.Join；大表中会造成大量短生命周期分配。
        return string.Create(17, normalized, static (span, source) =>
        {
            for (var i = 0; i < 6; i++)
            {
                var at = i * 3;
                span[at] = source[i * 2];
                span[at + 1] = source[(i * 2) + 1];
                if (i < 5)
                {
                    span[at + 2] = ':';
                }
            }
        });
    }

    /// <summary>
    /// 用**原始字节**格式化 MAC（现场 P0-4）：ifPhysAddress 这类字段回的是 6 个字节，
    /// 其中 58 69 6C 33 DF A8 恰好是合法 UTF-8，先按文本解码就会变成 "Xil3…" 再也救不回来。
    /// 长度不是 6 字节时，退化成十六进制串（仍然比乱码强）。
    /// </summary>
    public static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return "N/A";
        }

        if (bytes.Length != 6)
        {
            return string.Join(" ", bytes.ToArray().Select(b => b.ToString("X2")));
        }

        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = bytes[i].ToString("x2");
        }

        return string.Join(":", parts);
    }

    public static bool IsMatch(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);
    }
}
