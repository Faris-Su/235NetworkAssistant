using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// QuickPing：随手敲一个地址就 ping；也可以一次 ping 一片（网段简写或资源库里筛出来的设备）。
///
/// 定位：**最快的那个"通不通"工具** —— 不用连设备、不用选协议、不用等查询，
/// 输完回车就出结果。批量时并发 ping，谁不通一眼就能看到。
///
/// ⚠️ 口径（界面上也写着）：很多交换机默认忽略 ICMP，"ping 不通"不等于设备不在线。
/// </summary>
public sealed class QuickPingViewModel : ViewModelBase
{
    private readonly IResourceRepository _repository;
    private readonly IShellNavigator _shell;
    private readonly QuickPingService _service = new();
    private readonly Func<string?> _folderPicker;

    private string _targetText = string.Empty;
    private string _ipPrefix = "192.0.2";
    private int _rangeStart = 1;
    private int _rangeEnd = 254;
    private bool _graphMode;
    private string _startTimeText = "—";
    private string _endTimeText = "—";
    private string _elapsedText = "—";
    private int _foundCount;
    private string _buildingFilter = string.Empty;
    private string _keywordFilter = string.Empty;
    // 默认值对齐现场习惯（经典 QuickPing 就是 200ms / 20 线程）：内网都是毫秒级，
    // 超时短一点 + 线程多一点，扫一个 /24 段才等得起
    private int _timeoutMs = 200;
    private int _concurrency = 20;
    private bool _isBusy;
    private string _summary = "输入 IP / 域名 / 网段（如 192.0.2.1-32）后回车即可。";
    private string _progressText = string.Empty;
    private string _statusHint = string.Empty;
    private CancellationTokenSource? _cts;
    private QuickPingResult? _selectedResult;
    private bool _graphDirty = true;

