using System.Collections.ObjectModel;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 端口页面（Phase 3）：刷新端口状态（用户点击触发）、LLDP 邻居列、
/// 勾选端口后批量配置（全部经 Command Preview）。
/// </summary>
public sealed class PortsViewModel : DeviceShowPageViewModel, INavigationAware
{
    public const string ActionEnable = "开启端口 (no shutdown)";
    public const string ActionShutdown = "关闭端口 (shutdown)";
    public const string ActionAccessVlan = "划入 Access VLAN";
    public const string ActionTrunk = "设置为 Trunk";
    public const string ActionSpeed = "设置速率";
    public const string ActionDuplex = "设置双工";
    public const string ActionMedium = "设置介质类型";

    private readonly PortCommandGenerator _portGenerator = new();
    private readonly VlanCommandGenerator _vlanGenerator = new();
    private readonly TrunkCommandGenerator _trunkGenerator = new();

    private string _selectedAction = ActionEnable;
    private string _vlanIdText = "100";
    private string _speed = "1000";
    private string _duplex = "auto";
    private string _medium = "copper";
    private string _manualPortsText = string.Empty;
    private string _lldpHint = string.Empty;
    private PortStatusRecord? _selectedPort;

    public PortsViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
        : base(shell, connections, commands)
    {
        Title = "端口";
        RegisterCachedCollection(Ports);          // 断开/换设备时清空端口表

        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(false));
        ToggleAllPortsCommand = new RelayCommand(ToggleAllPorts);
        PreviewActionCommand = new RelayCommand(PreviewAction);
        RefreshLldpCommand = new AsyncRelayCommand(RefreshLldpAsync, () => IsConnected && !IsBusy);
        CopyRawCommand = new RelayCommand(CopyRaw);

