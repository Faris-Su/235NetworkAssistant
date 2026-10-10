using System.Collections.ObjectModel;
using System.IO;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>概览：当前设备、连接状态、常用操作入口、最近操作记录。不做自动诊断。</summary>
public sealed class OverviewViewModel : ViewModelBase
{
    private readonly IShellNavigator _shell;
    private readonly ConnectionService _connections;
    private readonly CommandService _commands;
    private readonly ObservableCollection<RecentOperation> _operations;
    private string _snmpStatus = "SNMP：未启用。到【设置】页填写只读 Community 后即可读取设备信息。";
    private bool _isSnmpBusy;
    private int _snmpRequestGeneration;
    private bool _isManagementIpBusy;
    private string _managementIpActionMessage = string.Empty;

    /// <param name="recentOperations">
    /// 最近操作列表。由 Shell 传进来（而不是本类去判断 <c>shell is MainViewModel</c>）：
    /// 概览页不该依赖 MainViewModel，否则自检/测试里想单独构造它就绕不开 WPF 主窗口。
    /// </param>
    public OverviewViewModel(
        IShellNavigator shell,
        ConnectionService connections,
        CommandService commands,
        ObservableCollection<RecentOperation>? recentOperations = null)
    {
        _shell = shell;
        _connections = connections;
        _commands = commands;
        _operations = recentOperations ?? new ObservableCollection<RecentOperation>();
        Title = "概览";

        OpenConnectionCommand = new RelayCommand(() => _shell.NavigateTo("connection"));
        OpenCliCommand = new RelayCommand(() => _shell.NavigateTo("cli"));
        OpenBackupCommand = new RelayCommand(() => _shell.NavigateTo("backup"));
        OpenResourcesCommand = new RelayCommand(() => _shell.NavigateTo("resources"));
        OpenSnmpCommand = new RelayCommand(() => _shell.NavigateTo("snmp"));
        OpenLedgerFolderCommand = new RelayCommand(OpenLedgerFolder);
        PingCommand = new AsyncRelayCommand(RunPingAsync, CanRunTool);
        TracertCommand = new AsyncRelayCommand(RunTracertAsync, CanRunTool);
        StopToolCommand = new RelayCommand(StopTool, () => IsToolBusy);
        RefreshCommand = new RelayCommand(Refresh);
        RefreshSnmpCommand = new RelayCommand(() => _ = LoadSnmpAsync(), () => !_isSnmpBusy && IsSnmpEnabled);
        ShowCommandPreviewDemoCommand = new RelayCommand(ShowCommandPreviewDemo);
        RefreshLocalNetworkCommand = new AsyncRelayCommand(RefreshLocalNetworkAsync, () => !_isLocalNetworkBusy);
        ReadManagementIpCommand = new AsyncRelayCommand(
            ReadManagementIpAsync,
            () => !_isManagementIpBusy && _connections.IsConnected && _connections.IsCliReady);

        _connections.SessionChanged += (_, _) =>
        {
            Refresh();
            ReadManagementIpCommand.RaiseCanExecuteChanged();
        };
        AppServices.ResourceRepository.Changed += (_, _) => Refresh();
        AppServices.Snmp.EnabledChanged += OnSnmpEnabledChanged;

        // 网络工具的默认目标：当前设备 IP / SNMP 上次用过的 IP
        _toolHost = ResolveSnmpTarget();
    }

    public ObservableCollection<RecentOperation> RecentOperations => _operations;

    /// <summary>本机网卡列表（只读快照，见 <see cref="LocalNetworkInfoService"/>）。</summary>
    public Helpers.BulkObservableCollection<LocalNetworkAdapter> LocalAdapters { get; } = new();

    private List<LocalNetworkAdapter> _allLocalAdapters = new();
    private bool _showVirtualAdapters;

    /// <summary>
    /// 是否把**虚拟网卡**（VMware / Hyper-V / VPN / TAP…）也列出来。默认关：
    /// 现场那台机器有 6~8 个虚拟网卡，全列出来会把真正该看的物理网卡淹掉。
    /// </summary>
    public bool ShowVirtualAdapters
    {
        get => _showVirtualAdapters;
        set
        {
            if (SetProperty(ref _showVirtualAdapters, value))
            {
                ApplyLocalAdapterFilter();
            }
        }
    }

