using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 以 show 命令为基础的设备页面（端口 / VLAN / Trunk / LLDP）公共部分：
/// 连接状态、忙碌状态、原始输出与解析提示、执行只读命令、Command Preview 入口。
/// 刷新只由用户点击触发，不做任何自动轮询。
/// </summary>
public abstract class DeviceShowPageViewModel : ViewModelBase
{
    private bool _isBusy;
    private string _statusHint = string.Empty;
    private string _rawOutput = string.Empty;
    private string _parserNote = string.Empty;
    private string _lastCommand = "—";
    private string _parseSummary = "尚未刷新。";
    private string? _lastSessionId;
    private readonly List<System.Collections.IList> _cachedCollections = new();

    protected DeviceShowPageViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
    {
        Shell = shell;
        Connections = connections;
        Commands = commands;

        // 断开后仍然允许点[刷新]：这一次刷新不会发任何命令（RequireConnection 会拦住），
        // 只是把上一台设备残留的表格清掉，让页面不显示"别的设备的数据"。
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected && !IsBusy);
        Connections.SessionChanged += (_, _) => OnSessionChanged();
    }

    protected IShellNavigator Shell { get; }

    protected ConnectionService Connections { get; }

    protected CommandService Commands { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsConnected => Connections.IsConnected;

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnBusyChanged();
            }
        }
    }

    /// <summary>
    /// 子类如果还有 CanExecute 依赖 IsBusy 的命令，在这里刷新它们的可用状态——
    /// 少了这一步，按钮就会停在“上一次判断”的状态（例如刷新完了仍然灰着）。
    /// </summary>
    protected virtual void OnBusyChanged()
    {
    }

    public string StatusHint
    {
        get => _statusHint;
        protected set => SetProperty(ref _statusHint, value);
    }

    /// <summary>最近一次 show 命令的原始输出（始终保留）。</summary>
    public string RawOutput
    {
        get => _rawOutput;
        private set
        {
            if (SetProperty(ref _rawOutput, value))
            {
                OnPropertyChanged(nameof(RawInfo));
            }
        }
    }

    /// <summary>解析提示：解析失败时明确说明只显示原始输出。</summary>
    public string ParserNote
    {
        get => _parserNote;
        protected set
        {
            if (SetProperty(ref _parserNote, value))
            {
                OnPropertyChanged(nameof(HasParserNote));
            }
        }
    }

    public bool HasParserNote => !string.IsNullOrWhiteSpace(ParserNote);

    public string LastCommand
    {
        get => _lastCommand;
        private set
        {
            if (SetProperty(ref _lastCommand, value))
            {
                OnPropertyChanged(nameof(RawInfo));
            }
        }
    }

    public string RawInfo => $"最近命令：{LastCommand}｜原始输出 {RawOutput.Length:N0} 字符";

    /// <summary>结构化解析摘要（例如“解析 24 行，2 行未识别”）。</summary>
    public string ParseSummary
    {
        get => _parseSummary;
        protected set => SetProperty(ref _parseSummary, value);
    }

    public string ConnectionHint => IsConnected
        ? $"{(string.IsNullOrWhiteSpace(Connections.DeviceName) ? "设备" : Connections.DeviceName)}｜{Connections.ManagementAddress}｜{Connections.CurrentKind}"
        : "未连接设备：请先在【连接】页连接 Console 或 Telnet。";

    public string NoPollingNote => "刷新由用户点击触发；执行结果中的原始输出始终保留，解析失败不影响信息查看。";

    protected abstract Task RefreshAsync();

    protected void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionHint));
        RefreshCommand.RaiseCanExecuteChanged();

        // 断开、或连上了另一台设备（例如 LLDP 连到邻居）→ **立刻清空本页数据**，
        // 否则页面会继续显示上一台设备的端口/VLAN/MAC，被误读成"新设备的数据"（现场反馈）。
        //
        // 两个坑都要绕开：
        //  ① 事件可能在后台线程触发 → 改绑定到表格的 ObservableCollection 会抛跨线程异常，
        //     所以投递到 UI 线程执行；
        //  ② 投递进去的任务**绝不能再读 ConnectionService**（它的属性有锁）——
        //     断开流程正持有那把锁并等 UI 线程，UI 线程又去等锁 → 界面卡死（现场实测）。
        //     所以下面先把要用的状态读出来（在事件线程上），只把纯 UI 工作投递过去。
        var connected = Connections.IsConnected;
        var sessionId = connected ? Connections.SessionId : null;
        if (!string.Equals(_lastSessionId, sessionId, StringComparison.Ordinal))
        {
            _lastSessionId = sessionId;
            ClearDeviceData(connected);
        }

        OnSessionChangedCore();
    }

    /// <summary>立刻清空与设备绑定的缓存（原始输出、解析摘要、子类自己的表格）。</summary>
    private void ClearDeviceData(bool connected)
    {
        // connected 是调用方在事件线程上先取好的，这里不再碰 ConnectionService（见上面的说明）
        UiThread.Post(() =>
        {
            RawOutput = string.Empty;
            ParserNote = string.Empty;
            LastCommand = "—";
            ParseSummary = "尚未刷新。";
            StatusHint = connected
                ? "已连接新设备：点[刷新]读取数据（上一台设备的数据已清空）。"
                : "设备已断开：本页数据已清空，重新连接后点[刷新]。";
            ResetDeviceData();
            RefreshCommand.RaiseCanExecuteChanged();
        });
    }

    /// <summary>
    /// 子类把"跟设备绑定、换设备后必须清空"的表格注册进来（在构造函数里调一次即可），
    /// 断开 / 换设备时统一清空，不需要每个页面各写一套清理逻辑。
    /// </summary>
    protected void RegisterCachedCollection(System.Collections.IList collection) =>
        _cachedCollections.Add(collection);

    /// <summary>
    /// 清空本页缓存（断开或换设备时调用）。默认清掉所有注册过的表格；
    /// 只在"会话 ID 变化"或"断开"时触发，不会因为普通状态刷新而清数据。
    /// </summary>
    protected virtual void ResetDeviceData()
    {
        foreach (var collection in _cachedCollections)
        {
            collection.Clear();
        }
    }

    protected virtual void OnSessionChangedCore()
    {
    }

    /// <summary>执行一条只读 show 命令并保存原始输出。</summary>
    protected async Task<string?> RunShowAsync(string command)
    {
        if (!RequireConnection())
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var raw = await Commands.RunShowCommandAsync(command).ConfigureAwait(true);
            LastCommand = command;
            RawOutput = raw;

            // 顺手从提示符里提取设备名（不额外发命令），让顶部状态栏显示真实设备名。
            Connections.UpdateDeviceIdentity(ShowOutputParser.TryExtractHostnameFromPrompt(raw));

            // 分页信息：让用户知道“这次读了几页”，也能确认自动续页真的生效了。
            var connection = Connections.Current;
            var pagingNote = connection is null || connection.LastCommandPageCount == 0
                ? string.Empty
                : connection.LastCommandHitPageLimit
                    ? $"｜自动续页 {connection.LastCommandPageCount} 页后触发上限（可能未识别提示符）"
                    : $"｜自动续页 {connection.LastCommandPageCount} 页";

            if (connection is { LastCommandEndedAtPrompt: false, LastCommandPageCount: 0 })
            {
                pagingNote = "｜未检测到命令提示符（输出可能被截断，请点[刷新]重试或查看原始输出）";
            }

            StatusHint = $"已执行 {command}（{DateTime.Now:HH:mm:ss}）{pagingNote}";
            return raw;
        }
        catch (Exception ex)
        {
            StatusHint = $"执行失败：{ex.Message}";
            AppServices.Log.Warn($"执行 {command} 失败", ex);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected bool RequireConnection()
    {
        if (Connections.IsConnected)
        {
            return true;
        }

        StatusHint = "设备未连接：请先在【连接】页连接 Console 或 Telnet（断线禁止发送命令）。";
        return false;
    }

    /// <summary>
    /// 子类自己编排多条命令时（例如 LLDP：基础邻居列表 + 逐端口 Detail）用它记录
    /// “最近命令”和完整原始输出，保证用户始终能看到设备原文。
    /// </summary>
    protected void SetRawOutput(string command, string raw)
    {
        LastCommand = command;
        RawOutput = raw;
    }

    /// <summary>所有配置操作统一走 Command Preview，只有用户点[执行]才会发送。</summary>
    protected void PreviewCommands(CommandPlan plan)
    {
        Shell.ShowCommandPreview(plan);
        StatusHint = $"已生成命令预览：{plan.Title}（确认后才会执行）";
    }

    protected void ReportNoPorts()
    {
        StatusHint = "请先勾选端口，或在“端口输入”里填写要操作的端口（例如 g0/1-3,g0/5）。";
    }
}
