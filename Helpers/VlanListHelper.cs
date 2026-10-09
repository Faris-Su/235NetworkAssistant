namespace RuijieNetworkAssistant.Helpers;

/// <summary>VLAN 列表输入解析：支持 “200”、“200,210”、“200-212”、“200,210-212”。</summary>
public static class VlanListHelper
{
    /// <summary>
    /// 单个范围最多展开的 VLAN 数。取值 = 整个 VLAN 空间（1~4094），也就是说**正常写法都能完整展开**。
    /// 旧值是 128，而且超限时**静默截断**：用户写 `1-200`，预览里却是 `1-128` —— 这是唯一一个
    /// 会把"少配的配置"真下发到设备的截断点（2026-09-21 DSH 复核查出）。现在超限一律报错，绝不静默丢。
    /// </summary>
    public const int MaxVlansPerRange = 4094;

    public static bool TryParse(string? text, out IReadOnlyList<int> vlans, out string? error)
    {
        var result = new List<int>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            vlans = result;
            error = "VLAN 列表为空";
            return false;
        }

        foreach (var token in text.Split(
                     new[] { ',', '\uFF0C', ' ', ';', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bounds.Length == 1 &&
                int.TryParse(bounds[0], out var single) &&
                single is > 0 and < 4095)
            {
                result.Add(single);
                continue;
            }

            if (bounds.Length == 2 &&
                int.TryParse(bounds[0], out var start) &&
                int.TryParse(bounds[1], out var end) &&
                start is > 0 and < 4095 &&
                end is > 0 and < 4095)
            {
                if (end < start)
                {
                    (start, end) = (end, start);
                }

                var span = end - start + 1;
                if (span > MaxVlansPerRange)
                {
                    error = $"单个 VLAN 范围最多 {MaxVlansPerRange} 个：{token} 有 {span} 个，请拆开写";
                    vlans = result;
                    return false;
                }

                for (var vlan = start; vlan <= end; vlan++)
                {
                    result.Add(vlan);
                }

                continue;
            }

            error = $"无法识别的 VLAN：{token}";
            vlans = result;
            return false;
        }

        vlans = result.Distinct().OrderBy(v => v).ToList();
        if (vlans.Count == 0)
        {
            error = "VLAN 列表为空";
            return false;
        }

        return true;
    }
}