        Connections.SessionChanged += (_, _) => RefreshLldpCommand.RaiseCanExecuteChanged();
    }

    /// <summary>[获取 LLDP 邻居] 依赖 IsBusy：忙碌状态一变就要刷新按钮可用状态。</summary>
    protected override void OnBusyChanged() => RefreshLldpCommand.RaiseCanExecuteChanged();

    public ObservableCollection<PortStatusRecord> Ports { get; } = new();

    /// <summary>当前选中端口（用于选中项详情面板）。</summary>
    public PortStatusRecord? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetProperty(ref _selectedPort, value))
            {
                OnPropertyChanged(nameof(SelectedPortDetail));
            }
        }
    }

    /// <summary>详情优先跟随 DataGrid 焦点行；尚无焦点行时回退到首个勾选项。</summary>
    public PortStatusRecord? SelectedPortDetail =>
        (SelectedPort is { } selected && Ports.Contains(selected) ? selected : null)
        ?? Ports.FirstOrDefault(port => port.IsSelected);

    public IReadOnlyList<string> PortActionOptions { get; } = new[]
    {
        ActionEnable, ActionShutdown, ActionAccessVlan, ActionTrunk, ActionSpeed, ActionDuplex, ActionMedium,
    };

    public IReadOnlyList<string> SpeedOptions { get; } = new[] { "auto", "10", "100", "1000" };

    public IReadOnlyList<string> DuplexOptions { get; } = new[] { "auto", "full", "half" };

    public IReadOnlyList<string> MediumOptions { get; } = new[] { "copper", "fiber" };

    public string SelectedAction
    {
        get => _selectedAction;
        set => SetProperty(ref _selectedAction, value);
    }

    public string VlanIdText
    {
        get => _vlanIdText;
        set => SetProperty(ref _vlanIdText, value);
    }

    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    public string Duplex
    {
        get => _duplex;
        set => SetProperty(ref _duplex, value);
    }

    public string Medium
    {
        get => _medium;
        set => SetProperty(ref _medium, value);
    }

    /// <summary>手工输入的端口（支持 g0/1-3,g0/5 写法），会与勾选的端口合并去重。</summary>
    public string ManualPortsText
    {
        get => _manualPortsText;
        set
        {
            if (SetProperty(ref _manualPortsText, value))
            {
                OnPropertyChanged(nameof(PortsInputText));
                OnPropertyChanged(nameof(SelectionSummary));
            }
        }
    }

    public string LldpHint
    {
        get => _lldpHint;
        private set => SetProperty(ref _lldpHint, value);
    }

    public IReadOnlyList<string> SelectedPorts =>
        Ports.Where(p => p.IsSelected).Select(p => p.NormalizedPort).ToList();

    public string SelectedPortsSummary
    {
        get
        {
            var selected = SelectedPorts;
            return selected.Count == 0
                ? "未勾选端口"
                : $"{selected.Count} 个：{string.Join("、", selected)}";
        }
    }

    /// <summary>表头复选框只根据行状态显示全选 / 部分 / 未选状态；点击行为由 ToggleAllPortsCommand 处理。</summary>
    public bool? IsAllPortsSelected
    {
        get
        {
            if (Ports.Count == 0)
            {
                return false;
            }

            var selected = Ports.Count(p => p.IsSelected);
            return selected == 0 ? false : selected == Ports.Count ? true : null;
        }
    }

    public string SelectionSummary
    {
        get
        {
            var ports = InterfaceNameHelper.ExpandAll(PortsInputText, out var unrecognized);
            var text = ports.Count == 0
                ? "未选择端口"
                : $"将影响 {ports.Count} 个端口：{string.Join(", ", ports.Take(12))}{(ports.Count > 12 ? " …" : string.Empty)}";
            // 认不出来的 token、以及超出单范围上限被截掉的端口，都必须说出来
            //（旧实现静默丢弃 → 命令少配端口，用户以为都配上了）
            return unrecognized.Count == 0
                ? text
                : $"{text}｜⚠ 以下输入没有完整展开（不会下发）：{string.Join(", ", unrecognized)}";
        }
    }

    public string PortsInputText => BuildPortsInput();

    public string BatchHint =>
        "勾选端口或手工输入端口（例如 g0/1-3,g0/5；单个范围最多展开 128 个端口）→ 选择操作 → [生成命令并预览]。";

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand ToggleAllPortsCommand { get; }

    public RelayCommand PreviewActionCommand { get; }

    public AsyncRelayCommand RefreshLldpCommand { get; }

    public RelayCommand CopyRawCommand { get; }

    protected override void ResetDeviceData()
    {
        base.ResetDeviceData();
        SelectedPort = null;
        OnPropertyChanged(nameof(SelectedPortDetail));
        OnPropertyChanged(nameof(SelectedPortsSummary));
    }

    /// <summary>从 LLDP 页跳转过来时预选端口。</summary>
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not string port || string.IsNullOrWhiteSpace(port))
        {
            return;
        }

        ManualPortsText = port;
        // 用身份键比较：LLDP 页传过来的可能是 `Ag128`，而端口表里写的是 `AggregatePort 128`
        var normalized = InterfaceNameHelper.IdentityKey(port);
        var row = Ports.FirstOrDefault(p =>
            string.Equals(p.IdentityKey, normalized, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            row.IsSelected = true;
            PortsInputTextChanged();
        }

        StatusHint = $"已从 LLDP 带入端口 {port}（可继续勾选其它端口后生成命令预览）。";
    }

    protected override async Task RefreshAsync()
    {
        var raw = await RunShowAsync(ShowCommands.InterfaceStatus);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseInterfaceStatus(raw);
        Ports.Clear();
        foreach (var port in parsed.Items)
        {
            // 勾选状态变化时刷新“将影响 N 个端口”的提示。
            port.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PortStatusRecord.IsSelected))
                {
                    PortsInputTextChanged();
                    OnPropertyChanged(nameof(SelectedPortDetail));
                }
            };
            Ports.Add(port);
        }

        var modeCheckSummary = "端口模式未核实";
        string? trunkRaw = null;
        ShowParseResult<TrunkPortRecord>? trunkParsed = null;
        if (parsed.HasStructuredData)
        {
            // show interface status 的 VLAN 数字既可能是 Access VLAN，也可能是 Trunk 的 Native VLAN；
            // 用 Trunk 页相同的权威清单核对，避免同一接口在两页被标成不同模式。
            trunkRaw = await RunShowAsync(ShowCommands.InterfacesTrunk);
            if (trunkRaw is not null)
            {
                trunkParsed = ShowOutputParser.ParseTrunk(trunkRaw);
                var hasCompleteTrunkList = trunkParsed.HasStructuredData || ShowOutputParser.HasTrunkTableHeader(trunkRaw);
                if (hasCompleteTrunkList)
                {
                    var trunkKeys = trunkParsed.Items
                        .Select(item => InterfaceNameHelper.IdentityKey(item.Port))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var port in Ports)
                    {
                        var vlan = port.Vlan.Trim();
                        if (vlan.Length > 0 && vlan.All(char.IsDigit))
                        {
                            port.ApplyTrunkMembership(trunkKeys.Contains(port.IdentityKey));
                        }
                    }

                    modeCheckSummary = $"已按 Trunk 清单核对（{trunkParsed.Items.Count} 个）";
                }
            }
        }

        SelectedPort = null;
        OnPropertyChanged(nameof(SelectedPortDetail));
        ParseSummary = $"端口 {parsed.Items.Count} 个｜{parsed.SummaryText}｜{modeCheckSummary}";
        ParserNote = parsed.ParserNote ?? (modeCheckSummary == "端口模式未核实"
            ? "未能确认 Trunk 清单；数字 VLAN 不据此猜测 Access / Trunk。请查看原始输出。"
            : string.Empty);
        if (trunkRaw is not null)
        {
            SetRawOutput(
                $"{ShowCommands.InterfaceStatus} + {ShowCommands.InterfacesTrunk}",
                $"=== {ShowCommands.InterfaceStatus} ===\r\n{raw}\r\n=== {ShowCommands.InterfacesTrunk} ===\r\n{trunkRaw}");
        }

        StatusHint = trunkRaw is null
            ? "端口状态已刷新；Trunk 模式未能核实。"
            : $"端口状态已刷新；{modeCheckSummary}。";
        LldpHint = parsed.HasStructuredData
            ? "可点击[获取 LLDP 邻居]填充 LLDP 列"
            : "端口状态未解析成功，请查看原始输出";
        PortsInputTextChanged();
    }

    private async Task RefreshLldpAsync()
    {
        var raw = await RunShowAsync(ShowCommands.LldpNeighbors);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseLldpNeighbors(raw);
        var map = new Dictionary<string, LldpNeighborRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var neighbor in parsed.Items)
        {
            // 身份键匹配：LLDP 表里写 `Ag128`、端口表里写 `AggregatePort 128` 也要能对上
            //（跟 PortStatusRecord.NormalizedPort 用同一套键，两边必须一致）。
            var key = InterfaceNameHelper.IdentityKey(neighbor.LocalPort);
            map[key] = neighbor;
        }

        var matched = 0;
        foreach (var port in Ports)
        {
            if (map.TryGetValue(port.IdentityKey, out var neighbor))
            {
                port.LldpNeighbor = string.IsNullOrWhiteSpace(neighbor.NeighborPort)
                    ? neighbor.DisplayName
                    : $"{neighbor.DisplayName}({neighbor.NeighborPort})";
                matched++;
            }
            else
            {
                port.LldpNeighbor = "无";
            }
        }

        LldpHint = $"LLDP 邻居 {parsed.Items.Count} 条，匹配到 {matched}/{Ports.Count} 个端口";
        ParseSummary = $"端口 {Ports.Count} 个｜LLDP 邻居 {parsed.Items.Count} 条（匹配 {matched}）";
        ParserNote = parsed.ParserNote ?? string.Empty;
    }

    private void PreviewAction()
    {
        var ports = InterfaceNameHelper.ExpandAll(PortsInputText);
        if (ports.Count == 0)
        {
            ReportNoPorts();
            return;
        }

        try
        {
            var plan = SelectedAction switch
            {
                ActionEnable => _portGenerator.SetEnabled(ports, true),
                ActionShutdown => _portGenerator.SetEnabled(ports, false),
                ActionAccessVlan => _vlanGenerator.AssignAccessVlan(ports, RequireVlanId()),
                ActionTrunk => _trunkGenerator.SetTrunk(ports),
                ActionSpeed => _portGenerator.SetSpeed(ports, Speed),
                ActionDuplex => _portGenerator.SetDuplex(ports, Duplex),
                ActionMedium => _portGenerator.SetMediumType(ports, string.Equals(Medium, "fiber", StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };

            if (plan is null)
            {
                StatusHint = $"未识别的操作：{SelectedAction}";
                return;
            }

            PreviewCommands(plan);
        }
        catch (Exception ex)
        {
            StatusHint = $"生成命令失败：{ex.Message}";
        }
    }

    private int RequireVlanId()
    {
        if (int.TryParse(VlanIdText?.Trim(), out var vlanId) && vlanId is > 0 and < 4095)
        {
            return vlanId;
        }

        throw new InvalidOperationException($"VLAN ID 无效：{VlanIdText}");
    }

    private void SetSelection(bool selected)
    {
        foreach (var port in Ports)
        {
            port.IsSelected = selected;
        }

        PortsInputTextChanged();
    }

    private void ToggleAllPorts() =>
        SetSelection(Ports.Count > 0 && Ports.Any(port => !port.IsSelected));

    private void PortsInputTextChanged()
    {
        OnPropertyChanged(nameof(SelectedPorts));
        OnPropertyChanged(nameof(SelectedPortsSummary));
        OnPropertyChanged(nameof(PortsInputText));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IsAllPortsSelected));
    }

    private string BuildPortsInput()
    {
        var manual = ManualPortsText?.Trim() ?? string.Empty;
        var selected = Ports.Where(p => p.IsSelected).Select(p => p.NormalizedPort).ToList();
        if (selected.Count == 0)
        {
            return manual;
        }

        var combined = string.IsNullOrEmpty(manual)
            ? string.Join(",", selected)
            : manual + "," + string.Join(",", selected);
        return combined;
    }

    private void CopyRaw()
    {
        StatusHint = AppServices.Clipboard.TrySetText(RawOutput ?? string.Empty, out var error)
            ? "原始输出已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}
