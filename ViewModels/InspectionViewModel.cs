using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 批量巡检页：从资源库挑一批交换机 → 轻量 SNMP 采集 → "需关注"清单 → 导出 CSV。
///
/// 现场背景：资源库里已经有几百台设备，每周真正要看的只有
/// "谁取不到、CPU/内存高、温度超阈值"这几件事。以前只能在 SNMP 页一台台填 IP 点查询，
/// 而且每次都把 MAC/ARP 整表 WALK 下来；这里走 <see cref="SnmpInspectionService"/> 的轻量路径
/// （真机实测同一台设备 1.3 秒 vs 完整查询 4.7 秒）。
/// </summary>
public sealed class InspectionViewModel : ViewModelBase
{
    private readonly IResourceRepository _repository;
    private readonly IShellNavigator _shell;
    private readonly SnmpInspectionService _service;
    private readonly Func<string?> _folderPicker;

    private string _buildingFilter = string.Empty;
    private string _keywordFilter = string.Empty;
    private string _community = string.Empty;
    private int _timeoutMs = 3000;
    private int _retries = 1;
    private int _concurrency = 4;
    private string _progressText = "尚未开始。";
    private string _summaryText = "未选择设备。";
    private string _statusHint = "先在【资源库】页导入设备，再回来勾选 → 填 Community → 点[开始巡检]。";
    private bool _isBusy;
    private bool _onlyAnomaly;
    private CancellationTokenSource? _cts;
    private List<InspectionRow> _allResults = new();

