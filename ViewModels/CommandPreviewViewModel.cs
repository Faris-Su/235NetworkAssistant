using System.Windows;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// Command Preview（配置安全核心）：显示操作说明 + 影响范围 + CLI 命令，
/// 只有用户点击[执行]才会发送。断线时禁止执行。
/// </summary>
public sealed class CommandPreviewViewModel : ObservableObject
{
    private readonly CommandService _commands;
    private readonly ConnectionService _connections;
    private readonly IShellNavigator _shell;
    private string _resultText = string.Empty;
    private bool _isExecuting;
    private bool _hasResult;

    public CommandPreviewViewModel(
        CommandPlan plan,
        CommandService commands,
        ConnectionService connections,
        IShellNavigator shell)
    {
        Plan = plan;
        _commands = commands;
        _connections = connections;
        _shell = shell;

        CopyCommand = new RelayCommand(CopyToClipboard);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
        ElevateCommand = new AsyncRelayCommand(ElevateAsync, () => CanElevate);
        _enablePassword = AppServices.EnablePassword;
        _privilegeNotice = _connections.PrivilegeNotice ?? string.Empty;
    }

    private string _enablePassword;
    private string _privilegeNotice;
    private string _privilegeStatus = string.Empty;
    private bool _isElevating;

    public event EventHandler? CloseRequested;

    public CommandPlan Plan { get; }

    public string Title => Plan.Title;

    public string Description => Plan.Description;

    public string ImpactScope => string.IsNullOrWhiteSpace(Plan.ImpactScope) ? "未标注影响范围。" : Plan.ImpactScope;

    public string CommandsText => Plan.CommandsText;

    public string RiskText => Plan.RiskLevel switch
    {
        CommandRiskLevel.Safe => "安全：只读或新增，不影响现有业务",
        CommandRiskLevel.Caution => "注意：会改变端口/VLAN 状态，请确认影响范围",
        CommandRiskLevel.Dangerous => "危险：可能造成业务中断，请先备份 running-config",
        _ => string.Empty,
    };

    public bool IsDangerous => Plan.IsDangerous;

    public bool IsConnected => _connections.IsConnected;

    /// <summary>这条命令计划是否需要管理权限（本项目里非 show 命令都是配置命令）。</summary>
    public bool RequiresPrivilege => Plan.Commands.Any(command =>
        !string.IsNullOrWhiteSpace(command) &&
        !command.TrimStart().StartsWith("show", StringComparison.OrdinalIgnoreCase));

    public DevicePrivilegeLevel PrivilegeLevel => _connections.PrivilegeLevel;

    /// <summary>明确处于普通模式且该操作需要管理权限 → 需要先提升权限。</summary>
    public bool IsPrivilegeBlocked => RequiresPrivilege && !PrivilegeLevel.CanConfigure();

    public bool CanExecute => _connections.IsConnected && !IsExecuting && !IsPrivilegeBlocked;

    public bool CanElevate => _connections.IsConnected && !IsElevating && PrivilegeLevel != DevicePrivilegeLevel.Privileged;

    /// <summary>权限提示（提升失败原因 / 已进入特权模式）。</summary>
    public string PrivilegeNotice
    {
        get => _privilegeNotice;
        private set => SetProperty(ref _privilegeNotice, value);
    }

    public string PrivilegeStatus
    {
        get => _privilegeStatus;
        private set => SetProperty(ref _privilegeStatus, value);
    }

    /// <summary>Enable 密码输入（默认值来自 AppServices，可改；不落盘）。</summary>
    public string EnablePassword
    {
        get => _enablePassword;
        set => SetProperty(ref _enablePassword, value);
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

    public string PrivilegeHint => IsPrivilegeBlocked
        ? "当前设备处于普通模式（>），该操作需要管理权限。填写 Enable 密码后点[提升权限]，成功后会自动继续。"
        : PrivilegeLevel == DevicePrivilegeLevel.Privileged
            ? "当前权限：特权模式（#），可以直接执行。"
            : "当前权限未知：将按现有权限直接尝试执行。";

    public string ExecuteHint => _connections.IsConnected
        ? "点击[执行]后命令将逐条发送到设备（软件不会自动 write；要保存到启动配置，改完后到顶栏 [保存配置] 按住确认）。"
        : "设备未连接：只能复制命令，无法执行。";

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(ExecuteHint));
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand CopyCommand { get; }

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand ExecuteCommand { get; }

