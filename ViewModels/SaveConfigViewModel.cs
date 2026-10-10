using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// [保存配置] 弹窗的 ViewModel：把 running-config 写入设备启动配置（write）。
///
/// 体验目标：**好按但极难误触**。
///   - 按钮在顶栏与 CLI 页随时可点（一步就能到）；
///   - 点了不会立刻写入：必须在这个弹窗里**按住 1.2 秒**，中途松开/移出即取消；
///     按住状态由本 ViewModel 维护（<see cref="BeginHold"/> / <see cref="UpdateHold"/> /
///     <see cref="CancelHold"/>），<see cref="WriteAsync"/> 在没有按满时直接拒绝——
///     即使谁绕过界面直接调用也不会写设备；
///   - 键盘 Enter / Space 不触发写入（按钮不响应键盘，也刻意不做快捷键）；
///   - 普通模式（&gt;）先提权，**提权成功后不会自动写入**，必须重新按住确认；
///   - 写入成功后进入冷却期，防止连点重复 write。
/// </summary>
public sealed class SaveConfigViewModel : ObservableObject
{
    /// <summary>需要按住多久才允许写入（防误触核心参数）。</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1200);

    private readonly ConfigSaveService _save;
    private readonly ConnectionService _connections;
    private readonly IShellNavigator _shell;

    private string _commandText = ConfigSaveService.DefaultCommand;
    private DateTimeOffset _holdStartedAt = DateTimeOffset.MinValue;
    private bool _isHolding;
    private bool _holdSatisfied;
    private bool _isWriting;
    private double _holdProgress;
    private string _statusText = "还没写入。按住下面的按钮 1.2 秒才会开始。";
    private string _resultText = string.Empty;
    private string _resultSummary = string.Empty;
    private string _resultHint = string.Empty;
    private bool _hasResult;
    private bool _isResultSuccess;
    private bool _suggestsMemory;
    private string _enablePassword;
    private string _privilegeStatus = string.Empty;
    private string _privilegeNotice;
    private bool _isElevating;

    public SaveConfigViewModel(
        ConfigSaveService save,
        ConnectionService connections,
        IShellNavigator shell)
    {
        _save = save;
        _connections = connections;
        _shell = shell;
        _enablePassword = AppServices.EnablePassword;
        _privilegeNotice = _connections.PrivilegeNotice ?? string.Empty;

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        WriteCommand = new AsyncRelayCommand(WriteAsync, () => CanWrite);
        ElevateCommand = new AsyncRelayCommand(ElevateAsync, () => CanElevate);
        SwitchToMemoryCommand = new RelayCommand(SwitchToMemory, () => SuggestsMemoryCommand && !IsWriting);
        CopyResultCommand = new RelayCommand(CopyResult, () => HasResult && ResultText.Length > 0);

        _connections.SessionChanged += (_, _) => RefreshConnectionState();
    }

    public event EventHandler? CloseRequested;

    // ---------- 目标设备信息（让人在按之前先看清"这是哪台设备"） ----------

    public string DeviceNameText => string.IsNullOrWhiteSpace(_connections.DeviceName)
        ? "（未识别设备名）"
        : _connections.DeviceName!;

    public string AddressText => string.IsNullOrWhiteSpace(_connections.ManagementAddress)
        ? "（无）"
        : _connections.ManagementAddress!;

    public string KindText => _connections.CurrentKind switch
    {
        DeviceConnectionKind.Serial => "Console / 配置线",
        DeviceConnectionKind.Telnet => "Telnet",
        DeviceConnectionKind.Ssh => "SSH",
        _ => "未连接",
    };

    public string PrivilegeText => _connections.PrivilegeLevel.ToChinese();

    /// <summary>写在弹窗里的固定警示文案。</summary>
    public string WarningText =>
        "要写入设备：running-config → startup-config。写下去就生效，重启后仍然是这份配置；" +
        "如果当前配置有问题，也会被一起保存下来。软件不会自动执行这条命令。";

    // ---------- 命令 ----------

