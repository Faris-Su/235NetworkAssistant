using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 主窗口 ViewModel：左侧导航 + 顶部设备状态栏 + 中央内容 + 底部状态栏。
/// 只负责 Shell 层面的状态，不直接操作设备、不生成 CLI。
/// </summary>
public sealed class MainViewModel : ViewModelBase, IShellNavigator
{
    private const int MaxRecentOperations = 50;

    private readonly ConnectionService _connections;
    private readonly DispatcherTimer _clockTimer;
    private NavigationItem? _selectedNavigationItem;
    private ViewModelBase? _currentViewModel;
    private string _statusMessage = "就绪。Console / Telnet / SSH 连接，或直接查 SNMP 都可以；所有改配置的操作都会先给命令预览与确认。";
    private string _currentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private bool _isNavigationCollapsed;
    private bool _navigationOverriddenByUser;
    private bool _isFocusMode;

    public MainViewModel(ConnectionService connections)
    {
        _connections = connections;
        Title = AppInfo.ChineseName;

        NavigationItems = new ObservableCollection<NavigationItem>(BuildNavigation());
        ToggleNavigationCommand = new RelayCommand(ToggleNavigation);
        ToggleFocusModeCommand = new RelayCommand(ToggleFocusMode);
        SaveConfigCommand = new RelayCommand(ShowSaveConfig, () => AppServices.ConfigSave.CanSave);
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;

        _connections.SessionChanged += (_, _) =>
        {
            RefreshConnectionState();
            SaveConfigCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public ObservableCollection<RecentOperation> RecentOperations { get; } = new();

    public RelayCommand ToggleNavigationCommand { get; }

    public RelayCommand ToggleFocusModeCommand { get; }

    /// <summary>
    /// [保存配置]（write）。点击只打开确认弹窗；真正的写入要用户在弹窗里按住 1.2 秒。
    /// 未连接或刚保存过（冷却期）时按钮直接不可用。
    /// </summary>
    public RelayCommand SaveConfigCommand { get; }

    /// <summary>[保存配置] 按钮的悬停说明：平时讲清"按住确认"，不可用时直接说原因。</summary>
    public string SaveConfigHint
    {
        get
        {
            const string baseText =
                "保存配置（write）：把 running-config 写入设备启动配置，重启后仍生效。\n" +
                "点开后必须**按住按钮 1.2 秒**才会真正发送，中途松开就取消。";
            var blocked = AppServices.ConfigSave.BlockedReason;
            return string.IsNullOrEmpty(blocked) ? baseText : $"{baseText}\n现在不能按：{blocked}";
        }
    }

    /// <summary>左侧导航是否折叠为仅图标（小屏默认折叠，可手动切换）。</summary>
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        private set
        {
            if (SetProperty(ref _isNavigationCollapsed, value))
            {
                OnPropertyChanged(nameof(IsNavigationExpanded));
                OnPropertyChanged(nameof(NavigationWidth));
            }
        }
    }

    public bool IsNavigationExpanded => !IsNavigationCollapsed && !IsFocusMode;

    /// <summary>导航栏当前宽度（折叠 52，展开 188，专注模式 0）。</summary>
    public double NavigationWidth => IsFocusMode ? 0 : IsNavigationCollapsed ? 52 : 188;

    /// <summary>专注 CLI 模式：隐藏左侧导航，把空间让给终端。</summary>
    public bool IsFocusMode
    {
        get => _isFocusMode;
        private set
        {
            if (SetProperty(ref _isFocusMode, value))
            {
                OnPropertyChanged(nameof(IsNavigationExpanded));
                OnPropertyChanged(nameof(NavigationWidth));
                OnPropertyChanged(nameof(FocusModeButtonText));
                FocusModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string FocusModeButtonText => IsFocusMode ? "退出专注" : "专注 CLI";

    /// <summary>顶部状态栏状态图标（用 Segoe UI Emoji 渲染，避免与中文字体冲突）。</summary>
    public string ConnectionStateIcon => _connections.State switch
    {
        DeviceConnectionState.Connected => "🟢",
        DeviceConnectionState.Authenticating => "🟢",
        DeviceConnectionState.Ready => "🟢",
        DeviceConnectionState.Connecting => "🟡",
        DeviceConnectionState.Error => "🔴",
        _ => "⚪",
    };

    /// <summary>顶部状态栏单行文本：设备名 + 管理地址（连接方式与状态在 ToolTip 里）。</summary>
    public string CompactDeviceText
    {
        get
        {
            // 已连接但还没识别出主机名时，不能显示「未连接设备」——那会让用户以为连接失败了。
            var name = string.IsNullOrWhiteSpace(_connections.DeviceName)
                ? (_connections.IsConnected ? "已连接设备" : "未连接设备")
                : _connections.DeviceName!;
            var address = string.IsNullOrWhiteSpace(_connections.ManagementAddress) ? "—" : _connections.ManagementAddress!;
            return $"{name} · {address}";
        }
    }

    /// <summary>顶部状态栏悬停详情（连接方式、状态、Session、离线说明）。</summary>
    public string StatusBarTooltip =>
        $"设备名称：{DeviceNameText}\n" +
        $"管理地址：{ManagementAddressText}\n" +
        $"连接方式：{ConnectionKindText}\n" +
        $"连接状态：{ConnectionStateText}\n" +
        $"CLI 状态：{CliStateText}\n" +
        $"权限：{PrivilegeText}\n" +
        $"{SessionText}\n" +
        "离线可用：不依赖网络、在线服务或更新。";

    /// <summary>CLI 初始化维度说明：已连接但还没看到提示符时给出原因。</summary>
    public string CliStateText => string.IsNullOrWhiteSpace(_connections.CliStateText)
        ? (_connections.IsConnected ? "未确认" : "—")
        : _connections.CliStateText!;

    /// <summary>当前权限（特权模式 # / 普通模式 > / 权限未知）。</summary>
    public string PrivilegeText => _connections.PrivilegeLevel.ToChinese();

    public string PrivilegeIcon => _connections.PrivilegeLevel.ToIcon();

    /// <summary>已连接才显示权限标识。</summary>
    public bool ShowPrivilege => _connections.IsConnected;

    public string PrivilegeTooltip => _connections.PrivilegeLevel switch
    {
        DevicePrivilegeLevel.Privileged => "特权模式（#）：可以修改配置。",
        DevicePrivilegeLevel.User => "普通模式（>）：只能查看；改配置前会提示提升权限。",
        _ => "权限未知：还没拿到设备提示符。",
    };

    /// <summary>由 View 层负责弹出 Command Preview 对话框（ViewModel 不引用 View）。</summary>
    public event EventHandler<CommandPlan>? CommandPreviewRequested;

    /// <summary>由 View 层负责弹出 [保存配置]（write）对话框。</summary>
    public event EventHandler? SaveConfigRequested;

    /// <summary>专注 CLI 模式变化（页面据此调整自身布局）。</summary>
    public event EventHandler? FocusModeChanged;

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (SetProperty(ref _selectedNavigationItem, value) && value is not null)
            {
                _ = ActivateAsync(value);
            }
        }
    }

    public ViewModelBase? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set => SetProperty(ref _currentTimeText, value);
    }

    public string DeviceNameText => string.IsNullOrWhiteSpace(_connections.DeviceName)
        ? "未获取设备名"
        : _connections.DeviceName!;

    public string ManagementAddressText => string.IsNullOrWhiteSpace(_connections.ManagementAddress)
        ? "管理 IP：—"
        : (_connections.CurrentKind == DeviceConnectionKind.Serial
            ? $"串口：{_connections.ManagementAddress}"
            : $"管理 IP：{_connections.ManagementAddress}");

    public string ConnectionKindText => _connections.CurrentKind switch
    {
        DeviceConnectionKind.Serial => "Console / Serial",
        DeviceConnectionKind.Telnet => "Telnet",
        DeviceConnectionKind.Ssh => "SSH",
        _ => "未连接",
    };

    public string ConnectionStateText => _connections.State.ToChinese();

    public string SessionText => string.IsNullOrWhiteSpace(_connections.SessionId)
        ? "Session：—"
        : $"Session：{_connections.SessionId}";

    public bool IsConnected => _connections.IsConnected;

    public bool HasConnectionError => _connections.State == DeviceConnectionState.Error;

    /// <summary>启动 Shell：开启状态栏时钟，并默认打开第一个导航页面（概览）。</summary>
    public void Start()
    {
        _clockTimer.Start();
        if (SelectedNavigationItem is null && NavigationItems.Count > 0)
        {
            SelectedNavigationItem = NavigationItems[0];
        }
    }

    /// <summary>窗口尺寸变化时自动折叠/展开导航（用户手动切换后不再自动干预）。</summary>
    public void UpdateAdaptiveLayout(double windowWidth)
    {
        if (_navigationOverriddenByUser || IsFocusMode)
        {
            return;
        }

        var threshold = AppServices.Settings.NavCollapseWidth;
        IsNavigationCollapsed = windowWidth < threshold;
    }

    /// <summary>手动切换导航折叠（切换后本次运行不再自动改变）。</summary>
    public void ToggleNavigation()
    {
        _navigationOverriddenByUser = true;
        IsNavigationCollapsed = !IsNavigationCollapsed;
    }

    /// <summary>切换专注 CLI 模式（进入时自动跳到 CLI 页）。</summary>
    public void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;
        if (IsFocusMode)
        {
            NavigateTo("cli");
            ReportStatus("已进入专注 CLI 模式（再次点击[退出专注]或按 Esc 返回）。");
        }
        else
        {
            ReportStatus("已退出专注 CLI 模式。");
        }
    }

