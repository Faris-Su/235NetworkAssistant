using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 补 MAC / 主机名的结果统计。界面上要能回答"为什么好多行是空的"：
///   · MAC 只有**本机同网段（同一广播域）**的设备读得到 —— 跨网段是经过路由的，本机 ARP 表里只有网关；
///   · 主机名要有反向 DNS 或 NetBIOS 名 —— 交换机/打印机/手机这类设备本来就没有名字。
/// </summary>
public sealed record QuickPingEnrichSummary(int Online, int MacFound, int HostNameFound);

/// <summary>
/// QuickPing：输入即出结果的快速连通性检查（单个目标或一批/一段地址）。
///
/// 和【概览】页那个 Ping/Tracert 的分工：那边是"针对当前连接的设备"做诊断，
/// 这边是**随手敲一个地址就 PING**，并且支持一次查一片（网段简写、资源库设备清单）。
///
/// ⚠️ 一个必须说清的口径：**很多交换机默认忽略 ICMP**，"Ping 不通"只能说明 ICMP 没回，
/// 不能说明设备不在线（要确认得上 SNMP 或端口探测）。界面上必须把这句话写出来。
/// </summary>
public sealed partial class QuickPingService
{
    /// <summary>一次最多处理多少个目标（防止手滑把 /16 整段贴进来）。</summary>
    public const int MaxTargets = 1024;

