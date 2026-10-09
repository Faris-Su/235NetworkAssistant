using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Models;

/// <summary>`show interface status` 的一行端口状态。</summary>
public sealed class PortStatusRecord : ObservableObject
{
    private bool _isSelected;
    private string _lldpNeighbor = string.Empty;
    private string? _verifiedMode;

    public string Port { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Vlan { get; init; } = string.Empty;

    public string Duplex { get; init; } = string.Empty;

    public string Speed { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    /// <summary>批量配置时是否勾选该端口。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>LLDP 邻居摘要（点击[获取 LLDP 邻居]后填充）。</summary>
    public string LldpNeighbor
    {
        get => _lldpNeighbor;
        set => SetProperty(ref _lldpNeighbor, value);
    }

    public string StatusText => Status.ToLowerInvariant() switch
    {
        "up" => "up",
        "down" => "down",
        "administratively down" or "admdown" or "*down" => "adm down",
        _ => Status,
    };

    /// <summary>
    /// 只有得到 Trunk 清单确认后才显示 Access / Trunk；数字 VLAN 可能是 Trunk 的 Native VLAN，
    /// 不能仅凭该值推断端口模式。若设备状态列明确返回 trunk，则可直接确认。
    /// </summary>
    public string Mode => _verifiedMode ?? (Vlan.Trim().Equals("trunk", StringComparison.OrdinalIgnoreCase)
        ? "Trunk"
        : "待确认");

    /// <summary>按 `show int trunk` 的完整清单确认端口模式。</summary>
    public void ApplyTrunkMembership(bool isTrunk)
    {
        var mode = isTrunk ? "Trunk" : "Access";
        if (string.Equals(_verifiedMode, mode, StringComparison.Ordinal))
        {
            return;
        }

        _verifiedMode = mode;
        OnPropertyChanged(nameof(Mode));
    }

    /// <summary>用于批量命令的规范端口名（例如 g0/1）。</summary>
    /// <summary>
    /// 归一化后的端口名，**用于生成要发给设备的命令文本**（例如 `g0/1`、`AggregatePort 128`）。
    /// 这里必须保留"设备认的写法"，不要换成 IdentityKey —— `ag:128` 这种身份键发给设备是非法命令。
    /// （2026-09-22 曾一度改错，见 <see cref="IdentityKey"/> 的注释。）
    /// </summary>
    public string NormalizedPort =>
        InterfaceNameHelper.TryParse(Port.Replace(" ", string.Empty, StringComparison.Ordinal), out var token)
            ? token.ToString()
            : Port;

    /// <summary>
    /// 接口的**身份键**，只用于**匹配/去重**（LLDP 邻居贴到端口行、跨段落对齐），不发往设备。
    ///
    /// 为什么和 <see cref="NormalizedPort"/> 分开：`show interface status` 与 `show lldp neighbors`
    /// 对同一个逻辑口的写法可能不同（`AggregatePort 128` vs `Ag128`），当键用必须等价；
    /// 但发命令时必须用设备认的写法。两者混用会二选一地坏掉一边。
    /// </summary>
    public string IdentityKey => InterfaceNameHelper.IdentityKey(Port);
}