    public void SetFocusMode(bool enabled)
    {
        if (IsFocusMode != enabled)
        {
            ToggleFocusMode();
        }
    }

    /// <summary>关闭应用：停止时钟并释放页面资源（终端缓冲、会话文件、事件订阅）。</summary>
    public void Stop()
    {
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;

        foreach (var item in NavigationItems)
        {
            if (item.Instance is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // 退出阶段忽略释放异常。
                }
            }
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // 保存配置的冷却期会随时间自己结束：每秒刷一次按钮可用性与提示文案。
        SaveConfigCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SaveConfigHint));
    }

    public void NavigateTo(string key)
    {
        var item = NavigationItems.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            SelectedNavigationItem = item;
        }
    }

    public void NavigateTo(string key, object? parameter)
    {
        var item = NavigationItems.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectedNavigationItem = item;
        if (item.Instance is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    /// <summary>
    /// 写状态栏。状态栏是单行的：多行消息（例如结构化报错）在这里压成一行并限长，
    /// 否则会把状态栏撑高、挤压页面内容。
    /// </summary>
    public void ReportStatus(string message)
    {
        var single = (message ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (single.Length > 160)
        {
            single = single[..160] + "…";
        }

        StatusMessage = $"{DateTime.Now:HH:mm:ss}  {single}";
    }

    /// <param name="ledgerDetail">
    /// 只写进**操作台账**的详细内容（例如实际下发的命令原文）。
    /// 界面上的"最近操作"列表要保持短，所以两者分开 —— 台账是给交接班/回单看的，越具体越有用。
    /// </param>
    public void AddRecentOperation(string title, string detail, bool succeeded = true, string? ledgerDetail = null)
    {
        RecentOperations.Insert(0, new RecentOperation { Title = title, Detail = detail, Succeeded = succeeded });
        while (RecentOperations.Count > MaxRecentOperations)
        {
            RecentOperations.RemoveAt(RecentOperations.Count - 1);
        }

        // 落盘台账（按天 CSV）。**唯一汇聚点**：界面上所有"做过的事"都从这里过，
        // 所以只在这一处接线就能覆盖全部操作。写失败不影响主流程（见 OperationLedger）。
        AppServices.OperationLedger.Append(
            title,
            ledgerDetail ?? detail,
            succeeded,
            DeviceLabelForLedger());
    }

    /// <summary>台账里记的"设备"：优先主机名，其次管理地址；没连接就是空。</summary>
    private string DeviceLabelForLedger()
    {
        if (!_connections.IsConnected)
        {
            return string.Empty;
        }

        var name = _connections.DeviceName;
        var address = _connections.ManagementAddress;
        return string.IsNullOrWhiteSpace(name) ? address ?? string.Empty
            : string.IsNullOrWhiteSpace(address) ? name
            : $"{name}（{address}）";
    }

    public void ShowCommandPreview(CommandPlan plan) => CommandPreviewRequested?.Invoke(this, plan);

    /// <summary>
    /// 打开 [保存配置] 对话框。这里只负责"弹窗"，
    /// 真正的写入由弹窗里的按住确认触发（见 SaveConfigViewModel）。
    /// </summary>
    public void ShowSaveConfig() => SaveConfigRequested?.Invoke(this, EventArgs.Empty);

    private async Task ActivateAsync(NavigationItem item)
    {
        // 先通知"上一个页面要离开了"：像 SNMP 页的自动刷新这种只该在页面上跑的东西，
        // 不能因为用户切走就一直在后台查设备（页面 VM 是缓存复用的，不能用 Dispose 表达"离开"）。
        if (CurrentViewModel is INavigationAware previousAware &&
            !ReferenceEquals(CurrentViewModel, item.Instance))
        {
            try
            {
                previousAware.OnNavigatedFrom();
            }
            catch (Exception ex)
            {
                AppServices.Log.Warn("页面离开处理失败", ex);
            }
        }

        ViewModelBase viewModel;
        try
        {
            viewModel = item.Instance ??= item.Factory();
        }
        catch (Exception ex)
        {
            // 页面构造失败（例如本地目录不可写）必须说出来，否则表现为“点了没反应”。
            ReportStatus($"页面打开失败：{item.Title}｜{ex.Message}");
            AppServices.Log.Error($"页面创建失败：{item.Title}", ex);
            return;
        }

        CurrentViewModel = viewModel;
        try
        {
            await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportStatus($"页面初始化失败：{ex.Message}");
        }
    }

    private IEnumerable<NavigationItem> BuildNavigation()
    {
        yield return Item("overview", NavigationGlyph.Home, "概览", () => new OverviewViewModel(this, _connections, AppServices.Commands, RecentOperations));
        yield return Item("connection", NavigationGlyph.Connect, "连接", () => new ConnectionViewModel(this, _connections, AppServices.ResourceRepository));
        yield return Item("cli", NavigationGlyph.CommandPrompt, "CLI", () => new CliViewModel(this, _connections));
        yield return Item("ports", NavigationGlyph.Ethernet, "端口", () => new PortsViewModel(this, _connections, AppServices.Commands));
        yield return Item("vlan", NavigationGlyph.Tag, "VLAN", () => new VlanViewModel(this, _connections, AppServices.Commands));
        yield return Item("trunk", NavigationGlyph.Share, "Trunk", () => new TrunkViewModel(this, _connections, AppServices.Commands));
        yield return Item("lldp", NavigationGlyph.Relationship, "LLDP", () => new LldpViewModel(this, _connections, AppServices.Commands));
        yield return Item("macip", NavigationGlyph.Search, "MAC/IP", () => new MacIpViewModel(this, _connections, AppServices.Commands, AppServices.ResourceRepository));
        yield return Item("snmp", NavigationGlyph.NetworkTower, "SNMP", () => new SnmpViewModel(this, _connections));
        // 巡检 / 定位：用户明确反馈"完全用不上、只占地方"，已从导航移除。
        // VM/View/服务（SnmpInspectionService / SnmpMacLocator / InspectionViewModel / LocateViewModel）
        // 都留在仓库里没删 —— 哪天要用，把下面两行取消注释即可恢复：
        // yield return Item("inspection", "🧭", "巡检", () => new InspectionViewModel(
        //     this, AppServices.ResourceRepository,
        //     new SnmpInspectionService(AppServices.SnmpQuery, AppServices.Log), PickFolder));
        // yield return Item("locate", "📍", "定位", () => new LocateViewModel(
        //     this, AppServices.ResourceRepository,
        //     new SnmpMacLocator(new SnmpV2cClient(), AppServices.Log), PickFolder));
        // QuickPing：随手敲一个地址就 ping，也能一次 ping 一片（网段简写 / 资源库筛出的设备）。
        // 说明：用户反馈「巡检 / 定位」这两个功能他们完全用不上、只占地方，所以从导航里去掉了
        //（VM/View/服务代码保留在仓库里，要恢复只需把上面那两个 Item 加回来）。
        yield return Item("quickping", NavigationGlyph.LightningBolt, "QuickPing", () => new QuickPingViewModel(
            this,
            AppServices.ResourceRepository,
            PickFolder));
        yield return Item("device", NavigationGlyph.Devices, "设备", () => new DeviceInfoViewModel(this, _connections, AppServices.Commands));
        yield return Item("backup", NavigationGlyph.Save, "备份", () => new BackupViewModel(this, _connections));
        yield return Item("resources", NavigationGlyph.Library, "资源库", () => new ResourceLibraryViewModel(this, AppServices.ResourceRepository));
        yield return Item("settings", NavigationGlyph.Settings, "设置", () => new SettingsViewModel(this, AppServices.Settings, AppServices.ResourceRepository, AppServices.ExcelImporter));
    }

    // Segoe MDL2 Assets 字形：Windows 10 系统自带，避免 emoji 在不同电脑上尺寸/颜色不一致。
    private static class NavigationGlyph
    {
        public const string Home = "\uE80F";
        public const string Connect = "\uE703";
        public const string CommandPrompt = "\uE756";
        public const string Ethernet = "\uE839";
        public const string Tag = "\uE8EC";
        public const string Share = "\uE72D";
        public const string Relationship = "\uF003";
        public const string Search = "\uE721";
        public const string NetworkTower = "\uEC05";
        public const string LightningBolt = "\uE945";
        public const string Devices = "\uE772";
        public const string Save = "\uE74E";
        public const string Library = "\uE8F1";
        public const string Settings = "\uE713";
    }

    private static NavigationItem Item(string key, string icon, string title, Func<ViewModelBase> factory) =>
        new() { Key = key, Icon = icon, Title = title, Factory = factory };

    /// <summary>
    /// 让不依赖 WPF 的页面 VM 也能选目录：把对话框留在这一层（MainViewModel 属于 WPF 工程，
    /// 而 <see cref="InspectionViewModel"/> 要能进自检工程被自动化覆盖）。
    /// 用户取消返回 null。
    /// </summary>
    private static string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择巡检清单的保存目录",
            InitialDirectory = Directory.Exists(Helpers.AppPaths.BackupDirectory)
                ? Helpers.AppPaths.BackupDirectory
                : Helpers.AppPaths.RootDirectory,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void RefreshConnectionState()
    {
        OnPropertyChanged(nameof(DeviceNameText));
        OnPropertyChanged(nameof(ManagementAddressText));
        OnPropertyChanged(nameof(ConnectionKindText));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CliStateText));
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(PrivilegeIcon));
        OnPropertyChanged(nameof(ShowPrivilege));
        OnPropertyChanged(nameof(HasConnectionError));
        OnPropertyChanged(nameof(CompactDeviceText));
        OnPropertyChanged(nameof(ConnectionStateIcon));
        OnPropertyChanged(nameof(StatusBarTooltip));
    }
}
