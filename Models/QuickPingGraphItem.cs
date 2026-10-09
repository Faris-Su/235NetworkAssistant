using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 图形模式里的一个格子（对应最后一段地址 0..255）。
/// 经典 QuickPing 的"图形模式"就是把这 256 个格子铺开——**一眼就能看出这一段里哪些地址活着**，
/// 比在表格里逐行找"在线"快得多。
/// </summary>
public sealed class QuickPingGraphItem : ObservableObject
{
    private QuickPingStatus _status = QuickPingStatus.Pending;
    private long? _rttMs;

    public QuickPingGraphItem(int lastOctet, string ip)
    {
        LastOctet = lastOctet;
        Ip = ip;
    }

    /// <summary>最后一段（0..255），格子上显示的就是它。</summary>
    public int LastOctet { get; }

    public string Ip { get; }

    public QuickPingStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    public long? RttMs
    {
        get => _rttMs;
        set
        {
            if (SetProperty(ref _rttMs, value))
            {
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    public bool IsOnline => Status == QuickPingStatus.Ok;

    /// <summary>
    /// 这一格是不是"非主机地址"：`.0` 是 /24 的网络号、`.255` 是广播地址，
    /// 它们**不是某台主机**，默认扫描范围（1~254）也不会包含它们 ——
    /// 界面据此把它们画得不一样，并在点击时说清原因（用户实测反馈："点 0 和 255 每次都没反应"）。
    /// 注意：这是按最常见的 /24 口径判断；如果实际掩码不同（/25、/23 等），把"从/到"改成包含它们也能正常扫。
    /// </summary>
    public bool IsNonHost => LastOctet is 0 or 255;

    public string ToolTipText => Status switch
    {
        QuickPingStatus.Ok => $"{Ip}：在线（{RttMs ?? 0} ms）",
        QuickPingStatus.Timeout => $"{Ip}：超时（ICMP 无应答；交换机常忽略 ICMP，不代表不在线）",
        QuickPingStatus.Error => $"{Ip}：错误",
        _ => IsNonHost
            ? $"{Ip}：{(LastOctet == 0 ? "网络号（/24 里不是主机）" : "广播地址（通常不应答）")}，默认范围 1~254 不扫它"
            : $"{Ip}：未测",
    };
}
