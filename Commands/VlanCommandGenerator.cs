using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Commands;

/// <summary>VLAN 相关命令生成：创建/删除 VLAN、端口加入 Access VLAN。</summary>
public sealed class VlanCommandGenerator : ICommandGenerator
{
    public string Name => "VlanCommandGenerator";

    public CommandPlan CreateVlan(int vlanId, string? name)
    {
        var commands = new List<string> { $"vlan {vlanId}" };
        if (!string.IsNullOrWhiteSpace(name))
        {
            commands.Add($"name {name.Trim()}");
        }

        commands.Add("exit");

        return new CommandPlan
        {
            Title = $"创建 VLAN {vlanId}",
            Description = string.IsNullOrWhiteSpace(name)
                ? $"创建 VLAN {vlanId}"
                : $"创建 VLAN {vlanId}，名称为 {name.Trim()}",
            // ⚠️ 不能说"不影响已有 VLAN"：`vlan X` 对**已存在**的 VLAN 只是进入它的配置上下文，
            // 后面那句 `name Y` 就成了改名。调用方（VlanViewModel）会先用刷新到的 VLAN 列表判重，
            // 但列表为空时判不了 —— 所以这里也要如实写清楚，不能给一个绝对承诺。
            ImpactScope = "在设备上新增一个 VLAN。注意：如果这个 VLAN 号在设备上已经存在，"
                          + "本条命令（vlan X → name Y）的实际效果是**改名字**，不会新建。",
            RiskLevel = CommandRiskLevel.Safe,
            Commands = commands,
        };
    }

    /// <summary>
    /// 修改已有 VLAN 的名称。必须走 VLAN 配置上下文（vlan X → name Y），
    /// **不能**实现成“删除 VLAN + 重建”（那样会丢掉该 VLAN 的端口与业务）。
    /// </summary>
    public CommandPlan RenameVlan(int vlanId, string? newName, string? currentName = null)
    {
        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("VLAN 名称不能为空。");
        }

        if (name.Length > 32)
        {
            throw new InvalidOperationException("VLAN 名称最长 32 个字符。");
        }

        if (name.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("VLAN 名称不能包含空格。");
        }

        var commands = new List<string>
        {
            $"vlan {vlanId}",
            $"name {name}",
            "exit",
        };

        return new CommandPlan
        {
            Title = $"修改 VLAN {vlanId} 名称",
            Description = string.IsNullOrWhiteSpace(currentName)
                ? $"把 VLAN {vlanId} 的名称改为 {name}"
                : $"把 VLAN {vlanId} 的名称从 {currentName} 改为 {name}",
            ImpactScope =
                $"只改 VLAN {vlanId} 的名称，不删除 VLAN、不改动端口成员与 VLAN 接口配置。" +
                "（设备上 VLAN 名称通常被网管平台/监控用作标识，改名后请同步更新。）",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan DeleteVlan(int vlanId)
    {
        return new CommandPlan
        {
            Title = $"删除 VLAN {vlanId}",
            Description = $"删除 VLAN {vlanId}",
            ImpactScope = $"危险操作：VLAN {vlanId} 下所有端口将失去该 VLAN 的网络访问，可能造成业务中断。",
            RiskLevel = CommandRiskLevel.Dangerous,
            Commands = new[] { $"no vlan {vlanId}" },
        };
    }

    public CommandPlan AssignAccessVlan(IEnumerable<string> ports, int vlanId, bool setAccessMode = true)
    {
        var normalized = InterfaceNameHelper.NormalizeAll(ports);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("未选择端口。");
        }

        var body = new List<string>();
        if (setAccessMode)
        {
            body.Add("switchport mode access");
        }

        body.Add($"switchport access vlan {vlanId}");

        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, body);

        return new CommandPlan
        {
            Title = $"将端口划入 VLAN {vlanId}",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 配置为 Access 模式并划入 VLAN {vlanId}",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。这些端口当前所属 VLAN 会被改变（含 Trunk 端口会被改为 Access）。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }

    public CommandPlan RemoveAccessVlan(IEnumerable<string> ports)
    {
        var normalized = InterfaceNameHelper.NormalizeAll(ports);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("未选择端口。");
        }

        var commands = new List<string>();
        InterfaceBlockBuilder.AppendBlock(commands, normalized, new[] { "no switchport access vlan" });

        return new CommandPlan
        {
            Title = "端口恢复默认 VLAN",
            Description = $"将 {InterfaceBlockBuilder.DescribePorts(normalized)} 恢复到默认 VLAN 1",
            ImpactScope = $"影响端口：{InterfaceBlockBuilder.DescribePorts(normalized, 32)}。",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = commands,
        };
    }
}
