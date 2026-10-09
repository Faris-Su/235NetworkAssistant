using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 全网定位页：输入一个 IP 或 MAC，在资源库选中的设备里找出"在哪台交换机、哪个端口"。
///
/// 两阶段（顺序很重要，反了就是白扫几百台接入交换机）：
///   ① 输入是 IP → 先在设备的 **ARP 表** 里把 IP 换成 MAC（IP 只存在于有三层 SVI 的设备上，接入交换机 ARP 是空的）；
///   ② 拿着 MAC 去设备的 **标准转发表（BRIDGE-MIB FDB）** 找端口 → MAC → 桥口 → ifIndex → 端口名。
/// 默认"命中即停"：找到就停，不用扫完 411 台。
/// </summary>
public sealed class LocateViewModel : ViewModelBase
{
    /// <summary>逐跳追踪最多走几跳（成环有保护，这里只是兜底上限）。</summary>
    private const int MaxTraceHops = 8;

    private readonly IResourceRepository _repository;
    private readonly IShellNavigator _shell;
    private readonly SnmpMacLocator _locator;
    private readonly Func<string?> _folderPicker;

    private string _queryText = string.Empty;
    private string _buildingFilter = string.Empty;
    private string _keywordFilter = string.Empty;
    private string _community = string.Empty;
    private int _timeoutMs = 3000;
    private int _retries = 1;
    private int _concurrency = 4;
    private bool _stopOnFirstHit = true;
    private bool _traceMode;
    private bool _isBusy;
    private string _progressText = "尚未开始。";
    private string _summaryText = "未选择设备。";
    private string _statusHint = "输入一个 IP 或 MAC，勾选要查的范围，然后点[开始定位]。";
    private CancellationTokenSource? _cts;
    private List<LocateHit> _allHits = new();

