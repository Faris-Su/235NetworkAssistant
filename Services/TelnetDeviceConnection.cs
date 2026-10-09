using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// Telnet 连接实现（原始 socket + 最小 Telnet 协商）。
/// 只依赖 System.Net.Sockets，不引入任何第三方 Telnet 库。
/// </summary>
public sealed partial class TelnetDeviceConnection : DeviceConnectionBase
{
    private const byte Iac = 255;
    private const byte Sb = 250;
    private const byte Se = 240;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    /// <summary>
    /// NAWS（RFC 1073，选项号 31）：把**窗口尺寸**告诉设备。
    ///
    /// 为什么只放行这一个（借鉴 PuTTY）：设备靠它决定"一页放多少行、一行放多少字符" ——
    /// 不协商时锐捷默认按 80×24 处理，`show running-config` 这类长输出就会**又折行又频繁翻页**；
    /// 我们的 SSH 通道本来就传 200×60，这里统一成同一套尺寸，Telnet 才和 SSH 行为一致。
    /// 其余选项（ECHO/SGA/BINARY 等）仍一律回绝 —— 保持"纯透传、不做终端仿真"的既定口径。
    /// </summary>
    private const byte NawsOption = 31;

    private readonly TelnetConnectionSettings _settings;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private Task? _keepAliveLoop;

    public TelnetDeviceConnection(TelnetConnectionSettings settings, ILogService log)
        : base(log)
    {
        _settings = settings.Clone();
        _log = log;
        CommandTimeoutMs = _settings.CommandTimeoutMs;
        IdleQuietMs = _settings.IdleQuietMs;
    }

    public override DeviceConnectionKind Kind => DeviceConnectionKind.Telnet;

