using System.Collections.ObjectModel;
using System.Data;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// SNMP 独立页面：只依赖 IP + Community + UDP 端口，**不需要任何 CLI 连接**。
/// 查询结果按分类（系统 / CPU·内存 / 温度 / 风扇 / 电源 / 接口 / VLAN / MAC / IP / 其他）分组展示。
/// 自动刷新默认 OFF；开启后按 5 秒整数倍间隔刷新当前目标，并且"上一轮没跑完就不启动下一轮"。
/// </summary>
public sealed class SnmpViewModel : ViewModelBase, IDisposable
{
    /// <summary>自动刷新可选间隔（秒）—— 5 秒步进。</summary>
    public static readonly int[] IntervalOptions = { 5, 10, 15, 20, 30, 60, 120 };

    private readonly IShellNavigator _shell;
    private readonly ConnectionService _connections;
    private readonly Timer _timer;
    private bool _disposed;

    private string _host = string.Empty;
    private string _community = string.Empty;
    private int _port = 161;
    private int _intervalSeconds = 15;
    private bool _autoRefresh;
    private bool _isBusy;
    private int _trafficIntervalSeconds = 5;
    private string _trafficSummary = "点[采样流量]：间隔读两次接口计数器求差，按利用率排出最忙的端口。";
    private bool _trafficRunning;
    private CancellationTokenSource? _trafficCts;
    private string _status = string.Empty;
    private string _lastUpdate = "尚未查询";
    private SnmpSection? _selectedSection;
    private string _sectionSummary = string.Empty;

