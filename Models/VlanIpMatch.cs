namespace RuijieNetworkAssistant.Models;

/// <summary>
/// IP → VLAN 反查结果。导入的规划表可能存在不规范 IP 范围（例如 192.0.2.2-192.0.2.8），
/// 因此除了命中的记录，还带回匹配依据，便于界面提示“请核对原表”。
/// </summary>
public sealed record VlanIpMatch(VlanRecord Record, bool RangeMatched, bool SubnetMatched, int PrefixLength)
{
    /// <summary>范围与网关网段同时命中，结果可信度最高。</summary>
    public bool IsConsistent => RangeMatched && SubnetMatched;
}
