using System.Collections.ObjectModel;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// VLAN 页面（Phase 3）：查看设备 VLAN（VLAN ID / Name / Status / Ports）、
/// 创建 / 删除 VLAN、批量把端口划入 Access VLAN。所有配置经 Command Preview。
/// </summary>
public sealed class VlanViewModel : DeviceShowPageViewModel
{
    private readonly VlanCommandGenerator _generator = new();
    private VlanInfoRecord? _selectedVlan;
    private string _vlanIdText = "100";
    private string _vlanNameText = "test";
    private string _assignPortsText = string.Empty;
    private string _assignVlanIdText = "100";
    private string _renameNameText = string.Empty;

    public VlanViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
        : base(shell, connections, commands)
    {
        Title = "VLAN";
        RegisterCachedCollection(Vlans);          // 断开/换设备时清空 VLAN 表

        PreviewCreateCommand = new RelayCommand(PreviewCreate);
        PreviewDeleteCommand = new RelayCommand(PreviewDelete, () => SelectedVlan is not null);
        PreviewRenameCommand = new RelayCommand(PreviewRename, () => SelectedVlan is not null);
        PreviewAssignCommand = new RelayCommand(PreviewAssign);
        CopyRawCommand = new RelayCommand(CopyRaw);
    }

    public Helpers.BulkObservableCollection<VlanInfoRecord> Vlans { get; } = new();

    public VlanInfoRecord? SelectedVlan
    {
        get => _selectedVlan;
        set
        {
            if (SetProperty(ref _selectedVlan, value))
            {
                PreviewDeleteCommand.RaiseCanExecuteChanged();
                PreviewRenameCommand.RaiseCanExecuteChanged();

                // 选中即把当前名称填进“改名”输入框，用户改一下就能预览命令。
                RenameNameText = value?.Name ?? string.Empty;
            }
        }
    }

    /// <summary>选中 VLAN 后要改成的新名称（默认填当前名称）。</summary>
    public string RenameNameText
    {
        get => _renameNameText;
        set => SetProperty(ref _renameNameText, value);
    }

    public string VlanIdText
    {
        get => _vlanIdText;
        set => SetProperty(ref _vlanIdText, value);
    }

    public string VlanNameText
    {
        get => _vlanNameText;
        set => SetProperty(ref _vlanNameText, value);
    }

    public string AssignPortsText
    {
        get => _assignPortsText;
        set
        {
            if (SetProperty(ref _assignPortsText, value))
            {
                OnPropertyChanged(nameof(AssignPortsSummary));
            }
        }
    }

    public string AssignVlanIdText
    {
        get => _assignVlanIdText;
        set => SetProperty(ref _assignVlanIdText, value);
    }

    public string AssignPortsSummary
    {
        get
        {
            var ports = InterfaceNameHelper.ExpandAll(AssignPortsText, out var unrecognized);
            var text = ports.Count == 0
                ? "未填写端口（例如 g0/1-3,g0/5）"
                : $"将影响 {ports.Count} 个端口：{string.Join(", ", ports.Take(12))}{(ports.Count > 12 ? " …" : string.Empty)}";
            return unrecognized.Count == 0
                ? text
                : $"{text}｜⚠ 以下输入没有完整展开（不会下发）：{string.Join(", ", unrecognized)}";
        }
    }

    public string RefreshHint => "点击[刷新]执行 show vlan（不做自动轮询）。删除 VLAN 属于危险操作，会有明确提示。";

    public RelayCommand PreviewCreateCommand { get; }

    public RelayCommand PreviewDeleteCommand { get; }

    /// <summary>修改选中 VLAN 的名称（vlan X → name Y，绝不删了重建）。</summary>
    public RelayCommand PreviewRenameCommand { get; }

    public RelayCommand PreviewAssignCommand { get; }

    public RelayCommand CopyRawCommand { get; }

    protected override async Task RefreshAsync()
    {
        var raw = await RunShowAsync(ShowCommands.Vlan);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseVlan(raw);
        Vlans.ReplaceAll(parsed.Items);

        ParseSummary = $"VLAN {parsed.Items.Count} 个｜{parsed.SummaryText}";
        ParserNote = parsed.ParserNote ?? string.Empty;
    }

