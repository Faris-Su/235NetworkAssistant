using System.IO.Ports;
using System.Text;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>Console / Serial 连接实现。</summary>
public sealed class SerialDeviceConnection : DeviceConnectionBase
{
    private readonly SerialConnectionSettings _settings;
    private readonly ILogService _log;
    private SerialPort? _port;

    /// <summary>
    /// 串口写入串行化。
    ///
    /// 为什么必须有（2026-09-25）：CLI 终端模式下**每个按键都是一次独立的 WriteRawAsync**
    /// （`PreviewTextInput` → `SendTerminalTextAsync`），人手快速连打时会有多个写并发落到同一个
    /// `SerialPort.BaseStream`；而 `BaseStream.WriteAsync` 不保证并发安全 ——
    /// 轻则字节顺序打乱、重则丢字符（现场表现为"输入会少显示"）。
    /// Telnet 侧本来就有 `_writeLock`，串口侧缺了这道锁，这里补齐，两边行为一致。
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _readLoop;
    private Encoding _encoding = Encoding.UTF8;

    public SerialDeviceConnection(SerialConnectionSettings settings, ILogService log)
        : base(log)
    {
        _settings = settings.Clone();
        _log = log;
        CommandTimeoutMs = _settings.CommandTimeoutMs;
        IdleQuietMs = _settings.IdleQuietMs;
    }

    public override DeviceConnectionKind Kind => DeviceConnectionKind.Serial;

    /// <summary>Console 命令行结束符：默认 CR（锐捷 Console 通常只需回车）。</summary>
    protected override string CommandLineEnding => _settings.LineEnding;

