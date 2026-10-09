using System.Net.NetworkInformation;
using System.Text;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// Ping / Tracert 只读网络工具。
/// 与 SNMP、CLI 一样是**独立**的：只对目标地址发探测包，不依赖任何连接会话。
/// Tracert 用"逐跳增加 TTL + ICMP 超时"实现（System.Net.NetworkInformation.Ping），
/// 不需要管理员权限，也不需要 raw socket。
/// </summary>
public sealed class NetworkTools
{
    public const int DefaultPingCount = 4;
    public const int DefaultTracertMaxHops = 15;

    /// <summary>Ping：逐包显示延迟，最后给出统计（发送/接收/丢失/最小/最大/平均）。</summary>
    public async Task<string> PingAsync(
        string host,
        int count = DefaultPingCount,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var target = (host ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return "请先填写要 Ping 的 IP 或域名。";
        }

        var text = new StringBuilder();
        text.AppendLine($"正在 Ping {target}，共 {count} 次（超时 {timeoutMs} ms）：");
        text.AppendLine();
        progress?.Report(text.ToString());

        var sent = 0;
        var received = 0;
        var times = new List<long>();

        using var ping = new Ping();
        for (var i = 1; i <= count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                text.AppendLine("（已取消）");
                break;
            }

            sent++;
            try
            {
                var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    times.Add(reply.RoundtripTime);
                    text.AppendLine($"  第 {i} 次：来自 {reply.Address} 的回复：时间={reply.RoundtripTime}ms TTL={reply.Options?.Ttl}");
                }
                else
                {
                    text.AppendLine($"  第 {i} 次：{Translate(reply.Status)}");
                }

                // 每包都回报一次：界面上是"边打边出"，不用等全部跑完
                progress?.Report(text.ToString());
            }
            catch (Exception ex)
            {
                text.AppendLine($"  第 {i} 次：失败 - {ex.Message}");
            }
        }

        text.AppendLine();
        if (received > 0)
        {
            text.AppendLine(
                $"统计：发送 {sent}，接收 {received}，丢失 {sent - received}（{100.0 * (sent - received) / Math.Max(1, sent):F0}%），" +
                $"最小 {times.Min()}ms，最大 {times.Max()}ms，平均 {times.Average():F0}ms");
        }
        else
        {
            text.AppendLine(
                $"统计：发送 {sent}，接收 0，全部丢失。" + Environment.NewLine +
                "排查：① 目标是否在线、网线是否插好；② 电脑 IP 是否与目标同网段；" +
                "③ 很多交换机会禁用 ICMP（Ping 不通但 Telnet 可用），这种情况请直接试[连接]或[连通性自检]。");
        }

        return text.ToString();
    }

    /// <summary>
    /// Tracert：逐跳增加 TTL，显示每一跳的地址与延迟。
    /// 交换机/防火墙常不回 ICMP 超时包，此时该跳显示 *（属正常现象）。
    /// </summary>
    public async Task<string> TracertAsync(
        string host,
        int maxHops = DefaultTracertMaxHops,
        int timeoutMs = 1000,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var target = (host ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return "请先填写要 Tracert 的 IP 或域名。";
        }

        var text = new StringBuilder();
        text.AppendLine($"正在跟踪到 {target} 的路由，最多 {maxHops} 跳（每跳超时 {timeoutMs} ms）：");
        text.AppendLine();
        progress?.Report(text.ToString());

        using var ping = new Ping();
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                text.AppendLine("（已取消）");
                break;
            }

            var options = new PingOptions(ttl, dontFragment: true);
            try
            {
                var reply = await ping.SendPingAsync(target, timeoutMs, new byte[32], options).ConfigureAwait(false);
                switch (reply.Status)
                {
                    case IPStatus.Success:
                        text.AppendLine($"  {ttl,2}  {reply.Address,-15} {reply.RoundtripTime} ms   ← 已到达目标");
                        if (ttl == 1)
                        {
                            // 第一跳就是目标：多半是"同网段直连"或路径只有一跳——
                            // 这是**正常结果**，不是工具卡住（以前这里没有任何说明，容易被误判成 bug）。
                            text.AppendLine();
                            text.AppendLine("（第 1 跳即到达目标：目标与本机在同一网段、或路径只有一跳，属正常现象，不是没有后续。）");
                        }

                        progress?.Report(text.ToString());
                        return text.ToString();
                    case IPStatus.TtlExpired:
                        text.AppendLine($"  {ttl,2}  {reply.Address,-15} {reply.RoundtripTime} ms");
                        break;
                    case IPStatus.TimedOut:
                        text.AppendLine($"  {ttl,2}  *               请求超时（该跳可能不回 ICMP，属常见现象；会自动继续下一跳）");
                        break;
                    default:
                        text.AppendLine($"  {ttl,2}  {reply.Address,-15} {Translate(reply.Status)}");
                        break;
                }
            }
            catch (Exception ex)
            {
                text.AppendLine($"  {ttl,2}  失败 - {ex.Message}");
            }

            // 逐跳实时回报：14 跳超时要跑十几秒，边跑边出才不会被当成"卡住了"
            progress?.Report(text.ToString());
        }

        text.AppendLine();
        text.AppendLine($"（已到第 {maxHops} 跳仍未到达目标：可能路径更长，或中间设备不回 ICMP 超时包。）");
        progress?.Report(text.ToString());
        return text.ToString();
    }

    /// <summary>IPStatus → 中文说明。</summary>
    private static string Translate(IPStatus status) => status switch
    {
        IPStatus.Success => "成功",
        IPStatus.TimedOut => "请求超时",
        IPStatus.DestinationHostUnreachable => "目标主机不可达",
        IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
        IPStatus.DestinationPortUnreachable => "目标端口不可达",
        IPStatus.DestinationUnreachable => "目标不可达",
        IPStatus.TtlExpired => "TTL 到期",
        IPStatus.BadDestination => "目标地址不合法",
        IPStatus.Unknown => "未知结果（地址可能无法解析）",
        _ => status.ToString(),
    };
}
