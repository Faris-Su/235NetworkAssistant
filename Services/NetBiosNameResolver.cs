using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// NetBIOS 名称查询（NBSTAT，UDP 137）。
///
/// 为什么需要它：经典 QuickPing 的"主机名"列其实是靠 NetBIOS 问出来的，
/// 而**反向 DNS 在校园网里基本没人维护** —— 实测（2026-09-23）扫本机同网段，
/// 通了的设备主机名全是空。Windows 机器（办公 PC、机房学生机）的 NetBIOS 名
/// 在同一广播域里随问随有，这条兜底能把"主机名"列真正填起来。
///
/// 只在**同网段**（同一广播域）才发查询：NetBIOS 不走路由，跨网段问了也白发，
/// 而且省掉 200 多个无谓的超时等待。查不到/超时一律返回空串，不抛异常、不拖慢主流程。
/// </summary>
public static class NetBiosNameResolver
{
    /// <summary>默认超时（毫秒）。局域网里 NetBIOS 要么秒回要么不回，400ms 够用。</summary>
    public const int DefaultTimeoutMs = 400;

    /// <summary>
    /// 查一个 IPv4 地址的 NetBIOS 名。查不到返回空串。
    /// </summary>
    public static async Task<string> QueryAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return string.Empty;
        }

        if (!IsInLocalSubnet(address))
        {
            return string.Empty;
        }

        try
        {
            using var udp = new UdpClient();
            udp.Connect(address, 137);
            var query = BuildQuery();
            await udp.SendAsync(query, query.Length).WaitAsync(cancellationToken).ConfigureAwait(false);

            var receive = udp.ReceiveAsync();
            var result = await receive
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 3000)), cancellationToken)
                .ConfigureAwait(false);
            return ParseResponse(result.Buffer) ?? string.Empty;
        }
        catch (Exception)
        {
            // 不在线 / 不回 NetBIOS / 被防火墙挡：都属于"没有名字"，不是错误
            return string.Empty;
        }
    }

    /// <summary>
    /// 目标是否和本机某个网卡在**同一网段**（有掩码可算时才判断；算不出来按"不是"处理）。
    /// </summary>
    internal static bool IsInLocalSubnet(IPAddress address)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    if (SameSubnet(address, unicast.Address, unicast.IPv4Mask))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读网卡失败：当作不在同一网段（宁可不查，也不要多发一轮无用包）
        }

        return false;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4 || m.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((x[i] & m[i]) != (y[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 构造 NBSTAT 通配查询（问"这个 IP 上有哪些 NetBIOS 名"）。
    /// 结构：12 字节头 + 1 长度字节 + 32 字节编码名 + 1 结束字节 + QTYPE(0x0021) + QCLASS(0x0001)。
    /// </summary>
    internal static byte[] BuildQuery()
    {
        // 事务 ID 用进程内的递增值，出问题时抓包能区分是哪一次查询
        var transactionId = (ushort)(Interlocked.Increment(ref _transactionId) & 0xFFFF);

        var buffer = new byte[12 + 1 + 32 + 1 + 4];
        buffer[0] = (byte)(transactionId >> 8);
        buffer[1] = (byte)(transactionId & 0xFF);
        // flags = 0（标准查询）、QDCOUNT = 1，其余计数为 0（默认 0，不用写）
        buffer[5] = 0x01;

        var offset = 12;
        buffer[offset++] = 32;                     // 编码名固定 32 字节
        offset += WriteEncodedWildcardName(buffer, offset);
        buffer[offset++] = 0x00;                   // 名称结束符
        buffer[offset++] = 0x00;                   // QTYPE = 0x0021（NBSTAT）
        buffer[offset++] = 0x21;
        buffer[offset++] = 0x00;                   // QCLASS = 0x0001（IN）
        buffer[offset] = 0x01;
        return buffer;
    }

    private static int _transactionId;

    /// <summary>把 NetBIOS 通配名 `*`（0x2A + 15 个 0x00）按半字节编码成 32 个字母。</summary>
    private static int WriteEncodedWildcardName(byte[] buffer, int offset)
    {
        var raw = new byte[16];
        raw[0] = 0x2A;   // '*'
        return WriteEncodedName(buffer, offset, raw);
    }

    /// <summary>半字节编码：每字节拆成两个 'A'+nibble 的字母。</summary>
    private static int WriteEncodedName(byte[] buffer, int offset, byte[] raw16)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var written = 0;
        foreach (var b in raw16)
        {
            buffer[offset + written++] = (byte)alphabet[(b >> 4) & 0x0F];
            buffer[offset + written++] = (byte)alphabet[b & 0x0F];
        }

        return written;
    }

    /// <summary>
    /// 解析 NBSTAT 应答，取第一个名字（工作站名）。拿不到返回 null。
    /// 只做“够用”的解析：跳过问题段与回答段的名称（可能是压缩指针），再按 RDLENGTH 定位名字表。
    /// </summary>
    internal static string? ParseResponse(byte[] data)
    {
        if (data.Length < 12 + 34 + 4 + 2)
        {
            return null;
        }

        var offset = 12;

        // 跳过问题段的名称（长度字节 + 名称 + 结束符）
        var nameLength = data[offset];
        offset += 1 + nameLength;
        if (offset < data.Length && data[offset] == 0x00)
        {
            offset++;
        }

        offset += 4;   // QTYPE + QCLASS

        // 回答段的名称：可能是压缩指针（0xC0 xx），也可能是完整名称
        if (offset + 2 > data.Length)
        {
            return null;
        }

        if ((data[offset] & 0xC0) == 0xC0)
        {
            offset += 2;
        }
        else
        {
            while (offset < data.Length && data[offset] != 0x00)
            {
                offset += data[offset] + 1;
            }

            offset++;
        }

        offset += 8;   // TYPE(2) + CLASS(2) + TTL(4)
        if (offset + 2 > data.Length)
        {
            return null;
        }

        var rdLength = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        if (rdLength < 19 || offset + rdLength > data.Length)
        {
            return null;
        }

        var nameCount = data[offset];
        if (nameCount == 0)
        {
            return null;
        }

        var name = Encoding.ASCII.GetString(data, offset + 1, 15).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
