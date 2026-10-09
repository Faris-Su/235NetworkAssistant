using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 本机 ARP 缓存读取：ping 通之后，从 ARP 表里把对方的 **MAC 地址**捞出来。
///
/// 为什么要读 ARP：QuickPing 光给"通/不通"不够用 —— 现场看到"这个 IP 活着"之后，
/// 下一个问题永远是"那它是什么设备"（MAC 能看出厂商，主机名能看出是哪台机器）。
/// 经典 QuickPing 的"网卡地址/主机名"两列就是干这个的。
///
/// 实现取舍：**整段只读一次 ARP 表**（跑一次 `arp -a` 解析全表），
/// 而不是每个 IP 跑一次 —— 扫 254 个地址时这是 1 次进程调用 vs 254 次。
/// </summary>
public static partial class ArpCacheReader
{
    /// <summary>
    /// 解析 `arp -a` 输出。中英文 Windows 都要认（表头/列名不一样，但**数据行的结构一样**）：
    /// <code>
    ///   192.0.2.1           02-00-00-00-00-02     动态
    ///   192.0.2.255         ff-ff-ff-ff-ff-ff     静态
    ///   192.0.2.1           02-00-00-00-00-01     dynamic
    /// </code>
    /// 判定规则很简单也很稳：**第一个 token 是合法 IPv4、第二个 token 能归一化成 MAC** 才算数据行。
    /// 这样不用管本地化表头，也不会把 `接口: 192.0.2.100 --- 0x5` 这种行误当数据。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return map;
        }

        foreach (var line in output.Split('\n'))
        {
            var tokens = Splitter().Split(line.Trim());
            if (tokens.Length < 2)
            {
                continue;
            }

            if (!IPAddress.TryParse(tokens[0], out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            var mac = MacAddressHelper.Normalize(tokens[1]);
            if (mac is null)
            {
                continue;
            }

            // 广播/组播地址（ff-ff-ff-…、01-00-5e-…）不是"某台主机"，留着只会误导。
            // 全 0（00-00-00-00-00-00）是 Windows ARP 表里"还没解析出来"的占位行，同样要丢
            // —— 否则"网卡地址"列会出现一个看着像地址、实际没有任何意义的全零值。
            if (mac is "ffffffffffff" or "000000000000" || mac.StartsWith("01005e", StringComparison.Ordinal))
            {
                continue;
            }

            map[tokens[0]] = MacAddressHelper.Format(tokens[1]);
        }

        return map;
    }

    /// <summary>跑一次 `arp -a` 并解析。**任何失败都返回空表**（ARP 读不到不该让 QuickPing 失败）。</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return new Dictionary<string, string>();
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return Parse(output);
        }
        catch (Exception)
        {
            // 取不到就是取不到：界面上"网卡地址"列留空，而不是编一个
            return new Dictionary<string, string>();
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Splitter();
}
