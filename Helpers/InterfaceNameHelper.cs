using System.Text;
using System.Text.RegularExpressions;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 接口名解析与范围压缩。锐捷设备同时存在 Gi0/1、g0/1、GigabitEthernet0/1、Fa0/1 等写法，
/// 所有接口名归一化集中在这里，禁止把拼接逻辑散落到各个 View/ViewModel。
/// </summary>
public static partial class InterfaceNameHelper
{
    [GeneratedRegex(@"^\s*(?<prefix>[a-zA-Z]+)?\s*(?<slot>\d+)\s*/\s*(?<port>\d+)\s*$")]
    private static partial Regex PortRegex();

    /// <summary>
    /// 逻辑接口（聚合口 / VLAN 口）：锐捷设备输出里可能写作 `AggregatePort 128`、`Ag128`、`VLAN 100`。
    /// 它们**没有 slot/port**，所以单独用 <see cref="LogicalName"/> 承载"照原样写回去"的接口名 ——
    /// 这样既不会把它们拼成 `ag0/128` 这种不存在的名字，也不需要我们发明设备语法。
    /// </summary>
    [GeneratedRegex(@"^\s*(?<kind>aggregateport|ag|vlan-interface|vlan|vl)\s*(?<index>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LogicalPortRegex();

    public sealed record PortToken(
        string Prefix,
        int Slot,
        int Port,
        string? LogicalName = null,
        int LogicalIndex = 0)
    {
        /// <summary>是不是逻辑接口（聚合口 / VLAN 口）。</summary>
        public bool IsLogical => LogicalName is not null;

        public override string ToString() => LogicalName ?? $"{Prefix}{Slot}/{Port}";
    }

    /// <summary>归一化单个接口，例如 “GigabitEthernet 0/1” → “g0/1”。</summary>
    public static bool TryParse(string? text, out PortToken token)
    {
        token = new PortToken("g", 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 逻辑接口优先判断：上联口可能写作 AggregatePort 128 / Ag128，VLAN 口写作 VLAN 100。
        // 旧实现只认 `xN/N` → 这类接口在表格里被丢行、在输入框里被**静默丢弃**（命令少配端口）。
        var logical = LogicalPortRegex().Match(text);
        if (logical.Success)
        {
            var kind = logical.Groups["kind"].Value.ToLowerInvariant();
            var index = logical.Groups["index"].Value;
            var logicalPrefix = kind.StartsWith("ag", StringComparison.Ordinal) ? "ag" : "vl";
            // 按用户写法回写（长写保留长写），只把中间多余空格去掉
            var canonical = kind switch
            {
                "aggregateport" => $"AggregatePort {index}",
                "ag" => $"Ag{index}",
                "vlan-interface" => $"VLAN {index}",
                "vlan" => $"VLAN {index}",
                _ => $"Vl{index}",
            };
            token = int.TryParse(index, out var logicalIndex)
                ? new PortToken(logicalPrefix, 0, 0, canonical, logicalIndex)
                : new PortToken(logicalPrefix, 0, 0, canonical);
            return true;
        }

        var match = PortRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var prefix = match.Groups["prefix"].Value.ToLowerInvariant();
        prefix = prefix switch
        {
            "" => "g",
            "gi" or "gig" or "gige" or "gigabitethernet" or "g0" => "g",
            "f" or "fa" or "fast" or "fastethernet" => "f",
            "te" or "tengigabitethernet" or "ten" => "tg",
            "fo" or "fortygigabitethernet" => "fg",
            _ => prefix,
        };

        token = new PortToken(prefix, int.Parse(match.Groups["slot"].Value), int.Parse(match.Groups["port"].Value));
        return true;
    }

    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string> ports)
    {
        return ExpandAll(string.Join(",", ports ?? Array.Empty<string>()));
    }

    /// <summary>
    /// 接口的**身份键**：同一个接口的不同写法必须得到同一个键，用于去重、字典键、跨段落对齐。
    ///
    /// 为什么不能直接用 <see cref="PortToken.ToString"/>：
    /// ToString 有意保留"照用户/设备写法回写"的形式（`AggregatePort 128` / `Ag128` / `VLAN 100` / `Vl100`），
    /// 因为往设备发命令时得用设备认的写法；但**当键用**时这四种写法指的是同一个接口。
    /// 现场证据：`show interfaces trunk` 是多段式输出（Mode 段 / allowed 段 / active 段 / forwarding 段），
    /// 真机各段对同一个聚合口的写法并不一致 —— 用 ToString 当键会把同一个口裂成两行，
    /// 一行只有 Mode/Encapsulation/Status，另一行只有 AllowedVlans，用户看到的"允许 VLAN"与端口对不上。
    /// </summary>
    public static string IdentityKey(PortToken token) =>
        token.IsLogical ? $"{token.Prefix}:{token.LogicalIndex}" : $"{token.Prefix}{token.Slot}/{token.Port}";

    /// <summary>同上，直接吃文本；认不出来的写法回退成"去空格 + 小写"的原样键。</summary>
    public static string IdentityKey(string? text) =>
        TryParse(text, out var token)
            ? IdentityKey(token)
            : (text ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>
    /// 展开端口输入（供端口/VLAN/Trunk 页面使用）。支持资料中的写法：
    /// “g0/1-3”、“fa 0/1-2,0/5,0/7-9”（后一段可省略前缀与槽位）、“Gi0/1 Gi0/2”。
    /// 单个范围最多展开 <paramref name="maxPortsPerRange"/> 个端口，避免输入笔误生成海量命令。
    /// </summary>
    public static IReadOnlyList<string> ExpandAll(string? text, int maxPortsPerRange = 128) =>
        ExpandAll(text, out _, maxPortsPerRange);

    /// <summary>
    /// 同 <see cref="ExpandAll(string,int)"/>，另外把**没认出来的 token** 通过
    /// <paramref name="unrecognized"/> 报出来。旧实现认不出就直接 continue —— 用户写
    /// "g0/1,Ag128"，实际只配了 g0/1，界面上没有任何提示（少配端口）。
    /// </summary>
    public static IReadOnlyList<string> ExpandAll(
        string? text,
        out IReadOnlyList<string> unrecognized,
        int maxPortsPerRange = 128)
    {
        var result = new List<string>();
        var skipped = new List<string>();
        unrecognized = skipped;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lastPrefix = "g";
        var lastSlot = 0;

        foreach (var rawToken in text.Split([',', '\uFF0C', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken;
            var match = RangeTokenRegex().Match(token);
            if (!match.Success)
            {
                if (TryParse(token, out var single))
                {
                    AddPort(result, single);
                    if (!single.IsLogical)
                    {
                        lastPrefix = single.Prefix;
                        lastSlot = single.Slot;
                    }
                }
                else
                {
                    skipped.Add(token);
                }

                continue;
            }

            var prefix = match.Groups["prefix"].Value;
            var slotText = match.Groups["slot"].Value;
            var startText = match.Groups["start"].Value;
            var endText = match.Groups["end"].Value;

            if (string.IsNullOrEmpty(prefix))
            {
                prefix = lastPrefix;
            }
            else
            {
                prefix = prefix.ToLowerInvariant();
                prefix = prefix switch
                {
                    "gi" or "gig" or "gige" or "gigabitethernet" => "g",
                    "f" or "fa" or "fast" or "fastethernet" => "f",
                    _ => prefix,
                };
            }

            if (string.IsNullOrEmpty(slotText))
            {
                slotText = lastSlot.ToString();
            }

            if (!int.TryParse(slotText, out var slot) || !int.TryParse(startText, out var start))
            {
                skipped.Add(token);
                continue;
            }

            var end = string.IsNullOrEmpty(endText) ? start : (int.TryParse(endText, out var parsedEnd) ? parsedEnd : start);
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var count = Math.Min(end - start + 1, Math.Max(1, maxPortsPerRange));
            for (var port = start; port < start + count; port++)
            {
                AddPort(result, new PortToken(prefix, slot, port));
            }

            // 超出单范围上限的部分**必须说出来**：旧实现直接 Math.Min 砍掉，
            // 被砍掉的端口既不进结果、也不进 unrecognized → 界面显示"将影响 128 个端口"，
            // 用户以为 g0/1-200 全配上了，实际后 72 个根本没下发（和之前 unrecognized 那次是同一类坑）。
            var keptEnd = start + count - 1;
            if (keptEnd < end)
            {
                skipped.Add(
                    $"{token} 只展开了前 {count} 个（{prefix}{slot}/{start}-{keptEnd}），"
                    + $"剩余 {end - keptEnd} 个（{prefix}{slot}/{keptEnd + 1}-{end}）超出单范围上限 {maxPortsPerRange}，不会下发");
            }

            lastPrefix = prefix;
            lastSlot = slot;
        }

        return result;
    }

    private static void AddPort(ICollection<string> result, PortToken token)
    {
        var text = token.ToString();
        if (!result.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(text);
        }
    }

    [GeneratedRegex(@"^\s*(?<prefix>[a-zA-Z]*)\s*(?<slot>\d*)\s*/\s*(?<start>\d+)\s*(?:-\s*(?<end>\d+)\s*)?$")]
    private static partial Regex RangeTokenRegex();

    /// <summary>
    /// 把接口集合压缩成锐捷 interface range 表达式，例如 g0/1,g0/2,g0/3,g0/5 → “g0/1-3,0/5”。
    /// 只会在同一前缀、同一槽位内压缩；跨槽位由调用方拆分多条 range 命令。
    /// </summary>
    public static IReadOnlyList<string> BuildRangeGroups(IEnumerable<string> normalizedPorts)
    {
        var tokens = new List<PortToken>();
        foreach (var port in normalizedPorts)
        {
            if (TryParse(port, out var token) && !tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        var groups = new List<string>();

        // 逻辑口（AggregatePort / VLAN）没有 slot/port，**不能**和物理口一起做 range 压缩：
        // 拼出来会变成 "ag0/128" 这种设备上根本不存在的名字。原样单独成组返回。
        foreach (var logical in tokens
                     .Where(t => t.IsLogical)
                     .Select(t => t.ToString())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(logical);
        }

        foreach (var slotGroup in tokens
                     .Where(t => !t.IsLogical)
                     .GroupBy(t => (t.Prefix, t.Slot))
                     .OrderBy(g => g.Key.Prefix, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Slot))
        {
            var ports = slotGroup.Select(t => t.Port).Distinct().OrderBy(p => p).ToList();
            var builder = new StringBuilder();
            builder.Append(slotGroup.Key.Prefix).Append(slotGroup.Key.Slot).Append('/');

            var segments = new List<string>();
            var start = ports[0];
            var previous = ports[0];
            for (var i = 1; i < ports.Count; i++)
            {
                var current = ports[i];
                if (current == previous + 1)
                {
                    previous = current;
                    continue;
                }

                segments.Add(start == previous ? start.ToString() : $"{start}-{previous}");
                start = previous = current;
            }

            segments.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            builder.Append(string.Join(",", segments));
            groups.Add(builder.ToString());
        }

        return groups;
    }
}
