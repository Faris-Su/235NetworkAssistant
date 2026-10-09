using System.Collections.ObjectModel;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// Trunk 页面（Phase 3）：查看 Trunk 端口（Mode / Encapsulation / Status / Native VLAN / Allowed VLANs）、
/// 设置 Trunk、Allowed VLAN 增删、Native VLAN。V0.1 不做复杂 VLAN Matrix。
/// </summary>
public sealed class TrunkViewModel : DeviceShowPageViewModel
{
    private readonly TrunkCommandGenerator _generator = new();
    private TrunkPortRecord? _selectedTrunk;
    private string _portsText = string.Empty;
    private string _vlanListText = string.Empty;
    private string _nativeVlanText = "1";

    public TrunkViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
        : base(shell, connections, commands)
    {
        Title = "Trunk";
        RegisterCachedCollection(Trunks);         // 断开/换设备时清空 Trunk 表

        PreviewSetTrunkCommand = new RelayCommand(PreviewSetTrunk);
        PreviewAddVlanCommand = new RelayCommand(PreviewAddVlan);
        PreviewRemoveVlanCommand = new RelayCommand(PreviewRemoveVlan);
        PreviewNativeVlanCommand = new RelayCommand(PreviewNativeVlan);
        UseSelectedPortCommand = new RelayCommand(UseSelectedPort, () => SelectedTrunk is not null);
        CopyRawCommand = new RelayCommand(CopyRaw);
    }

    public ObservableCollection<TrunkPortRecord> Trunks { get; } = new();

    public TrunkPortRecord? SelectedTrunk
    {
        get => _selectedTrunk;
        set
        {
            if (SetProperty(ref _selectedTrunk, value))
            {
                UseSelectedPortCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PortsText
    {
        get => _portsText;
        set
        {
            if (SetProperty(ref _portsText, value))
            {
                OnPropertyChanged(nameof(PortsSummary));
            }
        }
    }

    public string VlanListText
    {
        get => _vlanListText;
        set => SetProperty(ref _vlanListText, value);
    }

    public string NativeVlanText
    {
        get => _nativeVlanText;
        set => SetProperty(ref _nativeVlanText, value);
    }

    public string PortsSummary
    {
        get
        {
            var ports = InterfaceNameHelper.ExpandAll(PortsText, out var unrecognized);
            var text = ports.Count == 0
                ? "未填写端口（例如 g0/24,g0/25；也可在列表选中 Trunk 端口后点[使用选中端口]）"
                : $"将影响 {ports.Count} 个端口：{string.Join(", ", ports.Take(12))}{(ports.Count > 12 ? " …" : string.Empty)}";
            return unrecognized.Count == 0
                ? text
                : $"{text}｜⚠ 以下输入没有完整展开（不会下发）：{string.Join(", ", unrecognized)}";
        }
    }

    public string TrunkHint =>
        "Allowed VLAN 使用 add / remove 只追加或移除指定 VLAN；Native VLAN 修改两端必须一致，属于危险操作。";

    public RelayCommand PreviewSetTrunkCommand { get; }

    public RelayCommand PreviewAddVlanCommand { get; }

    public RelayCommand PreviewRemoveVlanCommand { get; }

    public RelayCommand PreviewNativeVlanCommand { get; }

    public RelayCommand UseSelectedPortCommand { get; }

    public RelayCommand CopyRawCommand { get; }

    protected override async Task RefreshAsync()
    {
        var raw = await RunShowAsync(ShowCommands.InterfacesTrunk);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseTrunk(raw);
        Trunks.Clear();
        foreach (var trunk in parsed.Items)
        {
            Trunks.Add(trunk);
        }

        ParseSummary = $"Trunk 端口 {parsed.Items.Count} 个｜{parsed.SummaryText}";
        ParserNote = parsed.ParserNote ?? string.Empty;
    }

    private void PreviewSetTrunk()
    {
        if (!TryReadPorts(out var ports))
        {
            return;
        }

        PreviewCommands(_generator.SetTrunk(ports));
    }

    private void PreviewAddVlan()
    {
        if (!TryReadPorts(out var ports) || !TryReadVlans(out var vlans))
        {
            return;
        }

        PreviewCommands(_generator.AddAllowedVlan(ports, vlans));
    }

    private void PreviewRemoveVlan()
    {
        if (!TryReadPorts(out var ports) || !TryReadVlans(out var vlans))
        {
            return;
        }

        PreviewCommands(_generator.RemoveAllowedVlan(ports, vlans));
    }

    private void PreviewNativeVlan()
    {
        if (!TryReadPorts(out var ports))
        {
            return;
        }

        if (!int.TryParse(NativeVlanText?.Trim(), out var vlanId) || vlanId is <= 0 or >= 4095)
        {
            StatusHint = $"Native VLAN 无效：{NativeVlanText}（应为 1-4094）";
            return;
        }

        PreviewCommands(_generator.SetNativeVlan(ports, vlanId));
    }

    private void UseSelectedPort()
    {
        var trunk = SelectedTrunk;
        if (trunk is null)
        {
            return;
        }

        PortsText = trunk.Port;
        VlanListText = string.IsNullOrWhiteSpace(trunk.ActiveVlans) ? VlanListText : trunk.ActiveVlans;
        NativeVlanText = string.IsNullOrWhiteSpace(trunk.NativeVlan) ? NativeVlanText : trunk.NativeVlan;
        StatusHint = $"已填入选中 Trunk 端口：{trunk.Port}";
    }

    private bool TryReadPorts(out IReadOnlyList<string> ports)
    {
        ports = InterfaceNameHelper.ExpandAll(PortsText);
        if (ports.Count == 0)
        {
            ReportNoPorts();
            return false;
        }

        return true;
    }

    private bool TryReadVlans(out IReadOnlyList<int> vlans)
    {
        if (VlanListHelper.TryParse(VlanListText, out vlans, out var error))
        {
            return true;
        }

        StatusHint = $"VLAN 列表无效：{error}（应为 200 或 200,210-212 这类写法）";
        return false;
    }

    private void CopyRaw()
    {
        StatusHint = AppServices.Clipboard.TrySetText(RawOutput ?? string.Empty, out var error)
            ? "原始输出已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}
