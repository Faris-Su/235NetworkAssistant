using System.Text;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 设备文本解码：**先按严格 UTF-8，失败再按 GB18030**。
///
/// 为什么需要：锐捷真机的中文描述 / VLAN 名 / LLDP system description 通常是 **GBK**
/// （项目在 SNMP 侧早就做了同样的回退，见 `SnmpV2cClient.DecodeText`）。终端侧原来写死 UTF-8 →
/// 屏幕上显示 `�`，而且**这个解码结果会被写进会话留档**（TerminalSession → SessionRecorder），
/// 现场证据一旦失真就再也回不来了。
///
/// 分块问题：TCP 会把一个 UTF-8 字符切成两块（3 字节的中文只到了 1 字节），严格解码会失败、
/// 被误判成 GBK。所以这里会**挂起结尾半个 UTF-8 序列**，等下一块到达再拼上（GBK 的两字节字符
/// 即使被切开也只会被多挂一次，顺序不受影响）。
/// </summary>
public sealed class DeviceTextDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static Encoding? _fallback;
    private byte[] _carry = Array.Empty<byte>();

    /// <summary>回退编码：GB18030（覆盖 GBK/GB2312，且能解码任意字节序列，不会抛异常）。</summary>
    public static Encoding FallbackEncoding
    {
        get
        {
            if (_fallback is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _fallback = Encoding.GetEncoding(54936);      // GB18030
            }

            return _fallback;
        }
    }

    /// <summary>清空挂起的半个字符（重连 / 换设备时调用）。</summary>
    public void Reset() => _carry = Array.Empty<byte>();

    /// <summary>解码一段字节；不足一个完整字符的尾巴会挂起到下一次调用。</summary>
    public string Decode(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[_carry.Length + data.Length];
        _carry.CopyTo(buffer, 0);
        data.CopyTo(buffer.AsSpan(_carry.Length));
        _carry = Array.Empty<byte>();
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        var usable = TrimIncompleteUtf8Tail(buffer, out var tail);
        if (usable > 0)
        {
            try
            {
                var text = StrictUtf8.GetString(buffer, 0, usable);
                _carry = tail;      // 结尾那半截留给下一块
                return text;
            }
            catch (DecoderFallbackException)
            {
                // 不是合法 UTF-8 → 按 GBK/GB18030 解（含结尾那半截，交给 GB18030 自己处理）
            }
        }
        else if (tail.Length > 0)
        {
            // 整块就是一个"半个 UTF-8 字符"：先挂起，等下一块（否则会误判成 GBK）
            _carry = tail;
            return string.Empty;
        }

        return FallbackEncoding.GetString(buffer);
    }

    /// <summary>
    /// 找出结尾处"不完整的 UTF-8 序列"，返回可安全解码的长度并把尾巴输出到 <paramref name="tail"/>。
    /// 结尾是 ASCII 或明显非法时不做处理（返回原长度）。
    /// </summary>
    private static int TrimIncompleteUtf8Tail(byte[] buffer, out byte[] tail)
    {
        tail = Array.Empty<byte>();
        var index = buffer.Length - 1;
        var continuations = 0;
        while (index >= 0 && (buffer[index] & 0xC0) == 0x80)
        {
            continuations++;
            index--;
            if (continuations > 3)
            {
                return buffer.Length;      // 续字节太多，显然不是 UTF-8 序列
            }
        }

        if (index < 0)
        {
            return buffer.Length;
        }

        var needed = buffer[index] switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 0,
        };
        if (needed == 0 || 1 + continuations >= needed)
        {
            return buffer.Length;          // ASCII/非法首字节，或序列已经完整
        }

        tail = buffer[index..];
        return index;
    }
}