    public SnmpViewModel(IShellNavigator shell, ConnectionService connections)
    {
        _shell = shell;
        _connections = connections;
        Title = "SNMP";

        _host = AppServices.Snmp.LastHost;
        _community = AppServices.Snmp.Community;
        _port = AppServices.Snmp.Port;
        _intervalSeconds = NormalizeInterval(AppServices.Snmp.AutoRefreshSeconds);

        QueryCommand = new AsyncRelayCommand(QueryAsync, () => !IsBusy);
        ResumeBigTablesCommand = new AsyncRelayCommand(ResumeBigTablesAsync, () => !IsBusy && CanResumeBigTables);
        TrafficCommand = new AsyncRelayCommand(SampleTrafficAsync, () => !IsBusy && !TrafficRunning);
        StopTrafficCommand = new RelayCommand(StopTraffic, () => TrafficRunning);
        UseCurrentDeviceCommand = new RelayCommand(UseCurrentDevice, () => _connections.Current is not null);
        OpenDevicePageCommand = new RelayCommand(() => _shell.NavigateTo("device"));

        // 用 System.Threading.Timer（不依赖 WPF），tick 通过 UiThread 回到 UI 线程更新界面。
        _timer = new Timer(
            _ => UiThread.Post(() => _ = RunAutoRefreshTickAsync()),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _connections.SessionChanged += OnSessionChangedForCommands;

        // 设备信息库（数据来源：%LocalAppData%\235NetworkAssistant\snmp-devices.json）
        RefreshLibraryCommand = new RelayCommand(RefreshLibrary);
        ClearLibraryCommand = new AsyncRelayCommand(ClearLibraryAsync, () => Devices.Count > 0);
        ShowLibraryCommand = new RelayCommand(ShowLibrary);
        // 设备库的保存发生在**线程池线程**上：这里必须回 UI 线程再动集合，
        // 否则改到绑定 CollectionView 的 ObservableCollection 会抛 NotSupportedException（现场 P0-6）。
        AppServices.SnmpDevices.Changed += (_, _) => UiThread.Post(RefreshLibrary);
        RefreshLibrary();
    }

    public AsyncRelayCommand QueryCommand { get; }

    /// <summary>
    /// 增量续拉：MAC / ARP 被时间预算截断后，从这里继续（每轮 1~2 分钟，可反复点直到取完）。
    /// </summary>
    public AsyncRelayCommand ResumeBigTablesCommand { get; }

    /// <summary>当前是否有"还能继续拉"的大表（分区上存着断点游标）。</summary>
    public bool CanResumeBigTables => Sections.Any(s => s.ResumeOid is not null);

    /// <summary>
    /// 完整拉取大表（MAC / ARP）。
    ///
    /// 默认**关闭**：大型设备的 MAC 表可能有大量记录，连续请求过多时设备可能限速，
    /// 整表要几分钟 —— 默认按 60 秒时间预算取一部分并标注"已截断"，这样查询能在两分钟内出结果。
    /// 勾上之后不设时间预算（查询会明显变慢，可能几分钟），适合确实需要全量 MAC 的场合。
    /// </summary>
    public bool FullLargeTables
    {
        get => AppServices.Snmp.FullLargeTables;
        set
        {
            if (AppServices.Snmp.FullLargeTables == value)
            {
                return;
            }

            AppServices.Snmp.FullLargeTables = value;
            OnPropertyChanged();
            // 勾一次就记住（下次启动仍是这个选择）；保存失败不影响本次查询。
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public RelayCommand UseCurrentDeviceCommand { get; }

    public RelayCommand OpenDevicePageCommand { get; }

    // ---------- SNMP 信息库（按 IP 保存的查询结果 + 历史快照；不含 Community） ----------

    /// <summary>查询过的设备（IP 为稳定标识）。</summary>
    public ObservableCollection<SnmpDeviceRecord> Devices { get; } = new();

    public RelayCommand RefreshLibraryCommand { get; }

    public AsyncRelayCommand ClearLibraryCommand { get; }

    /// <summary>[采样流量]：间隔读两次接口计数器求差 —— 现场"整栋楼卡"时用来看哪个口跑满、哪个口错包在涨。</summary>
    public AsyncRelayCommand TrafficCommand { get; }

    public RelayCommand StopTrafficCommand { get; }

    /// <summary>流量 Top-N 结果（按利用率降序）。</summary>
    public BulkObservableCollection<InterfaceTrafficResult> TrafficResults { get; } = new();

    /// <summary>两次采样之间的间隔（秒）。默认 5 秒：太短计数器差值太小、噪声大；太长用户等不起。</summary>
    public int TrafficIntervalSeconds
    {
        get => _trafficIntervalSeconds;
        set => SetProperty(ref _trafficIntervalSeconds, Math.Clamp(value, 2, 60));
    }

    public bool TrafficRunning
    {
        get => _trafficRunning;
        private set
        {
            if (SetProperty(ref _trafficRunning, value))
            {
                TrafficCommand.RaiseCanExecuteChanged();
                StopTrafficCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TrafficSummary
    {
        get => _trafficSummary;
        private set => SetProperty(ref _trafficSummary, value);
    }

    /// <summary>工具栏 [信息库]：展开下面的信息库面板并刷新列表（不用跳到别的页面）。</summary>
    public RelayCommand ShowLibraryCommand { get; }

    private bool _isLibraryExpanded;

    /// <summary>
    /// 采样两次接口计数器并求差。
    ///
    /// 只读操作。间隔由用户设定（默认 5 秒），期间界面**明确显示"正在等第二次采样"**，
    /// 否则用户会以为卡住了（这一步天然要等几秒）。
    /// </summary>
    private async Task SampleTrafficAsync()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            TrafficSummary = "请先填设备 IP。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Community))
        {
            TrafficSummary = "请先填 Community（只读团体名）。";
            return;
        }

        var service = new SnmpTrafficService(new SnmpV2cClient());
        _trafficCts = new CancellationTokenSource();
        var token = _trafficCts.Token;
        TrafficRunning = true;
        TrafficResults.ReplaceAll(Array.Empty<InterfaceTrafficResult>());

        try
        {
            var host = Host.Trim();
            TrafficSummary = $"正在读第一次计数器（{host}:{Port}）…";
            var before = await service
                .SampleAsync(host, Port, Community, AppServices.Snmp.TimeoutMs, AppServices.Snmp.Retries, token)
                .ConfigureAwait(true);
            if (before.Count == 0)
            {
                TrafficSummary = "第一次采样就没拿到接口计数器：检查 IP / Community / UDP 161。";
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            TrafficSummary = $"已读到 {before.Count} 个接口，正在等 {TrafficIntervalSeconds} 秒后的第二次采样…（点[停止]可取消）";
            await Task.Delay(TimeSpan.FromSeconds(TrafficIntervalSeconds), token).ConfigureAwait(true);

            var after = await service
                .SampleAsync(host, Port, Community, AppServices.Snmp.TimeoutMs, AppServices.Snmp.Retries, token)
                .ConfigureAwait(true);
            watch.Stop();

            var seconds = watch.Elapsed.TotalSeconds;
            var results = SnmpTrafficService.Compare(before, after, seconds, topCount: 20);
            TrafficResults.ReplaceAll(results);

            var busy = results.FirstOrDefault();
            TrafficSummary = results.Count == 0
                ? "两次采样没有可比对的接口（可能这台设备的计数器取不到）。"
                : $"采样完成（间隔 {seconds:F1} 秒）：最忙的是 {busy?.Port}，利用率 {busy?.UtilizationText}、"
                  + $"错包 {busy?.ErrorText}。利用率按接口速率估算，只作参考。";
        }
        catch (OperationCanceledException)
        {
            TrafficSummary = "已取消采样。";
        }
        catch (Exception ex)
        {
            TrafficSummary = $"采样失败：{ex.Message}";
        }
        finally
        {
            TrafficRunning = false;
            _trafficCts?.Dispose();
            _trafficCts = null;
        }
    }

    /// <summary>诊断/自检用：直接跑一次流量采样。</summary>
    public Task RunTrafficForDiagnosticsAsync() => SampleTrafficAsync();

    private void StopTraffic()
    {
        _trafficCts?.Cancel();
    }

    public bool IsLibraryExpanded
    {
        get => _isLibraryExpanded;
        set => SetProperty(ref _isLibraryExpanded, value);
    }

    private void ShowLibrary()
    {
        RefreshLibrary();
        IsLibraryExpanded = true;
        Status = Devices.Count == 0
            ? "信息库还是空的：查一次就会按设备存进来。"
            : $"信息库里有 {Devices.Count} 台设备（同一 IP 重复查询只更新它自己并追加历史快照）。";
    }

    private SnmpDeviceRecord? _selectedDevice;

    public SnmpDeviceRecord? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(DeviceSummary));
                OnPropertyChanged(nameof(DeviceFields));
                OnPropertyChanged(nameof(DeviceSections));
                DeviceSectionChoice = DeviceSections.FirstOrDefault();
            }
        }
    }

    public IReadOnlyList<SnmpStoredField> DeviceFields =>
        SelectedDevice?.Latest?.Fields ?? (IReadOnlyList<SnmpStoredField>)Array.Empty<SnmpStoredField>();

    public IReadOnlyList<SnmpStoredSection> DeviceSections =>
        SelectedDevice?.Latest?.Sections ?? (IReadOnlyList<SnmpStoredSection>)Array.Empty<SnmpStoredSection>();

    private SnmpStoredSection? _deviceSectionChoice;

    public SnmpStoredSection? DeviceSectionChoice
    {
        get => _deviceSectionChoice;
        set
        {
            if (SetProperty(ref _deviceSectionChoice, value))
            {
                OnPropertyChanged(nameof(DeviceTable));
                OnPropertyChanged(nameof(DeviceSectionSummary));
            }
        }
    }

    /// <summary>选中的历史分类表（DataTable：DataGrid 自动生成列）。</summary>
    public DataView? DeviceTable
    {
        get
        {
            var section = DeviceSectionChoice;
            if (section is null || section.Columns.Count == 0)
            {
                return null;
            }

            var table = new DataTable();
            foreach (var column in section.Columns)
            {
                table.Columns.Add(column);
            }

            foreach (var row in section.Rows)
            {
                var values = new object[section.Columns.Count];
                for (var i = 0; i < section.Columns.Count; i++)
                {
                    values[i] = i < row.Count ? row[i] : "N/A";
                }

                table.Rows.Add(values);
            }

            return table.DefaultView;
        }
    }

    public string DeviceSectionSummary => DeviceSectionChoice is null
        ? "N/A（这台设备还没有分类结果）"
        : $"{DeviceSectionChoice.Title}：{DeviceSectionChoice.Rows.Count} 行"
          + (DeviceSectionChoice.Truncated ? "（已截断）" : string.Empty);

    public string DeviceSummary
    {
        get
        {
            var device = SelectedDevice;
            if (device is null)
            {
                return Devices.Count == 0
                    ? "还没有查询过的设备：在上面填 IP + Community 点[查询]，结果就会按设备存进这里。"
                    : "选择一台设备，查看它的 SNMP 结果与历史快照。";
            }

            return $"{device.Ip}｜{device.Model}｜序列号 {device.SerialNumber}｜版本 {device.SoftwareVersion}｜"
                 + $"最后查询 {device.LastQueryAt}｜历史快照 {device.History.Count} 次｜{device.LastStatus}";
        }
    }

    /// <summary>把设备信息库读进界面（同一 IP 反复查询只会更新它自己那一条 + 追加历史快照）。</summary>
    public void RefreshLibrary()
    {
        var selectedIp = SelectedDevice?.Ip;
        Devices.Clear();
        foreach (var device in AppServices.SnmpDevices.Devices)
        {
            Devices.Add(device);
        }

        SelectedDevice = Devices.FirstOrDefault(d =>
            string.Equals(d.Ip, selectedIp, StringComparison.OrdinalIgnoreCase)) ?? Devices.FirstOrDefault();
        ClearLibraryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(DeviceSummary));
    }

    private async Task ClearLibraryAsync()
    {
        await AppServices.SnmpDevices.ClearAsync().ConfigureAwait(true);
        RefreshLibrary();
        Status = "已清空 SNMP 设备信息库（只删查询结果；Community 从来没有落过盘）。";
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value ?? string.Empty);
    }