    /// <summary>探测顺序：先按用户配置的波特率，再依次试这些常见值。</summary>
    private static readonly int[] FallbackBaudRates = { 115200, 38400, 57600, 19200, 9600 };

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.PortName))
        {
            throw new InvalidOperationException(
                "没有选择串口。请点[刷新串口]选择实际端口；如果下拉是空的，说明 USB-Serial 驱动没有安装。" +
                "\n原始错误：SerialPortName=空");
        }

        _encoding = ResolveEncoding(_settings.Encoding);

        SerialPort? port = null;
        try
        {
            var handshake = MapHandshake(_settings.FlowControl);
            var driverControlsRts = handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;

            port = new SerialPort(
                _settings.PortName,
                _settings.BaudRate,
                MapParity(_settings.Parity),
                _settings.DataBits,
                MapStopBits(_settings.StopBits))
            {
                Handshake = handshake,
                ReadTimeout = 500,
                WriteTimeout = 3000,

                // DTR/RTS 常开是 Console 线的常规做法（PuTTY/SecureCRT 默认也拉高），
                // 但选了 RTS/CTS 硬件流控时 RTS 必须交给驱动管理，否则会互相打架。
                DtrEnable = _settings.DtrEnable,
                RtsEnable = _settings.RtsEnable && !driverControlsRts,
                NewLine = "\r\n",
            };

            port.Open();
        }
        catch (Exception ex)
        {
            port?.Dispose();
            // .NET 原生文案是英文的（例如 "Could not find file 'COM3'."），
            // 这里翻译成能照做中文提示，同时保留原始错误进日志。
            _log.Warn($"串口打开失败：{_settings.PortName}", ex);
            throw new InvalidOperationException(
                ConnectionDiagnostics.DescribeSerialFailure(_settings.PortName, ex),
                ex);
        }

        if (port is null)
        {
            throw new InvalidOperationException("串口对象未能创建。");
        }

        _port = port;
        ManagementAddress = _settings.PortName;
        _log.Info($"串口已打开：{_settings.PortName} {_settings.BaudRate},{_settings.DataBits},{_settings.Parity},{_settings.StopBits}");

        // 串口打开成功就是"已连接"（Console 不走用户名/密码流程）。
        MarkTransportEstablished();

        // ⚠️ 以前这里直接 MarkCliReady("串口已打开") —— 也就是**没探测过设备有没有回应**就宣称 CLI 就绪。
        // 现场表现就是"显示已连接，但终端一个字都没有"（COM 口能打开，但设备/线缆其实没通）。
        // 现在改成：发回车探一次，看到提示符才算 CLI 就绪；收不到数据就自动试常见波特率；
        // 全都收不到 → 保持"已连接"，但把原因写清楚（CLI 未确认），绝不影响连接状态。
        var probe = await Task.Run(() => ProbeForPrompt(port, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var effectiveBaud = port.BaudRate;
        var switchedBaud = false;
        var triedBaud = new List<string> { $"{port.BaudRate}({DescribeProbe(probe)})" };

        if (!probe.PromptFound && probe.Bytes == 0)
        {
            foreach (var baud in FallbackBaudRates.Where(b => b != effectiveBaud))
            {
                cancellationToken.ThrowIfCancellationRequested();

                SerialPort candidate;
                try
                {
                    candidate = OpenPort(port, baud);
                }
                catch (Exception ex)
                {
                    triedBaud.Add($"{baud}(打开失败:{ex.Message})");
                    continue;
                }

                var next = await Task.Run(() => ProbeForPrompt(candidate, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                triedBaud.Add($"{baud}({DescribeProbe(next)})");

                if (next.PromptFound)
                {
                    // 换波特率成功：沿用这根已打开的端口，把配置里的值也更新掉
                    _settings.BaudRate = baud;
                    port = candidate;
                    probe = next;
                    effectiveBaud = baud;
                    switchedBaud = true;
                    break;
                }

                try
                {
                    candidate.Close();
                    candidate.Dispose();
                }
                catch
                {
                    // 关不掉也不影响后续流程
                }
            }
        }

        _port = port;
        _readLoop = Task.Run(() => ReadLoopAsync(LifetimeToken), CancellationToken.None);

        if (probe.PromptFound)
        {
            MarkCliReady(probe.Bytes == 0
                ? "串口已打开"
                : $"已看到命令提示符（{effectiveBaud} 8N1）");
            ReportConnectNotice(
                switchedBaud
                    ? $"串口已连接：{_settings.PortName}｜原波特率下没有任何回应，已在 {effectiveBaud} 下看到命令提示符（已自动切换）。"
                    : $"串口已连接：{_settings.PortName}（{effectiveBaud} 8N1），已看到命令提示符。");
            return;
        }

        // 没探测到提示符：连接保持（这是 Console 的约定，不能判成连接失败），但说清"收到了什么"
        var detail = probe.Bytes == 0
            ? "依次试过 " + string.Join("、", triedBaud) + " 都没有收到任何数据 —— " +
              "连一个字节都没有基本是物理层问题：RJ45 console 必须用翻转线（普通网线不行）、" +
              "USB-Serial 驱动、或线缆本身接触不良。"
            : $"在 {effectiveBaud} 收到 {probe.Bytes} 字节但里面没有命令提示符（可能是乱码）—— " +
              "波特率/校验位不匹配的可能性最大（锐捷一般是 9600 8N1、无流控）。";
        MarkCliNotReady($"串口已连接，但 CLI 未确认：{detail}");
        ReportConnectNotice($"串口已连接（{_settings.PortName}）：{detail}");
        _log.Warn($"Console 探测未获得提示符：{detail}");
    }

    /// <summary>按指定波特率重新打开同一个串口（沿用其余参数）。</summary>
    private SerialPort OpenPort(SerialPort template, int baudRate)
    {
        var handshake = MapHandshake(_settings.FlowControl);
        var driverControlsRts = handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;

        var port = new SerialPort(
            template.PortName,
            baudRate,
            template.Parity,
            template.DataBits,
            template.StopBits)
        {
            Handshake = handshake,
            ReadTimeout = 500,
            WriteTimeout = 3000,
            DtrEnable = _settings.DtrEnable,
            RtsEnable = _settings.RtsEnable && !driverControlsRts,
            NewLine = "\r\n",
        };

        template.Close();
        template.Dispose();
        port.Open();
        return port;
    }

    /// <summary>
    /// 探一次：先收掉可能已经到达的开机横幅，再发两个回车，然后最多等 1.6 秒看有没有回显/提示符。
    /// **同步读**，调用方必须放到后台线程执行（否则会卡住 UI）。
    /// </summary>
    private (int Bytes, string Text, bool PromptFound) ProbeForPrompt(
        SerialPort port,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var text = new System.Text.StringBuilder();
        var bytes = 0;

        void Drain(int milliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var read = port.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        bytes += read;
                        text.Append(_encoding.GetString(buffer, 0, read));
                    }
                }
                catch (TimeoutException)
                {
                    // 正常：这一轮没有数据
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        Drain(250);                       // 先看有没有主动输出（横幅 / 已有提示符）
        if (!LooksLikePrompt(text.ToString()))
        {
            try
            {
                port.Write("\r\r");       // 唤醒：多数 Console 敲回车才吐提示符
                port.BaseStream.Flush();
            }
            catch (Exception)
            {
                return (bytes, text.ToString(), false);
            }

            Drain(1400);
        }

        var all = text.ToString();
        return (bytes, all, LooksLikePrompt(all));
    }

    private static bool LooksLikePrompt(string text) =>
        text.Contains("Username:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Password:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Press ENTER", StringComparison.OrdinalIgnoreCase)
        || text.Contains("login:", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(text, @"[#>]\s*$");

    private static string DescribeProbe((int Bytes, string Text, bool PromptFound) probe) =>
        probe.PromptFound ? "有提示符" : probe.Bytes == 0 ? "无任何数据" : $"收到 {probe.Bytes} 字节但无提示符";

    protected override async Task WriteCoreAsync(string text, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开。");
        var bytes = _encoding.GetBytes(text);
        // 写入必须串行化：见 _writeLock 的注释（并发写同一个 BaseStream 会乱序/丢字符，
        // 现场表现就是"CLI 里输入会少显示"）。
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await port.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task ShutdownCoreAsync()
    {
        var port = _port;
        _port = null;
        if (port is not null)
        {
            // SerialPort.Close() 是**同步阻塞**调用，而且它会等内部事件循环退出 ——
            // 此刻读循环很可能正阻塞在 BaseStream.ReadAsync（驱动异常时更久）。
            // 直接在 UI 线程调它 = 现场那次"点[断开]程序未响应"，所以挪到后台线程去做，
            // 并且只等 3 秒：等不到就先放行（后台线程继续关），由外层 SafeShutdownAsync 兜住整体流程。
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        port.Close();
                        port.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("关闭串口时出现异常", ex);
                    }
                }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn("关闭串口超过 3 秒仍未返回（已放弃等待，后台继续关闭）");
            }
        }

        await WaitForReadLoopAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    /// <summary>等待读取循环退出（最多 1 秒），避免断开后后台线程还在读串口。</summary>
    private async Task WaitForReadLoopAsync()
    {
        var loop = _readLoop;
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 超时或循环自身异常都不影响断开流程。
        }
        finally
        {
            _readLoop = null;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var decoder = _encoding.GetDecoder();
        var charBuffer = new char[_encoding.GetMaxCharCount(buffer.Length)];

        while (!cancellationToken.IsCancellationRequested)
        {
            SerialPort? port = _port;
            if (port is null || !port.IsOpen)
            {
                return;
            }

            try
            {
                var read = await port.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    continue;
                }

                var chars = decoder.GetChars(buffer, 0, read, charBuffer, 0);
                if (chars > 0)
                {
                    AppendOutput(new string(charBuffer, 0, chars));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // 串口空闲：正常情况，继续读取。
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log.Warn("串口读取中断", ex);
                }

                return;
            }
        }
    }

    private static Encoding ResolveEncoding(string name) => name?.ToUpperInvariant() switch
    {
        "ASCII" => Encoding.ASCII,
        "LATIN1" or "ISO-8859-1" => Encoding.Latin1,
        // 锐捷真机中文是 GBK（选 UTF-8 会显示乱码，而且会话留档会一起写坏）
        "GB18030" or "GBK" or "GB2312" => Helpers.DeviceTextDecoder.FallbackEncoding,
        _ => Encoding.UTF8,
    };

    private static Parity MapParity(SerialParity parity) => parity switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None,
    };

    private static StopBits MapStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One,
    };

    private static Handshake MapHandshake(SerialFlowControl flowControl) => flowControl switch
    {
        SerialFlowControl.XOnXOff => Handshake.XOnXOff,
        SerialFlowControl.RequestToSend => Handshake.RequestToSend,
        SerialFlowControl.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => Handshake.None,
    };
}
