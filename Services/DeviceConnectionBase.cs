using System.Text;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 连接实现的公共部分：状态机、输出缓冲、批量发送、取消与资源释放。
/// 子类只负责真正的 I/O（打开端口 / 建立 Socket / 写数据）。
/// </summary>
public abstract class DeviceConnectionBase : IDeviceConnection
{
    /// <summary>单连接最大累积缓冲，防止设备连续输出导致内存无限增长。</summary>
    /// <summary>
    /// 单条命令的输出缓冲上限（字符）。核心机的 `show running-config` 常达 MB 级，旧值 4 MB
    /// 在备份时会把**开头**静默丢掉（保留尾部提示符 → 连"没等到提示符"的兜底提示都不会触发），
    /// 备份文件看起来完全正常却是残缺的。现在：① 上限提到 16 MB；② 真丢过就记下来
    /// （<see cref="LastCommandDroppedChars"/>）并在返回文本开头插一行 `!` 注释警告，绝不闷着。
    /// </summary>
    private const int DefaultMaxCaptureChars = 16 * 1024 * 1024;

    /// <summary>
    /// 测试用：把输出缓冲上限临时调小，用来验证"截断必须报出来"这条链路（0 = 用默认值）。
    /// 真机上要造一份 16 MB 的配置才能触发，自检里没法这么干。
    /// </summary>
    internal static int CaptureLimitOverride { get; set; }

    private static int MaxCaptureChars =>
        CaptureLimitOverride > 0 ? CaptureLimitOverride : DefaultMaxCaptureChars;

    /// <summary>识别提示符用的滚动尾部长度（够容纳最长的主机名 + 模式后缀）。</summary>
    private const int PromptTailLength = 160;

    private readonly StringBuilder _capture = new();
    private readonly object _captureSync = new();

    /// <summary>
    /// <see cref="_capture"/> 的缓存快照（必须在 <see cref="_captureSync"/> 里读写）。
    /// 为什么需要：命令执行时每 40ms 要看一次输出（提示符/翻页判定），而 `StringBuilder.ToString()`
    /// 会把整个缓冲拷一份 —— 大表/大配置下（上限 16 MB）等于每 40ms 拷一次几 MB，既拖慢读线程也喂 GC。
    /// 只有"真的追加过数据"时才失效重建。
    /// </summary>
    private string? _captureSnapshot;

    /// <summary>子类传入的日志（无日志环境可为空）。断开超时这类"必须留痕"的事走它。</summary>
    protected ILogService? Log { get; }

    protected DeviceConnectionBase(ILogService? log = null) => Log = log;

    /// <summary>本次命令因超出缓冲上限而被丢掉的字符数（丢的是开头；0 = 完整）。</summary>
    private long _captureDroppedChars;
    private DateTime _lastOutputUtc = DateTime.UtcNow;
    private CancellationTokenSource? _lifetimeCts;
    private bool _disposed;
    private string _promptTail = string.Empty;

    public abstract DeviceConnectionKind Kind { get; }

    public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;

    public string? DeviceName { get; protected set; }

    public string? ManagementAddress { get; protected set; }

    public string? LastError { get; private set; }

    /// <summary>连接过程提示（自动登录结果等），由具体连接实现填写。</summary>
    public string? ConnectNotice { get; protected set; }

    /// <summary>最近一条命令自动续页的次数（&gt;0 表示输出被分页过，已自动翻完）。</summary>
    public int LastCommandPageCount { get; private set; }

    /// <summary>最近一条命令是否在设备提示符处正常结束。</summary>
    public bool LastCommandEndedAtPrompt { get; private set; }

    /// <summary>最近一条命令是否触发了分页上限保护（避免无限发空格）。</summary>
    public bool LastCommandHitPageLimit { get; private set; }

    /// <summary>CLI 是否已经确认可以交互（看到提示符 / 登录完成）。</summary>
    public bool CliReady { get; private set; }

    /// <summary>CLI 初始化维度的一句话说明。</summary>
    public string? CliStateText { get; private set; }