    private void PreviewCreate()
    {
        if (!TryReadVlanId(VlanIdText, out var vlanId))
        {
            StatusHint = $"VLAN ID 无效：{VlanIdText}（应为 1-4094）";
            return;
        }

        var name = VlanNameText?.Trim() ?? string.Empty;
        var existing = Vlans.FirstOrDefault(v => v.VlanId == vlanId);

        if (existing is not null)
        {
            // ⚠️ 设备上已经有这个 VLAN 时，"创建"发出去的命令（vlan X → name Y → exit）
            // **真实效果是改名**，不是新建 —— 因为 `vlan X` 对已存在的 VLAN 只是进入它的配置上下文。
            // 旧实现照样走 CreateVlan，而它的 ImpactScope 写着"在设备上新增一个 VLAN，不影响已有 VLAN"，
            // 于是现场"新建 VLAN 100"实际把在用的 VLAN 100 名字改掉了（例如把业务名改成 test），
            // 预览还明确告诉用户不影响已有 VLAN。这里改成走改名流程 + 如实文案。
            var currentName = existing.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                StatusHint = $"VLAN {vlanId} 在设备上已存在（名称 {currentName}），不需要创建。"
                             + "要改名称请在下方“修改选中 VLAN 的名称”里操作。";
                return;
            }

            if (string.Equals(name, currentName, StringComparison.Ordinal))
            {
                StatusHint = $"VLAN {vlanId} 已存在，名称就是 {name}，无需修改。";
                return;
            }

            try
            {
                PreviewCommands(_generator.RenameVlan(vlanId, name, currentName));
                // PreviewCommands 会把 StatusHint 覆盖成"已生成命令预览"，所以这句必须写在它后面
                StatusHint = $"⚠ VLAN {vlanId} 在设备上已存在：这条命令的真实效果是改名称"
                             + $"（{currentName} → {name}），不是新建 VLAN。确认无误再执行。";
            }
            catch (InvalidOperationException ex)
            {
                StatusHint = ex.Message;
            }

            return;
        }

        PreviewCommands(_generator.CreateVlan(vlanId, name));
        if (Vlans.Count == 0)
        {
            // 没刷新过 VLAN 列表 → 无法判断设备上是否已有这个 VLAN，那就如实说，
            // 而不是让用户以为"一定是新建"。
            StatusHint = "已生成命令预览。⚠ 还没有刷新过 VLAN 列表，无法确认这个 VLAN 是否已存在；"
                         + "如果已存在，这条命令的真实效果是改名。建议先点[刷新 VLAN]再创建。";
        }
    }

    private void PreviewDelete()
    {
        var vlan = SelectedVlan;
        if (vlan is null)
        {
            StatusHint = "请先在列表中选择要删除的 VLAN。";
            return;
        }

        PreviewCommands(_generator.DeleteVlan(vlan.VlanId));
    }

    private void PreviewRename()
    {
        var vlan = SelectedVlan;
        if (vlan is null)
        {
            StatusHint = "请先在列表中选择要改名的 VLAN。";
            return;
        }

        var newName = RenameNameText?.Trim() ?? string.Empty;
        if (newName.Length == 0)
        {
            StatusHint = "请输入新的 VLAN 名称。";
            return;
        }

        if (string.Equals(newName, vlan.Name?.Trim(), StringComparison.Ordinal))
        {
            StatusHint = $"VLAN {vlan.VlanId} 的名称已经是 {newName}，无需修改。";
            return;
        }

        try
        {
            PreviewCommands(_generator.RenameVlan(vlan.VlanId, newName, vlan.Name));
        }
        catch (InvalidOperationException ex)
        {
            StatusHint = ex.Message;
        }
    }

    private void PreviewAssign()
    {
        if (!TryReadVlanId(AssignVlanIdText, out var vlanId))
        {
            StatusHint = $"VLAN ID 无效：{AssignVlanIdText}（应为 1-4094）";
            return;
        }

        var ports = InterfaceNameHelper.ExpandAll(AssignPortsText);
        if (ports.Count == 0)
        {
            ReportNoPorts();
            return;
        }

        PreviewCommands(_generator.AssignAccessVlan(ports, vlanId));
        // 目标 VLAN 在设备上不存在时，`switchport access vlan X` 会让设备**自动新建**这个 VLAN
        // （或被拒绝），两种结果都会和资源库规划对不上 —— 旧实现只校验了 1-4094 范围，什么都没说。
        if (Vlans.Count > 0 && Vlans.All(v => v.VlanId != vlanId))
        {
            var known = string.Join(", ", Vlans.Select(v => v.VlanId).OrderBy(id => id).Take(20));
            StatusHint = $"⚠ 设备当前的 VLAN 列表里没有 VLAN {vlanId}（刚刷新到 {Vlans.Count} 个：{known}"
                         + (Vlans.Count > 20 ? " …" : string.Empty)
                         + "）：下发后设备会自动新建这个 VLAN，或直接拒绝。"
                         + "如果这不是你要的，请先确认 VLAN 号。";
        }
    }

    private static bool TryReadVlanId(string? text, out int vlanId) =>
        int.TryParse(text?.Trim(), out vlanId) && vlanId is > 0 and < 4095;

    private void CopyRaw()
    {
        StatusHint = AppServices.Clipboard.TrySetText(RawOutput ?? string.Empty, out var error)
            ? "原始输出已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}
