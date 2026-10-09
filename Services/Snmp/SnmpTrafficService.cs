using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>
/// 端口流量：**两次采样求差**算出真实速率与利用率。
///
/// 为什么必须两次：SNMP 接口表给的是**累计字节**（`ifHCInOctets` 这类），
/// 单看一个数字看不出"这个口现在忙不忙"——现场问"整栋楼卡"时，
/// 需要的是"哪个口在跑满、哪个口错包在涨"，那只能靠间隔采两次求差。
///
/// 只用 6 列（ifName / 收发字节 / 收发错包 / 速率），比完整接口表的 12 列省一半请求。
/// </summary>
public sealed class SnmpTrafficService
{
    private readonly SnmpV2cClient _client;

    public SnmpTrafficService(SnmpV2cClient client) => _client = client;

    /// <summary>采一次计数器快照（按 ifIndex 索引）。</summary>
    public async Task<Dictionary<string, InterfaceCounterSample>> SampleAsync(
        string host,
        int port,
        string community,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        // 接口名是**字符串**，其它几列都是数字 —— 不能用同一个字典，否则名字会被当成数字丢掉
        var names = await WalkTextAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfName), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var inOctets = await WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfHCInOctets), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var outOctets = await WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfHCOutOctets), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var inErrors = await WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfInErrors), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var outErrors = await WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfOutErrors), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var highSpeed = await WalkAsync(host, port, community, SnmpOidRepository.StdIfX(SnmpOidRepository.StdIfHighSpeed), timeoutMs, retries, cancellationToken).ConfigureAwait(false);
        var speed = await WalkAsync(host, port, community, SnmpOidRepository.StdIf(SnmpOidRepository.StdIfSpeed), timeoutMs, retries, cancellationToken).ConfigureAwait(false);

        var samples = new Dictionary<string, InterfaceCounterSample>(StringComparer.Ordinal);
        foreach (var (index, text) in names)
        {
            samples[index] = new InterfaceCounterSample
            {
                IfIndex = index,
                Port = text,
                InOctets = LookupLong(inOctets, index),
                OutOctets = LookupLong(outOctets, index),
                InErrors = LookupLong(inErrors, index),
                OutErrors = LookupLong(outErrors, index),
                // ifHighSpeed 单位是 Mbps、ifSpeed 单位是 bps，换算成同一个单位再比
                SpeedBps = LookupLong(highSpeed, index) is { } mbps && mbps > 0
                    ? mbps * 1_000_000
                    : LookupLong(speed, index),
            };
        }

        return samples;
    }

    /// <summary>
    /// 对比两次采样，按利用率降序返回前 <paramref name="topCount"/> 个端口。
    /// <paramref name="seconds"/> 是两次采样之间的真实间隔（由调用方计时）。
    /// </summary>
    public static IReadOnlyList<InterfaceTrafficResult> Compare(
        IReadOnlyDictionary<string, InterfaceCounterSample> before,
        IReadOnlyDictionary<string, InterfaceCounterSample> after,
        double seconds,
        int topCount)
    {
        var results = new List<InterfaceTrafficResult>();
        if (seconds <= 0)
        {
            return results;
        }

        foreach (var (index, now) in after)
        {
            if (!before.TryGetValue(index, out var then))
            {
                continue;   // 这一轮才出现的接口（比如刚插上模块）：没有基线，跳过而不是瞎算
            }

            var inDelta = Delta(then.InOctets, now.InOctets);
            var outDelta = Delta(then.OutOctets, now.OutOctets);
            var reset = inDelta is null && then.InOctets is not null && now.InOctets is not null
                        || outDelta is null && then.OutOctets is not null && now.OutOctets is not null;

            double? inBps = inDelta is { } inBytes ? inBytes * 8 / seconds : null;
            double? outBps = outDelta is { } outBytes ? outBytes * 8 / seconds : null;

            double? utilization = null;
            // ⚠️ 两个方向都算不出来（计数器回绕）时利用率必须是 **N/A**，不能是 0%：
            // `Math.Max(inBps ?? 0, outBps ?? 0)` 会把"没有数据"变成 0，
            // 界面上就成了"这个口很闲"——正好读反。只有至少一个方向有数据才算利用率。
            if (now.SpeedBps is { } speedBps && speedBps > 0 && (inBps is not null || outBps is not null))
            {
                var busiest = Math.Max(inBps ?? 0, outBps ?? 0);
                utilization = busiest / speedBps * 100;
            }

            var errorDelta = SumDelta(
                Delta(then.InErrors, now.InErrors),
                Delta(then.OutErrors, now.OutErrors));

            results.Add(new InterfaceTrafficResult
            {
                Port = now.Port,
                InBps = inBps,
                OutBps = outBps,
                UtilizationPercent = utilization,
                ErrorDelta = errorDelta,
                CounterReset = reset,
            });
        }

        // 排序：有利用率的按利用率；没有速率的排后面（但不能丢，用户可能就想看那个口的字节差）
        return results
            .OrderByDescending(r => r.UtilizationPercent ?? -1)
            .ThenByDescending(r => (r.InBps ?? 0) + (r.OutBps ?? 0))
            .Take(Math.Max(1, topCount))
            .ToList();
    }

    /// <summary>
    /// 差值。<c>now &lt; before</c> 说明计数器**回绕或设备重启**了 —— 返回 null 让上层标出来，
    /// 绝不能拿一个负值去算速率（会变成"这个口在倒着收数据"）。
    /// </summary>
    private static long? Delta(long? beforeValue, long? nowValue) =>
        beforeValue is { } b && nowValue is { } n
            ? n >= b ? n - b : null
            : null;

    private static long? SumDelta(long? a, long? b) => a is null && b is null
        ? null
        : (a ?? 0) + (b ?? 0);

    private async Task<Dictionary<string, long>> WalkAsync(
        string host,
        int port,
        string community,
        string root,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var walk = await _client
            .WalkAsync(host, port, community, root, timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in walk.Rows)
        {
            var index = row.Oid.StartsWith(root + ".", StringComparison.Ordinal) ? row.Oid[(root.Length + 1)..] : null;
            if (index is not null && row.Number is { } value)
            {
                map[index] = value;
            }
        }

        return map;
    }

    private static long? LookupLong(IReadOnlyDictionary<string, long> map, string index) =>
        map.TryGetValue(index, out var value) ? value : null;

    /// <summary>走一列**文本**（接口名），按 ifIndex 索引。</summary>
    private async Task<Dictionary<string, string>> WalkTextAsync(
        string host,
        int port,
        string community,
        string root,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var walk = await _client
            .WalkAsync(host, port, community, root, timeoutMs, retries, SnmpOidRepository.InterfaceMaxRows, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in walk.Rows)
        {
            var index = row.Oid.StartsWith(root + ".", StringComparison.Ordinal) ? row.Oid[(root.Length + 1)..] : null;
            if (index is not null && !string.IsNullOrWhiteSpace(row.Display))
            {
                map[index] = row.Display.Trim();
            }
        }

        return map;
    }
}
