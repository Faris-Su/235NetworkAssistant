using System.Windows.Threading;
using Microsoft.Win32;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// CLI 终端（Phase 1）。Serial 与 Telnet 共用本页面，只依赖 IDeviceConnection。
/// 数据管线由 TerminalSession 承担（接收缓冲 → 批量刷新 → 显示 + 原始输出留档），
/// ViewModel 只负责会话状态、命令输入、历史、按键动作与保存输出。
/// </summary>
public sealed class CliViewModel : ViewModelBase, IDisposable
{
    private readonly IShellNavigator _shell;
    private readonly ConnectionService _connections;
    private readonly CommandHistory _history;
    private readonly TerminalSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flushTimer;

    private IDeviceConnection? _attached;
    private bool _lastStateConnected;
    private bool _disposed;

    private string _inputText = string.Empty;
    private string _terminalInfo = string.Empty;
    private string _historyInfo = string.Empty;
    private string _stateText = "未连接";
    private string _statusHint = "选择交换机并连接后即可使用 CLI 终端。";
    private bool _isConnected;
    private bool _autoScroll;
    private bool _localEcho;
    private bool _terminalMode;
    private bool _isFocusMode;
    private double _cliFontSize;

    public CliViewModel(IShellNavigator shell, ConnectionService connections)
    {
        _shell = shell;
        _connections = connections;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Title = "CLI";

        var settings = AppServices.Settings;
        _history = new CommandHistory(settings.CliHistorySize);
        _session = new TerminalSession(
            settings.TerminalBufferChars,
            settings.TerminalFlushIntervalMs,
            recorder: null,
            log: AppServices.Log);
        _autoScroll = settings.CliAutoScroll;
        _localEcho = settings.CliLocalEcho;
        _terminalMode = settings.CliTerminalMode;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.TerminalFlushIntervalMs, 20, 1000)),
        };
        _flushTimer.Tick += OnFlushTick;

        SendCommand = new AsyncRelayCommand(SendInputAsync, () => IsConnected);
        SendCtrlCCommand = new AsyncRelayCommand(SendCtrlCAsync, () => IsConnected);
        ClearCommand = new RelayCommand(ClearTerminal);
        SaveOutputCommand = new RelayCommand(SaveOutput);
        SaveConfigCommand = new RelayCommand(() => _shell.ShowSaveConfig(), () => IsConnected);
        ApplyBufferSettingsCommand = new RelayCommand(ApplyBufferSettings);
        OpenConnectionPageCommand = new RelayCommand(() => _shell.NavigateTo("connection"));
        ToggleFocusModeCommand = new RelayCommand(() => _shell.SetFocusMode(!IsFocusMode));

        _connections.SessionChanged += OnSessionChanged;
        _shell.FocusModeChanged += OnFocusModeChanged;
        AppearanceService.AppearanceChanged += OnAppearanceChanged;
        _isFocusMode = _shell.IsFocusMode;
        _cliFontSize = AppearanceService.CliFontSize;
        RefreshTerminalInfo();
        RefreshHistoryInfo();
        UpdateSessionState();
    }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand SendCtrlCCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand SaveOutputCommand { get; }

    /// <summary>
    /// [保存配置]（write）：在 CLI 页也能一步按到。
    /// 点击只是打开确认弹窗，真正写入要在弹窗里按住 1.2 秒。
    /// </summary>
    public RelayCommand SaveConfigCommand { get; }

    public RelayCommand ApplyBufferSettingsCommand { get; }

    public RelayCommand OpenConnectionPageCommand { get; }

    public RelayCommand ToggleFocusModeCommand { get; }

    /// <summary>专注 CLI 模式：隐藏页面头部与次要说明，把垂直空间让给终端。</summary>
    public bool IsFocusMode
    {
        get => _isFocusMode;
        private set
        {
            if (SetProperty(ref _isFocusMode, value))
            {
                OnPropertyChanged(nameof(FocusModeButtonText));
            }
        }
    }

    public string FocusModeButtonText => IsFocusMode ? "退出专注" : "专注 CLI";

    /// <summary>CLI 终端字体大小（设置 → 外观，可独立于界面字体）。</summary>
    public double CliFontSize
    {
        get => _cliFontSize;
        private set => SetProperty(ref _cliFontSize, value);
    }

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public string TerminalInfo
    {
        get => _terminalInfo;
        private set => SetProperty(ref _terminalInfo, value);
    }

    public string HistoryInfo
    {
        get => _historyInfo;
        private set => SetProperty(ref _historyInfo, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                SendCommand.RaiseCanExecuteChanged();
                SendCtrlCCommand.RaiseCanExecuteChanged();
                SaveConfigCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>自动滚动到最新输出（同时写回设置）。</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (SetProperty(ref _autoScroll, value))
            {
                AppServices.Settings.CliAutoScroll = value;
                _session.Display?.Rewrite(_session.Text);
            }
        }
    }

    /// <summary>本地回显（默认关闭：由设备回显，避免重复显示）。</summary>
    public bool LocalEcho
    {
        get => _localEcho;
        set
        {
            if (SetProperty(ref _localEcho, value))
            {
                AppServices.Settings.CliLocalEcho = value;
            }
        }
    }

    /// <summary>
    /// 终端模式：直接在终端里打字，字符逐个发给设备（设备回显即所见），像 Xshell；
    /// 关闭则回到“下方输入框整行输入 + 回车/发送”。
    /// </summary>
    public bool TerminalMode
    {
        get => _terminalMode;
        set
        {
            if (SetProperty(ref _terminalMode, value))
            {
                AppServices.Settings.CliTerminalMode = value;
                StatusHint = value
                    ? "终端模式：直接在终端里输入，回车发送；↑/↓ 设备历史、Tab 补全、Ctrl+C 中断。"
                    : "行模式：在下方输入框输入，回车或点[发送]发给设备。";
            }
        }
    }

    /// <summary>终端模式：把用户输入的文本原样发给设备（不追加回车）。</summary>
    public Task SendTerminalTextAsync(string text) => WriteAsync(text ?? string.Empty);

    /// <summary>终端模式：把一次按键转成对应的控制序列发给设备。</summary>
    public Task SendTerminalKeyAsync(TerminalKey key) => key switch
    {
        // 回车用当前连接配置的结束符（Console 一般 CR，Telnet 为 CRLF）
        TerminalKey.Enter => WriteAsync(_connections.CurrentLineEnding),
        // 退格字节按设置走：0x08（Ctrl-H，多数网络设备）或 0x7F（DEL，部分设备/服务器）。
        // 借鉴 PuTTY 的 "Backspace key" —— 遇到"退格没用/退格变乱码"时换一个就好。
        TerminalKey.Backspace => WriteAsync(AppServices.Settings.CliBackspaceSendsDel ? "\u007f" : "\u0008"),
        TerminalKey.Tab => WriteAsync("\t"),
        TerminalKey.Escape => WriteAsync("\u001b"),
        // 上下键交给设备做历史（和 Xshell 一致：终端把 ESC[A / ESC[B 发给设备）
        TerminalKey.HistoryPrevious => WriteAsync("\u001b[A"),
        TerminalKey.HistoryNext => WriteAsync("\u001b[B"),
        TerminalKey.Interrupt => SendInterruptAsync(),
        _ => Task.CompletedTask,
    };

    private async Task SendInterruptAsync()
    {
        _session.AppendLocal("^C");
        await WriteAsync("\u0003").ConfigureAwait(true);
        StatusHint = "已发送 Ctrl+C (0x03)。";
    }

    public string ConnectionBanner => IsConnected
        ? $"{(string.IsNullOrWhiteSpace(_connections.DeviceName) ? "设备" : _connections.DeviceName)}｜{_connections.ManagementAddress}｜{_connections.CurrentKind}｜Session {_connections.SessionId}"
        : "未连接设备：请到【连接】页选择交换机并连接（Console 或 Telnet）。";

    /// <summary>诊断/验收用：当前终端显示文本（只读，不参与任何命令逻辑）。</summary>
    public string TerminalText => _session.Text;

    /// <summary>由 View 在加载/卸载时注入终端显示表面（ViewModel 不引用任何 WPF 类型）。</summary>
    public void AttachDisplay(ITerminalDisplay? display)
    {
        _session.Display = display;
        if (display is null)
        {
            return;
        }

        display.Rewrite(_session.Text);
        _session.Flush(force: true);
        RefreshTerminalInfo();
    }

    /// <summary>↑ 调取上一条命令。</summary>
    public bool TryHistoryPrevious()
    {
        var value = _history.MovePrevious(InputText);
        if (value is null)
        {
            return false;
        }

        InputText = value;
        return true;
    }

    /// <summary>↓ 调取下一条命令（回到最后时恢复进入历史前的输入）。</summary>
    public bool TryHistoryNext()
    {
        var value = _history.MoveNext();
        if (value is null)
        {
            return false;
        }

        InputText = value;
        return true;
    }

    /// <summary>Tab 透传：发给设备做命令补全，不切换焦点。</summary>
    public async Task SendTabAsync()
    {
        if (!IsConnected)
        {
            StatusHint = "未连接设备，无法发送。";
            return;
        }

        await WriteAsync("\t").ConfigureAwait(true);
        StatusHint = "已发送 Tab（命令补全）。";
    }

    /// <summary>? 透传：把当前输入连同 ? 直接发给设备（不等待回车）。</summary>
    public async Task SendQuestionMarkAsync()
    {
        if (!IsConnected)
        {
            StatusHint = "未连接设备，无法发送。";
            return;
        }

        var text = (InputText ?? string.Empty) + "?";
        InputText = string.Empty;
        await WriteAsync(text).ConfigureAwait(true);
        StatusHint = "已向设备提问（?）。";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTick;
        _connections.SessionChanged -= OnSessionChanged;
        _shell.FocusModeChanged -= OnFocusModeChanged;
        AppearanceService.AppearanceChanged -= OnAppearanceChanged;
        DetachConnection();
        _session.Dispose();
    }

    private void OnFocusModeChanged(object? sender, EventArgs e) => RunOnUi(() => IsFocusMode = _shell.IsFocusMode);

    private void OnAppearanceChanged(object? sender, EventArgs e) => RunOnUi(() => CliFontSize = AppearanceService.CliFontSize);

    /// <summary>
    /// 终端模式下要发给设备的按键（View 只做“按键 → 语义”的转换，转义序列集中在这里）。
    /// </summary>
    public enum TerminalKey
    {
        Enter,
        Backspace,
        Tab,
        Escape,
        HistoryPrevious,
        HistoryNext,
        Interrupt,
    }

    private async Task SendInputAsync()
    {
        if (!IsConnected)
        {
            StatusHint = "未连接设备，无法发送命令。";
            return;
        }

        var command = InputText ?? string.Empty;
        InputText = string.Empty;
        _history.Add(command);
        RefreshHistoryInfo();

        if (LocalEcho)
        {
            _session.AppendLocal(command + "\r\n");
        }

        await WriteAsync(command + _connections.CurrentLineEnding).ConfigureAwait(true);
        StatusHint = command.Length == 0 ? "已发送回车。" : $"已发送：{command}";
    }

    private async Task SendCtrlCAsync()
    {
        if (!IsConnected)
        {
            StatusHint = "未连接设备，无法发送。";
            return;
        }

        _session.AppendLocal("^C");
        await WriteAsync("\u0003").ConfigureAwait(true);
        StatusHint = "已发送 Ctrl+C (0x03)。";
    }

    private async Task WriteAsync(string text)
    {
        var connection = _connections.Current;
        if (connection is null || !connection.State.IsTransportUp())
        {
            StatusHint = "设备未连接，禁止发送命令。";
            return;
        }

        try
        {
            await connection.WriteRawAsync(text, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendSystemLine($"发送失败：{ex.Message}");
            StatusHint = $"发送失败：{ex.Message}";
            AppServices.Log.Warn("CLI 发送失败", ex);
        }
    }

    private void ClearTerminal()
    {
        _session.Clear();
        RefreshTerminalInfo();
        StatusHint = "显示已清空（原始输出文件仍在继续记录，可用[保存输出]导出完整内容）。";
    }

    private void SaveOutput()
    {
        var recorder = _session.Recorder;
        if (recorder is null || recorder.TotalChars == 0)
        {
            StatusHint = "当前会话还没有原始输出可保存。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 CLI 原始输出",
            Filter = "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            FileName = $"cli-{(string.IsNullOrWhiteSpace(_connections.SessionId) ? DateTime.Now.ToString("yyyyMMdd-HHmmss") : _connections.SessionId)}.txt",
            InitialDirectory = AppPaths.BackupDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var path = recorder.SaveCopy(dialog.FileName);
            StatusHint = $"原始输出已保存：{path}";
            _shell.ReportStatus($"CLI 原始输出已保存：{path}");
            _shell.AddRecentOperation("保存 CLI 输出", path);
        }
        catch (Exception ex)
        {
            StatusHint = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>应用【设置】页的终端缓冲 / 刷新间隔（无需重启）。</summary>
    private void ApplyBufferSettings()
    {
        var settings = AppServices.Settings;
        _session.Resize(settings.TerminalBufferChars, settings.TerminalFlushIntervalMs);
        _flushTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.TerminalFlushIntervalMs, 20, 1000));
        AutoScroll = settings.CliAutoScroll;
        LocalEcho = settings.CliLocalEcho;
        RefreshTerminalInfo();
        StatusHint = $"已应用缓冲设置：{settings.TerminalBufferChars:N0} 字符 / {settings.TerminalFlushIntervalMs} ms 刷新。";
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RunOnUi(UpdateSessionState);

    private void OnFlushTick(object? sender, EventArgs e)
    {
        if (_session.Flush())
        {
            RefreshTerminalInfo();
        }
    }

    /// <summary>设备输出回调：可能来自读取线程，交给 TerminalSession 只做加锁追加。</summary>
    private void OnOutputReceived(object? sender, DeviceOutputEventArgs e) => _session.OnDeviceOutput(e.Text);

    private void UpdateSessionState()
    {
        DetachConnection();

        var connection = _connections.Current;
        // 底层连上就算“已连接”：CLI 是否已经确认可用另算，不能因此把页面判成未连接。
        var connected = connection is not null && connection.State.IsTransportUp();
        var wasConnected = _lastStateConnected;
        _lastStateConnected = connected;
        IsConnected = connected;

        if (connection is not null)
        {
            connection.OutputReceived += OnOutputReceived;
            _attached = connection;
        }

        if (connected)
        {
            // 只在「未连接 → 已连接」时重建会话记录，避免设备名识别等后续刷新重复打印“已连接”。
            if (!wasConnected)
            {
                _session.Recorder?.Dispose();
                _session.Recorder = new SessionRecorder(_connections.SessionId);
                _history.Reset();
                RefreshHistoryInfo();
                _flushTimer.Start();
                _session.Flush(force: true);
                AppendSystemLine($"已连接：{_connections.CurrentKind} {_connections.ManagementAddress}（Session {_connections.SessionId}）");

                // 本地目录不可写时，终端照常可用，但要明确告知无法导出原始输出。
                if (!_session.Recorder.IsRecording)
                {
                    AppendSystemLine($"原始输出无法写入磁盘（{_session.Recorder.FailureReason}）：终端仍可正常使用，[保存输出]不可用。");
                }

                // 自动登录的结果（跳过登录 / 账号可能不对）必须让用户看到。
                var notice = _connections.Current?.ConnectNotice;
                if (!string.IsNullOrWhiteSpace(notice))
                {
                    AppendSystemLine(notice);
                }

                StatusHint = string.IsNullOrWhiteSpace(notice)
                    ? "已连接。输入命令后回车发送；↑/↓ 调历史，Tab 补全，Ctrl+C 发送 0x03。"
                    : notice.Split('\n')[0].Trim();
            }

            RefreshTerminalInfo();
        }
        else
        {
            _flushTimer.Stop();
            _session.Flush(force: true);

            if (_connections.State == DeviceConnectionState.Error)
            {
                AppendSystemLine($"连接错误：{_connections.LastError}");
                StatusHint = $"连接错误：{_connections.LastError}";
            }
            else if (wasConnected)
            {
                AppendSystemLine("设备已断开。");
                StatusHint = "设备已断开；原始输出仍可用[保存输出]导出。";
            }

            RefreshTerminalInfo();
        }

        StateText = $"{_connections.State.ToChinese()}｜{_connections.CurrentKind?.ToString() ?? "—"}";
        OnPropertyChanged(nameof(ConnectionBanner));
    }

    private void DetachConnection()
    {
        if (_attached is null)
        {
            return;
        }

        // 避免重复订阅：每次会话切换都先解绑旧连接。
        _attached.OutputReceived -= OnOutputReceived;
        _attached = null;
    }

    private void AppendSystemLine(string message)
    {
        _session.AppendLocal($"[CLI] {message}\r\n");
        RefreshTerminalInfo();
    }

    private void RefreshTerminalInfo()
    {
        var buffer = _session.Buffer;
        TerminalInfo =
            $"显示缓冲 {buffer.Length:N0}/{buffer.Capacity:N0} · 累计 {buffer.TotalAppendedChars:N0} · 已裁剪 {buffer.TrimmedChars:N0} · 原始输出 {_session.RawChars:N0} 字符";
    }

    private void RefreshHistoryInfo() => HistoryInfo = $"命令历史 {_history.Count} 条（↑/↓ 调取）";

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }
}