    private string _localNetworkVirtualNote = string.Empty;

    /// <summary>被折叠掉的虚拟网卡数量说明（有的话点一下上面的勾就能看到）。</summary>
    public string LocalNetworkVirtualNote
    {
        get => _localNetworkVirtualNote;
        private set => SetProperty(ref _localNetworkVirtualNote, value);
    }

    private bool _isLocalNetworkBusy;
    private string _localNetworkGateway = "正在读取本机网卡…";
    private string _localNetworkDns = string.Empty;
    private string _localNetworkHint = string.Empty;
    private string _localNetworkAt = string.Empty;

    /// <summary>默认网关那一行（没有网关时明确说"没有"，而不是留空）。</summary>
    public string LocalNetworkGateway
    {
        get => _localNetworkGateway;
        private set => SetProperty(ref _localNetworkGateway, value);
    }

    public string LocalNetworkDns
    {
        get => _localNetworkDns;
        private set
        {
            if (SetProperty(ref _localNetworkDns, value))
            {
                OnPropertyChanged(nameof(HasLocalNetworkDns));
            }
        }
    }

    /// <summary>有没有 DNS 可显示（没读到就整行不占位）。</summary>
    public bool HasLocalNetworkDns => !string.IsNullOrWhiteSpace(LocalNetworkDns);

    /// <summary>同网段结论（连不上时最该看的一句）。</summary>
    public string LocalNetworkHint
    {
        get => _localNetworkHint;
        private set => SetProperty(ref _localNetworkHint, value);
    }

    public string LocalNetworkAt
    {
        get => _localNetworkAt;
        private set => SetProperty(ref _localNetworkAt, value);
    }

    /// <summary>[刷新] 本机网络信息：插拔网线、换网络之后点一下。</summary>
    public AsyncRelayCommand RefreshLocalNetworkCommand { get; }

    /// <summary>
    /// 【概览】页「管理 IP」那一格的 [获取]（用户 2026-09-25 要求）：
    /// 点一下就从设备读一次 `show ip interface brief`，把**设备自己的**管理 IP 填到这一格，
    /// 不用专门跳到【设备】页去点。
    ///
    /// 为什么是按钮而不是连接时自动读：用户明确要求"别在连接时自动发命令"——
    /// 自动发命令在登录没走完 / 链路慢的现场会把会话搅乱。
    /// </summary>
    public AsyncRelayCommand ReadManagementIpCommand { get; }