    /// <summary>
    /// 把用户输入展开成目标列表。支持：
    ///   · 逗号/空格/分号/换行分隔；
    ///   · 网段简写 `192.0.2.1-32`（前 3 段沿用，最后一段 1..32）与 `192.0.2.1-192.0.2.32`（完整两端）；
    ///   · 域名原样保留。
    /// 重复项会被去掉（同一台设备写两遍没必要 ping 两次）。
    /// </summary>
    public static IReadOnlyList<string> ExpandTargets(string? text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Split(
                     new[] { ',', '，', ' ', ';', '；', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var target in ExpandOne(raw))
            {
                if (seen.Add(target))
                {
                    result.Add(target);
                }

                if (result.Count >= MaxTargets)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ExpandOne(string token)
    {
        var match = RangeTokenRegex().Match(token);
        if (!match.Success)
        {
            yield return token;
            yield break;
        }

        var prefix = match.Groups["prefix"].Value;      // "192.0.2." 或 "192.0.2."（完整两端时为空）
        var startText = match.Groups["start"].Value;
        var endText = match.Groups["end"].Value;

        // `192.0.2.1-192.0.2.32`：右端是完整 IP → 拆出各自的最后一段、并校验前缀一致
        if (endText.Contains('.', StringComparison.Ordinal))
        {
            var endParts = endText.Split('.');
            if (endParts.Length != 4)
            {
                yield return token;
                yield break;
            }

            prefix = string.Join('.', endParts.Take(3)) + ".";
            endText = endParts[3];
        }

        if (!int.TryParse(startText, out var start) || !int.TryParse(endText, out var end))
        {
            yield return token;
            yield break;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        if (start is < 0 or > 255 || end is < 0 or > 255)
        {
            yield return token;
            yield break;
        }

        for (var i = start; i <= end; i++)
        {
            yield return $"{prefix}{i}";
        }
    }

    /// <summary>
    /// Ping 一批目标（有限并发）。
    /// <paramref name="progress"/> 每完成一个回报一次，界面据此显示进度与增量结果。
    /// </summary>
    public async Task<IReadOnlyList<QuickPingResult>> PingManyAsync(
        IReadOnlyList<string> targets,
        int timeoutMs,
        int concurrency,
        CancellationToken cancellationToken,
        IProgress<QuickPingResult>? progress = null)
    {
        var results = new List<QuickPingResult>();
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 32));
        var collected = new List<QuickPingResult>();

        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var row = await PingOnceAsync(target, timeoutMs, cancellationToken).ConfigureAwait(false);
                lock (collected)
                {
                    collected.Add(row);
                }

                progress?.Report(row);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时保留已完成的结果（用户点了停止，看到的应该是"查了哪些"）
        }

        lock (collected)
        {
            results.AddRange(collected);
        }

        return results;
    }

    /// <summary>
    /// 给"在线"的目标补 **MAC（读本机 ARP 表，整段只读一次）** 与 **主机名（反向 DNS）**。
    ///
    /// 为什么只给在线的补：ARP 表里只有"刚刚有来往"的地址，主机名更是只有注册了 DNS 的才有；
    /// 对没通的地址去查这两样纯属浪费（254 个地址时能省掉绝大部分时间）。
    /// 主机名查不到是**常态**（校园网里大量设备没有反向解析），界面留空即可，不要显示成错误。
    /// </summary>
    public async Task<QuickPingEnrichSummary> EnrichAsync(
        IReadOnlyList<QuickPingResult> results,
        CancellationToken cancellationToken,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var online = results.Where(r => r.Status == QuickPingStatus.Ok).ToList();
        if (online.Count == 0)
        {
            return new QuickPingEnrichSummary(0, 0, 0);
        }

        // ① MAC：一次 `arp -a` 覆盖全部地址（只有本机同网段的设备才会在表里）
        var arp = await ArpCacheReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var macFound = 0;
        foreach (var row in online)
        {
            if (arp.TryGetValue(row.Target, out var mac))
            {
                row.MacAddress = mac;
                macFound++;
            }
        }

        progress?.Report((0, online.Count));

        // ② 主机名：先反向 DNS（跨网段也可能有），再拿 NetBIOS 兜底（只在同一广播域发查询）。
        //    反向 DNS 在校园网里基本没人维护，纯靠它会整列空白 —— 实测 192.0.2.1 都解析不出来。
        var done = 0;
        var hostNameFound = 0;
        using var gate = new SemaphoreSlim(24);
        var tasks = online.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var name = await TryResolveHostNameAsync(row.Target, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(name)
                    && IPAddress.TryParse(row.Target, out var address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    name = await NetBiosNameResolver
                        .QueryAsync(address, NetBiosNameResolver.DefaultTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                }

                row.HostName = name;
                if (!string.IsNullOrEmpty(name))
                {
                    Interlocked.Increment(ref hostNameFound);
                }

                progress?.Report((Interlocked.Increment(ref done), online.Count));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消就取消，已经查到的留着
        }

        return new QuickPingEnrichSummary(online.Count, macFound, hostNameFound);
    }

    /// <summary>反向 DNS 查主机名；查不到/超时都返回空串（**不要**把失败显示成"错误"）。</summary>
    private static async Task<string> TryResolveHostNameAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns
                .GetHostEntryAsync(ip)
                .WaitAsync(TimeSpan.FromMilliseconds(1500), cancellationToken)
                .ConfigureAwait(false);
            var name = entry.HostName;
            // 反向解析失败时 .NET 会把 IP 原样当主机名返回 —— 那不算"查到了名字"
            return string.Equals(name, ip, StringComparison.OrdinalIgnoreCase) ? string.Empty : name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Ping 一个目标。**不抛异常**：把失败原因放进结果里返回。</summary>
    public static async Task<QuickPingResult> PingOnceAsync(string target, int timeoutMs, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(target, Math.Clamp(timeoutMs, 100, 10000))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            watch.Stop();

            return reply.Status == IPStatus.Success
                ? new QuickPingResult
                {
                    Target = target,
                    LastOctet = LastOctetOf(target),
                    Status = QuickPingStatus.Ok,
                    RttMs = reply.RoundtripTime,
                    ReplyAddress = reply.Address?.ToString() ?? string.Empty,
                    ElapsedMs = watch.ElapsedMilliseconds,
                }
                : new QuickPingResult
                {
                    Target = target,
                    LastOctet = LastOctetOf(target),
                    Status = QuickPingStatus.Timeout,
                    Detail = $"ICMP 无应答（{reply.Status}）—— 交换机常默认忽略 ICMP，"
                             + "这只能说明 ping 不通，不能说明设备不在线。",
                    ElapsedMs = watch.ElapsedMilliseconds,
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new QuickPingResult
            {
                Target = target,
                LastOctet = LastOctetOf(target),
                Status = QuickPingStatus.Error,
                Detail = ex.Message,
                ElapsedMs = watch.ElapsedMilliseconds,
            };
        }
    }

    /// <summary>
    /// 取 IPv4 的最后一段（图形模式要按它定位格子）。
    /// ⚠️ 这个值必须**在生成结果时就填好**：图形模式是靠 `LastOctet` 找到对应格子的，
    /// 漏填的话所有行都会指向第 0 格 —— 界面表现是"整张网格只有第 0 格在变色"（真机上就是这么发现的）。
    /// 不是 IPv4（域名/异常输入）返回 -1，调用方据此跳过网格更新。
    /// </summary>
    internal static int LastOctetOf(string target)
    {
        if (!IPAddress.TryParse((target ?? string.Empty).Trim(), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return -1;
        }

        return address.GetAddressBytes()[3];
    }

    /// <summary>`192.0.2.1-32` 或 `192.0.2.1-192.0.2.32`（前 3 段可省略）。</summary>
    [GeneratedRegex(
        @"^(?<prefix>\d{1,3}\.\d{1,3}\.\d{1,3}\.)(?<start>\d{1,3})-(?<end>\d{1,3}(?:\.\d{1,3}){0,3})$",
        RegexOptions.None)]
    private static partial Regex RangeTokenRegex();
}
