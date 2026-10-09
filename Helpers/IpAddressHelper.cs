using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 资源表里的 IP 写法并不规范（例如“VLAN100  192.0.2.200  VLAN200  192.0.2.106”、
/// “192.0.2.101（锐捷交换机）”、“192.0.2.2-128”），这里集中处理这些格式。
/// </summary>
public static partial class IpAddressHelper
{
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    public static IReadOnlyList<string> ExtractAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (Match m in Ipv4Regex().Matches(text))
        {
            if (TryParse(m.Value, out var ip))
            {
                result.Add(ip.ToString());
            }
        }

        return result;
    }

    public static string? ExtractFirst(string? text)
    {
        var all = ExtractAll(text);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// 把"连接地址"拆成 IP 与端口两段 —— 概览页的「管理 IP」与「连接端口」两格都靠它，
    /// 免得再把端口名当成 IP 显示（现场两次踩过这个坑）。
    ///
    /// 例：
    ///   `192.0.2.235:23` → ("192.0.2.235", "23")   （Telnet / SSH 连接地址就是这个形状）
    ///   `127.0.0.1:58234`  → ("127.0.0.1", "58234")
    ///   `COM5`             → (null, "COM5")            （配置线：没有 IP，端口就是 COM 口名）
    ///   `""` / null        → (null, null)
    ///
    /// 只按"最后一个冒号"切，且只在冒号右边不是空的情况下当端口 —— 避免把 IPv6 的字面量切得乱七八糟
    /// （本项目目前只连 IPv4，真出现 IPv6 时这里会退化成"整串当 IP"，不会瞎切）。
    /// </summary>
    public static (string? Ip, string? Port) SplitEndpoint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (null, null);
        }

        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1)
        {
            var hostPart = text[..colon];
            var portPart = text[(colon + 1)..];
            return (ExtractFirst(hostPart) ?? hostPart, portPart);
        }

        return (ExtractFirst(text), text);
    }

    public static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text.Trim();
        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = candidate.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return IPAddress.TryParse(candidate, out address!);
    }

    /// <summary>把 IP 转成可比较的 32 位整数（仅 IPv4）。</summary>
    public static bool TryToUInt32(IPAddress address, out uint value)
    {
        value = 0;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    /// <summary>判断 IP 是否落在形如 “192.0.2.2-192.0.2.254” 或 “192.0.2.2-128” 的范围内。</summary>
    public static bool RangeContains(string? range, IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(range) || !TryToUInt32(address, out var target))
        {
            return false;
        }

        var bounds = ParseRange(range);
        if (bounds is null)
        {
            return false;
        }

        return target >= bounds.Value.Start && target <= bounds.Value.End;
    }

    /// <summary>由掩码推算前缀长度（用于“最长前缀优先”的网段匹配）。</summary>
    public static bool TryGetPrefixLength(IPAddress mask, out int prefixLength)
    {
        prefixLength = 0;
        var bytes = mask.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        var seenZero = false;
        foreach (var value in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isSet = (value & (1 << bit)) != 0;
                if (isSet && seenZero)
                {
                    // 非连续掩码（例如 255.0.255.0）不参与前缀比较。
                    return false;
                }

                if (isSet)
                {
                    prefixLength++;
                }
                else
                {
                    seenZero = true;
                }
            }
        }

        return prefixLength > 0;
    }

    /// <summary>解析 IP 区间，支持完整写法与省略前缀的简写。</summary>
    public static (uint Start, uint End)? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var text = range.Replace("—", "-").Replace("－", "-").Replace("~", "-").Trim();
        var index = text.IndexOf('-', StringComparison.Ordinal);
        if (index <= 0)
        {
            return null;
        }

        var startText = text[..index].Trim();
        var endText = text[(index + 1)..].Trim();
        if (!TryParse(startText, out var start) || !TryToUInt32(start, out var startValue))
        {
            return null;
        }

        if (!endText.Contains('.', StringComparison.Ordinal))
        {
            // 简写形式：192.0.2.2-128, 192.0.2.140-254
            var startParts = startText.Split('.');
            if (startParts.Length != 4)
            {
                return null;
            }

            endText = $"{startParts[0]}.{startParts[1]}.{startParts[2]}.{endText}";
        }

        if (!TryParse(endText, out var end) || !TryToUInt32(end, out var endValue))
        {
            return null;
        }

        return startValue <= endValue ? (startValue, endValue) : (endValue, startValue);
    }
}
