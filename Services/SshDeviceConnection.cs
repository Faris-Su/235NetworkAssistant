using System.Text;
using Renci.SshNet;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// SSH 连接实现（SSH.NET）。
///
/// 分工与 Telnet/Serial 完全一致：本类只负责“把字节搬进搬出 + 把 CLI 是否就绪讲清楚”，
/// 命令执行、分页( --More-- )、提示符判定、权限(enable)、输出缓冲都在 <see cref="DeviceConnectionBase"/> 里复用。
///
/// 状态语义（易错点，和 Telnet 保持一致）：
///   · TCP/SSH 认证成功            → MarkTransportEstablished()，界面显示“已连接”；
///   · 认证后没看到 CLI 提示符     → MarkCliNotReady()，**不是连接失败**；
///   · 只有 TCP/SSH/认证本身失败   才抛异常（界面显示“连接失败”）。
///
/// 主机密钥：网络设备没有可维护的 known_hosts，这里按“首次信任”处理（不阻断连接），
/// 但会把算法/位数/指纹写进日志，事后可以核对是不是被换过。
/// </summary>
public sealed partial class SshDeviceConnection : DeviceConnectionBase
{
    private readonly SshConnectionSettings _settings;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SshClient? _client;
    private ShellStream? _shell;
    private Task? _readLoop;

    public SshDeviceConnection(SshConnectionSettings settings, ILogService log)
        : base(log)
    {
        _settings = settings.Clone();
        _log = log;
        CommandTimeoutMs = _settings.CommandTimeoutMs;
        IdleQuietMs = _settings.IdleQuietMs;
    }

    public override DeviceConnectionKind Kind => DeviceConnectionKind.Ssh;