    /// <summary>当前权限级别（提示符判定）。CLI 与所有图形化页面共用这一份。</summary>
    public Models.DevicePrivilegeLevel PrivilegeLevel { get; private set; } = Models.DevicePrivilegeLevel.Unknown;

    /// <summary>权限提示：成功原因或失败原因（失败也**不影响连接**）。</summary>
    public string? PrivilegeNotice { get; private set; }

    public event EventHandler? PrivilegeChanged;

    /// <summary>命令行的行结束符（Serial 一般为 CR，Telnet 为 CRLF）。</summary>
    protected abstract string CommandLineEnding { get; }

    /// <summary>底层连接（TCP / 串口）是否已经建立。建立之后的异常不再算“连接失败”。</summary>
    protected bool TransportEstablished { get; private set; }

    /// <summary>子类在底层连接真正建立后调用：这才是“已连接”。</summary>
    protected void MarkTransportEstablished()
    {
        TransportEstablished = true;
        SetState(DeviceConnectionState.Connected, "已连接");
    }

    /// <summary>进入自动登录阶段（底层已连接，CLI 还没确认）。</summary>
    protected void MarkAuthenticating(string text = "正在自动登录…")
    {
        AutoLoginInProgress = true;
        CliStateText = text;
        if (TransportEstablished && CanAdvanceCliState)
        {
            SetState(DeviceConnectionState.Authenticating, text);
        }
    }

    /// <summary>
    /// 自动登录流程结束（成功或失败都要调用）。结束后，设备输出再带回提示符时，
    /// 仍然允许由 <see cref="AppendOutput"/> 把 CLI 状态补成“就绪”。
    /// </summary>
    protected void MarkAutoLoginFinished() => AutoLoginInProgress = false;

    /// <summary>
    /// 是否正在自动登录。自动登录期间「CLI 就绪」只由登录流程确认：
    /// 两边都写会让最终提示取决于线程调度（表现为同一台设备提示时好时坏）。
    /// </summary>
    protected bool AutoLoginInProgress { get; private set; }

    /// <summary>CLI 已确认可以交互。</summary>
    protected void MarkCliReady(string text = "CLI 就绪")
    {
        CliReady = true;
        CliStateText = text;
        if (TransportEstablished && CanAdvanceCliState)
        {
            SetState(DeviceConnectionState.Ready, text);
        }
    }

    /// <summary>底层连接在，但 CLI 没确认可用（登录超时 / 设备无输出）。不改变“已连接”这个事实。</summary>
    protected void MarkCliNotReady(string reason)
    {
        CliReady = false;
        CliStateText = reason;
        if (TransportEstablished && CanAdvanceCliState)
        {
            SetState(DeviceConnectionState.Connected, reason);
        }
    }

    /// <summary>连接已经掉了（Error）或已断开时，CLI 阶段的结论不能再把状态改回“已连接”。</summary>
    private bool CanAdvanceCliState =>
        State is not (DeviceConnectionState.Error or DeviceConnectionState.Disconnected);

    /// <summary>补充连接提示：已经有提示时不覆盖，避免把更具体的信息（如登录结果）冲掉。</summary>
    public void ReportConnectNotice(string notice)
    {
        if (!string.IsNullOrWhiteSpace(notice) && string.IsNullOrWhiteSpace(ConnectNotice))
        {
            ConnectNotice = notice;
        }
    }

    public void ReportCliNotReady(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        ConnectNotice = reason;
        MarkCliNotReady(reason);
    }