    public QuickPingViewModel(IShellNavigator shell, IResourceRepository repository, Func<string?>? folderPicker = null)
    {
        _shell = shell;
        _repository = repository;
        _folderPicker = folderPicker ?? (() => null);
        Title = "QuickPing";

        // 主入口按经典 QuickPing 的样子：IP 前缀 + 从/到（比让用户拼一串地址快得多）
        PingCommand = new AsyncRelayCommand(PingRangeAsync, () => !IsBusy && BuildRangeTargets().Count > 0);
        PingLibraryCommand = new AsyncRelayCommand(PingLibraryAsync, () => !IsBusy && LibraryTargetCount > 0);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && Results.Count > 0);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Results.Count > 0);

        RebuildGraphItems();
    }

    /// <summary>输入变了要重算按钮可用状态。</summary>
    private void CommandChanged()
    {
        PingCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(RangeSummary));
    }

    /// <summary>"192.0.2.1 - 192.0.2.254（254 个）"这样的即时反馈，让用户点之前就知道要扫多少。</summary>
    public string RangeSummary
    {
        get
        {
            var targets = BuildRangeTargets();
            return targets.Count == 0
                ? "IP 前三段不合法（示例：192.0.2）"
                : $"{targets[0]} ~ {targets[^1]}（共 {targets.Count} 个）";
        }
    }

    public BulkObservableCollection<QuickPingResult> Results { get; } = new();

    /// <summary>
    /// 图形模式的 256 个格子（对应最后一段 0..255）。
    /// 经典 QuickPing 的"图形模式"就是这个 —— 一眼看出这一段里哪些地址活着，
    /// 比在表格里逐行找"在线"快得多。**整段固定 0..255**（不是只铺你扫的那几个），
    /// 这样同一屏的网格位置固定、扫不同范围时不会跳。
    /// </summary>
    public BulkObservableCollection<QuickPingGraphItem> GraphItems { get; } = new();

    /// <summary>
    /// 表格里当前选中的那一行。图形模式点格子时会被设上（并切回表格模式），
    /// 界面据此选中/滚动到那一行。
    /// </summary>
    public QuickPingResult? SelectedResult
    {
        get => _selectedResult;
        set => SetProperty(ref _selectedResult, value);
    }

    /// <summary>
    /// 图形模式点某个格子 → 切回表格模式并定位到这台设备（用户要求）。
    /// 该地址不在本次结果里（没扫到 / 已清空）时只给提示，不假装有数据。
    /// </summary>
    public void ShowInTable(QuickPingGraphItem item)
    {
        GraphMode = false;

        var row = Results.FirstOrDefault(r => string.Equals(r.Target, item.Ip, StringComparison.OrdinalIgnoreCase));
        SelectedResult = row;
        if (row is null)
        {
            StatusHint = item.IsNonHost
                ? $"{item.Ip} 是{(item.LastOctet == 0 ? "网络号" : "广播地址")}（按最常见的 /24 口径）："
                  + "它不是一个主机地址，所以默认扫描范围 1~254 不会扫到它 —— 这就是“点它没反应”的原因。"
                  + "（如果这台设备实际掩码不是 /24，把上面的“从/到”改成包含它，也能正常扫。）"
                : $"{item.Ip} 不在本次结果里（这一格还没扫到，或结果已被清除）。"
                  + "按[开始]扫过这一段之后，点格子就能跳到表格里的那一行。";
            return;
        }

        var parts = new List<string> { $"已定位到 {row.Target}：{row.StatusText}" };
        if (row.RttMs is { } rtt)
        {
            parts.Add($"{rtt} ms");
        }

        if (!string.IsNullOrWhiteSpace(row.MacAddress))
        {
            parts.Add($"MAC {row.MacAddress}");
        }

        if (!string.IsNullOrWhiteSpace(row.HostName))
        {
            parts.Add(row.HostName);
        }

        StatusHint = string.Join("｜", parts);
    }

    /// <summary>IP 前三段（例如 `192.0.2`）。</summary>
    public string IpPrefix
    {
        get => _ipPrefix;
        set
        {
            if (SetProperty(ref _ipPrefix, value))
            {
                RebuildGraphItems();
                CommandChanged();
            }
        }
    }

    public int RangeStart
    {
        get => _rangeStart;
        set { if (SetProperty(ref _rangeStart, Math.Clamp(value, 0, 255))) { CommandChanged(); } }
    }

    public int RangeEnd
    {
        get => _rangeEnd;
        set { if (SetProperty(ref _rangeEnd, Math.Clamp(value, 0, 255))) { CommandChanged(); } }
    }

    /// <summary>图形模式（默认关，先给表格）：true = 铺 256 个格子看存活，false = 逐行看详情。</summary>
    public bool GraphMode
    {
        get => _graphMode;
        set
        {
            if (SetProperty(ref _graphMode, value))
            {
                OnPropertyChanged(nameof(TableMode));
                // 切到图形模式时才真正铺格子（延迟重建，见 RebuildGraphItems 的注释）
                if (value && (_graphDirty || GraphItems.Count == 0))
                {
                    RebuildGraphItemsCore();
                }
            }
        }
    }

    /// <summary>表格模式的单选按钮绑这个（就是 <see cref="GraphMode"/> 的反面）。</summary>
    public bool TableMode
    {
        get => !_graphMode;
        set
        {
            if (value && _graphMode)
            {
                GraphMode = false;
            }
        }
    }

    public string StartTimeText
    {
        get => _startTimeText;
        private set => SetProperty(ref _startTimeText, value);
    }

    public string EndTimeText
    {
        get => _endTimeText;
        private set => SetProperty(ref _endTimeText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>已发现（在线）台数。</summary>
    public int FoundCount
    {
        get => _foundCount;
        private set => SetProperty(ref _foundCount, value);
    }

    /// <summary>把 `192.0.2` + 从/到 拼成目标列表；前缀不合法就返回空。</summary>
    public IReadOnlyList<string> BuildRangeTargets()
    {
        var prefix = (IpPrefix ?? string.Empty).Trim().TrimEnd('.');
        // 前缀必须是三段数字（允许最后一段是 0..255 之外的写法就直接不认）
        var parts = prefix.Split('.');
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, out var octet) || octet is < 0 or > 255))
        {
            return Array.Empty<string>();
        }

        var start = Math.Min(RangeStart, RangeEnd);
        var end = Math.Max(RangeStart, RangeEnd);
        return Enumerable.Range(start, end - start + 1).Select(i => $"{prefix}.{i}").ToList();
    }

    private void RebuildGraphItems()
    {
        // **按需重建**（2026-09-23 优化）：图形模式没显示时，连"格子"都不必建 ——
        // IpPrefix 是逐字符输入的（输入 192.0.2 会触发 7 次 setter），
        // 旧实现每次都 new 256 个格子 + 一次 Reset 通知，纯属白做（弱 CPU/小屏上能感到顿）。
        // 真正需要格子的只有两种情况：切到图形模式、或开始一次扫描（扫描要按格子回填状态）。
        if (!GraphMode)
        {
            _graphDirty = true;
            return;
        }

        RebuildGraphItemsCore();
    }

    private void RebuildGraphItemsCore()
    {
        var prefix = (IpPrefix ?? string.Empty).Trim().TrimEnd('.');
        GraphItems.ReplaceAll(Enumerable.Range(0, 256)
            .Select(i => new QuickPingGraphItem(i, $"{prefix}.{i}")));
        _graphDirty = false;
    }

    /// <summary>要 ping 的内容：单个 IP/域名，或一串/一段（`192.0.2.1-32`）。</summary>
    public string TargetText
    {
        get => _targetText;
        set
        {
            if (SetProperty(ref _targetText, value))
            {
                PingCommand.RaiseCanExecuteChanged();
                // 顺手把展开后的目标数报出来 —— `192.0.2.1-32` 展开成 32 个，用户点之前就该知道
                var count = QuickPingService.ExpandTargets(value).Count;
                if (count > 1)
                {
                    StatusHint = $"将 ping {count} 个目标（多个用逗号/空格分隔；`192.0.2.1-32` 这种网段简写也认）。";
                }
            }
        }
    }

    public string BuildingFilter
    {
        get => _buildingFilter;
        set { if (SetProperty(ref _buildingFilter, value)) { NotifyLibraryChanged(); } }
    }

    public string KeywordFilter
    {
        get => _keywordFilter;
        set { if (SetProperty(ref _keywordFilter, value)) { NotifyLibraryChanged(); } }
    }

    /// <summary>Ping 超时（ms）。默认 1000：街区内网一般毫秒级，1 秒足够，短一点批量才快。</summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, Math.Clamp(value, 100, 10000));
    }

    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, Math.Clamp(value, 1, 32));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PingCommand.RaiseCanExecuteChanged();
                PingLibraryCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                ClearCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public AsyncRelayCommand PingCommand { get; }
    public AsyncRelayCommand PingLibraryCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    public IReadOnlyList<string> Buildings =>
        _repository.Database.Switches
            .Select(s => s.Building)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

    /// <summary>资源库里按当前筛选条件能 ping 到多少台（按钮上用）。</summary>
    public int LibraryTargetCount => LibraryTargets().Count;

    /// <summary>资源库筛出来的管理 IP（去重）。</summary>
    private List<string> LibraryTargets() =>
        _repository.Database.Switches
            .Where(s => !string.IsNullOrWhiteSpace(s.ManagementIp))
            .Where(s => string.IsNullOrWhiteSpace(BuildingFilter)
                        || string.Equals(s.Building, BuildingFilter, StringComparison.Ordinal))
            .Where(s => string.IsNullOrWhiteSpace(KeywordFilter)
                        || s.ManagementIp.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase))
            .Select(s => IpAddressHelper.ExtractFirst(s.ManagementIp) ?? s.ManagementIp.Trim())
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.Ordinal)
            .ToList();

    private void NotifyLibraryChanged()
    {
        PingLibraryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(LibraryTargetCount));
    }

    private Task PingRangeAsync() => RunAsync(BuildRangeTargets(), $"{IpPrefix} 网段");

    private Task PingLibraryAsync() => RunAsync(LibraryTargets(), "资源库筛选出的设备");

    private async Task RunAsync(IReadOnlyList<string> targets, string source)
    {
        if (targets.Count == 0)
        {
            StatusHint = "没有可 ping 的目标：请输入 IP/域名（或选好资源库筛选条件）。";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Results.ReplaceAll(Array.Empty<QuickPingResult>());
        RebuildGraphItemsCore();   // 扫描要按格子回填状态 → 这里必须真的建出来
        FoundCount = 0;
        StartTimeText = DateTime.Now.ToString("HH:mm:ss");
        EndTimeText = "—";
        ElapsedText = "—";
        var sweepWatch = System.Diagnostics.Stopwatch.StartNew();
        ProgressText = $"0 / {targets.Count}";
        StatusHint = $"正在 ping {source}（{targets.Count} 个，{Concurrency} 路并发，超时 {TimeoutMs} ms）…";

        var done = 0;
        var enrichNote = string.Empty;
        // 图形模式按下标更新格子：一张 256 格的全景图，扫描过程中就在变
        var graphByOctet = GraphItems.ToDictionary(g => g.LastOctet);
        var progress = new Progress<QuickPingResult>(row =>
        {
            Results.Add(row);
            ProgressText = $"{++done} / {targets.Count}";
            if (graphByOctet.TryGetValue(row.LastOctet, out var cell))
            {
                cell.Status = row.Status;
                cell.RttMs = row.RttMs;
            }

            if (row.Status == QuickPingStatus.Ok)
            {
                FoundCount++;
            }
        });

        try
        {
            var rows = await _service
                .PingManyAsync(targets, TimeoutMs, Concurrency, token, progress)
                .ConfigureAwait(true);

            // 扫完补 MAC 与主机名（只给在线的补；MAC 整段只读一次 ARP 表）
            var onlineCount = rows.Count(r => r.Status == QuickPingStatus.Ok);
            if (onlineCount > 0)
            {
                StatusHint = $"已扫完，正在补 {onlineCount} 台在线设备的 MAC 与主机名…";
                var enrichProgress = new Progress<(int Done, int Total)>(p =>
                    StatusHint = $"正在补 MAC / 主机名：{p.Done} / {p.Total}…");
                var enrich = await _service.EnrichAsync(rows, token, enrichProgress).ConfigureAwait(true);
                // MAC/主机名是写进已有行对象的，DataGrid 不会自动刷新 → 整体替换一次
                Results.ReplaceAll(rows);
                enrichNote = $"补到 MAC {enrich.MacFound}/{enrich.Online}、主机名 {enrich.HostNameFound}/{enrich.Online}。"
                             + "网卡地址只有和本机同网段的设备能读到（跨网段是经过路由的，本机 ARP 表里只有网关，要看跨网段 MAC 得用 SNMP 的 MAC 表）；"
                             + "主机名要有反向 DNS 或 NetBIOS 名，交换机/打印机/手机这类设备本来就常常没有名字 —— 空着是正常现象。";
            }

            sweepWatch.Stop();
            EndTimeText = DateTime.Now.ToString("HH:mm:ss");
            var span = sweepWatch.Elapsed;
            ElapsedText = $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";

            var ok = rows.Count(r => r.Status == QuickPingStatus.Ok);
            var timeout = rows.Count(r => r.Status == QuickPingStatus.Timeout);
            var error = rows.Count - ok - timeout;
            FoundCount = ok;
            Summary = $"共 {rows.Count} 个｜在线 {ok}｜超时 {timeout}"
                      + (error > 0 ? $"｜错误 {error}" : string.Empty);

            // 台账：谁在什么时候 ping 了哪一片（交接班/回单用得上）
            _shell.AddRecentOperation(
                "QuickPing",
                Summary,
                ok > 0,
                ledgerDetail: $"来源：{source}；{Summary}；目标：{string.Join(",", targets.Take(20))}"
                              + (targets.Count > 20 ? $" 等 {targets.Count} 个" : string.Empty));

            StatusHint = token.IsCancellationRequested
                ? $"已停止：完成 {rows.Count} / {targets.Count} 个。"
                : "提示：很多交换机默认忽略 ICMP，超时只能说明 ping 不通，不代表设备不在线"
                  + "（要确认得上 SNMP 或端口探测）。"
                  + (string.IsNullOrEmpty(enrichNote) ? string.Empty : Environment.NewLine + enrichNote);
        }
        catch (OperationCanceledException)
        {
            StatusHint = $"已停止：完成 {Results.Count} / {targets.Count} 个（已完成的结果保留在表里）。";
            Summary = $"已停止：共 {Results.Count} 个｜通 {Results.Count(r => r.Status == QuickPingStatus.Ok)}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ExportCsvCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            PingLibraryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 诊断/自检用：直接 ping 一轮。
    /// 支持两种入参：`192.0.2` + 从/到（走 <see cref="BuildRangeTargets"/> 的经典路径），
    /// 或一串自由目标（`192.0.2.1,192.0.2.5` / `192.0.2.1-32`，走 <see cref="QuickPingService.ExpandTargets"/>）
    /// —— 后者是给自动化用的，界面上不暴露。
    /// </summary>
    public async Task RunForDiagnosticsAsync(string targets)
    {
        var freeForm = QuickPingService.ExpandTargets(targets);
        var toRun = freeForm.Count > 0 ? freeForm : BuildRangeTargets();
        await RunAsync(toRun, string.IsNullOrWhiteSpace(targets) ? $"{IpPrefix} 网段" : targets).ConfigureAwait(true);
    }

    /// <summary>诊断/自检用：走界面上那条路径（IP 前缀 + 从/到）。</summary>
    public Task RunRangeForDiagnosticsAsync() => RunAsync(BuildRangeTargets(), $"{IpPrefix} 网段");

    private void Stop() => _cts?.Cancel();

    private void Clear()
    {
        Results.ReplaceAll(Array.Empty<QuickPingResult>());
        Summary = "已清空结果。";
        ProgressText = "尚未开始。";
        ExportCsvCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void ExportCsv()
    {
        if (Results.Count == 0)
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
            var path = Path.Combine(folder, $"quickping_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var builder = new StringBuilder();
            builder.AppendLine(QuickPingResult.CsvHeader);
            foreach (var row in Results)
            {
                builder.AppendLine(row.ToCsvLine());
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            StatusHint = $"已导出 {Results.Count} 条：{path}";
            _shell.AddRecentOperation("QuickPing 导出", path);
        }
        catch (Exception ex)
        {
            StatusHint = $"导出失败：{ex.Message}";
        }
    }
}
