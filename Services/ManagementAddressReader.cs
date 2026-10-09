using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>读一次管理 IP 的结果：成功与否 + 地址 + 一句给界面看的说明。</summary>
public sealed record ManagementAddressReadResult(bool Success, string? IpAddress, string Message);

/// <summary>
/// 「从设备读一次管理 IP」这件事的**唯一实现**：跑 <c>show ip interface brief</c> → 从三层接口里挑一个
/// 最像"这台设备管理口"的地址 → 回填到会话（<see cref="ConnectionService.LearnedManagementAddress"/>）。
///
/// 为什么要抽出来：这个动作现在有两个入口 —— 【设备】页读「三层接口」、以及【概览】页管理 IP 那一格的
/// [获取] 按钮（用户要求：点一下就能拿到，不用专门跳到设备页）。挑法必须只有一份，
/// 否则两个页面显示的管理 IP 会不一致。
///
/// **绝不自动执行**：只在用户点按钮 / 点刷新时调用（用户明确要求"别在连接时偷偷发命令"）。
/// </summary>
public static class ManagementAddressReader
{
    /// <summary>读管理 IP 用的命令（只读，不涉及配置模式）。</summary>
    public const string Command = ShowCommands.InterfaceBrief;

    /// <summary>
    /// 从三层接口表里挑一个"这台设备的管理 IP"（没有就返回 null）。
    ///
    /// 取舍顺序（都很常见，按"最可能是管理口"排）：
    ///   ① VLAN 接口 + 状态/协议都 up —— 交换机管理口基本都是 SVI；
    ///   ② 任何状态/协议都 up 的已配置接口；
    ///   ③ VLAN 接口（哪怕 down）；
    ///   ④ 第一个已配置的接口。
    /// "已配置"= 地址不是 `unassigned` / `not set`（见 <see cref="IpInterfaceRecord.IsAssigned"/>）。
    /// </summary>
    public static string? PickIp(IReadOnlyList<IpInterfaceRecord> rows)
    {
        static bool IsUp(string text) => text.Equals("up", StringComparison.OrdinalIgnoreCase);
        static bool IsVlan(IpInterfaceRecord r) => r.Interface.StartsWith("VLAN", StringComparison.OrdinalIgnoreCase);

        var assigned = rows.Where(r => r.IsAssigned).ToList();
        var picked =
            assigned.FirstOrDefault(r => IsVlan(r) && IsUp(r.Status) && IsUp(r.Protocol))
            ?? assigned.FirstOrDefault(r => IsUp(r.Status) && IsUp(r.Protocol))
            ?? assigned.FirstOrDefault(IsVlan)
            ?? assigned.FirstOrDefault();

        return picked is null ? null : Helpers.IpAddressHelper.ExtractFirst(picked.IpAddress);
    }

    /// <summary>解析 `show ip interface brief` 原文并挑出一个管理 IP（纯函数，便于自检）。</summary>
    public static string? PickIpFromOutput(string rawOutput) =>
        PickIp(ShowOutputParser.ParseIpInterfaceBrief(rawOutput).Items);

    /// <summary>
    /// 跑一次命令、挑地址、回填会话。**不抛异常**：失败一律折成一条能照做的说明返回，
    /// 由调用方（概览页）原样显示——现场最怕的是"点了没反应"。
    /// </summary>
    public static async Task<ManagementAddressReadResult> ReadAsync(
        CommandService commands,
        ConnectionService connections,
        ILogService log,
        CancellationToken cancellationToken)
    {
        if (!connections.IsConnected)
        {
            return new ManagementAddressReadResult(false, null, "还没连上设备：先在【连接】页连上再点[获取]。");
        }

        if (!connections.IsCliReady)
        {
            // ⚠️ 登录没走完时绝不能发命令：`show ip interface brief` 会被设备当成用户名/密码吃掉，
            //    把本来还能手动登录的会话彻底搅乱（这一课在 2026-09-25 的现场留档里吃过）。
            return new ManagementAddressReadResult(
                false,
                null,
                "CLI 还没就绪（登录尚未完成）：先到【CLI】页把用户名密码输完，看到提示符后再点[获取]。");
        }

        try
        {
            var raw = await commands.RunShowCommandAsync(Command, cancellationToken).ConfigureAwait(false);
            var rows = ShowOutputParser.ParseIpInterfaceBrief(raw).Items;
            var ip = PickIp(rows);
            if (ip is null)
            {
                return new ManagementAddressReadResult(
                    false,
                    null,
                    rows.Count == 0
                        ? $"设备没有返回三层接口（{Command} 输出为空，可能型号不支持）。"
                        : "设备上的三层接口都没有配 IP（全是 unassigned），没有可显示的管理 IP。");
            }

            connections.UpdateManagementAddress(ip);
            log.Info($"已从设备读到管理 IP：{ip}（{Command}）");
            return new ManagementAddressReadResult(true, ip, $"已从设备读到管理 IP：{ip}");
        }
        catch (OperationCanceledException)
        {
            return new ManagementAddressReadResult(false, null, "读取管理 IP 已取消。");
        }
        catch (Exception ex)
        {
            log.Warn("读取管理 IP 失败", ex);
            return new ManagementAddressReadResult(false, null, $"读取管理 IP 失败：{ex.Message}");
        }
    }
}
