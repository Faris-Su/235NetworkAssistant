namespace RuijieNetworkAssistant.Commands;

/// <summary>
/// 命令生成器标记接口：GUI → Configuration Model → CommandGenerator → CommandPlan → Preview。
/// 每个生成器只负责把“用户意图”翻译成命令，不发送、不访问设备。
/// </summary>
public interface ICommandGenerator
{
    string Name { get; }
}

/// <summary>接口块生成工具：把端口集合转成 interface / interface range 块。</summary>
internal static class InterfaceBlockBuilder
{
    public static void AppendBlock(ICollection<string> commands, IReadOnlyList<string> normalizedPorts, IEnumerable<string> bodyCommands)
    {
        foreach (var group in RuijieNetworkAssistant.Helpers.InterfaceNameHelper.BuildRangeGroups(normalizedPorts))
        {
            commands.Add($"interface range {group}");

            foreach (var command in bodyCommands)
            {
                commands.Add(command);
            }

            commands.Add("exit");
        }
    }

    /// <summary>把 VLAN 列表压缩成 “10-12,15” 形式。</summary>
    public static string FormatVlanList(IEnumerable<int> vlanIds)
    {
        var ids = vlanIds.Where(id => id is > 0 and < 4095).Distinct().OrderBy(id => id).ToList();
        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var segments = new List<string>();
        var start = ids[0];
        var previous = ids[0];
        for (var i = 1; i < ids.Count; i++)
        {
            if (ids[i] == previous + 1)
            {
                previous = ids[i];
                continue;
            }

            segments.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = ids[i];
        }

        segments.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(",", segments);
    }

    public static string DescribePorts(IReadOnlyList<string> normalizedPorts, int maxShown = 8)
    {
        if (normalizedPorts.Count == 0)
        {
            return "（未选择端口）";
        }

        var shown = string.Join(", ", normalizedPorts.Take(maxShown));
        return normalizedPorts.Count > maxShown ? $"{shown} 等 {normalizedPorts.Count} 个端口" : shown;
    }
}