    /// <summary>实际要发送的命令（默认 write，可切 write memory）。</summary>
    public string CommandText
    {
        get => _commandText;
        private set
        {
            if (SetProperty(ref _commandText, value))
            {
                OnPropertyChanged(nameof(CommandHint));
            }
        }
    }

    public string CommandHint => CommandText.Equals(ConfigSaveService.MemoryCommand, StringComparison.OrdinalIgnoreCase)
        ? "write memory：和 write 等效，部分型号/版本只认这一种写法。"
        : "write：锐捷 RGOS 保存运行配置到启动配置的标准写法。";

    // ---------- 按住确认（防误触核心） ----------

    public double HoldProgress
    {
        get => _holdProgress;
        private set => SetProperty(ref _holdProgress, value);
    }

    public bool IsHolding
    {
        get => _isHolding;
        private set
        {
            if (SetProperty(ref _isHolding, value))
            {
                OnPropertyChanged(nameof(HoldButtonText));
            }
        }
    }

    /// <summary>按住是否已经满 1.2 秒（写设备前的最后一道闸）。</summary>
    public bool IsHoldSatisfied
    {
        get => _holdSatisfied;
        private set
        {
            if (SetProperty(ref _holdSatisfied, value))
            {
                OnPropertyChanged(nameof(HoldButtonText));
            }
        }
    }

    public string HoldButtonText => IsWriting
        ? "正在写入设备…"
        : IsHoldSatisfied
            ? "松开即写入设备"
            : "按住 1.2 秒写入设备";

    public string HoldHint => "鼠标按住不放满 1.2 秒才会写入；中途松手、移开鼠标都会取消。回车 / 空格不会触发。";

    /// <summary>开始按住（鼠标左键按下时调用）。</summary>
    public void BeginHold()
    {
        if (!CanWrite)
        {
            StatusText = BlockedForHoldReason();
            return;
        }

        _holdStartedAt = DateTimeOffset.Now;
        IsHoldSatisfied = false;
        IsHolding = true;
        HoldProgress = 0;
        StatusText = "按住中…不要松手（中途松开就取消）。";
    }

    /// <summary>
    /// 视图定时器每次调用：返回 true 表示已经按满 <see cref="HoldDuration"/>，可以写入。
    /// </summary>
    public bool UpdateHold()
    {
        if (!IsHolding)
        {
            return false;
        }

        var elapsed = DateTimeOffset.Now - _holdStartedAt;
        HoldProgress = Math.Clamp(elapsed.TotalMilliseconds / HoldDuration.TotalMilliseconds, 0, 1);

        if (elapsed >= HoldDuration)
        {
            IsHoldSatisfied = true;
            return true;
        }

        return false;
    }

    /// <summary>取消按住（松开鼠标 / 鼠标移出 / 失去捕获时调用）。</summary>
    public void CancelHold()
    {
        if (!IsHolding)
        {
            return;
        }

        IsHolding = false;
        if (IsHoldSatisfied)
        {
            return;
        }

        HoldProgress = 0;
        StatusText = "已取消：按住时间不足 1.2 秒，设备没有收到任何命令。";
    }

