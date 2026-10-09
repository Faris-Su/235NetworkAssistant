using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// LLDP 页面（Phase 3）：邻居列表 + 邻居详情 + Telnet 到邻居 + 查看本地端口。
///
/// 刷新执行流（点一次[刷新]就拿到完整信息，不需要用户逐个点详情）：
///   1. `show lldp neighbors`                       → 基础邻居 + 本地端口
///   2. 对每个存在邻居的本地端口**顺序**执行
///      `show lldp neighbors interface &lt;port&gt; detail`
///   3. 合并 Detail 到同一条记录（Detail 有的字段优先）
///
/// 约束：
///   - 全部走公共 CLI 管线（Raw → 归一化 → ANSI/退格 → 分页续页 → 提示符 → 解析），不绕过；
///   - 顺序执行，绝不并发轰炸设备（20 个邻居就是 20 条命令逐条发）；
///   - 单个端口失败不影响整页；设备完全不支持 Detail 时保留基础信息并提前停止后续查询；
///   - V0.1 明确不做自动拓扑推理与自动链路诊断。
/// </summary>
public sealed class LldpViewModel : DeviceShowPageViewModel
{
    /// <summary>连续多少个端口返回“不支持”就停止继续查（老设备上避免白跑几十条命令）。</summary>
    private const int UnsupportedStreakLimit = 2;

    private LldpNeighborRecord? _selectedNeighbor;
    private string _detailProgress = string.Empty;
    private bool _isDetailBusy;

    public LldpViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
        : base(shell, connections, commands)
    {
        Title = "LLDP";
        RegisterCachedCollection(Neighbors);      // 断开/换设备时清空邻居表

        ShowDetailCommand = new AsyncRelayCommand(ShowDetailAsync, () => SelectedNeighbor is not null && !IsDetailBusy && IsConnected);
        TelnetToNeighborCommand = new AsyncRelayCommand(TelnetToNeighborAsync, () => SelectedNeighbor?.HasManagementIp == true && !IsDetailBusy);
        ViewLocalPortCommand = new RelayCommand(ViewLocalPort, () => SelectedNeighbor is not null);
        CopyRawCommand = new RelayCommand(CopyRaw);
        CopyDetailCommand = new RelayCommand(CopyDetail, () => SelectedNeighbor is not null);
    }

    public ObservableCollection<LldpNeighborRecord> Neighbors { get; } = new();

