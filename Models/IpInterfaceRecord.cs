namespace RuijieNetworkAssistant.Models;

/// <summary>`show ip interface brief` 的一行。</summary>
public sealed class IpInterfaceRecord
{
    public string Interface { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    /// <summary>
    /// 次地址（仅新机型 `show ip interface brief` 的 `IP-Address(Sec)` 列有）。
    /// 设备原文里没配次地址时会写 `no address`；老机型根本没有这一列（为空）。
    /// </summary>
    public string SecondaryAddress { get; init; } = string.Empty;

    public string Ok { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    /// <summary>
    /// `OK?` 与 `Method` 两列的合并显示。新机型没有这两列（都为空）→ 显示 `—`，
    /// 不假装设备给过这两个值（见 <see cref="Services.ShowOutputParser.ParseIpInterfaceBrief"/> 的表头判定）。
    /// </summary>
    public string OkMethodText
    {
        get
        {
            var ok = Ok.Trim();
            var method = Method.Trim();
            if (ok.Length == 0 && method.Length == 0)
            {
                return "—";
            }

            if (ok.Length == 0)
            {
                return method;
            }

            return method.Length == 0 ? ok : $"{ok} / {method}";
        }
    }

    /// <summary>次地址列直接给界面用的文本：设备没这一列 / 没配次地址 → `—`。</summary>
    public string SecondaryText =>
        string.IsNullOrWhiteSpace(SecondaryAddress) ? "—" : SecondaryAddress;

    public bool IsAssigned =>
        !string.IsNullOrWhiteSpace(IpAddress) &&
        !IpAddress.Equals("unassigned", StringComparison.OrdinalIgnoreCase) &&
        !IpAddress.Equals("not set", StringComparison.OrdinalIgnoreCase);
}