    /// <param name="folderPicker">
    /// 选导出目录。**做成可注入的委托**而不是在 VM 里直接弹 WPF 对话框：
    /// 这样这个 VM 不依赖 WPF，能进自检工程被自动化覆盖（资源库页的 VM 就是因为直接弹对话框而没法进自检）。
    /// 传 null 表示当前环境没有选目录的能力（导出会提示"已取消"）。
    /// </param>
    public InspectionViewModel(
        IShellNavigator shell,
        IResourceRepository repository,
        SnmpInspectionService service,
        Func<string?>? folderPicker = null)
    {
        _shell = shell;
        _repository = repository;
        _service = service;
        _folderPicker = folderPicker ?? (() => null);
        Title = "巡检";

        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => !IsBusy && Devices.Count > 0);
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy && Devices.Count > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && Devices.Any(d => d.IsSelected));
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _allResults.Count > 0);
        OpenSnmpPageCommand = new RelayCommand(() => _shell.NavigateTo("snmp"));

        // Community 与 SNMP 页共用同一份内存设置（不落盘那条规矩不变）
        Community = AppServices.Snmp.Community;

        // 超时/重试也跟随 SNMP 页那份设置，而不是在这里另立一套默认值。
        // 现场教训（2026-09-22）：巡检页原先写死 3000ms，而用户已经把 SNMP 页调成 8000ms 走隧道，
        // 结果巡检每次都在"资源表"这一步超时 —— CPU/内存两个最关键的指标全是 N/A，
        // 看起来像"设备不支持"，实际是**两套设置不一致**造成的。
        TimeoutMs = AppServices.Snmp.TimeoutMs;
        Retries = AppServices.Snmp.Retries;

        // 进页面就把资源库里的候选设备列出来：这一步只读内存里的库，不碰网络，
        // 不该让用户先点一次[刷新候选设备]才看到东西（第一次打开看到空列表会以为没导入）。
        RefreshDevices();
    }

    /// <summary>资源库筛出来的候选设备（带勾选状态）。</summary>
    public BulkObservableCollection<SelectableDeviceItem> Devices { get; } = new();

    /// <summary>巡检结果（按"只看需关注"过滤后的视图）。</summary>
    public BulkObservableCollection<InspectionRow> Results { get; } = new();

    public string BuildingFilter
    {
        get => _buildingFilter;
        set { if (SetProperty(ref _buildingFilter, value)) { RefreshDevices(); } }
    }

    public string KeywordFilter
    {
        get => _keywordFilter;
        set { if (SetProperty(ref _keywordFilter, value)) { RefreshDevices(); } }
    }

    public string Community
    {
        get => _community;
        set { if (SetProperty(ref _community, value)) { AppServices.Snmp.Community = value; } }
    }

    public int TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, Math.Clamp(value, 500, 30000));
    }

    public int Retries
    {
        get => _retries;
        set => SetProperty(ref _retries, Math.Clamp(value, 0, 5));
    }

    /// <summary>
    /// 同时查几台。默认 4：每台设备各自独立查询，不会像"对同一台设备并发下命令"那样互相干扰；
    /// 几百台全串行要跑十几分钟，4 路并发能压到可接受，又不至于把网段压满。
    /// </summary>
    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, Math.Clamp(value, 1, 8));
    }

    public bool OnlyAnomaly
    {
        get => _onlyAnomaly;
        set { if (SetProperty(ref _onlyAnomaly, value)) { ApplyResultFilter(); } }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshDevicesCommand.RaiseCanExecuteChanged();
                SelectAllCommand.RaiseCanExecuteChanged();
                ClearSelectionCommand.RaiseCanExecuteChanged();
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenSnmpPageCommand { get; }

    /// <summary>资源库里出现过的楼栋（下拉筛选用）。</summary>
    public IReadOnlyList<string> Buildings =>
        _repository.Database.Switches
            .Select(s => s.Building)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

    /// <summary>按楼栋 / 关键字从资源库刷新候选设备列表。</summary>
    public void RefreshDevices()
    {
        var items = _repository.Database.Switches
            .Where(s => !string.IsNullOrWhiteSpace(s.ManagementIp))
            .Where(s => string.IsNullOrWhiteSpace(BuildingFilter)
                        || string.Equals(s.Building, BuildingFilter, StringComparison.Ordinal))
            .Where(s => string.IsNullOrWhiteSpace(KeywordFilter)
                        || s.ManagementIp.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase)
                        || s.Floor.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new DeviceTarget(
                IpAddressHelper.ExtractFirst(s.ManagementIp) ?? s.ManagementIp.Trim(),
                string.IsNullOrWhiteSpace(s.Name) ? s.Floor : s.Name,
                s.Building))
            // 同一个 IP 在资源库里可能出现多次（不同工作表各记了一遍）→ 去重，否则同一台会被查两遍
            .GroupBy(t => t.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Ip, StringComparer.Ordinal)
            .ToList();

        Devices.ReplaceAll(items.Select(t => new SelectableDeviceItem(t)));
        StatusHint = items.Count == 0
            ? "资源库里没有匹配的设备（先到【资源库】页导入，或放宽筛选条件）。"
            : $"候选 {items.Count} 台。勾选后点[开始巡检]（COM 口/Telnet 都不需要，只走 SNMP）。";
        SelectAllCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var device in Devices)
        {
            device.IsSelected = selected;
        }

        StartCommand.RaiseCanExecuteChanged();
        SummaryText = selected ? $"已选择 {Devices.Count} 台，点[开始巡检]" : "未选择设备。";
    }

    private List<DeviceTarget> SelectedTargets() =>
        Devices.Where(d => d.IsSelected).Select(d => d.Target).ToList();

    /// <summary>诊断/自检用：按 IP 勾选指定设备（找不到就忽略）。产品界面走复选框，不调用它。</summary>
    public void SelectIps(IEnumerable<string> ips)
    {
        var wanted = ips.Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            device.IsSelected = wanted.Contains(device.Ip);
        }

        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>诊断/自检用：直接跑一轮巡检（跳过界面上的按钮）。</summary>
    public Task RunInspectionAsync() => StartAsync();

    private async Task StartAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusHint = "请先勾选要巡检的设备。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Community))
        {
            StatusHint = "请先填 SNMP Community（只读团体名）。它只存在内存里，不会写进配置文件。";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        _allResults = new List<InspectionRow>();
        Results.ReplaceAll(Array.Empty<InspectionRow>());
        ProgressText = $"0 / {targets.Count}";
        StatusHint = $"正在巡检 {targets.Count} 台（{Concurrency} 路并发）…";

        var done = 0;
        var gate = new SemaphoreSlim(Math.Clamp(Concurrency, 1, 8));
        var collected = new List<InspectionRow>();

        try
        {
            var tasks = targets.Select(async target =>
            {
                await gate.WaitAsync(token).ConfigureAwait(true);
                try
                {
                    var row = await _service
                        .InspectAsync(target, Community, TimeoutMs, Retries, token)
                        .ConfigureAwait(true);

                    // 这里的续体回到 UI 线程（await 捕获了同步上下文），可以直接改绑定集合
                    collected.Add(row);
                    _allResults = collected.ToList();
                    ApplyResultFilter();
                    ProgressText = $"{Interlocked.Increment(ref done)} / {targets.Count}";
                    UpdateSummary();
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(true);
            StatusHint = token.IsCancellationRequested
                ? $"已停止：完成 {done} / {targets.Count} 台。"
                : $"巡检完成：{targets.Count} 台。点[导出 CSV] 可以把这份清单交给同事。";
        }
        catch (OperationCanceledException)
        {
            StatusHint = $"已停止：完成 {done} / {targets.Count} 台（已完成的结果保留在表里）。";
        }
        finally
        {
            // 一轮巡检在台账里留一条汇总（含"需关注"清单，交接班要看的就是这个）
            if (_allResults.Count > 0)
            {
                var anomalies = _allResults
                    .Where(r => r.Status != InspectionStatus.Normal)
                    .Take(10)
                    .Select(r => $"{r.Ip} {r.StatusLabel}{(r.Anomaly.Length > 0 ? "：" + r.Anomaly : string.Empty)}")
                    .ToList();
                _shell.AddRecentOperation(
                    "批量巡检",
                    SummaryText,
                    _allResults.All(r => r.Status == InspectionStatus.Normal),
                    ledgerDetail: SummaryText + (anomalies.Count > 0 ? $"；需关注：{string.Join("；", anomalies)}" : string.Empty));
            }

            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            UpdateSummary();
        }
    }

    private void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        StatusHint = "正在停止（已发出的查询会各自返回后收尾）…";
        _cts.Cancel();
    }

    private void ApplyResultFilter()
    {
        var rows = OnlyAnomaly
            ? _allResults.Where(r => r.Status != InspectionStatus.Normal).ToList()
            : _allResults;
        Results.ReplaceAll(rows.OrderBy(r => r.Status).ThenBy(r => r.Ip, StringComparer.Ordinal));
        ExportCsvCommand.RaiseCanExecuteChanged();
    }

    private void UpdateSummary()
    {
        if (_allResults.Count == 0)
        {
            return;
        }

        var normal = _allResults.Count(r => r.Status == InspectionStatus.Normal);
        var anomaly = _allResults.Count(r => r.Status == InspectionStatus.Anomaly);
        var failed = _allResults.Count(r => r.Status == InspectionStatus.Failed);
        SummaryText = $"共 {_allResults.Count} 台｜正常 {normal}｜需关注 {anomaly}｜取不到 {failed}";
    }

    private void ExportCsv()
    {
        if (_allResults.Count == 0)
        {
            return;
        }

        var folder = _folderPicker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusHint = "已取消导出。";
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"巡检清单_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var builder = new StringBuilder();
            builder.AppendLine(InspectionRow.CsvHeader);
            foreach (var row in _allResults.OrderBy(r => r.Ip, StringComparer.Ordinal))
            {
                builder.AppendLine(row.ToCsvLine());
            }

            // 带 BOM：不带的话 Excel 打开中文列名会乱码
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            StatusHint = $"已导出 {_allResults.Count} 条：{path}";
            _shell.AddRecentOperation("巡检导出", path);
        }
        catch (Exception ex)
        {
            StatusHint = $"导出失败：{ex.Message}";
        }
    }
}

// 注意：勾选用的设备项（原 InspectionDeviceItem）已挪到 Models/SelectableDeviceItem.cs ——
// 它是**批量备份**共用的类型，不能跟着巡检页一起被排除出发布件。