    /// <summary>提升权限：enable → 输入密码 → 成功后**自动继续执行**原操作。</summary>
    public AsyncRelayCommand ElevateCommand { get; }

    /// <summary>
    /// 提升权限并在成功后自动继续原来的操作（不需要用户重新走一遍流程）。
    /// 失败只显示原因，不关闭窗口、不影响连接。
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
            _shell.ReportStatus($"权限提升{(result.Succeeded ? "成功" : "失败")}：{result.Reason}");

            OnPropertyChanged(nameof(PrivilegeLevel));
            OnPropertyChanged(nameof(IsPrivilegeBlocked));
            OnPropertyChanged(nameof(CanExecute));
            OnPropertyChanged(nameof(PrivilegeHint));
            ExecuteCommand.RaiseCanExecuteChanged();
            ElevateCommand.RaiseCanExecuteChanged();

            if (result.Succeeded)
            {
                // 继续执行原操作：用户不需要再点一次
                await ExecuteAsync().ConfigureAwait(true);
            }
            else
            {
                _shell.AddRecentOperation("提升权限", result.Reason, succeeded: false);
            }
        }
        finally
        {
            IsElevating = false;
        }
    }

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetText(CommandsText);
            _shell.ReportStatus("命令已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _shell.ReportStatus($"复制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 台账里那一条的正文：把这次**实际下发的命令**逐条写清楚，失败的那条标出来。
    /// 为什么值得单独拼：交接班问的是"他在那台交换机上到底敲了什么"，
    /// 只写"执行命令：将端口划入 VLAN 100（3 条）"等于没说。
    /// </summary>
    private string BuildLedgerDetail(CommandExecutionResult result)
    {
        var lines = new List<string>
        {
            $"{Plan.Title}（{(result.Succeeded ? "成功" : "失败")}）",
        };

        foreach (var output in result.Outputs)
        {
            lines.Add($"{(output.Succeeded ? "✓" : "✗")} {output.Command}");
            if (!output.Succeeded && output.Error is { Length: > 0 } error)
            {
                lines.Add($"↳ {error}");
            }
        }

        // 台账是 CSV，一格里放多行会给阅读添麻烦 → 用「 / 」连成一行
        return string.Join(" / ", lines);
    }

    private async Task ExecuteAsync()
    {
        if (!_connections.IsConnected)
        {
            ResultText = "设备未连接，禁止发送命令。";
            HasResult = true;
            return;
        }

        IsExecuting = true;
        try
        {
            var result = await _commands.ExecuteAsync(Plan).ConfigureAwait(true);
            ResultText = result.RawText;
            HasResult = true;
            // 失败时把"具体原因"带进状态栏与最近操作：用户要求看到报错就知道该怎么修，
            // 只说"失败"等于没说（以前这里连失败都不会说，见 CommandService 里被拒判定那段的注释）。
            _shell.ReportStatus(result.Succeeded
                ? $"命令执行完成：{Plan.Title}"
                : $"命令执行失败：{Plan.Title} — {result.FailureSummary}");
            _shell.AddRecentOperation(
                "执行命令",
                result.Succeeded
                    ? $"{Plan.Title}（{result.Outputs.Count} 条）"
                    : $"{Plan.Title}：{result.FailureSummary}",
                result.Succeeded,
                // 台账里记**实际下发的命令原文**（交接班/回单要看的就是这个）：
                // 界面上的列表只放标题（要短），台账放全文（越具体越有用）。
                ledgerDetail: BuildLedgerDetail(result));
        }
        catch (Exception ex)
        {
            ResultText = $"执行失败：{ex.Message}";
            HasResult = true;
            _shell.ReportStatus($"执行失败：{ex.Message}");
            _shell.AddRecentOperation(
                "执行命令",
                $"{Plan.Title}：{ex.Message}",
                succeeded: false,
                ledgerDetail: $"{Plan.Title}｜未下发（异常）：{ex.Message}｜计划命令：{Plan.CommandsText}");
        }
        finally
        {
            IsExecuting = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }
}