    /// <summary>更新设备名（由上层从设备输出中解析得到）。</summary>
    public void SetDeviceIdentity(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) ||
            string.Equals(DeviceName, deviceName, StringComparison.Ordinal))
        {
            return;
        }

        DeviceName = deviceName.Trim();
        DeviceIdentityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一条命令的最长等待时间。</summary>
    protected int CommandTimeoutMs { get; set; } = 15_000;

    /// <summary>多久没有新数据就认为命令输出结束。</summary>
    protected int IdleQuietMs { get; set; } = 800;

    /// <summary>
    /// 判定"命令结束"时，回显里查找命令文本的探测窗口（只看开头这么多字符 —— 回显一定在最前面，
    /// 这样每 40ms 的轮询不会去全量扫描可能几百 KB/几 MB 的缓冲）。
    /// </summary>
    private const int EchoProbeLength = 2048;

    /// <summary>
    /// 没看到回显时，"仅凭提示符就判命令结束"至少要等这么久。
    /// 用途：兼容关闭回显的设备；同时避免把**迟到的旧提示符**当成本次命令结束
    /// （2026-09-24 修的"首刷偶尔空表"就是这个）。取值权衡见 <c>SendAsync</c> 里的注释。
    /// </summary>
    private static readonly TimeSpan PromptOnlyGrace = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 末尾那行提示符**之前**是否已经有非空内容行。
    ///
    /// 用途：区分"只有提示符"（可能是迟到的旧提示符，也可能设备关回显）与"真的回了一行以上输出"。
    /// 只看调用方给的尾部窗口（几千字符），不做全量扫描。
    /// </summary>
    private static bool HasContentLineBeforePrompt(string tail)
    {
        var lines = tail.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)   // 最后一行是提示符本身，不参与判断
        {
            if (lines[i].Trim('\r', ' ', '\t').Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    protected CancellationToken LifetimeToken => _lifetimeCts?.Token ?? CancellationToken.None;

    public event EventHandler<DeviceOutputEventArgs>? OutputReceived;

    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    public event EventHandler? DeviceIdentityChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State.IsTransportUp())
        {
            return;
        }

        _lifetimeCts?.Dispose();
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TransportEstablished = false;
        CliReady = false;
        CliStateText = null;
        AutoLoginInProgress = false;
        ResetCapture();
        SetState(DeviceConnectionState.Connecting, null);

        try
        {
            await ConnectCoreAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 底层已经连上之后再出错（登录、等提示符、读写出错）不算“连接失败”：
            // 保留连接，让用户能进 CLI 手动处理，只把原因写进提示。
            if (TransportEstablished)
            {
                CliReady = false;
                CliStateText = $"CLI 初始化未完成：{ex.Message}";
                ConnectNotice = ConnectionDiagnostics.Format(
                    "CLI-04",
                    "已连接，但 CLI 初始化出错",
                    $"{ex.Message}",
                    new[]
                    {
                        "连接本身是好的：可以直接进 CLI 页手动输入命令。",
                        "如果一直没反应，重新点一次[连接]或[断开]后重连。",
                    });
                SetState(DeviceConnectionState.Connected, ConnectNotice);
                return;
            }

            LastError = ex.Message;
            SetState(DeviceConnectionState.Error, ex.Message);
            await SafeShutdownAsync().ConfigureAwait(false);
            throw;
        }

        // 具体实现在 ConnectCoreAsync 里会推进到 Authenticating / Ready；
        // 如果它什么都没标记（例如没开自动登录），这里兜底为 Connected。
        if (State == DeviceConnectionState.Connecting)
        {
            SetState(DeviceConnectionState.Connected, "已连接");
        }
    }

    public async Task DisconnectAsync()
    {
        if (State == DeviceConnectionState.Disconnected)
        {
            return;
        }

        await SafeShutdownAsync().ConfigureAwait(false);
        TransportEstablished = false;
        CliReady = false;
        CliStateText = null;
        ResetPrivilege();
        SetState(DeviceConnectionState.Disconnected, "已断开");
    }

    /// <summary>断开/重连时把权限状态复位（不允许沿用上一次会话的权限）。</summary>
    private void ResetPrivilege()
    {
        var changed = PrivilegeLevel != Models.DevicePrivilegeLevel.Unknown || PrivilegeNotice is not null;
        PrivilegeLevel = Models.DevicePrivilegeLevel.Unknown;
        PrivilegeNotice = null;
        if (changed)
        {
            PrivilegeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 进入特权模式：enable →（若设备要求）发送密码 → 用提示符确认。
    /// 失败只返回原因，**不抛异常、不改连接状态**；密码不写日志、不回显。
    /// </summary>
    public async Task<Models.PrivilegeElevationResult> TryEnterPrivilegedModeAsync(
        string? enablePassword,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        if (PrivilegeLevel == Models.DevicePrivilegeLevel.Privileged)
        {
            return SetPrivilege(true, "当前已经是特权模式（#）。");
        }

        // ⚠️ **CLI 没就绪时绝不许发 `enable`**（2026-09-25 现场 bug）：
        //    慢链路（隧道 RTT 2~3 秒）上登录常常还没走完（设备停在 `Username:` / `Password:`），
        //    这时发出 `enable` 会被设备当成**用户名**吃掉（现场留档 `Username:enable`），
        //    把登录状态彻底搅乱 —— 用户随后手动输入也会"看着少字/不对"。
        //    正确做法：先让登录走完；登录没成功就如实报"CLI 未就绪"，不要往登录提示里塞命令。
        if (!CliReady)
        {
            return SetPrivilege(
                false,
                "CLI 还没有就绪（登录尚未完成），已**不发送** enable —— 请先在 CLI 里完成登录或重连一次。"
                + (string.IsNullOrWhiteSpace(CliStateText) ? string.Empty : $"（当前状态：{CliStateText}）"));
        }

        var timeoutMs = Math.Max(2000, CommandTimeoutMs);
        try
        {
            ResetCapture();
            await WriteCoreAsync("enable" + CommandLineEnding, cancellationToken).ConfigureAwait(false);

            await WaitForOutputAsync(
                text => CliPromptDetector.IsEnablePasswordPrompt(text) || CliPromptDetector.EndsWithPrompt(text),
                timeoutMs,
                cancellationToken).ConfigureAwait(false);

            var snapshot = CaptureSnapshot();
            if (CliPromptDetector.DetectPrivilegeLevel(snapshot) == Models.DevicePrivilegeLevel.Privileged)
            {
                return SetPrivilege(true, "设备无需 Enable 密码，已进入特权模式（#）。");
            }

            if (!CliPromptDetector.IsEnablePasswordPrompt(snapshot))
            {
                return SetPrivilege(
                    false,
                    "执行 enable 后设备既没有要求输入密码，也没有进入特权模式；可能是设备不接受 enable，或提示符格式不同。");
            }

            if (string.IsNullOrWhiteSpace(enablePassword))
            {
                return SetPrivilege(false, "设备要求 Enable 密码，但当前没有填写。请填写后重试。");
            }

            // 密码只发给设备：不写日志、不追加到任何缓冲（设备本身也不回显密码）
            await WriteCoreAsync(enablePassword + CommandLineEnding, cancellationToken).ConfigureAwait(false);

            await WaitForOutputAsync(
                text => CliPromptDetector.EndsWithPrompt(text),
                timeoutMs,
                cancellationToken).ConfigureAwait(false);

            var level = CliPromptDetector.DetectPrivilegeLevel(CaptureSnapshot());
            return level switch
            {
                Models.DevicePrivilegeLevel.Privileged =>
                    SetPrivilege(true, "已进入特权模式（#）。"),
                Models.DevicePrivilegeLevel.User =>
                    SetPrivilege(false, "Enable 密码不正确（设备仍停留在普通模式 >）。"),
                _ => SetPrivilege(false, "提交 Enable 密码后没有看到提示符，无法确认是否进入特权模式。"),
            };
        }
        catch (OperationCanceledException)
        {
            return SetPrivilege(false, "进入特权模式的操作已取消。");
        }
        catch (Exception ex)
        {
            return SetPrivilege(false, $"进入特权模式时出错：{ex.Message}");
        }
    }

    private Models.PrivilegeElevationResult SetPrivilege(bool succeeded, string reason)
    {
        PrivilegeNotice = reason;
        if (succeeded)
        {
            PrivilegeLevel = Models.DevicePrivilegeLevel.Privileged;
        }

        PrivilegeChanged?.Invoke(this, EventArgs.Empty);
        return succeeded
            ? Models.PrivilegeElevationResult.Success(reason)
            : Models.PrivilegeElevationResult.Failure(reason);
    }

    public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        ResetCapture();
        LastCommandPageCount = 0;
        LastCommandEndedAtPrompt = false;
        LastCommandHitPageLimit = false;
        await WriteCoreAsync(command + "\r\n", cancellationToken).ConfigureAwait(false);

        // 分页 + 提示符 + 空闲超时（不是“总时长”超时）：
        //   - 设备停在 --More-- → 发空格续页（不能发回车：回车是“下一行”）
        //   - 输出回到提示符 → 命令结束，立即返回
        //   - 长时间没有任何新数据（IdleTimeout）→ 认为结束/超时
        // 这样 show running-config 这种大输出不会因为“总时间超过 15 秒”被误判失败。
        var idleTimeout = TimeSpan.FromMilliseconds(Math.Max(2000, CommandTimeoutMs));
        var lastLengthAtSpace = -1;
        // 「本次命令的输出是否已经开始」：正常设备会回显命令本身；个别设备关回显，则用宽限期兜底。
        // 详见下面 2) 里的现场 bug 说明。
        var wroteAt = DateTime.UtcNow;
        var echoSeen = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);

            string snapshot;
            DateTime lastOutput;
            lock (_captureSync)
            {
                snapshot = _captureSnapshot ??= _capture.ToString();
                lastOutput = _lastOutputUtc;
            }

            // ⚠️ 判定只看**末尾一段**，不要把整份快照喂给这两个函数（2026-09-22 改）。
            // CliPager / CliPromptDetector 内部都要先做一次全量归一化
            //（ANSI 去码 + 退格扫描 + 换行替换 + 空行折叠，都是 O(缓冲长度)），
            // 而它们真正关心的只是"最后是不是分页提示 / 提示符"（各自只看末尾 160 字符）。
            // 缓冲上限 16 MB 时，旧写法等于每 40ms 全量正则扫一遍几 MB —— 轮询间隔被拉到几百毫秒，
            // 现场就是"读大配置时程序像卡住"。取末尾定长后，这段开销与输出大小无关。
            // 截断只可能切在**末尾之前很远**的头部：ANSI 序列被切一半最多在窗口开头留下几个杂字符，
            // 而两个判定都只看窗口**结尾**，不受影响；长度比较仍用完整 snapshot.Length。
            const int DecisionTailLength = 4096;
            var decisionTail = snapshot.Length > DecisionTailLength
                ? snapshot[^DecisionTailLength..]
                : snapshot;

            // 「输出已开始」的判据之一：回显里能看到命令本身（只看前 2K，回显一定在开头；
            // 不在整份快照里搜是为了避免每 40ms 全量扫描大缓冲 —— 那是另一处已修的性能坑）。
            if (!echoSeen && snapshot.Length > 0)
            {
                var probeLength = Math.Min(snapshot.Length, EchoProbeLength);
                echoSeen = snapshot.AsSpan(0, probeLength).Contains(command, StringComparison.OrdinalIgnoreCase);
            }

            // 1) 分页：只有“收到新数据之后”才续页，避免连发空格刷屏
            if (snapshot.Length != lastLengthAtSpace &&
                CliPager.IsWaitingForPage(decisionTail, out _))
            {
                if (LastCommandPageCount >= CliPager.MaxPages)
                {
                    LastCommandHitPageLimit = true;
                    break;
                }

                lastLengthAtSpace = snapshot.Length;
                LastCommandPageCount++;
                await WriteCoreAsync(CliPager.ContinueKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 2) 回到提示符 = 命令结束 —— 但**必须确认本次命令的输出已经开始**，否则会误判：
            //    现场 bug（2026-09-24 定位并修复）：登录 banner / 上一条命令的提示符**迟到**，
            //    正好落进本轮缓冲，40ms 后的第一次轮询就"看到提示符" → 命令被判已结束 →
            //    **连上后第一次读表偶尔是空的**（用户再点一次[刷新]就好）。
            //    修法：满足下面任一条才允许"靠提示符结束" ——
            //      ① 看到命令回显（最常见，零额外等待）；
            //      ② 提示符**之前已经有内容行**（关回显的设备：真输出来了，也不必等）；
            //      ③ 过了宽限期（关回显 + 命令本来就没输出，如 `vlan 3998`/`exit`）。
            //    只有第 ③ 条会多花时间，且只影响"关回显设备的无输出命令"。
            var outputStarted = echoSeen || HasContentLineBeforePrompt(decisionTail);
            if (snapshot.Length > 0
                && CliPromptDetector.EndsWithPrompt(decisionTail)
                && (outputStarted || DateTime.UtcNow - wroteAt >= PromptOnlyGrace))
            {
                LastCommandEndedAtPrompt = true;
                if (!outputStarted)
                {
                    Log?.Debug($"本命令未见回显、也无内容行（设备可能关了回显），按提示符判定结束：{command}");
                }

                break;
            }

            // 3) 空闲超时（持续有数据就不会超时）
            if (DateTime.UtcNow - lastOutput >= idleTimeout)
            {
                break;
            }
        }

        lock (_captureSync)
        {
            var text = _captureSnapshot ??= _capture.ToString();
            if (_captureDroppedChars <= 0)
            {
                return text;
            }

            // 丢过开头 → 在返回文本最前面插一行 `!` 注释（锐捷配置的注释符），
            // 这样备份 .cfg 仍是合法配置、但人一眼能看出"这份不完整"，不用去翻日志。
            var warning = $"! [235 助手] 本次输出超过 {FormatCaptureLimit(MaxCaptureChars)} 上限，" +
                          $"开头约 {_captureDroppedChars} 个字符已被丢弃 —— 内容可能不完整，请谨慎使用\r\n";
            return warning + text;
        }
    }

    public Task WriteRawAsync(string rawText, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return WriteCoreAsync(rawText, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await SafeShutdownAsync().ConfigureAwait(false);
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        GC.SuppressFinalize(this);
    }

    protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);

    protected abstract Task WriteCoreAsync(string text, CancellationToken cancellationToken);

    /// <summary>关闭底层资源。实现必须可重复调用且不抛异常。</summary>
    protected abstract Task ShutdownCoreAsync();

    /// <summary>设备输出统一从这里进入：更新缓冲 + 立即向 UI 推送（UI 侧再做批量刷新）。</summary>
    protected void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        bool identityChanged;
        bool promptSeen;
        bool privilegeChanged;
        lock (_captureSync)
        {
            _capture.Append(text);
            _captureSnapshot = null;      // 有新数据 → 缓存快照失效（下一次轮询重建）
            if (_capture.Length > MaxCaptureChars)
            {
                var remove = _capture.Length - MaxCaptureChars;
                _capture.Remove(0, remove);
                // 记下来：调用方（备份/导出/CLI）要能说清"这次输出不完整"
                _captureDroppedChars += remove;
            }

            _lastOutputUtc = DateTime.UtcNow;

            // 必须在同一把锁内更新设备名：否则「已经能读到提示符」的线程
            // （例如 Telnet 自动登录判定）可能先读到还没写入的 DeviceName。
            (identityChanged, promptSeen) = TryRecognizeDeviceName(text);

            // 权限级别与提示符在同一把锁里更新：CLI 手动 enable 后图形化页面要立刻知道。
            var level = CliPromptDetector.DetectPrivilegeLevel(_promptTail);
            privilegeChanged = level != Models.DevicePrivilegeLevel.Unknown && level != PrivilegeLevel;
            if (privilegeChanged)
            {
                PrivilegeLevel = level;
            }
        }

        if (identityChanged)
        {
            DeviceIdentityChanged?.Invoke(this, EventArgs.Empty);
        }

        if (privilegeChanged)
        {
            PrivilegeChanged?.Invoke(this, EventArgs.Empty);
        }

        // 设备后来才出提示符（例如用户手动输入了账号密码）：CLI 状态要跟着变成就绪，
        // 否则界面会一直停在「已连接（CLI 未确认）」。
        // 自动登录进行中不在这里抢答：那段时间的状态与提示统一由登录流程写，
        // 否则两处都会写 ConnectNotice，谁最后写到要看线程调度。
        if (promptSeen && TransportEstablished && !CliReady && !AutoLoginInProgress)
        {
            MarkCliReady("检测到命令提示符");
            ConnectNotice = "已检测到命令提示符，CLI 就绪。";
        }

        OutputReceived?.Invoke(this, new DeviceOutputEventArgs(text));
    }

    /// <summary>
    /// 设备提示符里就带着主机名（如 “Example-SW#”“Example-SW(config)#”），
    /// 连上就能识别设备名，不必等用户去点一次 show 命令。
    /// 返回：设备名是否发生变化、是否看到了提示符。
    /// </summary>
    private (bool NameChanged, bool PromptSeen) TryRecognizeDeviceName(string text)
    {
        // 用滚动尾部缓冲：TCP/串口的提示符可能被拆成两次到达（“Example-SW” + “#”），
        // 只看单个片段会把半截名字当成设备名。
        _promptTail = _promptTail.Length + text.Length > PromptTailLength
            ? (_promptTail + text)[^PromptTailLength..]
            : _promptTail + text;

        var trimmed = _promptTail.TrimEnd();
        if (trimmed.Length == 0 || trimmed[^1] is not ('#' or '>'))
        {
            return (false, false);
        }

        // 提示符所在的一行（正则要求整行形如 “Host#”“Host(config)#”）。
        var lineStart = trimmed.LastIndexOf('\n') + 1;
        var line = trimmed[lineStart..].Trim();

        var name = ShowOutputParser.TryExtractHostnameFromPrompt(line);
        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, false);
        }

        name = name.Trim();
        if (string.Equals(DeviceName, name, StringComparison.Ordinal))
        {
            return (false, true);
        }

        DeviceName = name;
        return (true, true);
    }

    protected string CaptureSnapshot()
    {
        lock (_captureSync)
        {
            return _captureSnapshot ??= _capture.ToString();
        }
    }

    /// <summary>等待设备输出满足条件（用于登录提示符判定）。</summary>
    protected async Task<bool> WaitForOutputAsync(Func<string, bool> predicate, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate(CaptureSnapshot()))
            {
                return true;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        return predicate(CaptureSnapshot());
    }

    protected void SetState(DeviceConnectionState state, string? message)
    {
        State = state;
        if (state == DeviceConnectionState.Error)
        {
            LastError = message;
            TransportEstablished = false;
            CliReady = false;
        }
        else if (state == DeviceConnectionState.Disconnected)
        {
            TransportEstablished = false;
            CliReady = false;
        }

        StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(state, message));
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!State.IsTransportUp())
        {
            throw new InvalidOperationException("设备当前未连接，禁止发送命令。");
        }
    }

    private void ResetCapture()
    {
        lock (_captureSync)
        {
            _capture.Clear();
            _captureSnapshot = null;
            _captureDroppedChars = 0;
            _lastOutputUtc = DateTime.UtcNow;
        }
    }

    /// <summary>上一条命令是否因为输出过大被丢过开头（备份/导出据此提示"可能不完整"）。</summary>
    public long LastCommandDroppedChars
    {
        get
        {
            lock (_captureSync)
            {
                return _captureDroppedChars;
            }
        }
    }

    /// <summary>把缓冲上限写成人类可读（16 MB / 4 KB / 64 字节）——测试里会把上限调得很小。</summary>
    private static string FormatCaptureLimit(int chars) => chars switch
    {
        >= 1024 * 1024 => $"{chars / (1024 * 1024)} MB",
        >= 1024 => $"{chars / 1024} KB",
        _ => $"{chars} 字节",
    };

    private async Task SafeShutdownAsync()
    {
        try
        {
            var cts = _lifetimeCts;
            if (cts is not null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            // 断开必须有**上限**：某些驱动的 SerialPort.Close() / 网络流 Dispose() 会一直等内部事件循环，
            // 没上限的话 DisconnectAsync 永久挂着 → 界面所有按钮因 IsBusy 变灰、只能杀进程
            // （现场"点[断开]程序未响应"就是这一类）。超时了就记日志并继续收尾，不阻断断开流程。
            var shutdown = ShutdownCoreAsync();
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log?.Warn("断开设备超过 5 秒仍未返回（已放弃等待，后台继续关闭）");
            }
        }
        catch
        {
            // 释放阶段不允许抛出，避免掩盖真实错误。
        }
    }
}
