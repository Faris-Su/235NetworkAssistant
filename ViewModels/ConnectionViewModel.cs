using System.Collections.ObjectModel;
using System.IO.Ports;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 连接页面：交换机地址簿（来自本地资源库）+ Console/Serial 参数 + Telnet 参数 + SSH 参数 + 连接/断开。
/// 地址簿只读本地资源库，不读取 Excel，也不访问互联网。
/// </summary>
public sealed class ConnectionViewModel : ViewModelBase
{
    public const string AllBuildings = ResourceBuildingFilter.AllBuildings;

    private readonly IShellNavigator _shell;
    private readonly ConnectionService _connections;
    private readonly IResourceRepository _repository;
    private string _addressSearchText = string.Empty;
    private SwitchRecord? _selectedSwitch;
    private string _stateText = "未连接";
    private string _errorText = string.Empty;
    private bool _isBusy;
    private string _selectedBuilding = AllBuildings;

    private string _addressSummary = string.Empty;
    private string _portHint = string.Empty;
    private string _connectNotice = string.Empty;
    private string _selfCheckReport = string.Empty;
    private string _privilegeNotice = string.Empty;

    public ConnectionViewModel(IShellNavigator shell, ConnectionService connections, IResourceRepository repository)
    {
        _shell = shell;
        _connections = connections;
        _repository = repository;
        Title = "连接";

        Serial = AppServices.Settings.Serial;
        Telnet = AppServices.Settings.Telnet;
        Ssh = AppServices.Settings.Ssh;

        SearchAddressCommand = new RelayCommand(SearchAddress);
        FillTelnetFromSelectedCommand = new RelayCommand(FillTelnetFromSelected, () => SelectedSwitch is not null);
        ConnectTelnetFromAddressCommand = new AsyncRelayCommand(
            ConnectTelnetFromAddressAsync,
            () => SelectedSwitch is not null && !IsBusy);
        FillSshFromSelectedCommand = new RelayCommand(FillSshFromSelected, () => SelectedSwitch is not null);
        ConnectSshFromAddressCommand = new AsyncRelayCommand(
            ConnectSshFromAddressAsync,
            () => SelectedSwitch is not null && !IsBusy);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        RunSelfCheckCommand = new AsyncRelayCommand(RunSelfCheckAsync, () => !IsBusy);
        ElevatePrivilegeCommand = new AsyncRelayCommand(
            ElevatePrivilegeAsync,
            () => !IsBusy && _connections.IsConnected && !_connections.IsPrivileged);
        CopyErrorCommand = new RelayCommand(CopyError, () => HasError);
        ConnectSerialCommand = new AsyncRelayCommand(ConnectSerialAsync, () => !IsBusy);
        ConnectTelnetCommand = new AsyncRelayCommand(ConnectTelnetAsync, () => !IsBusy);
        ConnectSshCommand = new AsyncRelayCommand(ConnectSshAsync, () => !IsBusy);
        ManageSshHostKeysCommand = new RelayCommand(() => SshHostKeyManagerRequested?.Invoke(this, EventArgs.Empty));
        DisconnectCommand = new AsyncRelayCommand(
            DisconnectAsync,
            () => !IsBusy && _connections.State != DeviceConnectionState.Disconnected);

        _connections.SessionChanged += (_, _) => RefreshState();
        // 仓库的 Changed 是在 ConfigureAwait(false) 之后触发的（线程池线程），而 RefreshAddressBook 会改
        // 绑定到界面的 BuildingOptions/AddressResults → 必须回 UI 线程。否则导入/保存资源库时 WPF 抛
        // NotSupportedException，异常还会冒回 ApplyImportAsync 的 Task，界面显示"写入失败"但数据其实已落盘。
        _repository.Changed += (_, _) => UiThread.Post(RefreshAddressBook);
    }

    public event EventHandler<SshHostKeyConfirmationRequestEventArgs>? SshHostKeyConfirmationRequested;

    public event EventHandler? SshHostKeyManagerRequested;

    public SerialConnectionSettings Serial { get; }

    public TelnetConnectionSettings Telnet { get; }

    public SshConnectionSettings Ssh { get; }

    /// <summary>
    /// 地址簿结果。用 <see cref="BulkObservableCollection{T}"/> **整体替换**：
    /// 旧写法是 `Clear()` + 逐条 `Add` —— 411 台设备 = 412 次集合通知，而绑定它的 DataGrid 每次都要响应。
    /// </summary>
    public BulkObservableCollection<SwitchRecord> AddressResults { get; } = new();