    // ---------- 状态与结果 ----------

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsWriting
    {
        get => _isWriting;
        private set
        {
            if (SetProperty(ref _isWriting, value))
            {
                OnPropertyChanged(nameof(CanWrite));
                OnPropertyChanged(nameof(HoldButtonText));
                WriteCommand.RaiseCanExecuteChanged();
                SwitchToMemoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanWrite => _connections.IsConnected && !IsWriting && !IsPrivilegeBlocked && !_save.IsCoolingDown;

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                CopyResultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string ResultHint
    {
        get => _resultHint;
        private set
        {
            if (SetProperty(ref _resultHint, value))
            {
                OnPropertyChanged(nameof(HasResultHint));
            }
        }
    }

    public bool HasResultHint => ResultHint.Length > 0;

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (SetProperty(ref _hasResult, value))
            {
                CopyResultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>设备明确回了成功标志。</summary>
    public bool IsResultSuccess
    {
        get => _isResultSuccess;
        private set => SetProperty(ref _isResultSuccess, value);
    }

    public bool SuggestsMemoryCommand
    {
        get => _suggestsMemory;
        private set
        {
            if (SetProperty(ref _suggestsMemory, value))
            {
                OnPropertyChanged(nameof(SwitchToMemoryText));
                SwitchToMemoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SwitchToMemoryText => $"改用 {ConfigSaveService.MemoryCommand}";

    public string LastSavedText => _save.LastSavedText;

    // ---------- 权限 ----------

    public DevicePrivilegeLevel PrivilegeLevel => _connections.PrivilegeLevel;

    /// <summary>明确处于普通模式（&gt;）→ 需要先提权，提权前禁止写入。</summary>
    public bool IsPrivilegeBlocked => !_connections.PrivilegeLevel.CanConfigure();

    public bool CanElevate =>
        _connections.IsConnected && !IsElevating && PrivilegeLevel != DevicePrivilegeLevel.Privileged;

    public string PrivilegeHint => IsPrivilegeBlocked
        ? "当前是普通模式（>）：保存配置需要管理权限。填 Enable 密码 → 点[提升权限]；" +
          "进入特权模式（#）后请**重新按住确认**（提权成功不会自动写入设备）。"
        : PrivilegeLevel == DevicePrivilegeLevel.Privileged
            ? "当前权限：特权模式（#），可以保存。"
            : "当前权限未知（还没拿到提示符）：请先确认设备已在命令提示符下。";

    public string EnablePassword
    {
        get => _enablePassword;
        set => SetProperty(ref _enablePassword, value);
    }

    public string PrivilegeStatus
    {
        get => _privilegeStatus;
        private set => SetProperty(ref _privilegeStatus, value);
    }

    public string PrivilegeNotice
    {
        get => _privilegeNotice;
        private set => SetProperty(ref _privilegeNotice, value);
    }

    public bool IsElevating
    {
        get => _isElevating;
        private set
        {
            if (SetProperty(ref _isElevating, value))
            {
                OnPropertyChanged(nameof(CanElevate));
                ElevateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand WriteCommand { get; }

    public AsyncRelayCommand ElevateCommand { get; }

    public RelayCommand SwitchToMemoryCommand { get; }

    public RelayCommand CopyResultCommand { get; }

    private string BlockedForHoldReason()
    {
        if (!_connections.IsConnected)
        {
            return "设备未连接：保存会写入设备，必须先连上交换机。";
        }

        if (IsPrivilegeBlocked)
        {
            return "当前是普通模式（>）：先提升到特权模式（#）再按住确认。";
        }

        if (_save.IsCoolingDown)
        {
            return _save.BlockedReason;
        }

        return "现在不能写入。";
    }

    /// <summary>
    /// 执行写入。**必须已经按住满 1.2 秒**（<see cref="IsHoldSatisfied"/>），
    /// 否则直接拒绝——这样即使界面被绕过，也不会误写设备。
    /// </summary>
    public async Task WriteAsync()
    {
        if (!IsHoldSatisfied)
        {
            StatusText = "必须按住按钮 1.2 秒才会写入设备（这次没有发送任何命令）。";
            return;
        }

        if (!CanWrite)
        {
            StatusText = BlockedForHoldReason();
            ResetHold();
            return;
        }

        ResetHold();
        IsWriting = true;
        StatusText = $"正在写入设备：{CommandText} …";

        try
        {
            var result = await _save.SaveAsync(CommandText).ConfigureAwait(true);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            StatusText = $"写入失败：{ex.Message}";
            ResultSummary = $"写入失败：{ex.Message}";
            ResultHint = "确认连接是否还在，然后重新按住确认一次。";
            ResultText = string.Empty;
            HasResult = true;
            IsResultSuccess = false;
            _shell.ReportStatus($"保存配置失败：{ex.Message}");
            _shell.AddRecentOperation("保存配置（write）", ex.Message, succeeded: false);
        }
        finally
        {
            IsWriting = false;
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(LastSavedText));
            WriteCommand.RaiseCanExecuteChanged();
        }
    }

    private void ResetHold()
    {
        IsHolding = false;
        IsHoldSatisfied = false;
        HoldProgress = 0;
    }

    private void ApplyResult(ConfigSaveResult result)
    {
        HasResult = true;
        IsResultSuccess = result.Confirmed;
        ResultSummary = result.Summary;
        ResultHint = result.Hint;
        ResultText = string.IsNullOrWhiteSpace(result.RawOutput)
            ? "（设备没有返回任何内容）"
            : result.RawOutput;
        SuggestsMemoryCommand = result.SuggestMemoryCommand;
        StatusText = result.Summary;

        _shell.ReportStatus(result.Summary);
        _shell.AddRecentOperation(
            "保存配置（write）",
            result.Confirmed
                ? $"{result.Command}：设备回显 [OK]，配置已写入启动配置"
                : $"{result.Command}：{result.Summary}",
            result.Confirmed);

        OnPropertyChanged(nameof(LastSavedText));
        OnPropertyChanged(nameof(CanWrite));
        WriteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>设备提示 write 无效时，切到 write memory（切换本身不发送任何命令）。</summary>
    private void SwitchToMemory()
    {
        CommandText = ConfigSaveService.MemoryCommand;
        SuggestsMemoryCommand = false;
        HasResult = false;
        ResultSummary = string.Empty;
        ResultHint = string.Empty;
        ResultText = string.Empty;
        StatusText = $"已切换到 {ConfigSaveService.MemoryCommand}：确认无误后按住按钮 1.2 秒重新写入。";
    }

    /// <summary>
    /// 提升权限：enable → 输入密码。**成功后不会自动写入设备**，
    /// 必须由用户重新按住确认（写设备这种事不能"顺手"完成）。
    /// </summary>
    private async Task ElevateAsync()
    {
        IsElevating = true;
        PrivilegeStatus = "正在尝试进入特权模式…";
        try
        {
            AppServices.EnablePassword = EnablePassword;
            var result = await _connections.EnsurePrivilegedAsync(EnablePassword, CancellationToken.None)
                .ConfigureAwait(true);

            PrivilegeStatus = result.Reason;
            PrivilegeNotice = result.Reason;
            OnPropertyChanged(nameof(PrivilegeLevel));
            OnPropertyChanged(nameof(PrivilegeText));
            OnPropertyChanged(nameof(IsPrivilegeBlocked));
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(PrivilegeHint));
            WriteCommand.RaiseCanExecuteChanged();
            ElevateCommand.RaiseCanExecuteChanged();

            StatusText = result.Succeeded
                ? "已进入特权模式（#）：现在按住按钮 1.2 秒即可写入设备。"
                : $"提权失败，保持连接：{result.Reason}";

            _shell.ReportStatus($"权限提升{(result.Succeeded ? "成功" : "失败")}：{result.Reason}");
            if (!result.Succeeded)
            {
                _shell.AddRecentOperation("提升权限（保存配置）", result.Reason, succeeded: false);
            }
        }
        finally
        {
            IsElevating = false;
        }
    }

    private void CopyResult()
    {
        if (AppServices.Clipboard.TrySetText(ResultText, out var error))
        {
            _shell.ReportStatus("设备回显已复制到剪贴板。");
        }
        else
        {
            _shell.ReportStatus($"复制失败：{error}");
        }
    }

    private void RefreshConnectionState()
    {
        OnPropertyChanged(nameof(DeviceNameText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(PrivilegeLevel));
        OnPropertyChanged(nameof(IsPrivilegeBlocked));
        OnPropertyChanged(nameof(CanElevate));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(PrivilegeHint));
        WriteCommand.RaiseCanExecuteChanged();
        ElevateCommand.RaiseCanExecuteChanged();

        if (!_connections.IsConnected && !IsWriting)
        {
            StatusText = "设备已断开：保存已停用（不会发送任何命令）。";
            ResetHold();
        }
    }
}