    /// <summary>Community：敏感信息，只存内存、不写日志、不进设备信息库。</summary>
    public string Community
    {
        get => _community;
        set
        {
            if (SetProperty(ref _community, value ?? string.Empty))
            {
                AppServices.Snmp.Community = _community;
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value is > 0 and < 65536 ? value : 161))
            {
                AppServices.Snmp.Port = _port;
            }
        }
    }

    public IReadOnlyList<int> AutoRefreshIntervals => IntervalOptions;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            var normalized = NormalizeInterval(value);
            if (SetProperty(ref _intervalSeconds, normalized))
            {
                AppServices.Snmp.AutoRefreshSeconds = normalized;
                ApplyTimer();
            }
        }
    }

    /// <summary>自动刷新总开关（默认 OFF；本版本每次启动都是 OFF）。</summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!SetProperty(ref _autoRefresh, value))
            {
                return;
            }

            AppServices.Snmp.AutoRefreshEnabled = value;
            ApplyTimer();
            Status = value
                ? $"自动刷新已开启：每 {IntervalSeconds} 秒刷新 {SafeHost()}（上一轮没完成不会叠加下一轮）"
                : "自动刷新已关闭：只有点[查询]或[刷新]才查询。";
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                QueryCommand.RaiseCanExecuteChanged();
                ResumeBigTablesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string LastUpdate
    {
        get => _lastUpdate;
        private set => SetProperty(ref _lastUpdate, value);
    }

    /// <summary>分类结果（每类一张表；没取到的类别也保留，行内显示 N/A）。</summary>
    public ObservableCollection<SnmpSection> Sections { get; } = new();

    public SnmpSection? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                SectionSummary = value?.Summary ?? string.Empty;
                InvalidateSelectedTable();
                OnPropertyChanged(nameof(SelectedTable));
                OnPropertyChanged(nameof(SelectedNote));
                OnPropertyChanged(nameof(HasTable));
            }
        }
    }

    /// <summary>
    /// 表格数据用 DataTable 承载：DataGrid 直接自动生成列。
    /// （不要绑 string[]，那会把数组自身的 Length/Rank 当列显示出来。）
    ///
    /// ⚠️ 必须缓存（2026-09-22 改）。旧实现把这个 DataTable 建在 getter 里：
    /// 每次绑定求值都会把**整张表**重新灌一遍，而 `HasTable` 也在 getter 里再调一次 SelectedTable。
    /// 大型 MAC/ARP 表下，切换分区时重建大表会明显阻塞界面。现在只在 SelectedSection 真的换了对象时重建；
    /// 查询完成后 Sections 里是全新的 SnmpSection 实例，缓存自然失效，不会显示旧数据。
    /// </summary>
    private DataView? _selectedTableCache;
    private SnmpSection? _selectedTableCacheFor;
    private CancellationTokenSource? _tableFillCts;

    public DataView? SelectedTable
    {
        get
        {
            var section = SelectedSection;
            if (ReferenceEquals(_selectedTableCacheFor, section))
            {
                return _selectedTableCache;
            }

            _selectedTableCacheFor = section;
            _selectedTableCache = StartChunkedTableBuild(section);
            return _selectedTableCache;
        }
    }

    /// <summary>
    /// **分片喂 UI**（2026-09-23 新增，面向外勤小电脑）：先只建"表结构"（瞬间完成、表格立刻出现），
    /// 数据行每 500 行灌一批、批间让出一次 UI 线程。
    ///
    /// 为什么：大规模 MAC/ARP 表旧实现一次性 `Rows.Add` 全灌完 ——
    /// 在 4 核弱 CPU 上就是"点一下卡住几秒、界面不刷新"。分片之后表格边出现边填，
    /// 配合 `SnmpGrid` 的行虚拟化（见 SnmpView.xaml），UI 线程不会长时间被占住。
    /// DataTable 不是线程安全的，所以**行仍然在 UI 线程加**，只是分批 + 批间 `await` 让出调度。
    /// </summary>
    private DataView? StartChunkedTableBuild(SnmpSection? section)
    {
        _tableFillCts?.Cancel();
        _tableFillCts?.Dispose();
        _tableFillCts = null;

        if (section is null || section.Columns.Length == 0)
        {
            return null;
        }

        var table = new DataTable();
        foreach (var column in section.Columns)
        {
            table.Columns.Add(column);
        }

        if (section.Rows.Count == 0)
        {
            return table.DefaultView;
        }

        var cts = new CancellationTokenSource();
        _tableFillCts = cts;
        _ = FillTableInChunksAsync(table, section, cts);
        return table.DefaultView;
    }

    private async Task FillTableInChunksAsync(DataTable table, SnmpSection section, CancellationTokenSource cts)
    {
        const int chunkSize = 500;
        var chunks = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var start = 0; start < section.Rows.Count; start += chunkSize)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                var end = Math.Min(start + chunkSize, section.Rows.Count);
                for (var r = start; r < end; r++)
                {
                    var source = section.Rows[r];
                    var values = new object[section.Columns.Length];
                    for (var i = 0; i < section.Columns.Length; i++)
                    {
                        values[i] = i < source.Length ? source[i] : "N/A";
                    }

                    table.Rows.Add(values);
                }

                if (end < section.Rows.Count)
                {
                    // 让出一拍：UI 线程去处理消息/渲染，再灌下一批（1ms 的代价换"界面不冻"）
                    await Task.Delay(1, cts.Token).ConfigureAwait(true);
                }

                chunks++;
            }

            watch.Stop();
            AppServices.Log.Info(
                $"SNMP 表格分片填充完成：{section.Title} {section.Rows.Count} 行 / {chunks} 批 / {watch.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            // 切了别的分区/表：丢弃这次填充即可
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("SNMP 表格分片填充失败（不影响已显示的部分）", ex);
        }
    }

    /// <summary>让表格缓存失效（切换分区、或重建分区列表后调用）。</summary>
    private void InvalidateSelectedTable()
    {
        _selectedTableCache = null;
        _selectedTableCacheFor = null;
    }

    public string SelectedNote => SelectedSection?.Note ?? string.Empty;

    /// <summary>
    /// 直接看分区本身，**不要**再走 SelectedTable —— 那会顺带触发一次建表，
    /// 而这个属性会被 XAML 反复求值（旧实现等于每次求值建两遍表）。
    /// </summary>
    public bool HasTable => SelectedSection is { Columns.Length: > 0 };

    public string SectionSummary
    {
        get => _sectionSummary;
        private set => SetProperty(ref _sectionSummary, value);
    }

    /// <summary>[使用当前设备 IP]：有 CLI 连接时一键填入；没有连接就提示（SNMP 不因此不可用）。</summary>
    private void UseCurrentDevice()
    {
        var ip = IpAddressHelper.ExtractFirst(_connections.ManagementAddress);
        // 配置线没有 IP：用【设备】页读到过的那个（用户读过「三层接口」后就有了）
        if (string.IsNullOrWhiteSpace(ip))
        {
            ip = _connections.LearnedManagementAddress;
        }

        if (string.IsNullOrWhiteSpace(ip))
        {
            // 配置线连接时 ManagementAddress 是 COM 口名、不是 IP —— 要跟"压根没连接"区分开，
            // 否则用户会以为"明明连上了却提示没有连接"（现场反馈过同类困惑）。
            var raw = _connections.ManagementAddress;
            Status = _connections.IsConnected
                ? $"当前是配置线连接（{raw}），还没有读到管理 IP —— 先在【设备】页 →「三层接口」刷新一次，"
                  + "或在下面手工填交换机 IP。"
                : "当前没有 CLI 连接，无法自动填入；直接手工填 IP 也能查询。";
            return;
        }

        Host = ip;
        Status = $"已填入当前设备 IP：{ip}（SNMP 查询本身不依赖该连接）";
    }

    /// <summary>
    /// 增量续拉：把被时间预算截断的 MAC / ARP 从各自断点再拉一轮，**把新增行追加到原分区**上。
    ///
    /// 为什么这样设计：大型设备的 MAC 表可能有大量记录，连续请求过多时设备可能限速，
    /// 一次拉全实测 16 分钟都跑不完。分轮拉：每轮 1~2 分钟，界面立刻能看，反复点直到取完。
    /// </summary>
    private async Task ResumeBigTablesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var macSection = Sections.FirstOrDefault(s => s.Category == SnmpCategory.Mac);
        var arpSection = Sections.FirstOrDefault(s => s.Category == SnmpCategory.Ip);
        var macCursor = macSection?.ResumeOid;
        var arpCursor = arpSection?.ResumeOid;
        if (macCursor is null && arpCursor is null)
        {
            Status = "大表已经取完，没有需要继续拉取的部分。";
            return;
        }

        IsBusy = true;
        Status = "正在从断点继续拉取大表（MAC / ARP）…";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 续拉要"整表重排"，重排出来的是**服务端原始列**（端口列一律「未提供」）；
        // 先把上一轮已经解出来的端口按 `VLAN|MAC` 记下来，重排后搬回去 —— 否则每续拉一次，
        // 之前辛苦解出来的端口就被抹掉、还要再解一遍。
        var carriedPorts = SnapshotResolvedPorts(macSection);
        try
        {
            var budgetMs = FullLargeTables ? 0 : SnmpQueryService.DefaultLargeTableBudgetMs;
            var resumed = await Task.Run(() => AppServices.SnmpQuery.ResumeBigTablesAsync(
                    Host.Trim(),
                    Port,
                    Community,
                    AppServices.Snmp.TimeoutMs,
                    AppServices.Snmp.Retries,
                    macCursor,
                    arpCursor,
                    budgetMs,
                    CancellationToken.None,
                    macSection,
                    arpSection))
                .ConfigureAwait(true);

            var added = 0;
            var replaced = 0;
            foreach (var fresh in resumed)
            {
                var target = Sections.FirstOrDefault(s => s.Category == fresh.Category);
                if (target is null)
                {
                    Sections.Add(fresh);
                    added += fresh.Rows.Count;
                    continue;
                }

                var before = target.Rows.Count;
                if (fresh.RowsAreCumulative)
                {
                    // 服务端已经按行索引把**各轮拿到的列合并**后整表重渲染（fresh.Rows 是全量行），
                    // 这里必须**整体替换**：若还 AddRange，同一个 `<VLAN>.<MAC>` 会被再加一遍
                    // → 行数翻倍、同一 MAC 出现两行（2026-09-23 修）。
                    target.Rows.Clear();
                    target.Rows.AddRange(fresh.Rows);
                    target.RawColumns = fresh.RawColumns;
                    target.RawColumnOrder = fresh.RawColumnOrder;
                    replaced++;
                    if (carriedPorts.Count > 0)
                    {
                        ReapplyResolvedPorts(target, carriedPorts);
                    }
                }
                else
                {
                    // 界面手里的分区没有累积器（例如从设备信息库恢复的快照）→ 只能按老办法追加新行
                    target.Rows.AddRange(fresh.Rows);
                }

                target.Truncated = fresh.Truncated;
                target.ResumeOid = fresh.ResumeOid;
                target.Note = fresh.Note;
                added += Math.Max(0, target.Rows.Count - before);
            }

            watch.Stop();

            // 续拉顺带**补老行的端口**：端口解析只处理"还没有端口"的行，
            // 所以这里把合并后的整张 MAC 表再解一轮 —— 首批那 1.5 万行往往因为排在
            // 大表 WALK 之后、撞上设备限速而没解出端口，靠这一步逐轮补齐。
            if (macSection is { Rows.Count: > 0 })
            {
                try
                {
                    var (portResolved, portPending) = await Task.Run(async () =>
                            await AppServices.SnmpQuery.ResolveMacPortsAsync(
                                Host.Trim(), Port, Community, macSection, AppServices.Snmp.TimeoutMs,
                                AppServices.Snmp.Retries, 30_000, CancellationToken.None)
                                .ConfigureAwait(false))
                        .ConfigureAwait(true);
                    if (portPending > 0)
                    {
                        macSection.Note += $"\n端口列：本轮补解 {portResolved} / {portPending} 行"
                                           + (portResolved < portPending ? "（其余下轮继续）" : "（全部解出）");
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Log.Debug($"续拉补端口失败（不影响数据）：{ex.Message}");
                }
            }

            SectionSummary = SelectedSection?.Summary ?? string.Empty;
            InvalidateSelectedTable();
            OnPropertyChanged(nameof(SelectedTable));
            OnPropertyChanged(nameof(CanResumeBigTables));
            ResumeBigTablesCommand.RaiseCanExecuteChanged();

            var macRows = macSection?.Rows.Count ?? 0;
            var arpRows = arpSection?.Rows.Count ?? 0;
            var mergeNote = replaced > 0
                ? "（大表按行索引整表重排：同一行的列已合并，不会重复计数）"
                : string.Empty;
            Status = CanResumeBigTables
                ? $"继续拉取完成：本轮净增 {added} 行（MAC 累计 {macRows} / ARP 累计 {arpRows}）{mergeNote}，"
                  + $"还有更多 —— 可以再点一次继续；本轮耗时 {watch.Elapsed.TotalSeconds:F1} 秒"
                : $"继续拉取完成：本轮净增 {added} 行{mergeNote}，大表已全部取完"
                  + $"（MAC {macRows} / ARP {arpRows}）；本轮耗时 {watch.Elapsed.TotalSeconds:F1} 秒";
            LastUpdate = $"最后更新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}｜增量续拉 净增 {added} 行";
        }
        catch (Exception ex)
        {
            Status = $"继续拉取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把当前 MAC 表里**已经解出来的端口**按 `VLAN|MAC` 记一份（续拉整表重排后要搬回去）。
    /// 只认真正解出来的端口，「未提供」不记。
    /// </summary>
    private static Dictionary<string, string> SnapshotResolvedPorts(SnmpSection? section)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (section is null)
        {
            return map;
        }

        foreach (var row in section.Rows)
        {
            if (row.Length < 3)
            {
                continue;
            }

            var port = row[2];
            if (string.IsNullOrWhiteSpace(port) || port == SnmpQueryService.NotProvidedText)
            {
                continue;
            }

            map[$"{row[0]}|{row[1]}"] = port;
        }

        return map;
    }

    /// <summary>续拉整表重排后，把上一轮解出的端口搬回仍然显示「未提供」的行；返回搬运的行数。</summary>
    private static int ReapplyResolvedPorts(SnmpSection section, Dictionary<string, string> ports)
    {
        var applied = 0;
        foreach (var row in section.Rows)
        {
            if (row.Length < 3)
            {
                continue;
            }

            var port = row[2];
            if (!string.IsNullOrWhiteSpace(port) && port != SnmpQueryService.NotProvidedText)
            {
                continue;
            }

            if (!ports.TryGetValue($"{row[0]}|{row[1]}", out var saved))
            {
                continue;
            }

            row[2] = saved;
            if (row.Length > 3)
            {
                row[3] = "端口来自标准 FDB 表（前面某一轮已解出，续拉重排后沿用）";
            }

            applied++;
        }

        return applied;
    }

    private async Task QueryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            Status = "请先填写设备 IP。";
            return;
        }

        IsBusy = true;
        Status = $"正在查询 {Host}:{Port} …（大机型可能几十秒：上万行的表要逐项 WALK，界面不会卡）";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AppServices.Snmp.LastHost = Host.Trim();

            // 关键：整个 WALK + 解析（大机型可能上万行）都放到**后台线程**执行。
            // 之前是在 UI 线程上跑（await 回来后继续在 UI 线程解析），核心交换机一查就"卡住不动"，
            // 连状态文字都刷不出来。放到 Task.Run 之后 UI 线程只负责最后把结果贴上去。
            var budgetMs = FullLargeTables ? 0 : SnmpQueryService.DefaultLargeTableBudgetMs;
            var result = await Task.Run(
                    () => AppServices.SnmpQuery.QueryAsync(
                        Host.Trim(),
                        Port,
                        Community,
                        AppServices.Snmp.TimeoutMs,
                        AppServices.Snmp.Retries,
                        CancellationToken.None,
                        true,
                        budgetMs))
                .ConfigureAwait(true);

            watch.Stop();

            // 分区表换成新对象了：把表格缓存清掉，否则会继续显示上一台设备/上一次查询的表。
            InvalidateSelectedTable();
            Sections.Clear();
            foreach (var section in result.Sections)
            {
                Sections.Add(section);
            }

            // 保持用户当前看的那一类（自动刷新时不要每次都跳回"CPU / 内存"）。
            // 只有"这一类在新结果里没有了"才回退到第一个有数据的分类。
            var previous = SelectedSection?.Category;
            SelectedSection = (previous is { } category ? Sections.FirstOrDefault(s => s.Category == category) : null)
                              ?? Sections.FirstOrDefault(s => s.Rows.Count > 0)
                              ?? Sections.FirstOrDefault();
            Status = $"{result.StatusText}｜耗时 {watch.Elapsed.TotalSeconds:F1} 秒";
            LastUpdate =
                $"最后更新：{result.QueriedAt:yyyy-MM-dd HH:mm:ss}｜{result.Status}｜耗时 {watch.Elapsed.TotalSeconds:F1} 秒";

            if (result.Status != SnmpQueryStatus.Failed)
            {
                await AppServices.SnmpDevices.SaveAsync(result).ConfigureAwait(true);
                RefreshLibrary();
            }
        }
        catch (Exception ex)
        {
            Status = $"查询失败：{ex.Message}";
            LastUpdate = $"最后更新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}（失败）";
            AppServices.Log.Warn("SNMP 页面查询失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 自动刷新的一次 tick：**正在查询就直接跳过**，不排队、不叠加。
    /// 定时器与自动化测试共用（测试里直接调用，避免依赖消息循环）。
    /// </summary>
    public async Task RunAutoRefreshTickAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        await QueryAsync().ConfigureAwait(true);
    }

    private void ApplyTimer()
    {
        if (_autoRefresh)
        {
            var interval = TimeSpan.FromSeconds(IntervalSeconds);
            _timer.Change(interval, interval);
            IsAutoRefreshTimerRunning = true;
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            IsAutoRefreshTimerRunning = false;
        }
    }

    /// <summary>自动刷新定时器是否真的在跑（诊断/自检用：OFF 必须是 false）。</summary>
    public bool IsAutoRefreshTimerRunning { get; private set; }

    private static int NormalizeInterval(int seconds)
    {
        var value = Math.Max(5, seconds);
        return Math.Max(5, (int)(Math.Round(value / 5.0) * 5));
    }

    private string SafeHost() => string.IsNullOrWhiteSpace(Host) ? "(未填 IP)" : Host.Trim();

    /// <summary>页面离开时停掉定时器，避免后台空转。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
        // 必须用**同一个委托实例**解绑：原来的 `-= (_, _) => ...` 每次都是新 lambda，永远解绑不掉，
        // 结果是页面 VM 一直被 ConnectionService 强引用（里面还挂着上万行 SNMP 表格数据）。
        _connections.SessionChanged -= OnSessionChangedForCommands;
    }

    /// <summary>连接会话变化时刷新"用当前设备"按钮的可用状态（具名方法，便于成对订阅/解绑）。</summary>
    private void OnSessionChangedForCommands(object? sender, EventArgs e) =>
        UseCurrentDeviceCommand.RaiseCanExecuteChanged();

    /// <summary>
    /// 离开页面：把自动刷新关掉，别在后台继续隔几秒查一次设备。
    /// **不能 Dispose** —— 页面 VM 由 MainViewModel 缓存复用，释放后再进来就用不了了。
    /// </summary>
    public void OnNavigatedFrom()
    {
        if (_disposed)
        {
            return;
        }

        if (AutoRefresh)
        {
            AutoRefresh = false;             // setter 会停掉定时器
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }
}