    /// <summary>Telnet 命令行结束符：默认 CRLF。</summary>
    protected override string CommandLineEnding => _settings.LineEnding;

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            throw new InvalidOperationException(
                "没有填写 Telnet 主机地址。请在【连接】页填写交换机管理 IP，或在地址簿里选一台交换机后点[填入 Telnet 参数]。");
        }

        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_settings.ConnectionTimeoutMs);

            await client.ConnectAsync(_settings.Host, _settings.Port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            // 超时会被自己的 timeoutCts 取消，原生文案是「已取消该项任务」，对使用者毫无意义，
            // 这里统一翻译成「哪个地址、连了多久、下一步查什么」。
            _log.Warn($"Telnet 连接失败：{_settings.Host}:{_settings.Port}", ex);
            throw new InvalidOperationException(
                ConnectionDiagnostics.DescribeTelnetFailure(
                    _settings.Host,
                    _settings.Port,
                    _settings.ConnectionTimeoutMs,
                    ex),
                ex);
        }

        if (client is null)
        {
            throw new InvalidOperationException("Telnet 客户端未能创建。");
        }

        _client = client;
        _stream = client.GetStream();
        ManagementAddress = $"{_settings.Host}:{_settings.Port}";
        _readLoop = Task.Run(() => ReadLoopAsync(LifetimeToken), CancellationToken.None);
        _log.Info($"Telnet 已连接：{ManagementAddress}");

        // 主动协商窗口尺寸：按 Telnet 的标准顺序 **先 WILL NAWS、再发 SB 尺寸**
        // （设备若不认这个选项会回 DONT，我们就不再打扰它；认的话就能拿到 200×60 的页宽）。
        try
        {
            await WriteBytesAsync(new byte[] { Iac, Will, NawsOption }, cancellationToken).ConfigureAwait(false);
            await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"发送 NAWS 窗口尺寸失败（忽略）：{ex.Message}");
        }

        // 空闲保活：每 N 秒发一个 Telnet NOP（IAC NOP），防止"连上后放着一动不动"被 VTY/隧道回收。
        if (_settings.KeepAliveSeconds > 0)
        {
            _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(LifetimeToken), CancellationToken.None);
            _log.Info($"Telnet 保活已开启：每 {_settings.KeepAliveSeconds} 秒发一个 NOP");
        }

        // TCP 已经建立：从这一刻起就是“已连接”，后面的登录/提示符只影响 CLI 是否就绪。
        MarkTransportEstablished();

        if (!_settings.AutoLogin)
        {
            MarkCliReady("未启用自动登录（可在 CLI 里手动输入）");
            return;
        }

        MarkAuthenticating();
        try
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消：按未就绪处理，但连接本身保留。
            MarkCliNotReady("登录已被取消");
            ConnectNotice = "已连接，但自动登录被取消；可在 CLI 里手动输入。";
            return;
        }
        catch (Exception ex)
        {
            // 登录阶段失败不能升级成“连接失败”：底层连接仍然有效。
            _log.Warn("自动登录出错（保留连接）", ex);
            MarkCliNotReady($"登录出错：{ex.Message}");
            ConnectNotice = $"已连接，但自动登录出错：{ex.Message}；可在 CLI 里手动输入。";
        }
        finally
        {
            // 登录流程结束（成功 / 超时 / 取消都算）：之后设备再吐提示符时，
            // 允许由输出线程把 CLI 状态补成“就绪”。
            MarkAutoLoginFinished();
        }
    }

    protected override async Task WriteCoreAsync(string text, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Telnet 连接未建立。");
        // 发出去的文本也要考虑编码：真机（RGOS）用 GBK 处理中文，按 UTF-8 发过去设备收到的是乱码。
        // 纯 ASCII 两种编码一致，所以只在含非 ASCII 时按 GB18030（覆盖 GBK）发送。
        var bytes = text.All(static c => c < 0x80)
            ? Encoding.ASCII.GetBytes(text)
            : Helpers.DeviceTextDecoder.FallbackEncoding.GetBytes(text);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            _stream?.Dispose();
            _client?.Close();
        }
        catch (Exception ex)
        {
            _log.Warn("关闭 Telnet 连接时出现异常", ex);
        }
        finally
        {
            _stream = null;
            _client = null;
        }

        // 等读取循环真正退出（最多 1 秒）：否则断开后还可能往终端补一段输出。
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

        var keepAlive = _keepAliveLoop;
        if (keepAlive is not null)
        {
            try
            {
                await keepAlive.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上：超时/异常都不影响断开
            }
            finally
            {
                _keepAliveLoop = null;
            }
        }
    }

    /// <summary>空闲保活循环：定时发 IAC NOP（255,241）—— 终端上不可见，只用来"喂"住会话。</summary>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.KeepAliveSeconds));
        var nop = new byte[] { Iac, 241 };
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await WriteBytesAsync(nop, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _log.Debug($"Telnet 保活循环结束：{ex.Message}");
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        // ⚠️ 登录阶段的时间预算**不能**直接套用"连接超时"（那是给 TCP 连接用的）。
        //    2026-09-25 用裸 TCP 逐字节量过现场那台 目标交换机（经 UU 隧道），结论：
        //      · banner / `Username:` 有时 0.5 秒就到，有时**要 30 秒**才被隧道送过来；
        //      · 用户名回显与 `Password:` 提示之间还会停 5~8 秒；
        //      · 隧道还会把提示符压 30 秒后送达，设备那边的输入计时器早就过了 →
        //        它回一句 `% Username:  timeout expired!` 再往下走（见下面的超时分支）。
        //    旧逻辑拿 8 秒的"连接超时"当登录预算，在上述情况下必然"用户名刚发出去、设备还没轮到回话"就放弃，
        //    用户看到的就是 CLI-02「已发送登录信息，但在超时前没有看到命令提示符」。
        //    现在：等提示符给 30 秒、等凭据回话给 15 秒，**任何新输出都会续期**，整个登录最多 75 秒。
        //   2026-09-25 二轮实测（work/telnet_human_probe.js，同一条隧道，隔着 30 秒才收到一句）：
        //     设备的输出会以**约 30 秒**为间隔成批送达（`Password:` → `% Password: timeout expired!` → `Username:`），
        //     所以每个等待窗口都要 ≥45 秒，否则永远差一步。
        const int PromptWaitFloorMs = 45000;
        const int CredentialWaitFloorMs = 45000;
        const int LoginHardLimitMs = 150000;
        var budgetMs = Math.Max(2000, _settings.ConnectionTimeoutMs);
        var promptWaitMs = Math.Max(budgetMs, PromptWaitFloorMs);
        var credentialWaitMs = Math.Max(budgetMs, CredentialWaitFloorMs);
        var trace = System.Diagnostics.Stopwatch.StartNew();
        var hardDeadline = DateTime.UtcNow.AddMilliseconds(LoginHardLimitMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(promptWaitMs);
        var lastLength = 0;

        // 阶段 1：等设备开口。**在它说第一句话之前，一个字节都不往回灌**。
        //   抓包实证（2026-09-25，work/snapshots/tcp-proxy-log.txt）：在 `Username:` 提示符上补发的回车
        //   会被设备当成"空用户名"提交，设备随即重印 `Username:` 并回 `% Username: timeout expired!`，
        //   整条登录从此乱套（现场留档里那排 `Username:` 就是这么来的）。
        var progressTick = 0;
        while (CaptureSnapshot().Length == 0 && DateTime.UtcNow < deadline && DateTime.UtcNow < hardDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++progressTick % 38 == 0)
            {
                // 每 ~3 秒把"还在等"写到状态栏：隧道/跨网下等 30 秒是正常的，不能让用户以为卡死了。
                MarkAuthenticating($"已连接，正在等设备应答…（已等 {trace.Elapsed.TotalSeconds:F0} 秒；隧道/跨网会慢）");
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        // ⚠️ 2026-09-25 二轮结论：**登录前不补回车**（旧实现是"最多 3 次、每 1.5 秒一次"）。
        //   ① 裸 TCP 探针实证：这台 目标交换机 的 `Username:` 是自己会印的，之前"只发 banner 不回车就不出提示符"
        //      是误判 —— 真实原因是隧道把提示符压后了 30 秒才送达（work/telnet_human_probe.js 里能看到
        //      30.54s 才出现 `Username:`）。
        //   ② 回车若正好落在 `Username:` 提示符上，设备会把它当成**空用户名**提交，随后重印 `Username:`
        //      并回 `% Username: timeout expired!`，整条登录从此乱套（tcp-proxy-log.txt 里有原始字节）。
        //   所以现在改成"只等、不灌"，把耐心放在时间预算上；确需回车的设备，CLI-01 提示里会告诉用户手动敲一下回车。
        _log.Debug($"[登录] {trace.ElapsedMilliseconds}ms 设备已开口（{CaptureSnapshot().Length} 字符），按「不灌回车、只等提示符」策略继续");

        // 预步骤（等设备开口 + 补回车）**不能吃掉凭据循环的预算**：这里重新给足一个完整窗口。
        deadline = DateTime.UtcNow.AddMilliseconds(promptWaitMs);
        _log.Debug($"[登录] {trace.ElapsedMilliseconds}ms 进入凭据循环：已收 {CaptureSnapshot().Length} 字符｜"
                   + $"等提示符 {promptWaitMs}ms／等凭据回话 {credentialWaitMs}ms（隧道/跨网下这些是「至少」值）");

        var snapshot = CaptureSnapshot();

        // 设备已经在命令提示符上（不需要认证、或上一位管理员没退出）→ 直接跳过，不再白等。
        if (PromptPattern().IsMatch(snapshot))
        {
            ConnectNotice = "设备已在命令提示符下，已跳过自动登录。";
            MarkCliReady("设备已在命令提示符下");
            _log.Info(ConnectNotice);
            return;
        }

        if (snapshot.Length == 0)
        {
            ConnectNotice = ConnectionDiagnostics.Format(
                "CLI-01",
                "没有看到登录提示符",
                "设备没有主动发送登录提示符（也没有出现命令提示符）。",
                new[]
                {
                    "在 CLI 终端里按一下回车：正常设备会回一个提示符（如 Ruijie#）。",
                    "如果出现 Username/Password 提示，直接在这里手动输入账号密码即可登录。",
                    "如果一直没有任何输出，检查 Console 线的口是否插对、波特率是否为 9600。",
                });
            MarkCliNotReady("设备没有主动发送登录提示符");
            _log.Info(ConnectNotice);
            return;
        }

        var cursor = 0;
        var sentUsername = false;
        var sentPassword = false;
        var sawLoginPrompt = false;
        var loggedIn = false;
        // 登录失败重试（2026-09-25 现场定位）：慢链路（隧道 RTT 2~3 秒）上，
        // 设备可能已经打印 Password: 却在我们的密码到达前超时（设备回 `% Password: timeout expired!`），
        // 之后它会**重新索要 Username:**。旧实现是一次性的（sentUsername/sentPassword 标记住就不再看），
        // 于是"用户名/密码再来一遍"被无视 → 登录永远不完成，而外层还会接着发 `enable`，
        // 结果 `enable` 落进 Username 提示（现场留档里就是 `Username:enable`），状态彻底乱掉。
        // 现在：**允许整组凭据重来一次**；看到设备明确的失败回显（Login invalid 等）就不重试，直接如实报错。
        var loginAttempts = 0;
        var loginRejected = false;
        var passwordMissing = false;
        var sawTimeoutExpired = false;
        var deviceSaidTimeout = string.Empty;
        var deviceSaid = string.Empty;
        // 凭据最多整组重发几次（隧道把提示符压后 30 秒时，第一组几乎注定赶不上设备的输入计时器）。
        const int MaxLoginAttempts = 3;

        bool DeviceSaysLoginRejected(string text) =>
            text.Contains("Login invalid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication fail", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("密码错误", StringComparison.Ordinal)
            || text.Contains("认证失败", StringComparison.Ordinal);

        // 用户名、密码、提示符共用一个"空闲预算"：只要设备还在吐数据就续期（与命令路径同一套思路）。
        while (DateTime.UtcNow < deadline && DateTime.UtcNow < hardDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var full = CaptureSnapshot();
            // 只要设备**还在吐数据**，就把登录等待续期（与命令路径同一套"空闲超时"思路）。
            // 现场 2026-09-25：经隧道时，设备的 banner/`Username:`/`Password:` 晚于一个超时周期才到，
            // 旧实现到点即放弃 → 之后设备反复打印的 `Username:` 再没人应答，用户只看到一排 `Username:`。
            if (full.Length != lastLength)
            {
                lastLength = full.Length;
                deadline = DateTime.UtcNow.AddMilliseconds(credentialWaitMs);
                _log.Debug($"[登录] {trace.ElapsedMilliseconds}ms 收到新输出（共 {full.Length} 字符）→ 续期 {credentialWaitMs}ms");
            }

            if (PromptPattern().IsMatch(full))
            {
                loggedIn = true;
                break;
            }

            var tail = full.Length > cursor ? full[cursor..] : string.Empty;

            // 设备明确说"登录失败" → 不再重试（再试也是白试，还可能触发账号锁定），如实记下原因。
            if (!loginRejected && DeviceSaysLoginRejected(tail))
            {
                loginRejected = true;
                deviceSaid = tail.Trim();
            }

            // 设备自曝"你的输入来晚了"：`% Username:  timeout expired!` / `% Password:  timeout expired!`
            // 现场实证（2026-09-25，裸 TCP 探针 work/telnet_reactive_probe.js）：
            //   隧道把 `Username:` 压了 30 秒才送到我们这边，设备那边的输入计时器早就过了；
            //   我们的用户名一到，它先回这句，然后**顺着自己的流程往下印 `Password:`**。
            // 这时候千万不能把那个 `Password:` 当成"该发密码了" —— 那是给"空用户名"用的提示，
            //   密码发过去只会被设备当用户名吃掉（现场会话里可观察到密码文本被回显为用户名）。
            // 正确动作：整组凭据作废，**立刻重发用户名**，并把游标推过那个误导性的 `Password:`。
            if (sentUsername && TimeoutExpiredPattern().IsMatch(tail))
            {
                sawTimeoutExpired = true;
                deviceSaidTimeout = tail.Trim();
                loginAttempts++;
                _log.Info($"[登录] {trace.ElapsedMilliseconds}ms 设备回「{deviceSaidTimeout}」→ 作废本轮凭据，重发用户名（第 {loginAttempts} 组）");
                if (loginAttempts > MaxLoginAttempts || string.IsNullOrWhiteSpace(_settings.Username))
                {
                    _log.Info("[登录] 重试用尽或用户名是空的，停止自动登录（连接保留，可手动登录）。");
                    break;
                }

                cursor = full.Length;
                sentUsername = true;
                sentPassword = false;
                await WriteCoreAsync(_settings.Username + "\r\n", cancellationToken).ConfigureAwait(false);
                deadline = DateTime.UtcNow.AddMilliseconds(credentialWaitMs);
                continue;
            }

            if (!sentUsername && !string.IsNullOrWhiteSpace(_settings.Username) && LoginPattern().IsMatch(tail))
            {
                sawLoginPrompt = true;
                if (loginAttempts == 0)
                {
                    loginAttempts = 1;
                }

                await WriteCoreAsync(_settings.Username + "\r\n", cancellationToken).ConfigureAwait(false);
                sentUsername = true;
                cursor = CaptureSnapshot().Length;
                // 走了这一步就要重新计时：慢链路（隧道 RTT 2~3 秒）上，设备回 `Password:` 往往要再等 3~8 秒，
                // 共用同一条 deadline 会出现"用户名发出去了、`Password:` 也回来了，但窗口已经关了"（现场 19:18 复现：
                // 会话只剩 `<username><CR><LF>Password:` 15 字节，程序已报 CLI-02）。
                deadline = DateTime.UtcNow.AddMilliseconds(credentialWaitMs);
                _log.Debug($"[登录] {trace.ElapsedMilliseconds}ms 已发送用户名（第 {loginAttempts} 组），等 Password:/提示符 {credentialWaitMs}ms");
                continue;
            }

            if (!sentPassword && !string.IsNullOrWhiteSpace(_settings.Password) && PasswordPattern().IsMatch(tail))
            {
                await WriteCoreAsync(_settings.Password + "\r\n", cancellationToken).ConfigureAwait(false);
                sentPassword = true;
                cursor = CaptureSnapshot().Length;
                deadline = DateTime.UtcNow.AddMilliseconds(credentialWaitMs);   // 同理：等提示符也要重新计时
                _log.Debug($"[登录] {trace.ElapsedMilliseconds}ms 已发送密码，等命令提示符 {credentialWaitMs}ms");
                continue;
            }

            // 设备在要密码，但**我们手里根本没密码** → 立刻停下来说清楚，不要白等一个超时。
            // 现场（2026-09-25）：密码出于安全不落盘，用户只填了用户名就点连接 →
            // 程序只发用户名、之后什么都不发，设备反复重问 `Username:`，屏幕上一排 `Username:`，
            // 用户以为"输入少显示了"。其实缺的就是密码框那一格。
            if (!sentPassword && string.IsNullOrWhiteSpace(_settings.Password) && PasswordPattern().IsMatch(tail))
            {
                passwordMissing = true;
                break;
            }

            // 已经发过用户名，设备**又在问 Username:** ⇒ 上一轮没成（通常就是慢链路把密码的时机错过了）。
            // 允许整组凭据重来一次；失败过（或已经重试过）就停手并如实报错。
            if (sentUsername && sentPassword && !loginRejected && loginAttempts < MaxLoginAttempts
                && LoginPattern().IsMatch(tail))
            {
                loginAttempts++;
                sentUsername = false;
                sentPassword = false;
                // ⚠️ 这里**不能**把 cursor 推到提示符之后：否则下一轮 tail 里就看不到这个 `Username:`，
                //    凭据永远发不出去（第一版就是这么写的，自检直接抓到）。
                _log.Info($"[登录] {trace.ElapsedMilliseconds}ms 设备重新索要用户名（第 {loginAttempts} 组），按现场慢链路情形重发凭据。");
                continue;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        _log.Debug($"[登录] 凭据循环结束：{trace.ElapsedMilliseconds}ms｜收到 {CaptureSnapshot().Length} 字符｜"
                   + $"loggedIn={loggedIn}｜看到登录提示={sawLoginPrompt}｜凭据组数={loginAttempts}｜"
                   + $"缺密码={passwordMissing}｜被拒={loginRejected}｜设备报输入超时={sawTimeoutExpired}");

        ConnectNotice = loggedIn
            ? "自动登录完成，已看到命令提示符。"
            : passwordMissing
                ? ConnectionDiagnostics.Format(
                    "CLI-04",
                    "自动登录缺少密码（密码框是空的）",
                    "设备已经在询问密码，但 Telnet 面板的密码框是空的。"
                    + "密码出于安全**不落盘**，每次连接都要重新输入一次。",
                    new[]
                    {
                        "到【连接】页 → 展开 Telnet 面板 → 在「密码」里填一次 → 再点[连接 Telnet]。",
                        "或者保持现状，直接在下面的 Username:/Password: 提示里手动输入账号密码。",
                    })
            : sawLoginPrompt || sawTimeoutExpired
                ? ConnectionDiagnostics.Format(
                    "CLI-02",
                    loginRejected && !sawTimeoutExpired
                        ? "设备拒绝登录（账号或密码不对）"
                        : loginRejected
                            ? "登录失败：设备先判「输入超时」再判登录失败"
                            : "登录没有完成（没等到命令提示符）",
                    loginRejected && !sawTimeoutExpired
                        ? $"设备明确返回了拒绝信息：{deviceSaid}"
                        : loginRejected
                            // 关键区分（2026-09-25 实测）：设备先回 `% Password: timeout expired!` 再回 `% Login invalid`，
                            // 说明**不是**账号密码错，而是我们的输入没赶上设备那一步（链路把提示符压后了 ~30 秒）。
                            // 这条和"密码打错了"的处置完全不同，不能糊成一句"账号或密码不对"。
                            ? $"设备先回「{deviceSaidTimeout}」，紧接着把这一轮判成登录失败（{deviceSaid}）。"
                              + "**这不代表账号密码错**：是链路太慢 —— 设备的提示符到我们这儿时，它自己的输入计时器已经过了，"
                              + "我们回过去的凭据只能算下一轮。"
                        : sawTimeoutExpired
                            ? $"设备回过「{deviceSaidTimeout}」：它那边的输入计时器已经过期 —— 提示符是**迟到**送到"
                              + "我们这边的（隧道/跨网把 banner、`Username:` 压后几十秒才送达，2026-09-25 实测最慢 30 秒），"
                              + $"凭据再发也赶不上设备那一步。已整组重发 {loginAttempts} 组仍没进到命令提示符。"
                            : $"已发送登录信息（共 {loginAttempts} 组凭据），但在超时前没有看到命令提示符。"
                              + "慢链路（隧道/跨网）上常见：设备打印 Password: 后等不到输入就超时，然后重新索要 Username。",
                    new[]
                    {
                        "在 CLI 终端里手动输入一次用户名和密码；设备回过 `timeout expired` 的话，"
                        + "把账号和密码**连着敲完**（别等提示符）：设备按行读，先到的那行会排在缓冲区里等提示符。",
                        "设备回过 `% Username: timeout expired!` 说明链路延迟已超过设备的输入计时器："
                        + "手动登录更稳，或换到交换机本机/近距离网络（Console/直连）再连。",
                        "设备可能未开启 Telnet 登录方式，或需要先输入 enable 提权。",
                    })
                : ConnectionDiagnostics.Format(
                    "CLI-01",
                    "没有识别到登录提示符",
                    "未检测到用户名/密码提示符（设备可能不需要登录，或提示形式与预期不同）。",
                    new[]
                    {
                        "直接在 CLI 终端里手动输入用户名/密码（或直接输入命令）。",
                        "设备一出现 Host# 提示符，本工具会自动把状态切成“已连接”。",
                    });

        if (loggedIn)
        {
            MarkCliReady("自动登录完成，已看到命令提示符");
        }
        else
        {
            MarkCliNotReady(
                sawLoginPrompt || sawTimeoutExpired
                    ? "已发送登录信息，但在超时前没有看到命令提示符"
                    : "未检测到用户名/密码提示符");
        }

        _log.Info($"Telnet 登录结果：{ConnectNotice}");
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var raw = new byte[8192];
        var pending = new List<byte>(raw.Length * 2);
        // 设备文本按"严格 UTF-8 → 失败回退 GB18030"解码（真机中文是 GBK；写死 UTF-8 会把
        // 中文描述/VLAN 名显示成乱码，而且**会话留档会被一起写坏**）。见 DeviceTextDecoder。
        var decoder = new Helpers.DeviceTextDecoder();

        while (!cancellationToken.IsCancellationRequested)
        {
            NetworkStream? stream = _stream;
            if (stream is null)
            {
                return;
            }

            int read;
            try
            {
                read = await stream.ReadAsync(raw.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log.Warn("Telnet 读取中断", ex);
                }

                return;
            }

            if (read <= 0)
            {
                SetState(DeviceConnectionState.Error, "对端已关闭连接。");
                return;
            }

            pending.Clear();
            var index = 0;
            while (index < read)
            {
                var value = raw[index];
                if (value != Iac)
                {
                    pending.Add(value);
                    index++;
                    continue;
                }

                // Telnet 协商：本工具只做终端透传，不启用任何选项。
                if (index + 1 >= read)
                {
                    break;
                }

                var command = raw[index + 1];
                switch (command)
                {
                    case Will:
                    case Wont:
                    case Do:
                    case Dont:
                        if (index + 2 >= read)
                        {
                            index = read;
                            break;
                        }

                        await RespondAsync(command, raw[index + 2], cancellationToken).ConfigureAwait(false);
                        index += 3;
                        break;
                    case Sb:
                        while (index < read - 1 && !(raw[index] == Iac && raw[index + 1] == Se))
                        {
                            index++;
                        }

                        index += 2;
                        break;
                    default:
                        index += 2;
                        break;
                }
            }

            if (pending.Count == 0)
            {
                continue;
            }

            var decoded = decoder.Decode(pending.ToArray());
            if (decoded.Length > 0)
            {
                AppendOutput(decoded);
            }
        }
    }

    private async Task RespondAsync(byte command, byte option, CancellationToken cancellationToken)
    {
        // NAWS 是唯一放行的选项：设备问"你的窗口多大"，我们答应并立刻回尺寸
        if (option == NawsOption)
        {
            var reply = command switch
            {
                Do => new[] { Iac, Will, NawsOption },
                Dont => new[] { Iac, Wont, NawsOption },
                _ => Array.Empty<byte>(),
            };

            if (reply.Length > 0)
            {
                await WriteBytesAsync(reply, cancellationToken).ConfigureAwait(false);
            }

            if (command == Do)
            {
                // 记一笔：设备**接受了** NAWS（窗口尺寸协商）。有些型号不认这个选项，
                // 日志里有没有这行就能一眼看出该型号到底支不支持（验收用，也是现场排查依据）。
                _log.Info("设备接受了 NAWS：已把窗口尺寸 200×60 告诉设备");
                await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var response = command switch
        {
            Will => new[] { Iac, Dont, option },
            Do => new[] { Iac, Wont, option },
            Wont => new[] { Iac, Dont, option },
            Dont => new[] { Iac, Wont, option },
            _ => Array.Empty<byte>(),
        };

        if (response.Length == 0)
        {
            return;
        }

        await WriteBytesAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送 NAWS 子协商：IAC SB 31 &lt;列高字节&gt;&lt;列低字节&gt;&lt;行高字节&gt;&lt;行低字节&gt; IAC SE。</summary>
    private Task SendWindowSizeAsync(CancellationToken cancellationToken)
    {
        var columns = (ushort)Math.Clamp(_settings.TerminalColumns, 40, 512);
        var rows = (ushort)Math.Clamp(_settings.TerminalRows, 10, 200);
        var payload = new[]
        {
            Iac, Sb, NawsOption,
            (byte)(columns >> 8), (byte)(columns & 0xFF),
            (byte)(rows >> 8), (byte)(rows & 0xFF),
            Iac, Se,
        };
        return WriteBytesAsync(payload, cancellationToken);
    }

    /// <summary>在写锁保护下把裸字节发出去（协商应答与 NAWS 共用）。</summary>
    private async Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || bytes.Length == 0)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Telnet 协商字节发送失败：{ex.Message}");
        }
    }

    [GeneratedRegex(@"(?i)(username|login)\s*[:：]")]
    private static partial Regex LoginPattern();

    [GeneratedRegex(@"(?i)password\s*[:：]")]
    private static partial Regex PasswordPattern();

    /// <summary>
    /// 设备自曝"你的输入来晚了"：`% Username:  timeout expired!` / `% Password:  timeout expired!`。
    /// 现场（2026-09-25，目标交换机 经 UU 隧道）：隧道把提示符压后 30 秒才送达，设备的输入计时器早就过了，
    /// 这时它会把紧跟其后的 `Password:` 当成"空用户名"的下一步 —— 我们必须作废本轮凭据重来，不能照着发密码。
    /// </summary>
    [GeneratedRegex(@"(?i)(username|password)[^\r\n]{0,20}timeout\s*expired|输入超时")]
    private static partial Regex TimeoutExpiredPattern();

    [GeneratedRegex(@"[>#]\s*$")]
    private static partial Regex PromptPattern();
}
