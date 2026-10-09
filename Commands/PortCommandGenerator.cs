using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Commands;

/// <summary>端口相关命令生成：启停、模式、速率、双工、介质类型。</summary>
public sealed class PortCommandGenerator : ICommandGenerator
{
    public string Name => "PortCommandGenerator";

    public CommandPlan SetEnabled(IEnumerable<string> ports, bool enabled)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { enabled ? "no shutdown" : "shutdown" });

        return new CommandPlan
        {
            Title = enabled ? "开启端口" : "关闭端口（shutdown）",
            Description = $"{(enabled ? "开启" : "关闭")}端口 {InterfaceBlockBuilder.DescribePorts(normalized)}",
            ImpactScope = enabled
                ? $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。"
                  + "no shutdown 打开的是端口的**管理状态**；如果这个口没插网线（或对端没开机），"
                  + "链路状态会一直是 down —— 刷新后 Status 仍显示 down 属于正常现象，"
                  + "不代表命令没生效（真机判据：show interfaces <端口> 里不再出现 administratively down）。"
                : $"危险操作：端口 {InterfaceBlockBuilder.DescribePorts(normalized, 32)} 将立即停止转发，对应终端会断网。",
            RiskLevel = enabled ? CommandRiskLevel.Caution : CommandRiskLevel.Dangerous,
            Commands = commands,
        };
    }

    public CommandPlan SetMode(IEnumerable<string> ports, bool trunk)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { trunk ? "switchport mode trunk" : "switchport mode access" });

        return new CommandPlan
        {
            Title = trunk ? "端口改为 Trunk" : "端口改为 Access",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 设为 {(trunk ? "Trunk" : "Access")} 模式",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。模式切换会改变该端口的 VLAN 处理方式。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan SetAccessVlan(IEnumerable<string> ports, int vlanId)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"switchport access vlan {vlanId}" });

        return new CommandPlan
        {
            Title = $"设置端口 Access VLAN {vlanId}",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 的 Access VLAN 设为 {vlanId}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan SetSpeed(IEnumerable<string> ports, string speed)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"speed {speed}" });

        return new CommandPlan
        {
            Title = $"设置端口速率 {speed}",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 速率设为 {speed}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。速率与实际链路不匹配会导致端口 down（光口只能 auto）。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan SetDuplex(IEnumerable<string> ports, string duplex)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"duplex {duplex}" });

        return new CommandPlan
        {
            Title = $"设置端口双工 {duplex}",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 双工模式设为 {duplex}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。与对端双工不一致会导致丢包。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan SetMediumType(IEnumerable<string> ports, bool fiber)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"medium-type {(fiber ? "fiber" : "copper")}" });

        return new CommandPlan
        {
            Title = fiber ? "端口改为光口" : "端口改为电口",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 工作模式设为 {(fiber ? "光口" : "电口")}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。仅在设备支持 medium-type 的型号上使用。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    private static IReadOnlyList<string> RequirePorts(IEnumerable<string> ports)
    {
        var normalized = InterfaceNameHelper.NormalizeAll(ports);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("未选择端口。");
        }

        return normalized;
    }
}