    public LldpNeighborRecord? SelectedNeighbor
    {
        get => _selectedNeighbor;
        set
        {
            if (SetProperty(ref _selectedNeighbor, value))
            {
                ShowDetailCommand.RaiseCanExecuteChanged();
                TelnetToNeighborCommand.RaiseCanExecuteChanged();
                ViewLocalPortCommand.RaiseCanExecuteChanged();
                CopyDetailCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(NeighborSummary));
                OnPropertyChanged(nameof(SelectedDetailText));
                NotifyDetailPanel();
                OnPropertyChanged(nameof(SelectedDetailRaw));
            }
        }
    }

    /// <summary>刷新过程中显示“正在获取 Detail 3 / 7：g0/3”，让用户知道软件没卡死。</summary>
    public string DetailProgress
    {
        get => _detailProgress;
        private set
        {
            if (SetProperty(ref _detailProgress, value))
            {
                OnPropertyChanged(nameof(HasDetailProgress));
            }
        }
    }

    public bool HasDetailProgress => !string.IsNullOrWhiteSpace(DetailProgress);

    public bool IsDetailBusy
    {
        get => _isDetailBusy;
        private set
        {
            if (SetProperty(ref _isDetailBusy, value))
            {
                ShowDetailCommand.RaiseCanExecuteChanged();
                TelnetToNeighborCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>详情面板正文：选中邻居的完整字段（表格只放关键列）。</summary>
    public string SelectedDetailText => SelectedNeighbor?.ToDetailText()
        ?? "选择左侧一个邻居，这里显示完整字段：管理 IP / Chassis ID / System Description / Port Description / Capability / Aging Time。";

    /// <summary>详情面板的字段行（图标 + 中文标签 + 值），用于图形化展示。</summary>
    public IReadOnlyList<LldpDetailField> SelectedDetailFields =>
        SelectedNeighbor?.DetailFields ?? Array.Empty<LldpDetailField>();

    /// <summary>设备能力徽标（网桥 / 路由器 …）。</summary>
    public IReadOnlyList<string> SelectedCapabilityChips =>
        SelectedNeighbor?.CapabilityChips ?? Array.Empty<string>();

    public bool HasCapabilityChips => SelectedCapabilityChips.Count > 0;

    /// <summary>链路示意图：本机（本地端口）→ 邻居（远端端口）。</summary>
    public string SelectedLocalEndpoint => SelectedNeighbor is { } n ? $"{n.LocalEndpointTitle} · {n.LocalEndpointPort}" : "未选择";

    public string SelectedRemoteEndpoint => SelectedNeighbor is { } n ? $"{n.RemoteEndpointTitle} · {n.RemoteEndpointPort}" : "未选择";

    public string SelectedLinkCaption => SelectedNeighbor is { } n
        ? $"{Or(n.LocalPort)} ↔ {n.DisplayDeviceName} 的 {Or(n.NeighborPort)}"
        : string.Empty;

    private static string Or(string? value) => LldpNeighborRecord.Or(value);

    /// <summary>详情面板的所有派生属性一起刷新（否则切换邻居后只有文本变了、图形部分不刷新）。</summary>
    private void NotifyDetailPanel()
    {
        OnPropertyChanged(nameof(SelectedDetailFields));
        OnPropertyChanged(nameof(SelectedCapabilityChips));
        OnPropertyChanged(nameof(HasCapabilityChips));
        OnPropertyChanged(nameof(SelectedLocalEndpoint));
        OnPropertyChanged(nameof(SelectedRemoteEndpoint));
        OnPropertyChanged(nameof(SelectedLinkCaption));
    }

    /// <summary>选中邻居的 Detail 原始输出（解析失败时看设备原文）。</summary>
    public string SelectedDetailRaw => SelectedNeighbor is { } neighbor
        ? (string.IsNullOrWhiteSpace(neighbor.RawDetailOutput)
            ? "（该邻居还没有 Detail 原始输出）"
            : neighbor.RawDetailOutput)
        : string.Empty;

    public string NeighborSummary
    {
        get
        {
            var neighbor = SelectedNeighbor;
            if (neighbor is null)
            {
                return "未选择邻居";
            }

            return $"{LldpNeighborRecord.Or(neighbor.LocalPort)} → {neighbor.DisplayName}｜" +
                   $"邻居端口 {LldpNeighborRecord.Or(neighbor.NeighborPort)}｜" +
                   $"管理 IP {LldpNeighborRecord.Or(neighbor.ManagementIp)}｜{neighbor.DetailStateDescription}";
        }
    }

    public AsyncRelayCommand ShowDetailCommand { get; }

    public AsyncRelayCommand TelnetToNeighborCommand { get; }

    public RelayCommand ViewLocalPortCommand { get; }

    public RelayCommand CopyRawCommand { get; }

    /// <summary>[复制详情]：把选中邻居的完整字段复制到剪贴板（工单 / 微信 / 文档）。</summary>
    public RelayCommand CopyDetailCommand { get; }

    /// <summary>
    /// 刷新 = 基础邻居 + 逐个端口 Detail。
    /// 顺序执行；单端口失败只标记该行；设备不支持时保留基础信息并提前停止。
    /// </summary>
    protected override async Task RefreshAsync()
    {
        if (!RequireConnection())
        {
            return;
        }

        IsBusy = true;
        DetailProgress = string.Empty;
        try
        {
            // 1) 基础邻居列表
            var basicResult = await Commands.RunShowResultAsync(ShowCommands.LldpNeighbors).ConfigureAwait(true);
            var basicOutput = basicResult.Outputs.FirstOrDefault();
            var basicRaw = basicOutput?.RawOutput ?? string.Empty;
            var parsed = ShowOutputParser.ParseLldpNeighbors(basicRaw);

            Neighbors.Clear();
            foreach (var neighbor in parsed.Items)
            {
                Neighbors.Add(neighbor);
            }

            var rawBuilder = new StringBuilder();
            rawBuilder.Append("# ").Append(ShowCommands.LldpNeighbors).AppendLine();
            rawBuilder.AppendLine(basicRaw.TrimEnd());

            var totalPages = LastCommandPages();
            var hitPageLimit = LastCommandHitPageLimit();

            // 2) 需要 Detail 的本地端口（同一端口只查一次；顺序执行，不并发轰炸设备）
            var ports = parsed.Items
                .Select(n => n.LocalPort)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var detailOk = 0;
            var detailFailed = 0;
            var detailUnsupported = 0;

            if (!basicResult.Succeeded)
            {
                // 基础命令就失败了：不继续查 Detail，但保留原始输出让用户看清原因。
                ParseSummary = $"基础命令执行失败｜{basicOutput?.Error}";
                ParserNote = "show lldp neighbors 执行失败：请查看原始输出；设备可能不支持 LLDP 或当前权限不足。";
            }
            else if (ports.Count > 0)
            {
                var unsupportedStreak = 0;
                for (var i = 0; i < ports.Count; i++)
                {
                    var port = ports[i];
                    if (unsupportedStreak >= UnsupportedStreakLimit)
                    {
                        MarkDetail(port, LldpDetailState.Unsupported, "设备不支持 Detail 查询（已停止继续查询）");
                        detailUnsupported++;
                        continue;
                    }

                    DetailProgress = $"正在获取 Detail {i + 1} / {ports.Count}：{port}";

                    var command = ShowCommands.LldpNeighborDetail(port);
                    string detailRaw;
                    bool succeeded;
                    string? error;
                    try
                    {
                        var result = await Commands.RunShowResultAsync(command).ConfigureAwait(true);
                        var output = result.Outputs.FirstOrDefault();
                        detailRaw = output?.RawOutput ?? string.Empty;
                        succeeded = output?.Succeeded ?? false;
                        error = output?.Error;
                    }
                    catch (Exception ex)
                    {
                        detailRaw = string.Empty;
                        succeeded = false;
                        error = ex.Message;
                    }

                    totalPages += LastCommandPages();
                    hitPageLimit |= LastCommandHitPageLimit();

                    rawBuilder.AppendLine();
                    rawBuilder.Append("# ").Append(command).AppendLine();
                    rawBuilder.AppendLine(detailRaw.TrimEnd());

                    if (!succeeded)
                    {
                        MarkDetail(port, LldpDetailState.Failed, $"查询失败：{error}");
                        detailFailed++;
                        continue;
                    }

                    // 设备不支持这个命令：保留基础信息，连续两次后不再逐个端口问下去。
                    if (ShowOutputParser.LooksLikeUnsupportedDetail(detailRaw))
                    {
                        unsupportedStreak++;
                        MarkDetail(port, LldpDetailState.Unsupported, "设备不支持 Detail 查询");
                        detailUnsupported++;
                        continue;
                    }

                    var detail = ShowOutputParser.ParseLldpNeighborDetail(detailRaw);
                    if (detail.Neighbor is null)
                    {
                        MarkDetail(port, LldpDetailState.Failed, "Detail 输出里没有解析到邻居信息");
                        detailFailed++;
                        continue;
                    }

                    unsupportedStreak = 0;
                    MergeDetail(port, detail.Neighbor, detailRaw);
                    detailOk++;
                }
            }

            SetRawOutput(ShowCommands.LldpNeighbors, rawBuilder.ToString());

            // 3) 汇总：让用户一眼看出详情拿全了没有
            if (basicResult.Succeeded)
            {
                var parts = new List<string> { $"邻居 {Neighbors.Count} 条" };
                if (Neighbors.Count == 0)
                {
                    parts.Add("未发现 LLDP 邻居");
                }
                else
                {
                    parts.Add($"Detail 成功 {detailOk}");
                    if (detailFailed > 0)
                    {
                        parts.Add($"Detail 失败 {detailFailed}");
                    }

                    if (detailUnsupported > 0)
                    {
                        parts.Add($"设备不支持 {detailUnsupported}");
                    }
                }

                ParseSummary = string.Join("｜", parts);

                var pagingNote = totalPages > 0
                    ? hitPageLimit
                        ? $"｜自动续页 {totalPages} 页后触发上限（可能未识别提示符）"
                        : $"｜自动续页 {totalPages} 页"
                    : string.Empty;
                StatusHint =
                    $"已刷新（{DateTime.Now:HH:mm:ss}）｜基础 1 条命令 + {ports.Count} 个端口逐个取 Detail" +
                    pagingNote;

                ParserNote = detailFailed > 0
                    ? $"{detailFailed} 个端口的 Detail 未取到：已保留这些端口的基础 LLDP 信息，可单独点[详情]重试。"
                    : string.Empty;
            }

            if (SelectedNeighbor is not null)
            {
                OnPropertyChanged(nameof(SelectedDetailText));
                NotifyDetailPanel();
                OnPropertyChanged(nameof(SelectedDetailRaw));
                OnPropertyChanged(nameof(NeighborSummary));
            }
        }
        catch (Exception ex)
        {
            StatusHint = $"刷新 LLDP 失败：{ex.Message}";
            AppServices.Log.Warn("刷新 LLDP（含 Detail）失败", ex);
        }
        finally
        {
            DetailProgress = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>手动重取单个端口的 Detail（刷新已经自动取过，这里用于失败重试）。</summary>
    private async Task ShowDetailAsync()
    {
        var neighbor = SelectedNeighbor;
        if (neighbor is null)
        {
            return;
        }

        var command = ShowCommands.LldpNeighborDetail(neighbor.LocalPort);
        IsDetailBusy = true;
        DetailProgress = $"正在获取 Detail：{neighbor.LocalPort}";
        try
        {
            var result = await Commands.RunShowResultAsync(command).ConfigureAwait(true);
            var output = result.Outputs.FirstOrDefault();
            var raw = output?.RawOutput ?? string.Empty;
            if (output?.Succeeded == false)
            {
                MarkDetail(neighbor.LocalPort, LldpDetailState.Failed, $"查询失败：{output.Error}");
                StatusHint = $"获取详情失败：{output.Error}";
                return;
            }

            if (ShowOutputParser.LooksLikeUnsupportedDetail(raw))
            {
                MarkDetail(neighbor.LocalPort, LldpDetailState.Unsupported, "设备不支持 Detail 查询");
                StatusHint = "这台设备不支持 Detail 查询（已保留基础 LLDP 信息）。";
                return;
            }

            var detail = ShowOutputParser.ParseLldpNeighborDetail(raw);
            if (detail.Neighbor is null)
            {
                MarkDetail(neighbor.LocalPort, LldpDetailState.Failed, "Detail 输出里没有解析到邻居信息");
                StatusHint = $"未能从 Detail 输出里解析出邻居信息：{command}（可查看原始输出）";
                return;
            }

            MergeDetail(neighbor.LocalPort, detail.Neighbor, raw);
            StatusHint = $"已获取详情：{command}";
        }
        catch (Exception ex)
        {
            MarkDetail(neighbor.LocalPort, LldpDetailState.Failed, $"查询失败：{ex.Message}");
            StatusHint = $"获取详情失败：{ex.Message}";
        }
        finally
        {
            DetailProgress = string.Empty;
            IsDetailBusy = false;
            OnPropertyChanged(nameof(SelectedDetailText));
            NotifyDetailPanel();
            OnPropertyChanged(nameof(SelectedDetailRaw));
            OnPropertyChanged(nameof(NeighborSummary));
        }
    }

    /// <summary>把 Detail 字段合并进同一条记录：Detail 有值的字段优先，缺失的保留基础值。</summary>
    private void MergeDetail(string port, LldpNeighborRecord detail, string rawDetail)
    {
        foreach (var record in Neighbors.Where(n => SamePort(n.LocalPort, port)))
        {
            record.NeighborDevice = Prefer(detail.NeighborDevice, record.NeighborDevice);
            record.NeighborPort = Prefer(detail.NeighborPort, record.NeighborPort);
            record.ManagementIp = Prefer(detail.ManagementIp, record.ManagementIp);
            record.ChassisId = Prefer(detail.ChassisId, record.ChassisId);
            record.ChassisType = Prefer(detail.ChassisType, record.ChassisType);
            record.PortType = Prefer(detail.PortType, record.PortType);
            record.SystemDescription = Prefer(detail.SystemDescription, record.SystemDescription);
            record.PortDescription = Prefer(detail.PortDescription, record.PortDescription);
            record.Capability = Prefer(detail.Capability, record.Capability);
            record.AgingTime = Prefer(detail.AgingTime, record.AgingTime);
            record.HoldTime = Prefer(detail.HoldTime, record.HoldTime);
            record.UpdateTime = Prefer(detail.UpdateTime, record.UpdateTime);
            record.NeighborIndex = Prefer(detail.NeighborIndex, record.NeighborIndex);
            record.RawDetailOutput = rawDetail;
            record.DetailState = LldpDetailState.Ok;
            record.DetailNote = string.Empty;
        }
    }

    /// <summary>标记某个端口 Detail 的状态（同一端口的记录一起标记）。</summary>
    private void MarkDetail(string port, LldpDetailState state, string note)
    {
        foreach (var record in Neighbors.Where(n => SamePort(n.LocalPort, port)))
        {
            record.DetailState = state;
            record.DetailNote = note;
            if (state != LldpDetailState.Ok)
            {
                record.RawDetailOutput = string.Empty;
            }
        }
    }

    /// <summary>端口比较统一走接口名归一化（GigabitEthernet 0/1 == Gi0/1 == g0/1）。</summary>
    private static bool SamePort(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var a = InterfaceNameHelper.ExpandAll(left).FirstOrDefault() ?? left.Trim();
        var b = InterfaceNameHelper.ExpandAll(right).FirstOrDefault() ?? right.Trim();
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Prefer(string candidate, string existing) =>
        string.IsNullOrWhiteSpace(candidate) ? existing : candidate.Trim();

    private int LastCommandPages() => Connections.Current?.LastCommandPageCount ?? 0;

    private bool LastCommandHitPageLimit() => Connections.Current?.LastCommandHitPageLimit == true;

    /// <summary>验收场景 4：LLDP → 查看管理 IP → Telnet 到邻居 → 连接下一台交换机。</summary>
    private async Task TelnetToNeighborAsync()
    {
        var neighbor = SelectedNeighbor;
        if (neighbor is null)
        {
            return;
        }

        var ip = IpAddressHelper.ExtractFirst(neighbor.ManagementIp);
        if (ip is null)
        {
            StatusHint = "该邻居没有可用的管理 IP，无法直接 Telnet。";
            return;
        }

        var settings = AppServices.Settings.Telnet;
        settings.Host = ip;

        IsDetailBusy = true;
        try
        {
            // 带上当前的权限模式与 Enable 密码：选的是"管理模式"就真的自动 enable，
            // 否则会出现"连接页勾着管理模式、实际却在普通模式、还得手动点[提升权限]"的矛盾（现场反馈）。
            var privilege = new PrivilegeRequest(AppServices.Settings.PrivilegeMode, AppServices.EnablePassword);
            await Connections.ConnectTelnetAsync(settings, CancellationToken.None, privilege).ConfigureAwait(true);

            var deviceName = string.IsNullOrWhiteSpace(Connections.DeviceName) ? neighbor.DisplayName : Connections.DeviceName!;
            var message = $"已连接到邻居 {deviceName}（{ip}）｜{Connections.PrivilegeText}"
                          + (Connections.PrivilegeNotice is { Length: > 0 } notice ? $"｜{notice}" : string.Empty);
            StatusHint = message + "　→ 已切到【连接】页，可在这里查看状态或[断开]。";
            Shell.ReportStatus(message);
            Shell.AddRecentOperation(
                "Telnet 到 LLDP 邻居",
                $"{ip}｜{deviceName}｜本地端口 {neighbor.LocalPort}｜{Connections.PrivilegeText}");

            // 跳到【连接】页（不是 CLI）：那里能看到连接状态、权限、[断开]，也是换设备的入口
            Shell.NavigateTo("connection");
        }
        catch (Exception ex)
        {
            StatusHint = $"连接邻居 {ip} 失败：{ex.Message}";
            Shell.ReportStatus($"Telnet 到邻居失败：{ex.Message}");
            Shell.AddRecentOperation("Telnet 到 LLDP 邻居", $"{ip}：{ex.Message}", succeeded: false);
        }
        finally
        {
            IsDetailBusy = false;
        }
    }

    private void ViewLocalPort()
    {
        var neighbor = SelectedNeighbor;
        if (neighbor is null)
        {
            return;
        }

        Shell.NavigateTo("ports", neighbor.LocalPort);
    }

    private void CopyRaw()
    {
        StatusHint = AppServices.Clipboard.TrySetText(RawOutput ?? string.Empty, out var error)
            ? "原始输出已复制到剪贴板。"
            : $"复制失败：{error}";
    }

    /// <summary>[复制详情]：完整字段复制成文本，方便贴到工单 / 微信 / 文档。</summary>
    private void CopyDetail()
    {
        var neighbor = SelectedNeighbor;
        if (neighbor is null)
        {
            return;
        }

        StatusHint = AppServices.Clipboard.TrySetText(neighbor.ToDetailText(), out var error)
            ? $"已复制 {LldpNeighborRecord.Or(neighbor.LocalPort)} 的详情到剪贴板。"
            : $"复制失败：{error}";
    }
}