    protected override string CommandLineEnding => _settings.LineEnding;

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            throw new InvalidOperationException(
                "没有填写 SSH 主机地址。请在【连接】页填写交换机管理 IP，或在地址簿里选一台交换机后点[填入 SSH 参数]。");
        }

        if (string.IsNullOrWhiteSpace(_settings.Username))
        {
            throw new InvalidOperationException("没有填写 SSH 用户名。锐捷设备需要先配置 SSH 登录账号（username ... password ...）。");
        }

        var client = new SshClient(BuildConnectionInfo())
        {
            // 设备端空闲断连很常见（vty timeout），保活避免扫表扫到一半掉线。
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        // 用 lambda 而不是具名方法：HostKeyEventArgs 在 SSH.NET 里属于 Common 命名空间，
        // 少一个 using 就少一处编译耦合。
        client.HostKeyReceived += (_, e) =>
        {
            try
            {
                _log.Info(
                    $"SSH 主机密钥：{e.HostKeyName} {e.KeyLength} 位，指纹 {e.FingerPrintSHA256}" +
                    "（网络设备没有 known_hosts，本次按首次信任处理；如指纹与设备实际不符请立即断开）");
            }
            catch (Exception ex)
            {
                _log.Debug($"记录 SSH 主机密钥指纹失败：{ex.Message}");
            }

            e.CanTrust = true;
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_settings.ConnectionTimeoutMs);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // 连接失败时的释放异常没有诊断价值，忽略。
            }

            _log.Warn($"SSH 连接失败：{_settings.Host}:{_settings.Port}", ex);
            throw new InvalidOperationException(
                ConnectionDiagnostics.DescribeSshFailure(
                    _settings.Host,
                    _settings.Port,
                    _settings.ConnectionTimeoutMs,
                    ex),
                ex);
        }

        _client = client;

        try
        {
            // 开一个交互式 shell（伪终端）。列宽给大一点：RGOS 的分页是按终端宽度算的，
            // 太窄会导致一行被折成多行、解析器误判。
            _shell = client.CreateShellStream("vt100", 200, 60, 0, 0, 16 * 1024);
        }
        catch (Exception ex)
        {
            _client = null;
            try
            {
                client.Dispose();
            }
            catch
            {
                // 同上
            }

            _log.Warn("SSH 已通过认证，但创建交互式终端失败", ex);
            throw new InvalidOperationException(
                $"SSH 账号已通过认证，但设备拒绝打开交互式终端（shell）：{ex.Message}。" +
                "常见原因：设备只允许执行命令（exec 通道）而不允许交互式终端，或 vty 线路被占满。",
                ex);
        }

        ManagementAddress = $"{_settings.Host}:{_settings.Port}";
        _readLoop = Task.Run(() => ReadLoopAsync(LifetimeToken), CancellationToken.None);
        _log.Info($"SSH 已连接：{ManagementAddress}（账号 {_settings.Username}，密码不记录）");

        // SSH 认证已经完成 → 从这一刻起就是“已连接”，后面的提示符只影响 CLI 是否就绪。
        MarkTransportEstablished();

        if (!_settings.AutoLogin)
        {
            MarkCliReady("未启用自动确认提示符（可在 CLI 里手动输入）");
            return;
        }

        MarkAuthenticating("SSH 认证完成，正在确认 CLI 提示符…");
        try
        {
            await ConfirmPromptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkCliNotReady("确认提示符已被取消");
            ConnectNotice = "SSH 已连接，但确认提示符被取消；可在 CLI 里手动输入。";
        }
        catch (Exception ex)
        {
            // 认证成功之后的问题一律按“CLI 未就绪”处理，绝不升级成“连接失败”。
            _log.Warn("SSH 连接后确认提示符出错（保留连接）", ex);
            MarkCliNotReady($"确认提示符出错：{ex.Message}");
            ConnectNotice = $"SSH 已连接，但确认提示符出错：{ex.Message}；可在 CLI 里手动输入。";
        }
        finally
        {
            MarkAutoLoginFinished();
        }
    }

    protected override async Task WriteCoreAsync(string text, CancellationToken cancellationToken)
    {
        var shell = _shell ?? throw new InvalidOperationException("SSH 连接未建立。");

        // 与 Telnet 同样的编码策略：纯 ASCII 直接发；含中文时按 GB18030（RGOS 认 GBK）。
        var bytes = text.All(static c => c < 0x80)
            ? Encoding.ASCII.GetBytes(text)
            : Helpers.DeviceTextDecoder.FallbackEncoding.GetBytes(text);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await shell.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task ShutdownCoreAsync()
    {
        try
        {
            _shell?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("关闭 SSH shell 时出现异常", ex);
        }

        try
        {
            if (_client is { IsConnected: true })
            {
                _client.Disconnect();
            }

            _client?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("关闭 SSH 连接时出现异常", ex);
        }
        finally
        {
            _shell = null;
            _client = null;
        }

        var loop = _readLoop;
        if (loop is not null)
        {
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
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        // 密码认证 + 键盘交互认证：锐捷/Cisco 类设备有时把交互式登录实现成 keyboard-interactive，
        // 两种都挂上，避免“账号密码明明对却说认证失败”。
        var methods = new AuthenticationMethod[]
        {
            new PasswordAuthenticationMethod(_settings.Username, _settings.Password ?? string.Empty),
            new KeyboardInteractiveAuthenticationMethod(_settings.Username),
        };

        return new ConnectionInfo(_settings.Host.Trim(), _settings.Port, _settings.Username, methods)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(2000, _settings.ConnectionTimeoutMs)),
        };
    }

    /// <summary>
    /// SSH 已经把认证做完了，这里只做一件事：确认拿到 CLI 提示符。
    /// 拿不到也**不算连接失败**（设备可能还在打 banner、或者在要求交互式改密码）。
    /// </summary>
    private async Task ConfirmPromptAsync(CancellationToken cancellationToken)
    {
        var budgetMs = Math.Max(2000, _settings.ConnectionTimeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);

        // 多数设备登录后马上给提示符，先安静等一小会儿。
        if (await WaitForOutputAsync(static t => t.Length > 0, 800, cancellationToken).ConfigureAwait(false) &&
            PromptPattern().IsMatch(CaptureSnapshot()))
        {
            ConnectNotice = "SSH 已连接，设备已在命令提示符下。";
            MarkCliReady("设备已在命令提示符下");
            _log.Info(ConnectNotice);
            return;
        }

        // 没等到就发一次回车唤醒（很多设备要收到第一个字符才吐提示符）。
        try
        {
            await WriteCoreAsync(_settings.LineEnding, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"SSH 唤醒回车发送失败：{ex.Message}");
        }

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PromptPattern().IsMatch(CaptureSnapshot()))
            {
                ConnectNotice = "SSH 已连接，已看到命令提示符。";
                MarkCliReady("已看到命令提示符");
                _log.Info(ConnectNotice);
                return;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        ConnectNotice = ConnectionDiagnostics.Format(
            "CLI-04",
            "SSH 已连接，但没有看到命令提示符",
            "SSH 认证已经通过（连接是好的），但在超时前没看到 > 或 # 提示符。",
            new[]
            {
                "在 CLI 终端里按一下回车：正常设备会回一个提示符（如 Ruijie#）。",
                "设备登录后可能要求“首次登录修改密码”或确认免责声明 —— 在 CLI 里按提示输入即可。",
                "确认这个账号有 CLI 权限（锐捷：privilege level 15；只给 exec/查看权限时看不到配置模式的提示符）。",
                "如果设备只允许单会话，先在别处退出已登录的 SSH/Telnet 会话。",
            });
        MarkCliNotReady("SSH 已连接，但未看到命令提示符");
        _log.Info(ConnectNotice);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        // 与 Telnet 一致：严格 UTF-8 → 失败回退 GB18030，避免真机中文变乱码。
        var decoder = new Helpers.DeviceTextDecoder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var shell = _shell;
            if (shell is null)
            {
                return;
            }

            int read;
            try
            {
                read = await shell.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log.Warn("SSH 读取中断", ex);
                }

                return;
            }

            if (read <= 0)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    SetState(DeviceConnectionState.Error, "SSH 会话已被对端关闭。");
                }

                return;
            }

            var decoded = decoder.Decode(buffer.AsSpan(0, read).ToArray());
            if (decoded.Length > 0)
            {
                AppendOutput(decoded);
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[>#]\s*$")]
    private static partial System.Text.RegularExpressions.Regex PromptPattern();
}