    public LocateViewModel(
        IShellNavigator shell,
        IResourceRepository repository,
        SnmpMacLocator locator,
        Func<string?>? folderPicker = null)
    {
        _shell = shell;
        _repository = repository;
        _locator = locator;
        _folderPicker = folderPicker ?? (() => null);
        Title = "定位";

        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => !IsBusy && Devices.Count > 0);
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy && Devices.Count > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && Devices.Any(d => d.IsSelected));
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _allHits.Count > 0);

        Community = AppServices.Snmp.Community;
        TimeoutMs = AppServices.Snmp.TimeoutMs;
        Retries = AppServices.Snmp.Retries;
        RefreshDevices();
    }

    public BulkObservableCollection<SelectableDeviceItem> Devices { get; } = new();
    public BulkObservableCollection<LocateHit> Hits { get; } = new();

    /// <summary>要定位的 IP 或 MAC。</summary>
    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value);
    }

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

    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, Math.Clamp(value, 1, 8));
    }

    /// <summary>找到第一台就停（默认开）。关掉则把所有命中的设备都列出来 —— 排查"MAC 漂移/私接"时才需要。</summary>
    public bool StopOnFirstHit
    {
        get => _stopOnFirstHit;
        set => SetProperty(ref _stopOnFirstHit, value);
    }

    /// <summary>
    /// 逐跳追踪（默认开）：从**选中列表的第一台**开始，顺着转发表 + LLDP 邻居一路往下走，
    /// 直到算出口接的是终端。这样通常只查 2~5 台，而不是把几百台全扫一遍。
    /// 关掉则是原来的"在勾选的设备里逐个查"。
    /// </summary>
    public bool TraceMode
    {
        get => _traceMode;
        set => SetProperty(ref _traceMode, value);
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

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    public IReadOnlyList<string> Buildings =>
        _repository.Database.Switches
            .Select(s => s.Building)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

    public void RefreshDevices()
    {
        var items = _repository.Database.Switches
            .Where(s => !string.IsNullOrWhiteSpace(s.ManagementIp))
            .Where(s => string.IsNullOrWhiteSpace(BuildingFilter)
                        || string.Equals(s.Building, BuildingFilter, StringComparison.Ordinal))
            .Where(s => string.IsNullOrWhiteSpace(KeywordFilter)
                        || s.ManagementIp.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new DeviceTarget(
                IpAddressHelper.ExtractFirst(s.ManagementIp) ?? s.ManagementIp.Trim(),
                string.IsNullOrWhiteSpace(s.Name) ? s.Floor : s.Name,
                s.Building))
            .GroupBy(t => t.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Ip, StringComparer.Ordinal)
            .ToList();

        Devices.ReplaceAll(items.Select(t => new SelectableDeviceItem(t)));
        StatusHint = items.Count == 0
            ? "资源库里没有匹配的设备（先到【资源库】页导入，或放宽筛选）。"
            : $"候选 {items.Count} 台。默认命中即停，不会傻扫完。";
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
    }

    private List<DeviceTarget> SelectedTargets() =>
        Devices.Where(d => d.IsSelected).Select(d => d.Target).ToList();

    /// <summary>诊断/自检用：按 IP 勾选设备。</summary>
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

    /// <summary>诊断/自检用：直接跑一轮定位。</summary>
    public Task RunLocateAsync() => StartAsync();

    private async Task StartAsync()
    {
        var query = QueryText?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            StatusHint = "请先输入要定位的 IP 或 MAC。";
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusHint = "请先勾选要在哪些设备里查。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Community))
        {
            StatusHint = "请先填 SNMP Community（只读团体名）。它只存在内存里。";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        _allHits = new List<LocateHit>();
        Hits.ReplaceAll(Array.Empty<LocateHit>());
        ProgressText = "准备中…";

        try
        {
            // ---------- ① 输入是 IP：先在 ARP 里换成 MAC ----------
            var mac = MacAddressHelper.Normalize(query);
            if (mac is null)
            {
                var ip = IpAddressHelper.ExtractFirst(query);
                if (ip is null)
                {
                    StatusHint = $"无法识别「{query}」：请输入 IPv4 地址或 MAC（02:00:00:00:00:01 / 0200.0000.0001）。";
                    return;
                }

                StatusHint = $"正在 {targets.Count} 台设备里按 ARP 把 {ip} 换成 MAC（命中即停）…";
                var resolved = await ResolveIpAsync(ip, targets, token).ConfigureAwait(true);
                if (resolved is null)
                {
                    StatusHint = $"没有找到 {ip} 的 MAC：查了 {targets.Count} 台设备的 ARP 表都没有。"
                                 + "可能它不在这些设备的 ARP 缓存里（已老化），或者它的网关不在这批设备里。";
                    return;
                }

                mac = resolved.Value.ReplaceMac;
                StatusHint = $"ARP 命中：{ip} → {mac}（在 {resolved.Value.SourceIp} 上解得，VLAN {resolved.Value.Vlan}）。"
                             + "继续按 MAC 找端口…";
            }

            // ---------- ② 拿 MAC 去转发表找端口 ----------
            if (TraceMode)
            {
                await TraceAsync(mac, targets, token).ConfigureAwait(true);
                return;
            }

            ProgressText = $"0 / {targets.Count}";
            var done = 0;
            var gate = new SemaphoreSlim(Math.Clamp(Concurrency, 1, 8));
            // ⚠️ 必须把第一阶段（ARP）已经记下的行带进来：第一版这里 new 了一个空表，
            // 第二阶段每加一行就把结果表整体替换一次 → **ARP 那条证据被覆盖掉了**，
            // 用户只看到"在某台交换机的某个口"，看不到"这个 MAC 是从哪个 IP 解出来的"。
            var collected = new List<LocateHit>(_allHits);

            var tasks = targets.Select(async target =>
            {
                await gate.WaitAsync(token).ConfigureAwait(true);
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    MacLocation location;
                    try
                    {
                        location = await _locator
                            .LocateAsync(target.Ip, 161, Community, mac, TimeoutMs, Retries, token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // ⚠️ 不能吞（DSH 复查挑出来的）：吞掉之后 `Task.WhenAll` 会正常返回，
                        // 于是"用户点了停止"会被后面的收尾逻辑说成"这批设备里都没有这个 MAC"。
                        // 原样上抛，交给外层 catch 统一处理成"已停止"。
                        throw;
                    }
                    catch (Exception ex)
                    {
                        watch.Stop();
                        collected.Add(new LocateHit
                        {
                            Kind = "FDB",
                            DeviceIp = target.Ip,
                            DeviceName = target.Name,
                            Building = target.Building,
                            Status = "查询失败",
                            Note = ex.Message,
                            ElapsedMs = watch.ElapsedMilliseconds,
                        });
                        Hits.ReplaceAll(collected);
                        ProgressText = $"{Interlocked.Increment(ref done)} / {targets.Count}";
                        return;
                    }

                    watch.Stop();
                    if (location.Found)
                    {
                        collected.Add(new LocateHit
                        {
                            Kind = "FDB",
                            DeviceIp = target.Ip,
                            DeviceName = target.Name,
                            Building = target.Building,
                            Status = location.Status == MacLocationStatus.FoundOnDeviceItself ? "设备自身" : "命中",
                            Port = location.PortName,
                            BridgePort = location.BridgePort,
                            IfIndex = location.IfIndex,
                            Note = location.Note,
                            ElapsedMs = watch.ElapsedMilliseconds,
                        });
                        Hits.ReplaceAll(collected);
                        if (StopOnFirstHit && location.Status == MacLocationStatus.Found)
                        {
                            StatusHint = $"已命中 {target.Name}（{target.Ip}），停止扫描。";
                            _cts?.Cancel();
                        }
                    }

                    ProgressText = $"{Interlocked.Increment(ref done)} / {targets.Count}";
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(true);

            // ⚠️ 只数 **FDB** 行（DSH 复查挑出来的）：ARP 那一行的结论也是"命中"，
            // 但它只说明"某台设备的 ARP 里有这个 IP↔MAC"，**没有任何端口结论**。
            // 旧写法把它算进命中数 → "端口没找到"却被报成"定位完成：命中 1 台"。
            var hits = collected.Count(h => h.Kind == "FDB" && h.Status is "命中" or "设备自身");
            _allHits = collected.ToList();
            SummaryText = $"MAC {MacAddressHelper.Format(mac)}｜命中 {hits} 台｜已查 {done} / {targets.Count}";
            if (hits == 0)
            {
                StatusHint = "这批设备的转发表里都没有这个 MAC。可能是：① 它不在你勾选的范围内；"
                             + "② 已经老化（转发表项通常几分钟不活动就删）；③ 设备没开放标准转发表的 SNMP 视图。";
            }
            else if (collected.Any(h => h.Status == "命中"))
            {
                StatusHint = "定位完成。**注意**：上游交换机（核心/汇聚）的转发表里也会有这个 MAC，"
                             + "所以同一个 MAC 在几台设备上都出现是正常的 —— **离接入最近的那一跳**才是用户实际接的口。";
            }
        }
        catch (OperationCanceledException)
        {
            StatusHint = "已停止（已完成的结果保留在表里）。";
        }
        finally
        {
            // 一次定位在台账里留一条汇总（"谁在查哪个 MAC/IP"也是交接班会有用的信息）
            if (Hits.Count > 0)
            {
                _shell.AddRecentOperation(
                    "全网定位",
                    SummaryText.Length > 0 ? SummaryText : $"查询 {QueryText}",
                    succeeded: Hits.Any(h => h.Status is "命中" or "设备自身"),
                    ledgerDetail: $"查询「{QueryText}」；{SummaryText}");
            }

            // ⚠️ 表格和 `_allHits` 必须始终一致（DSH 复查挑出来的）：
            // 旧写法只在 `await Task.WhenAll` **正常返回**之后才同步 `_allHits`，
            // 于是取消/命中即停导致 WhenAll 抛 OCE 时，表格里已经有 FDB 行、`_allHits` 却还停在 ARP 阶段
            // → 导出 CSV 缺行、CanExecute 甚至不可用。
            _allHits = Hits.ToList();
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>第一阶段：顺序问每台设备的 ARP 表，命中即停（ARP 只在三层设备上有）。</summary>
    /// <summary>
    /// 逐跳追踪：从选中列表的第一台开始，跟着 LLDP 邻居往下走。
    /// 结果按**跳**逐行落到同一张结果表里（复用 FDB 那套列），最后一跳就是答案。
    /// </summary>
    private async Task TraceAsync(string mac, List<DeviceTarget> targets, CancellationToken token)
    {
        var start = targets[0];
        StatusHint = $"逐跳追踪中：从 {start.Name}（{start.Ip}）开始…";
        ProgressText = "追踪中…";

        var hops = await _locator
            .TraceAsync(start.Ip, Community, mac, TimeoutMs, Retries, MaxTraceHops, token)
            .ConfigureAwait(true);

        var rows = new List<LocateHit>(_allHits);
        foreach (var hop in hops)
        {
            rows.Add(new LocateHit
            {
                Kind = "FDB",
                DeviceIp = hop.DeviceIp,
                DeviceName = targets.FirstOrDefault(t => string.Equals(t.Ip, hop.DeviceIp, StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty,
                Status = hop.Status,
                Port = hop.Port,
                BridgePort = hop.BridgePort,
                IfIndex = hop.IfIndex,
                Note = BuildHopNote(hop),
                ElapsedMs = hop.ElapsedMs,
            });
        }

        _allHits = rows;
        Hits.ReplaceAll(rows);
        ProgressText = $"追踪了 {hops.Count} 跳";

        var last = hops.LastOrDefault();
        if (last is null)
        {
            SummaryText = $"MAC {MacAddressHelper.Format(mac)}｜没有追踪到任何一跳";
            return;
        }

        SummaryText = $"MAC {MacAddressHelper.Format(mac)}｜追踪 {hops.Count} 跳｜最后一跳：{last.DeviceIp} {last.Port}";
        StatusHint = last.Status switch
        {
            "命中（终端口）" => $"✅ 定位结果：接在 **{last.DeviceIp}** 的 **{last.Port}** 上（这个口没有 LLDP 邻居，是终端口）。",
            "设备自身" => "命中的是设备自身的条目，不是用户设备。",
            "连不上" => $"下一跳 {last.DeviceIp} 连不上（超时/不可达/Community 不对），追踪到这里中断 —— "
                        + "**这不等于设备不在这台下面**，请确认那台设备是否可达后重试。",
            "该设备不支持" => $"{last.DeviceIp} 没有返回标准转发表，无法继续往下追。",
            _ => $"追踪结束：{last.Status}。{last.Note}",
        };
    }

    private static string BuildHopNote(MacTraceHop hop)
    {
        var parts = new List<string>();
        if (hop.NeighborName.Length > 0)
        {
            parts.Add($"该口对端是 {hop.NeighborName}（{hop.NeighborPort}）");
        }

        if (hop.NextHopIp.Length > 0)
        {
            parts.Add($"跳到 {hop.NextHopIp} 继续追");
        }

        if (hop.Note.Length > 0)
        {
            parts.Add(hop.Note);
        }

        return string.Join("；", parts);
    }

    private async Task<(string ReplaceMac, string SourceIp, string Vlan)?> ResolveIpAsync(
        string ip,
        List<DeviceTarget> targets,
        CancellationToken token)
    {
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resolved = await _locator
                    .ResolveIpToMacAsync(target.Ip, 161, Community, ip, TimeoutMs, Retries, token)
                    .ConfigureAwait(true);
                watch.Stop();
                if (resolved.Mac is not null)
                {
                    _allHits.Add(new LocateHit
                    {
                        Kind = "ARP",
                        DeviceIp = target.Ip,
                        DeviceName = target.Name,
                        Building = target.Building,
                        Status = "命中",
                        Vlan = resolved.Vlan,
                        Note = resolved.Interface.Length > 0
                            ? $"ARP 表里 {ip} → {resolved.Mac}（接口 {resolved.Interface}）"
                            : $"ARP 表里 {ip} → {resolved.Mac}",
                        ElapsedMs = watch.ElapsedMilliseconds,
                    });
                    Hits.ReplaceAll(_allHits);
                    return (resolved.Mac, target.Ip, resolved.Vlan);
                }

                // 用标志位判断，不再靠匹配提示文案（DSH 复查挑出来的）：
                // 旧写法用 `Note.Contains("没有返回 ARP 表")` 来区分"二层设备没 ARP 表"，
                // 而"设备根本没回应"也会走到这里 —— 文案一改就失配，而且两者含义完全不同。
                if (resolved.NoArpTable)
                {
                    // 二层设备本来就没有 ARP 表：记一条"不支持"，但**不算失败**，继续问下一台
                    _allHits.Add(new LocateHit
                    {
                        Kind = "ARP",
                        DeviceIp = target.Ip,
                        DeviceName = target.Name,
                        Building = target.Building,
                        Status = "无 ARP 表",
                        Note = resolved.Note,
                        ElapsedMs = watch.ElapsedMilliseconds,
                    });
                    Hits.ReplaceAll(_allHits);
                }
                else if (resolved.Unreachable)
                {
                    // 连不上要**单独记一笔并算失败**：不能混进"这些设备的 ARP 里都没有这个 IP"。
                    _allHits.Add(new LocateHit
                    {
                        Kind = "ARP",
                        DeviceIp = target.Ip,
                        DeviceName = target.Name,
                        Building = target.Building,
                        Status = "连不上",
                        Note = resolved.Note,
                        ElapsedMs = watch.ElapsedMilliseconds,
                    });
                    Hits.ReplaceAll(_allHits);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _allHits.Add(new LocateHit
                {
                    Kind = "ARP",
                    DeviceIp = target.Ip,
                    DeviceName = target.Name,
                    Building = target.Building,
                    Status = "查询失败",
                    Note = ex.Message,
                    ElapsedMs = watch.ElapsedMilliseconds,
                });
                Hits.ReplaceAll(_allHits);
            }
        }

        return null;
    }

    private void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        StatusHint = "正在停止…";
        _cts.Cancel();
    }

    private void ExportCsv()
    {
        if (_allHits.Count == 0)
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
            var safe = MacAddressHelper.Normalize(QueryText) ?? IpAddressHelper.ExtractFirst(QueryText) ?? "query";
            var path = Path.Combine(folder, $"定位_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var builder = new StringBuilder();
            builder.AppendLine(LocateHit.CsvHeader);
            foreach (var hit in _allHits)
            {
                builder.AppendLine(hit.ToCsvLine());
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            StatusHint = $"已导出 {_allHits.Count} 条：{path}";
            _shell.AddRecentOperation("定位导出", path);
        }
        catch (Exception ex)
        {
            StatusHint = $"导出失败：{ex.Message}";
        }
    }
}
