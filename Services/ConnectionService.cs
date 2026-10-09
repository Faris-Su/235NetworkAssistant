using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 当前会话（单设备）。顶部状态栏、CLI、命令执行全部通过本服务获取当前连接，
/// 保证 Serial / Telnet / SSH 对上层无差别。
/// </summary>
public sealed class ConnectionService : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConnectionFactory _factory;
    private readonly ILogService _log;
    private IDeviceConnection? _current;
    private DateTimeOffset? _sessionStartedAt;
    private string _sessionId = string.Empty;
    private string _currentLineEnding = LineEndingOption.DefaultTelnet;

    /// <summary>
    /// **从设备里读到的**管理 IP（本次会话内有效）——目前由【设备】页执行 `show ip interface brief` 后回填。
    ///
    /// 为什么要有它：配置线（Console）连接的 <see cref="ManagementAddress"/> 是端口名（COM5）、不是 IP，
    /// 而交换机自己是有管理 IP 的。用户明确要求**不要在连接时自动发命令**去读，所以：
    /// 用户什么时候在【设备】页读了「三层接口」，这里就记下来，概览页的「管理 IP」跟着显示。
    /// 断开 / 换设备时清空（不跨会话串数据）。
    /// </summary>
    public string? LearnedManagementAddress { get; private set; }

    public ConnectionService(DeviceConnectionFactory factory, ILogService log)
    {
        _factory = factory;
        _log = log;
    }

    public event EventHandler? SessionChanged;

    public IDeviceConnection? Current => _current;

    public DeviceConnectionState State => _current?.State ?? DeviceConnectionState.Disconnected;

    public DeviceConnectionKind? CurrentKind => _current?.Kind;

    public string? DeviceName => _current?.DeviceName;

    public string? ManagementAddress => _current?.ManagementAddress;

    public string? LastError => _current?.LastError;

    /// <summary>当前权限级别（提示符判定）。图形化页面据此判断能不能改配置。</summary>
    public DevicePrivilegeLevel PrivilegeLevel => _current?.PrivilegeLevel ?? DevicePrivilegeLevel.Unknown;

    /// <summary>权限提示：成功或失败原因（失败不代表连接失败）。</summary>
    public string? PrivilegeNotice => _current?.PrivilegeNotice;

    /// <summary>是否已确认处于特权模式。</summary>
    public bool IsPrivileged => PrivilegeLevel == DevicePrivilegeLevel.Privileged;

    /// <summary>权限的显示文本（例如“特权模式 #”）。</summary>
    public string PrivilegeText => PrivilegeLevel.ToChinese();

    public DateTimeOffset? SessionStartedAt => _sessionStartedAt;

    public string SessionId => _sessionId;

    /// <summary>当前会话使用的命令行结束符（来自连接参数）。</summary>
    public string CurrentLineEnding => _currentLineEnding;

    /// <summary>连接成功后发送一次回车，让设备立即回显提示符（可在设置中关闭）。</summary>
    public bool WakeDeviceOnConnect { get; set; } = true;

    /// <summary>底层连接已建立（含“已连接但 CLI 未确认”“正在登录”）。</summary>
    public bool IsConnected => State.IsTransportUp();

    /// <summary>CLI 是否已经确认可以交互（看到了提示符）。与 IsConnected 是两件事。</summary>
    public bool IsCliReady => _current?.CliReady ?? false;

    /// <summary>CLI 初始化维度说明（“正在自动登录”“CLI 就绪”“等待提示符超时”）。</summary>
    public string? CliStateText => _current?.CliStateText;

    public async Task ConnectSerialAsync(SerialConnectionSettings settings, CancellationToken cancellationToken)
        => await ConnectSerialAsync(settings, cancellationToken, null).ConfigureAwait(false);

    public async Task ConnectSerialAsync(
        SerialConnectionSettings settings,
        CancellationToken cancellationToken,
        PrivilegeRequest? privilege)
    {
        _currentLineEnding = settings.LineEnding;
        await ConnectAsync(_factory.Create(DeviceConnectionKind.Serial, settings, null), cancellationToken, privilege)
            .ConfigureAwait(false);
    }

    public async Task ConnectTelnetAsync(TelnetConnectionSettings settings, CancellationToken cancellationToken)
        => await ConnectTelnetAsync(settings, cancellationToken, null).ConfigureAwait(false);

    public async Task ConnectTelnetAsync(
        TelnetConnectionSettings settings,
        CancellationToken cancellationToken,
        PrivilegeRequest? privilege)
    {
        _currentLineEnding = settings.LineEnding;
        await ConnectAsync(_factory.Create(DeviceConnectionKind.Telnet, null, settings), cancellationToken, privilege)
            .ConfigureAwait(false);
    }

    public async Task ConnectSshAsync(SshConnectionSettings settings, CancellationToken cancellationToken)
        => await ConnectSshAsync(settings, cancellationToken, null).ConfigureAwait(false);

    public async Task ConnectSshAsync(
        SshConnectionSettings settings,
        CancellationToken cancellationToken,
        PrivilegeRequest? privilege)
    {
        _currentLineEnding = settings.LineEnding;
        await ConnectAsync(_factory.Create(DeviceConnectionKind.Ssh, null, null, settings), cancellationToken, privilege)
            .ConfigureAwait(false);
    }

    public async Task ConnectAsync(IDeviceConnection connection, CancellationToken cancellationToken)
        => await ConnectAsync(connection, cancellationToken, null).ConfigureAwait(false);

    public async Task ConnectAsync(
        IDeviceConnection connection,
        CancellationToken cancellationToken,
        PrivilegeRequest? privilege)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sessionId = $"{connection.Kind}-{DateTime.Now:yyyyMMdd-HHmmss}";
        _sessionStartedAt = DateTimeOffset.Now;

        connection.StateChanged += OnConnectionStateChanged;
        connection.DeviceIdentityChanged += OnDeviceIdentityChanged;
        connection.PrivilegeChanged += OnPrivilegeChanged;
        _current = connection;
        LearnedManagementAddress = null;
        RaiseSessionChanged();

        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 只有底层传输未建立的失败才会从 DeviceConnectionBase 抛出异常；
            // 这时会话对象没有继续保留的价值，自动完成断开与释放，避免用户还要手动点一次“断开”。
            // TCP/串口已建立但 CLI 初始化失败会被保留为 Connected，不会进入这个分支。
            if (ReferenceEquals(_current, connection) && !connection.State.IsTransportUp())
            {
                _log.Info($"连接未建立，自动清理失败会话：{connection.Kind}");
                await DisconnectAsync().ConfigureAwait(false);
            }

            throw;
        }

        if (WakeDeviceOnConnect)
        {
            try
            {
                // 唤醒会话：让设备立刻回显提示符，避免用户面对空白终端。
                await connection.WriteRawAsync(_currentLineEnding, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("连接后发送回车失败", ex);
            }
        }

        // 串口最常见的现场坑：线插在交换机的网口上 / 交换机没上电 / 波特率不对。
        // 这时串口能打开、界面显示「已连接」，但设备一个字符都不回，用户不知道该查什么。
        if (connection.Kind == DeviceConnectionKind.Serial &&
            string.IsNullOrWhiteSpace(connection.ConnectNotice) &&
            !await WaitForFirstOutputAsync(connection, 1500, cancellationToken).ConfigureAwait(false))
        {
            // 串口已经打开（连接是好的），只是没等到提示符：CLI 未确认，但不算连接失败。
            connection.ReportCliNotReady(
                ConnectionDiagnostics.Format(
                    "CLI-03",
                    "串口已连接，但设备没有输出",
                    $"{connection.ManagementAddress} 已打开，1.5 秒内没有收到任何字符。",
                    new[]
                    {
                        "确认 Console 线插在交换机的 Console 口上（不是网口）。",
                        "确认交换机已经上电、指示灯亮。",
                        "确认波特率：多数锐捷设备是 9600（本页可改），少数需要 115200。",
                        "在终端里按一下回车，正常设备会回一个提示符。",
                    },
                    environment: null,
                    detail: null));
            _log.Warn("串口已打开但设备无任何输出（CLI 未确认）");
        }

        // 管理模式：连接成功后尝试进入特权模式。
        // 失败**不影响连接**：仍然保持「已连接」，只记录原因，由界面提示用户。
        if (privilege is { Mode: PrivilegeMode.Manage })
        {
            await TryElevateAsync(connection, privilege.EnablePassword, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 尝试提升权限（enable）。失败只写 PrivilegeNotice，不抛异常、不改连接状态。
    /// 密码不写日志。
    /// </summary>
    public async Task<PrivilegeElevationResult> TryElevateAsync(IDeviceConnection connection, string? enablePassword, CancellationToken cancellationToken)
    {
        try
        {
            var result = await connection.TryEnterPrivilegedModeAsync(enablePassword, cancellationToken)
                .ConfigureAwait(false);
            _log.Info($"权限提升{(result.Succeeded ? "成功" : "失败")}：{result.Reason}（community/password 不记录）");
            RaiseSessionChanged();
            return result;
        }
        catch (Exception ex)
        {
            // 这里兜底：权限问题绝不能变成“连接失败”
            _log.Warn("权限提升过程出错（连接保持不变）", ex);
            var failure = PrivilegeElevationResult.Failure($"进入特权模式时出错：{ex.Message}");
            RaiseSessionChanged();
            return failure;
        }
    }

    /// <summary>当前会话下尝试提升权限（供图形化页面 / 连接页调用）。</summary>
    public Task<PrivilegeElevationResult> EnsurePrivilegedAsync(string? enablePassword, CancellationToken cancellationToken)
    {
        var connection = _current;
        if (connection is null || !connection.State.IsTransportUp())
        {
            return Task.FromResult(PrivilegeElevationResult.Failure("设备未连接，无法提升权限。"));
        }

        return TryElevateAsync(connection, enablePassword, cancellationToken);
    }

    /// <summary>等待设备吐出第一个字符（收到即返回），超时返回 false。</summary>
    private static async Task<bool> WaitForFirstOutputAsync(
        IDeviceConnection connection,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, DeviceOutputEventArgs e) => tcs.TrySetResult(true);

        connection.OutputReceived += Handler;
        try
        {
            var completed = await Task.WhenAny(
                tcs.Task,
                Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
            return completed == tcs.Task;
        }
        finally
        {
            connection.OutputReceived -= Handler;
        }
    }

    public async Task DisconnectAsync()
    {
        var connection = _current;
        if (connection is null)
        {
            return;
        }

        connection.StateChanged -= OnConnectionStateChanged;
        connection.DeviceIdentityChanged -= OnDeviceIdentityChanged;
        connection.PrivilegeChanged -= OnPrivilegeChanged;
        await connection.DisconnectAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        _current = null;
        _sessionStartedAt = null;
        _sessionId = string.Empty;
        LearnedManagementAddress = null;
        RaiseSessionChanged();
    }

    /// <summary>
    /// 记录"从设备里读到的管理 IP"（例如【设备】页跑完 `show ip interface brief`）。
    /// 传 null / 空 / 非 IP 时忽略（不许把端口名之类的东西混进来）；值变了才通知界面。
    /// </summary>
    public void UpdateManagementAddress(string? ipAddress)
    {
        var normalized = Helpers.IpAddressHelper.ExtractFirst(ipAddress);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (string.Equals(LearnedManagementAddress, normalized, StringComparison.Ordinal))
        {
            return;
        }

        LearnedManagementAddress = normalized;
        _log.Info($"已从设备读到管理 IP：{normalized}（概览页将显示它）");
        RaiseSessionChanged();
    }

    /// <summary>把从设备输出中解析出的设备名写回会话（顶部状态栏据此显示）。</summary>
    public void UpdateDeviceIdentity(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        if (string.Equals(DeviceName, deviceName, StringComparison.Ordinal))
        {
            return;
        }

        _current?.SetDeviceIdentity(deviceName);
        OnPropertyChanged(nameof(DeviceName));
        RaiseSessionChanged();
    }

    public IDeviceConnection RequireConnected()
    {
        var connection = _current;
        if (connection is null || !connection.State.IsTransportUp())
        {
            throw new InvalidOperationException("设备未连接，禁止发送命令。");
        }

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("释放连接会话时出现异常", ex);
        }
    }

    private void OnConnectionStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        switch (e.State)
        {
            case DeviceConnectionState.Authenticating:
                _log.Info($"底层已连接，正在自动登录：{_current?.ManagementAddress}");
                break;
            case DeviceConnectionState.Ready:
                _log.Info($"CLI 就绪：{e.Message}");
                break;
            case DeviceConnectionState.Connected:
                _log.Info($"底层已连接（CLI 未确认）：{_current?.ManagementAddress}");
                break;
            case DeviceConnectionState.Error:
                _log.Warn($"连接错误：{e.Message}");
                break;
        }

        RaiseSessionChanged();
    }

    /// <summary>设备名从提示符里被识别出来时立即刷新界面（顶部状态栏、概览页）。</summary>
    private void OnDeviceIdentityChanged(object? sender, EventArgs e)
    {
        _log.Info($"识别到设备名：{_current?.DeviceName}");
        OnPropertyChanged(nameof(DeviceName));
        RaiseSessionChanged();
    }

    /// <summary>权限级别变化（连接时自动 enable、或用户在 CLI 里手动 enable）→ 全界面同步。</summary>
    private void OnPrivilegeChanged(object? sender, EventArgs e)
    {
        // 现场排障需要看得出「为什么界面显示特权/普通模式」；这里只记判定结果，不含任何密码。
        _log.Info($"权限级别判定：{PrivilegeText}");
        OnPropertyChanged(nameof(PrivilegeLevel));
        OnPropertyChanged(nameof(PrivilegeNotice));
        OnPropertyChanged(nameof(IsPrivileged));
        OnPropertyChanged(nameof(PrivilegeText));
        RaiseSessionChanged();
    }

    private void RaiseSessionChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(CurrentKind));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(ManagementAddress));
        OnPropertyChanged(nameof(SessionStartedAt));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsCliReady));
        OnPropertyChanged(nameof(CliStateText));
        OnPropertyChanged(nameof(PrivilegeLevel));
        OnPropertyChanged(nameof(PrivilegeNotice));
        OnPropertyChanged(nameof(IsPrivileged));
        OnPropertyChanged(nameof(PrivilegeText));
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