    /// <summary>正在读管理 IP（按钮显示"读取中…"，避免连点）。</summary>
    public bool IsManagementIpBusy
    {
        get => _isManagementIpBusy;
        private set
        {
            if (SetProperty(ref _isManagementIpBusy, value))
            {
                OnPropertyChanged(nameof(ManagementIpButtonText));
                ReadManagementIpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>按钮文字：没有值 →「获取」，已有值 →「重取」，读取中 →「读取中…」。</summary>
    public string ManagementIpButtonText =>
        _isManagementIpBusy ? "读取中…" : _connections.LearnedManagementAddress is null ? "获取" : "重取";

    /// <summary>
    /// 管理 IP 那一格下面那行小字：优先显示刚点完[获取]的结果，
    /// 否则告诉用户"现在这个值是从哪来的、想拿设备真实地址该点哪儿"。
    /// </summary>
    public string ManagementIpNotice
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_managementIpActionMessage))
            {
                return _managementIpActionMessage;
            }

            if (_connections.LearnedManagementAddress is not null)
            {
                return $"管理 IP 来自设备读取（{Services.ManagementAddressReader.Command}）。";
            }

            if (!_connections.IsConnected)
            {
                return "连上设备后，可在这一格点[获取]直接读它的管理 IP（不用去【设备】页）。";
            }

            var dialed = IpAddressHelper.SplitEndpoint(_connections.ManagementAddress).Ip;
            return string.IsNullOrWhiteSpace(dialed)
                ? "配置线连接不携带 IP：点[获取]从设备读一次即可。"
                : IsLoopbackLike(dialed)
                    ? $"当前显示的是拨号地址 {dialed}（隧道/本机）。点[获取]读设备真实的管理 IP。"
                    : "点[获取]可从设备读一次真实的管理 IP（当前显示的是拨号地址）。";
        }
    }

    /// <summary>127.x / localhost / 0.0.0.0 —— 这类地址一定不是交换机自己的管理 IP（隧道、端口映射常见）。</summary>
    private static bool IsLoopbackLike(string ip) =>
        ip.StartsWith("127.", StringComparison.Ordinal)
        || ip.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || ip == "0.0.0.0"
        || ip == "::1";

    /// <summary>[获取] 的实际动作：读一次设备的三层接口 → 挑一个管理 IP → 回填会话 + 刷新本页。</summary>
    private async Task ReadManagementIpAsync()
    {
        IsManagementIpBusy = true;
        _managementIpActionMessage = "正在执行 " + Services.ManagementAddressReader.Command + " …";
        OnPropertyChanged(nameof(ManagementIpNotice));
        try
        {
            var result = await Services.ManagementAddressReader
                .ReadAsync(_commands, _connections, AppServices.Log, CancellationToken.None)
                .ConfigureAwait(true);
            _managementIpActionMessage = result.Success
                ? $"{result.Message}（来源：设备实际返回的三层接口）"
                : result.Message;
        }
        finally
        {
            IsManagementIpBusy = false;
            OnPropertyChanged(nameof(ManagementIpNotice));
            OnPropertyChanged(nameof(ManagementAddress));
            OnPropertyChanged(nameof(ManagementIpButtonText));
        }
    }

    /// <summary>
    /// 读本机网卡（只读、不碰设备）。放在后台线程跑，读完一次性替换集合 + 更新摘要。
    /// </summary>
    private async Task RefreshLocalNetworkAsync()
    {
        if (_isLocalNetworkBusy)
        {
            return;
        }

        _isLocalNetworkBusy = true;
        RefreshLocalNetworkCommand.RaiseCanExecuteChanged();
        try
        {
            // 同网段检查的目标：优先当前连接设备的 IP（Telnet/SSH），其次【设备】页读到的管理 IP，
            // 最后才用 SNMP 上次查询的地址 —— 都比拿"上次查 SNMP 的地址"更贴近"我现在要连谁"。
            var target = IpAddressHelper.ExtractFirst(_connections.ManagementAddress)
                         ?? _connections.LearnedManagementAddress
                         ?? ResolveSnmpTarget();
            var snapshot = await Task.Run(() => LocalNetworkInfoService.Collect(target)).ConfigureAwait(true);
            _allLocalAdapters = snapshot.Adapters.ToList();
            ApplyLocalAdapterFilter();
            LocalNetworkGateway = snapshot.DefaultGateway == "—"
                ? "默认网关：没有（本机可能没连网）"
                : $"默认网关：{snapshot.DefaultGateway}";
            LocalNetworkDns = snapshot.DnsServers == "—" ? string.Empty : $"DNS：{snapshot.DnsServers}";
            LocalNetworkHint = snapshot.SubnetNote;
            LocalNetworkAt = $"读取时间：{snapshot.CollectedAt:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            LocalNetworkHint = $"读取本机网卡失败：{ex.Message}（不影响其它功能）";
            AppServices.Log.Debug($"概览页读本机网卡失败：{ex.Message}");
        }
        finally
        {
            _isLocalNetworkBusy = false;
            RefreshLocalNetworkCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>按"是否显示虚拟网卡"把列表刷新一遍，并给出被折叠的数量说明。</summary>
    private void ApplyLocalAdapterFilter()
    {
        var visible = ShowVirtualAdapters
            ? _allLocalAdapters
            : _allLocalAdapters.Where(a => !a.IsVirtual).ToList();
        LocalAdapters.ReplaceAll(visible);

        var hiddenVirtual = _allLocalAdapters.Count(a => a.IsVirtual);
        LocalNetworkVirtualNote = hiddenVirtual == 0
            ? string.Empty
            : ShowVirtualAdapters
                ? $"（已展开 {hiddenVirtual} 个虚拟网卡）"
                : $"另有 {hiddenVirtual} 个虚拟网卡（VMware / Hyper-V / VPN / TAP…）已折叠 —— 勾选上面那格可以看。";
    }

    /// <summary>SNMP 简报（型号 / 版本 / 序列号 / 内存 / 运行时间…），完整信息在【SNMP】页。</summary>
    public ObservableCollection<SnmpField> SnmpLines { get; } = new();

    /// <summary>SNMP 查询目标（优先当前 CLI 设备 IP；没有 CLI 连接时用上次查询过的 IP）。</summary>
    public string SnmpTargetText
    {
        get
        {
            var ip = ResolveSnmpTarget();
            return string.IsNullOrWhiteSpace(ip)
                ? "未查询 SNMP：还没有目标 IP（可到【SNMP】页填写 IP 独立查询）"
                : $"SNMP 目标：{ip}"
                + (string.IsNullOrWhiteSpace(_connections.ManagementAddress) ? "（来自上次查询记录）" : "（来自当前连接设备）");
        }
    }

    /// <summary>[打开 SNMP]：SNMP 是独立模块，不要求先建 CLI 连接。</summary>
    public RelayCommand OpenSnmpCommand { get; }

    /// <summary>[打开操作台账]：台账是按天一个 CSV，在文件管理器里打开目录最方便（可翻历史、可发微信）。</summary>
    public RelayCommand OpenLedgerFolderCommand { get; }

    private void OpenLedgerFolder()
    {
        try
        {
            var directory = AppServices.OperationLedger.DirectoryPath;
            Directory.CreateDirectory(directory);
            BackupService.OpenDirectory(directory);
        }
        catch (Exception ex)
        {
            // 打开目录失败不该影响别的（有些环境没有默认文件管理器）
            ToolOutput = $"打开台账目录失败：{ex.Message}｜台账目录：{AppServices.OperationLedger.DirectoryPath}";
        }
    }

    // ---------- 网络工具（Ping / Tracert）：只读探测，不依赖任何连接 ----------

    private readonly NetworkTools _tools = new();
    private CancellationTokenSource? _toolCts;
    private string _toolHost = string.Empty;
    private string _toolOutput = "填入交换机管理 IP（或任意域名）后点 [Ping] / [Tracert]。";
    private string _toolStatus = string.Empty;
    private bool _isToolBusy;

    /// <summary>探测目标（默认取当前连接设备 IP，其次取 SNMP 页上次用过的 IP）。</summary>
    public string ToolHost
    {
        get => _toolHost;
        set
        {
            if (SetProperty(ref _toolHost, value ?? string.Empty))
            {
                PingCommand.RaiseCanExecuteChanged();
                TracertCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ToolOutput
    {
        get => _toolOutput;
        private set => SetProperty(ref _toolOutput, value);
    }

    public string ToolStatus
    {
        get => _toolStatus;
        private set
        {
            if (SetProperty(ref _toolStatus, value))
            {
                OnPropertyChanged(nameof(HasToolStatus));
            }
        }
    }

    public bool HasToolStatus => !string.IsNullOrWhiteSpace(ToolStatus);

    public bool IsToolBusy
    {
        get => _isToolBusy;
        private set
        {
            if (SetProperty(ref _isToolBusy, value))
            {
                PingCommand.RaiseCanExecuteChanged();
                TracertCommand.RaiseCanExecuteChanged();
                StopToolCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand PingCommand { get; }

    public AsyncRelayCommand TracertCommand { get; }

    public RelayCommand StopToolCommand { get; }

    private bool CanRunTool() => !IsToolBusy && !string.IsNullOrWhiteSpace(ToolHost);

    private Task RunPingAsync() =>
        RunToolAsync("Ping", (ct, progress) => _tools.PingAsync(ToolHost, 4, 1500, ct, progress));

    private Task RunTracertAsync() =>
        RunToolAsync("Tracert", (ct, progress) => _tools.TracertAsync(ToolHost, 15, 1000, ct, progress));

    private async Task RunToolAsync(string title, Func<CancellationToken, IProgress<string>, Task<string>> run)
    {
        if (IsToolBusy)
        {
            return;
        }

        // 先把新的 CTS 换上，再释放旧的：反过来的话（先 Dispose 再新建）中间那一瞬间
        // `_toolCts` 指向已释放对象，工具线程拿到它就会报"无法访问已释放的对象"。
        var previousToolCts = _toolCts;
        _toolCts = new CancellationTokenSource();
        previousToolCts?.Dispose();
        IsToolBusy = true;
        ToolStatus = $"{title} 正在执行：{ToolHost}（异步，界面不会卡；可点[停止]）";
        ToolOutput = $"{title} 正在执行：{ToolHost} …";
        try
        {
            // 逐包 / 逐跳实时刷新结果框：在 UI 上下文里构造 Progress，回调自动回 UI 线程
            var progress = new Progress<string>(text => ToolOutput = text);
            ToolOutput = await run(_toolCts.Token, progress).ConfigureAwait(true);
            ToolStatus = $"{title} 完成：{ToolHost}（{DateTime.Now:HH:mm:ss}）";
            _shell.AddRecentOperation(title, $"{ToolHost}", succeeded: true);
        }
        catch (OperationCanceledException)
        {
            ToolStatus = $"{title} 已取消。";
        }
        catch (Exception ex)
        {
            ToolOutput = $"{title} 失败：{ex.Message}";
            ToolStatus = $"{title} 失败：{ex.Message}";
            AppServices.Log.Warn($"{title} 失败", ex);
            _shell.AddRecentOperation(title, $"{ToolHost}：{ex.Message}", succeeded: false);
        }
        finally
        {
            IsToolBusy = false;
        }
    }

    private void StopTool()
    {
        try
        {
            _toolCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经结束，忽略
        }
    }

    public string SnmpStatus
    {
        get => _snmpStatus;
        private set => SetProperty(ref _snmpStatus, value);
    }

    public bool IsSnmpBusy
    {
        get => _isSnmpBusy;
        private set
        {
            if (SetProperty(ref _isSnmpBusy, value))
            {
                RefreshSnmpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSnmpLines => SnmpLines.Count > 0;

    public bool IsSnmpEnabled => AppServices.Snmp.Enabled;

    private void OnSnmpEnabledChanged(object? sender, EventArgs e)
    {
        _snmpRequestGeneration++;
        OnPropertyChanged(nameof(IsSnmpEnabled));
        RefreshSnmpCommand.RaiseCanExecuteChanged();

        if (IsSnmpEnabled)
        {
            SnmpStatus = "概览 SNMP 卡片已启用，点击刷新读取设备信息。";
            return;
        }

        SnmpLines.Clear();
        OnPropertyChanged(nameof(HasSnmpLines));
        SnmpStatus = "概览 SNMP 信息卡片已关闭。";
    }

    /// <summary>刷新 SNMP 卡片（用户点击；不自动轮询）。</summary>
    public RelayCommand RefreshSnmpCommand { get; }

    /// <summary>
    /// 读取一次 SNMP 设备信息。异步执行，绝不阻塞界面；失败只更新卡片文字。
    /// 概览页首次打开时调用一次，之后由用户点[刷新 SNMP]触发。
    /// </summary>
    /// <summary>
    /// 概览页只查"简报"：型号 / 版本 / 序列号 / 内存 / 运行时间。
    /// 完整分类信息在【SNMP】页；这里不依赖 CLI 连接，没有连接也能查（用 SNMP 页的 IP/Community）。
    /// </summary>
    private async Task LoadSnmpAsync()
    {
        if (IsSnmpBusy || !IsSnmpEnabled)
        {
            return;
        }

        var requestGeneration = _snmpRequestGeneration;
        var ip = ResolveSnmpTarget();
        if (string.IsNullOrWhiteSpace(ip))
        {
            SnmpStatus = "未查询 SNMP：还没有目标 IP。"
                + "到【SNMP】页填 IP + Community 就能独立查询（不需要先连 Console/Telnet）。";
            return;
        }

        IsSnmpBusy = true;
        SnmpLines.Clear();
        OnPropertyChanged(nameof(HasSnmpLines));
        SnmpStatus = $"SNMP：正在查询 {ip} …（异步，不阻塞界面）";
        try
        {
            var result = await AppServices.SnmpQuery
                .QueryCardAsync(ip, AppServices.Snmp.Port, AppServices.Snmp.Community, AppServices.Snmp.TimeoutMs, AppServices.Snmp.Retries)
                .ConfigureAwait(true);

            if (requestGeneration != _snmpRequestGeneration || !IsSnmpEnabled)
            {
                return;
            }

            var stamp = DateTime.Now.ToString("HH:mm:ss");
            SnmpStatus = $"{result.StatusText}｜最后更新 {stamp}";
            foreach (var field in result.Fields)
            {
                SnmpLines.Add(field);
            }

            OnPropertyChanged(nameof(HasSnmpLines));
        }
        catch (Exception ex)
        {
            if (requestGeneration != _snmpRequestGeneration || !IsSnmpEnabled)
            {
                return;
            }

            SnmpStatus = $"SNMP 查询失败：{ex.Message}";
            AppServices.Log.Warn("SNMP 查询失败", ex);
        }
        finally
        {
            IsSnmpBusy = false;
        }
    }

    /// <summary>SNMP 目标解析：当前 CLI 设备 IP 优先，其次 SNMP 页上次用过的 IP。</summary>
    private string ResolveSnmpTarget()
    {
        var fromConnection = IpAddressHelper.ExtractFirst(_connections.ManagementAddress);
        if (!string.IsNullOrWhiteSpace(fromConnection))
        {
            return fromConnection;
        }

        return IpAddressHelper.ExtractFirst(AppServices.Snmp.LastHost) ?? string.Empty;
    }

    public string DeviceName => string.IsNullOrWhiteSpace(_connections.DeviceName) ? "未获取设备名" : _connections.DeviceName!;

    /// <summary>
    /// "管理 IP"这一行**只显示 IP**（用户要求：管理 IP 与连接方式必须分开）。
    ///
    /// · Telnet / SSH：<see cref="ConnectionService.ManagementAddress"/> 就是拨过去的 IP → 直接显示；
    /// · 配置线（Serial）：这个字段里存的是**端口名**（COM5），根本不是 IP ——
    ///   端口属于"连接方式"那一格（见 <see cref="ConnectionKind"/>），这里如实显示"未获取"，
    ///   不再拿 COM 口充数，也不写"（配置线 COM5）"这种混在一起的说明。
    /// </summary>
    public string ManagementAddress =>
        // ① 从设备里**真读到**的地址优先：这才是"这台交换机的管理 IP"
        //    （来源：【设备】页读「三层接口」或【概览】页那一格的[获取]按钮 → ConnectionService.LearnedManagementAddress）；
        //    为什么它必须排在拨号地址前面：隧道/端口映射场景下拨号地址是 127.0.0.1，
        //    旧顺序会让用户点完[获取]仍然只看到 127.0.0.1（用户 2026-09-25 反馈的正是这个）；
        // ② 没读过 → 退回传输层地址（Telnet / SSH 拨过去的 IP）；
        // ③ 配置线且没读过 → 如实显示未获取（**不会**为了填这一格偷偷发命令）。
        _connections.LearnedManagementAddress
        ?? IpAddressHelper.SplitEndpoint(_connections.ManagementAddress).Ip
        ?? "—（未获取）";

    public string ConnectionKind => _connections.CurrentKind switch
    {
        DeviceConnectionKind.Serial => "Console / Serial",
        DeviceConnectionKind.Telnet => "Telnet",
        DeviceConnectionKind.Ssh => "SSH",
        _ => "未连接",
    };

    /// <summary>
    /// 连接**端口**单独一列：串口是 COM 口名（COM5），Telnet/SSH 是拨过去的端口号（23 / 22）。
    ///
    /// 为什么要单独一格：用户明确要求"管理 IP 就显示管理 IP，连接方式才是连接方式"——
    /// 端口既不是 IP 也不完全是"方式"，混在哪一格都会让人误解。这里把它摆在明面上。
    /// （Telnet/SSH 的 <see cref="ConnectionService.ManagementAddress"/> 形如 `192.0.2.235:23`，
    ///   端口就是冒号后面那段。）
    /// </summary>
    public string ConnectionPort
    {
        get
        {
            var address = _connections.ManagementAddress;
            if (string.IsNullOrWhiteSpace(address))
            {
                return "—";
            }

            return IpAddressHelper.SplitEndpoint(address).Port ?? "—";
        }
    }

    public string ConnectionState => _connections.State.ToChinese();

    public string SessionId => string.IsNullOrWhiteSpace(_connections.SessionId) ? "—" : _connections.SessionId;

    /// <summary>
    /// 卡片下面那句说明。重点把"管理 IP 为什么是 —"讲清楚，别让用户以为是坏了：
    /// 配置线（Console）连的是设备的配置口，**本身没有 IP 概念**，端口名显示在"连接方式"里。
    /// </summary>
    public string DeviceInfoNote => _connections.CurrentKind switch
    {
        DeviceConnectionKind.Serial =>
            "配置线只连到设备的 Console 口，本身没有 IP —— 所以「管理 IP」显示未获取是正常的，串口是「连接端口」那一格。"
            + "想看交换机自己的管理 IP：【设备】页 →「三层接口」→ 刷新，读到的地址会自动显示到「管理 IP」这一格"
            + "（只读一次，不会自动发命令）。",
        null => "未连接：设备名称与「管理 IP」会在连上设备后显示。",
        _ => "「管理 IP」取自本次连接的目标地址；设备型号/固件可在【设备】页 →「基本信息」读取。",
    };

    public string ResourceSummary =>
        $"交换机资源：{AppServices.ResourceRepository.Database.SwitchCount}    " +
        $"VLAN 资源：{AppServices.ResourceRepository.Database.VlanCount}    " +
        $"场所资源：{AppServices.ResourceRepository.Database.LocationCount}";

    public RelayCommand OpenConnectionCommand { get; }

    public RelayCommand OpenCliCommand { get; }

    public RelayCommand OpenBackupCommand { get; }

    public RelayCommand OpenResourcesCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ShowCommandPreviewDemoCommand { get; }

    protected override Task OnInitializeAsync()
    {
        Refresh();

        // 本机网卡：进页面读一次（后台线程，不阻塞界面）
        _ = RefreshLocalNetworkAsync();

        // 进入概览页时查询一次 SNMP（异步，不阻塞界面；之后由用户点[刷新 SNMP]）
        if (AppServices.Snmp.Enabled)
        {
            _ = LoadSnmpAsync();
        }

        return Task.CompletedTask;
    }

    private void Refresh()
    {
        // 连接/断开后自动补齐工具的目标地址（用户手动改过的值不覆盖）
        if (string.IsNullOrWhiteSpace(ToolHost))
        {
            ToolHost = ResolveSnmpTarget();
        }

        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(ManagementAddress));
        // 管理 IP 那行的小字/按钮文字也跟着连接状态变（没连接时说明"连上后可以点[获取]"）
        OnPropertyChanged(nameof(ManagementIpNotice));
        OnPropertyChanged(nameof(ManagementIpButtonText));
        OnPropertyChanged(nameof(ConnectionKind));
        OnPropertyChanged(nameof(ConnectionPort));
        // 这句说明也要跟着连接状态刷新 —— 现场出现过"明明已连接，下面却写着未连接"
        // （属性只在页面初始化时求值过一次，之后没收到通知）。
        OnPropertyChanged(nameof(DeviceInfoNote));
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(ResourceSummary));

        // 连接/断开往往就是"刚插了网线、刚换网段"——顺手把本机网卡重读一次
        // （也更新"与目标是否同网段"的结论；失败只记日志，不影响页面）
        _ = RefreshLocalNetworkAsync();
    }

    /// <summary>
    /// Phase 0 验证入口：演示 Command Preview 组件。
    /// Phase 3 起由端口/VLAN/Trunk 页面直接调用同一组件。
    /// </summary>
    private void ShowCommandPreviewDemo()
    {
        var generator = new VlanCommandGenerator();
        var plan = generator.AssignAccessVlan(new[] { "Gi0/1", "Gi0/2", "Gi0/3" }, 100);
        _shell.ShowCommandPreview(plan);
    }
}