    public ObservableCollection<string> BuildingOptions { get; } = new();

    public ObservableCollection<string> AvailablePorts { get; } = new();

    public IReadOnlyList<int> BaudRateOptions { get; } = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 5, 6, 7, 8 };

    public IReadOnlyList<SerialStopBits> StopBitsOptions { get; } = Enum.GetValues<SerialStopBits>();

    public IReadOnlyList<SerialParity> ParityOptions { get; } = Enum.GetValues<SerialParity>();

    public IReadOnlyList<SerialFlowControl> FlowControlOptions { get; } = Enum.GetValues<SerialFlowControl>();

    /// <summary>
    /// 串口文本编码。真机（RGOS）中文描述/VLAN 名是 **GBK**，所以默认选项里有 GB18030；
    /// UTF-8 只适合明确知道设备用 UTF-8 的场合（选错就是满屏乱码，且会话留档会一起写坏）。
    /// </summary>
    public IReadOnlyList<string> EncodingOptions { get; } = new[] { "UTF-8", "GB18030", "ASCII", "Latin1" };

    public IReadOnlyList<LineEndingOption> LineEndingOptions { get; } = LineEndingOption.All;

    public string AddressSearchText
    {
        get => _addressSearchText;
        set => SetProperty(ref _addressSearchText, value);
    }

    public SwitchRecord? SelectedSwitch
    {
        get => _selectedSwitch;
        set
        {
            if (SetProperty(ref _selectedSwitch, value))
            {
                FillTelnetFromSelectedCommand.RaiseCanExecuteChanged();
                ConnectTelnetFromAddressCommand.RaiseCanExecuteChanged();
                FillSshFromSelectedCommand.RaiseCanExecuteChanged();
                ConnectSshFromAddressCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>地址簿楼栋/位置筛选。</summary>
    public string SelectedBuilding
    {
        get => _selectedBuilding;
        set
        {
            if (SetProperty(ref _selectedBuilding, value))
            {
                SearchAddress();
            }
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>串口检测提示：没有串口时明确告诉用户是驱动问题。</summary>
    public string PortHint
    {
        get => _portHint;
        private set => SetProperty(ref _portHint, value);
    }

    /// <summary>连接成功后的附加提示（例如自动登录结果），非错误，用普通颜色显示。</summary>
    public string ConnectNotice
    {
        get => _connectNotice;
        private set
        {
            if (SetProperty(ref _connectNotice, value))
            {
                OnPropertyChanged(nameof(HasConnectNotice));
            }
        }
    }

    public bool HasConnectNotice => !string.IsNullOrWhiteSpace(ConnectNotice);

    /// <summary>连通性自检的多行结果。</summary>
    public string SelfCheckReport
    {
        get => _selfCheckReport;
        private set
        {
            if (SetProperty(ref _selfCheckReport, value))
            {
                OnPropertyChanged(nameof(HasSelfCheckReport));
            }
        }
    }

    public bool HasSelfCheckReport => !string.IsNullOrWhiteSpace(SelfCheckReport);

    // ---------- 登录权限（普通模式 / 管理模式 + Enable 密码） ----------

    public bool IsNormalMode
    {
        get => AppServices.Settings.PrivilegeMode == PrivilegeMode.Normal;
        set
        {
            if (value)
            {
                SetPrivilegeMode(PrivilegeMode.Normal);
            }
        }
    }

    public bool IsManageMode
    {
        get => AppServices.Settings.PrivilegeMode == PrivilegeMode.Manage;
        set
        {
            if (value)
            {
                SetPrivilegeMode(PrivilegeMode.Manage);
            }
        }
    }

    private void SetPrivilegeMode(PrivilegeMode mode)
    {
        if (AppServices.Settings.PrivilegeMode == mode)
        {
            return;
        }

        AppServices.Settings.PrivilegeMode = mode;
        OnPropertyChanged(nameof(IsNormalMode));
        OnPropertyChanged(nameof(IsManageMode));
        OnPropertyChanged(nameof(PrivilegeModeHint));
    }

    public string PrivilegeModeHint => IsManageMode
        ? "管理模式：连接成功后自动执行 enable 并输入下面的密码；失败只提示，不影响连接。"
        : "普通模式：连接后不发任何命令，保持设备登录后的权限（账号本身就是 # 的话仍然是 #，不会主动降权）；需要改配置时再点[提升权限]。";

    /// <summary>Enable 密码（默认值可改；只存内存，不落盘、不写日志）。</summary>
    public string EnablePassword
    {
        get => AppServices.EnablePassword;
        set
        {
            if (AppServices.EnablePassword == value)
            {
                return;
            }

            AppServices.EnablePassword = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>当前权限（“特权模式 #” / “普通模式 >” / “权限未知”）。</summary>
    public string PrivilegeText => _connections.PrivilegeText;

    /// <summary>权限提示（提升失败原因等），用普通色显示，不是“连接失败”。</summary>
    public string PrivilegeNotice
    {
        get => _privilegeNotice;
        private set
        {
            if (SetProperty(ref _privilegeNotice, value))
            {
                OnPropertyChanged(nameof(HasPrivilegeNotice));
            }
        }
    }

    public bool HasPrivilegeNotice => !string.IsNullOrWhiteSpace(PrivilegeNotice);

    /// <summary>手动提升权限（连接页也能点）：enable → 输入密码 → 成功切换到特权模式。</summary>
    public AsyncRelayCommand ElevatePrivilegeCommand { get; }

    /// <summary>
    /// [提升权限] 按钮为什么可用 / 不可用的一句话说明（也作为按钮的 ToolTip）。
    /// 按钮被禁用时必须让用户知道原因，否则看起来就是「点了没反应」。
    /// </summary>
    public string PrivilegeActionHint
    {
        get
        {
            if (!_connections.IsConnected)
            {
                return "先连接设备，再提升权限。";
            }

            if (_connections.IsPrivileged)
            {
                return "当前已经是特权模式（#），可以直接改配置，不需要提升权限。";
            }

            return _connections.PrivilegeLevel == DevicePrivilegeLevel.Unknown
                ? "还没识别到设备提示符；点这里会执行 enable，尝试进入特权模式（#）。"
                : "当前是普通模式（>）；点这里执行 enable，成功后改配置的操作会自动继续。";
        }
    }

    /// <summary>按当前界面选择构造连接时的权限请求（密码取内存值，不落盘）。</summary>
    internal PrivilegeRequest BuildPrivilegeRequest() =>
        new(AppServices.Settings.PrivilegeMode, AppServices.EnablePassword);

    private async Task ElevatePrivilegeAsync()
    {
        IsBusy = true;
        try
        {
            AppServices.EnablePassword = EnablePassword;
            var result = await _connections
                .EnsurePrivilegedAsync(EnablePassword, CancellationToken.None)
                .ConfigureAwait(true);

            PrivilegeNotice = result.Reason;
            _shell.ReportStatus($"权限提升{(result.Succeeded ? "成功" : "失败")}：{result.Reason}");
            _shell.AddRecentOperation(
                "提升权限",
                $"{_connections.ManagementAddress}：{result.Reason}",
                result.Succeeded);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ConnectSerialCommand.RaiseCanExecuteChanged();
                ConnectTelnetCommand.RaiseCanExecuteChanged();
                ConnectSshCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ConnectTelnetFromAddressCommand.RaiseCanExecuteChanged();
                ConnectSshFromAddressCommand.RaiseCanExecuteChanged();
                RunSelfCheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AddressHint => _repository.Database.SwitchCount == 0
        ? "地址簿为空：请在【设置】页导入本地设备清单（生成预览 → 写入资源库）。"
        : $"地址簿共 {_repository.Database.SwitchCount} 台交换机，数据来自本地资源库（非设备实时状态）。";

    /// <summary>本次搜索/筛选命中的条数（含"超过上限只显示前 N 台"的说明）。</summary>
    public string AddressSummary
    {
        get => _addressSummary;
        private set => SetProperty(ref _addressSummary, value);
    }

    public string CredentialHint => "账号/密码由用户填写；密码不入库、不写日志。参考项目资料：多数交换机使用学校统一运维账号。";

    public RelayCommand SearchAddressCommand { get; }

    public RelayCommand FillTelnetFromSelectedCommand { get; }

    public AsyncRelayCommand ConnectTelnetFromAddressCommand { get; }

    public RelayCommand FillSshFromSelectedCommand { get; }

    public AsyncRelayCommand ConnectSshFromAddressCommand { get; }

    public RelayCommand RefreshPortsCommand { get; }

    /// <summary>连通性自检：串口 / 地址 / 网段 / Ping / 端口，现场一键判断卡在哪一步。</summary>
    public AsyncRelayCommand RunSelfCheckCommand { get; }

    /// <summary>复制完整报错（含编号/原因/怎么修），便于把现场问题原样发给开发者。</summary>
    public RelayCommand CopyErrorCommand { get; }

    public AsyncRelayCommand ConnectSerialCommand { get; }

    public AsyncRelayCommand ConnectTelnetCommand { get; }

    public AsyncRelayCommand ConnectSshCommand { get; }

    public RelayCommand ManageSshHostKeysCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    protected override async Task OnInitializeAsync()
    {
        if (!_repository.IsLoaded)
        {
            await _repository.LoadAsync().ConfigureAwait(true);
        }

        RefreshPorts();
        RefreshAddressBook();
        RefreshState();
        OnPropertyChanged(nameof(AddressHint));
    }

    /// <summary>重建楼栋下拉并重新查询（导入资源库后立即生效）。</summary>
    private void RefreshAddressBook()
    {
        var buildings = _repository.GetBuildings();
        BuildingOptions.Clear();
        BuildingOptions.Add(AllBuildings);
        foreach (var building in buildings)
        {
            BuildingOptions.Add(building);
        }

        if (!BuildingOptions.Contains(SelectedBuilding))
        {
            _selectedBuilding = AllBuildings;
            OnPropertyChanged(nameof(SelectedBuilding));
        }

        SearchAddress();
        OnPropertyChanged(nameof(AddressHint));
    }

    /// <summary>
    /// 刷新串口列表。**不许把用户已经选好的串口刷掉**（2026-09-24 修）。
    ///
    /// 旧实现每次都 `AvailablePorts.Clear()` 再重填；而 COM 口下拉框是 `IsEditable="True"` +
    /// `Text="{Binding Serial.PortName}"` 的双向绑定 —— 清空 ItemsSource 会让 ComboBox 把 Text 置空
    /// 并**回写**到 `Serial.PortName`。于是【连通性自检】（它开头会先刷新一次串口）跑完，
    /// 用户选的 COM5 就"被刷没了"，接下来自检/连接一直报"未选择串口"。
    ///
    /// 现在：① 列表没变就不重建集合（不去触发那次回写）；② 列表变了也只换列表，
    /// 选择值原样保留；③ 万一还是被回写清空，就把原值放回去。
    /// </summary>
    private void RefreshPorts()
    {
        var ports = ConnectionDiagnostics.SafePortNames();
        var current = (Serial.PortName ?? string.Empty).Trim();

        if (!ports.SequenceEqual(AvailablePorts, StringComparer.OrdinalIgnoreCase))
        {
            AvailablePorts.Clear();
            foreach (var port in ports)
            {
                AvailablePorts.Add(port);
            }
        }

        // 清空+重填期间 ComboBox 可能把空串回写进来 → 恢复用户原来的选择
        if (current.Length > 0 && string.IsNullOrWhiteSpace(Serial.PortName))
        {
            Serial.PortName = current;
        }

        if (ports.Length == 0)
        {
            PortHint = "未检测到任何串口：先装 USB-Serial 驱动（CH340 / PL2303 / FTDI）并插好 Console 线，再点[刷新串口]。";
            return;
        }

        var stillThere = ports.Any(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        if (stillThere)
        {
            PortHint = "检测到串口：" + string.Join("、", ports) + "。";
            return;
        }

        // 只有一个串口、且用户还没选：直接选中它（默认的 COM1 在笔记本上几乎一定不存在）。
        if (current.Length == 0 && ports.Length == 1)
        {
            Serial.PortName = ports[0];
            PortHint = "检测到串口：" + string.Join("、", ports) + "。";
            return;
        }

        PortHint = current.Length == 0
            ? $"还没有选择串口，请在下拉框里选择：{string.Join("、", ports)}。"
            : $"当前选择的 {current} 不存在，请在下拉框里选择：{string.Join("、", ports)}。";
    }

    /// <summary>
    /// 连通性自检：只做只读探测（枚举串口 / 解析地址 / 比对网段 / Ping / 探测 Telnet 端口），
    /// 让用户在现场一眼看出是驱动问题、IP 问题还是交换机服务问题。
    /// </summary>
    private void CopyError()
    {
        if (!HasError)
        {
            return;
        }

        var text = $"235修网助手 连接报错 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ErrorText}";
        var clipboard = AppServices.Clipboard;
        if (clipboard is null)
        {
            _shell.ReportStatus("当前环境不支持剪贴板。");
            return;
        }

        if (clipboard.TrySetText(text, out var error))
        {
            _shell.ReportStatus("报错信息已复制到剪贴板，可直接发给开发者。");
        }
        else
        {
            _shell.ReportStatus($"复制失败：{error}");
        }
    }

    private async Task RunSelfCheckAsync()
    {
        IsBusy = true;
        SelfCheckReport = "正在自检…";
        try
        {
            // 自检**只读**：先记住用户选的串口，再刷新列表 —— 刷新不该改变、也不该丢掉这个选择
            // （2026-09-24 现场 bug：点完自检 COM5 就没了，之后一直报"未选择串口"）。
            var portBeforeRefresh = (Serial.PortName ?? string.Empty).Trim();
            RefreshPorts();
            var portForCheck = portBeforeRefresh.Length > 0
                ? portBeforeRefresh
                : (Serial.PortName ?? string.Empty).Trim();
            var timeout = Math.Clamp(Telnet.ConnectionTimeoutMs, 1000, 3000);
            var results = await ConnectionDiagnostics.RunSelfCheckAsync(
                portForCheck,
                Telnet.Host,
                Telnet.Port,
                timeout,
                CancellationToken.None,
                Ssh.Port).ConfigureAwait(true);

            SelfCheckReport = string.Join(Environment.NewLine, results.Select(r => r.ToLine()));

            var summary = results.Any(r => r.Level == ConnectionCheckLevel.Fail)
                ? "自检发现阻塞项，请按提示处理。"
                : "自检未发现明显阻塞项。";
            _shell.ReportStatus("连通性自检完成：" + summary);
            _shell.AddRecentOperation("连通性自检", summary);
        }
        catch (Exception ex)
        {
            SelfCheckReport = "自检失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SearchAddress()
    {
        AddressResults.Clear();
        // 楼栋筛选与【资源库】页用**同一套规则**（ResourceBuildingFilter：忽略大小写与首尾空格、
        // 去掉表名带出来的 "Vlan" 尾巴）。以前这里各写一套：选 "A11" 时 "a11" 的 13 条会被漏掉。
        var records = ResourceBuildingFilter
            .Apply(_repository.SearchSwitches(AddressSearchText), SelectedBuilding, r => r.Building)
            .ToList();

        const int maxAddressRows = 500;
        AddressResults.ReplaceAll(records.Take(maxAddressRows));   // 一次 Reset 通知，不再是 N 次

        // 列表有上限就要说出来（资源库页有"仅显示前 N 条"，这里以前什么都不显示）
        AddressSummary = records.Count switch
        {
            0 => "地址簿：没有匹配的交换机。",
            _ when records.Count > maxAddressRows =>
                $"地址簿：匹配 {records.Count} 台，仅显示前 {maxAddressRows} 台（用搜索框缩小范围）。",
            _ => $"地址簿：匹配 {records.Count} 台。",
        };
    }

    /// <summary>地址簿一键连接：填入 Telnet 参数 → 连接 → 跳到 CLI（验收场景 1）。</summary>
    private async Task ConnectTelnetFromAddressAsync()
    {
        var record = SelectedSwitch;
        if (record is null)
        {
            return;
        }

        var ip = IpAddressHelper.ExtractFirst(record.ManagementIp);
        if (ip is null)
        {
            _shell.ReportStatus($"所选记录没有可用的管理 IP：{record.ManagementIp}");
            return;
        }

        Telnet.Host = ip;
        _shell.ReportStatus($"地址簿：连接 {record.LocationText} {ip} …");
        var privilege = BuildPrivilegeRequest();
        await ConnectAsync(
            () => _connections.ConnectTelnetAsync(Telnet, CancellationToken.None, privilege),
            $"Telnet {ip}（{record.LocationText}）",
            privilege).ConfigureAwait(true);

        if (_connections.IsConnected)
        {
            _shell.NavigateTo("cli");
        }
    }

    private void FillTelnetFromSelected()
    {
        if (SelectedSwitch is null)
        {
            return;
        }

        var ip = IpAddressHelper.ExtractFirst(SelectedSwitch.ManagementIp);
        if (ip is null)
        {
            _shell.ReportStatus($"所选记录没有可用的管理 IP：{SelectedSwitch.ManagementIp}");
            return;
        }

        Telnet.Host = ip;
        _shell.ReportStatus($"已填入 Telnet 主机：{ip}（{SelectedSwitch.LocationText}）");
    }

    /// <summary>地址簿一键连接（SSH）：填入主机 → 连接 → 跳到 CLI。</summary>
    private async Task ConnectSshFromAddressAsync()
    {
        var record = SelectedSwitch;
        if (record is null)
        {
            return;
        }

        var ip = IpAddressHelper.ExtractFirst(record.ManagementIp);
        if (ip is null)
        {
            _shell.ReportStatus($"所选记录没有可用的管理 IP：{record.ManagementIp}");
            return;
        }

        Ssh.Host = ip;
        _shell.ReportStatus($"地址簿：SSH 连接 {record.LocationText} {ip} …");
        var privilege = BuildPrivilegeRequest();
        await ConnectAsync(
            () => ConnectSshWithTrustAsync(privilege),
            $"SSH {ip}（{record.LocationText}）",
            privilege).ConfigureAwait(true);

        if (_connections.IsConnected)
        {
            _shell.NavigateTo("cli");
        }
    }

    private void FillSshFromSelected()
    {
        if (SelectedSwitch is null)
        {
            return;
        }

        var ip = IpAddressHelper.ExtractFirst(SelectedSwitch.ManagementIp);
        if (ip is null)
        {
            _shell.ReportStatus($"所选记录没有可用的管理 IP：{SelectedSwitch.ManagementIp}");
            return;
        }

        Ssh.Host = ip;
        _shell.ReportStatus($"已填入 SSH 主机：{ip}（{SelectedSwitch.LocationText}）");
    }

    private Task ConnectSerialAsync()
    {
        var privilege = BuildPrivilegeRequest();
        return ConnectAsync(
            () => _connections.ConnectSerialAsync(Serial, CancellationToken.None, privilege),
            $"串口 {Serial.PortName}",
            privilege);
    }

    private Task ConnectTelnetAsync()
    {
        var privilege = BuildPrivilegeRequest();
        return ConnectAsync(
            () => _connections.ConnectTelnetAsync(Telnet, CancellationToken.None, privilege),
            $"Telnet {Telnet.Host}",
            privilege);
    }

    private Task ConnectSshAsync()
    {
        var privilege = BuildPrivilegeRequest();
        return ConnectAsync(
            () => ConnectSshWithTrustAsync(privilege),
            $"SSH {Ssh.Host}",
            privilege);
    }

    private async Task ConnectSshWithTrustAsync(PrivilegeRequest privilege)
    {
        var host = SshHostKeyTrustStore.NormalizeHost(Ssh.Host);
        var workflow = new SshHostKeyTrustWorkflow(AppServices.SshHostKeys);
        await workflow.EnsureTrustedAsync(
            host,
            Ssh.Port,
            ct => AppServices.SshHostKeyProbe.ProbeAsync(host, Ssh.Port, Ssh.ConnectionTimeoutMs, ct),
            async candidate => await RequestSshHostKeyTrustAsync(candidate).ConfigureAwait(true))
            .ConfigureAwait(true);

        await _connections.ConnectSshAsync(Ssh, CancellationToken.None, privilege).ConfigureAwait(true);
    }

    private async Task<bool> RequestSshHostKeyTrustAsync(SshHostKeyInfo candidate)
    {
        var handler = SshHostKeyConfirmationRequested;
        if (handler is null)
        {
            throw new SshHostKeyTrustException(
                "SSH 主机密钥确认界面尚未就绪，已停止连接；未发送登录凭据，也未保存信任记录。");
        }

        var request = new SshHostKeyConfirmationRequestEventArgs(candidate);
        handler.Invoke(this, request);
        var accepted = await request.Decision.ConfigureAwait(true);
        if (request.Failure is { } failure)
        {
            throw new SshHostKeyTrustException(
                $"无法显示 SSH 主机密钥确认框，已停止连接：{failure.Message}",
                failure);
        }

        return accepted;
    }

    private async Task ConnectAsync(Func<Task> connect, string description, PrivilegeRequest privilege)
    {
        IsBusy = true;
        ErrorText = string.Empty;
        ConnectNotice = string.Empty;
        StateText = "连接中…";
        try
        {
            await connect().ConfigureAwait(true);
            StateText = _connections.State.ToChinese();
            _shell.ReportStatus($"已连接：{description}");

            // 连接成功但登录有异常（账号错、设备已在提示符下等）时，必须让用户看到，而不是只显示「已连接」。
            var notice = _connections.Current?.ConnectNotice;
            ConnectNotice = string.IsNullOrWhiteSpace(notice) ? string.Empty : notice;

            // 权限提示单独显示：管理模式自动 enable 失败时，连接仍然是成功的。
            // 提示内容统一在 RefreshState 里按当前会话状态生成（提示符可能比连接返回晚到）。

            _shell.AddRecentOperation(
                "连接设备",
                string.IsNullOrEmpty(ConnectNotice)
                    ? $"{description}｜{_connections.PrivilegeText}"
                    : $"{description}：{ConnectNotice}｜{_connections.PrivilegeText}");
        }
        catch (SshHostKeyTrustCancelledException ex)
        {
            StateText = "未连接";
            ErrorText = string.Empty;
            ConnectNotice = ex.Message;
            _shell.ReportStatus(ex.Message);
        }
        catch (Exception ex)
        {
            StateText = "连接失败";
            ErrorText = ex.Message;
            AppServices.Log.Warn($"连接失败：{description}", ex);
            // 状态栏与「最近操作」只放一行摘要，完整报错在连接页的红框里（可复制）。
            var headline = Headline(ex.Message);
            _shell.ReportStatus($"连接失败：{headline}");
            _shell.AddRecentOperation("连接设备", $"{description}：{headline}", succeeded: false);
        }
        finally
        {
            IsBusy = false;
            if (_connections.Current is null && !string.IsNullOrWhiteSpace(ErrorText))
            {
                // 传输连接失败后 ConnectionService 会自动清理会话；保留本次失败摘要，
                // 不要在 finally 里把“连接失败”覆盖成普通的“未连接”。
                DisconnectCommand.RaiseCanExecuteChanged();
            }
            else
            {
                RefreshState();
            }
        }
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _connections.DisconnectAsync().ConfigureAwait(true);
            ConnectNotice = "已断开连接。";
            _shell.ReportStatus("已断开连接。");
            _shell.AddRecentOperation("断开连接", "用户主动断开");
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    private void RefreshState()
    {
        StateText = $"{_connections.State.ToChinese()}｜{_connections.CurrentKind?.ToString() ?? "—"}｜{_connections.PrivilegeText}";

        // 权限提示以当前会话为准：连接时自动 enable 的结果、CLI 里手动 enable 的结果都同步到这里，
        // 保证「设备已连接但进不了特权模式」这件事在页面上留得住，不只是状态栏一闪而过。
        if (_connections.Current is null)
        {
            PrivilegeNotice = string.Empty;
        }
        else
        {
            var privilege = _connections.PrivilegeNotice;
            if (!string.IsNullOrWhiteSpace(privilege))
            {
                PrivilegeNotice = privilege;
            }
            else if (AppServices.Settings.PrivilegeMode == PrivilegeMode.Normal &&
                     _connections.PrivilegeLevel == DevicePrivilegeLevel.Privileged &&
                     string.IsNullOrWhiteSpace(PrivilegeNotice))
            {
                // 常见的困惑点：选了普通模式却仍显示特权模式 —— 那是账号登录后本来就在 #，
                // 普通模式只是「不主动执行 enable」，不会把设备降权。这里把它说清楚。
                PrivilegeNotice =
                    "普通模式：不主动执行 enable，保持设备登录后的权限 —— 当前是特权模式（#），" +
                    "说明该账号登录后本身就是 #，可以直接改配置。";
            }
        }

        DisconnectCommand.RaiseCanExecuteChanged();
        ElevatePrivilegeCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(PrivilegeActionHint));
    }

    /// <summary>取多行报错的第一行作为摘要（状态栏、最近操作记录用）。</summary>
    private static string Headline(string text)
    {
        var firstLine = (text ?? string.Empty).Split('\n')[0].Trim();
        return firstLine.Length > 80 ? firstLine[..80] + "…" : firstLine;
    }
}
