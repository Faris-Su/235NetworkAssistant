using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Commands;

/// <summary>Trunk 相关命令生成：模式、允许 VLAN 增删、Native VLAN。</summary>
public sealed class TrunkCommandGenerator : ICommandGenerator
{
    public string Name => "TrunkCommandGenerator";

    public CommandPlan SetTrunk(IEnumerable<string> ports)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { "switchport mode trunk" });

        return new CommandPlan
        {
            Title = "设置端口为 Trunk",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 设为 Trunk 模式",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。Trunk 端口承载多个 VLAN，误改会造成上联中断。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan AddAllowedVlan(IEnumerable<string> ports, IEnumerable<int> vlanIds)
    {
        var normalized = RequirePorts(ports);
        var vlanList = InterfaceBlockBuilder.FormatVlanList(vlanIds);
        if (string.IsNullOrEmpty(vlanList))
        {
            throw new InvalidOperationException("未指定 VLAN。");
        }

        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"switchport trunk allowed vlan add {vlanList}" });

        return new CommandPlan
        {
            Title = $"Trunk 放通 VLAN {vlanList}",
            Description = $"在 {InterfaceBlockBuilder.DescribePorts(normalized)} 上追加允许 VLAN：{vlanList}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。使用 add 只追加，不会删除已有允许 VLAN。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan RemoveAllowedVlan(IEnumerable<string> ports, IEnumerable<int> vlanIds)
    {
        var normalized = RequirePorts(ports);
        var vlanList = InterfaceBlockBuilder.FormatVlanList(vlanIds);
        if (string.IsNullOrEmpty(vlanList))
        {
            throw new InvalidOperationException("未指定 VLAN。");
        }

        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"switchport trunk allowed vlan remove {vlanList}" });

        return new CommandPlan
        {
            Title = $"Trunk 移除 VLAN {vlanList}",
            Description = $"在 {InterfaceBlockBuilder.DescribePorts(normalized)} 上移除允许 VLAN：{vlanList}",
            ImpactScope = $"危险操作：从 Trunk 移除 VLAN {vlanList} 后，这些 VLAN 的流量将无法通过 {InterfaceBlockBuilder.DescribePorts(normalized, 32)}。",
            RiskLevel = CommandRiskLevel.Dangerous,
            Commands = commands,
        };
    }

    public CommandPlan ClearAllowedVlan(IEnumerable<string> ports)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { "no switchport trunk allowed vlan" });

        return new CommandPlan
        {
            Title = "取消 Trunk VLAN 修剪",
            Description = $"取消 {InterfaceBlockBuilder.DescribePorts(normalized)} 上的 allowed vlan 限制（恢复全部放通）",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。会恢复为允许所有 VLAN。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan SetNativeVlan(IEnumerable<string> ports, int vlanId)
    {
        var normalized = RequirePorts(ports);
        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { $"switchport trunk native vlan {vlanId}" });

        return new CommandPlan
        {
            Title = $"设置 Trunk Native VLAN {vlanId}",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 的 Native VLAN 设为 {vlanId}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。两端 Native VLAN 不一致会导致 VLAN 泄漏与通信异常，请确认设备支持该命令。",
            RiskLevel = CommandRiskLevel.Dangerous,
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
