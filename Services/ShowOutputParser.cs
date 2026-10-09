using System.Text;
using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// Show 命令输出解析器。原则：
/// - 只调用资料中出现过的命令（见 docs/commands.md）；
/// - 解析按“按空白列切分 + 关键字定位”，对不同型号/固件的列宽差异保持容忍；
/// - 解析不出来的行进入 UnparsedLines，原始输出始终完整保留，界面优先显示原始文本而不是猜测。
/// 需要在实机联调时按真实输出校正（见 docs/roadmap.md Phase 3）。
/// </summary>
public static partial class ShowOutputParser
{
    private static readonly string[] VlanStatusKeywords =
    {
        "active", "act/unsup", "act/lshut", "suspend", "suspended", "inactive",
        // RGOS 真机这里是 STATIC / DYNAMIC（Cisco 口径才是 active / act-unsup…，见 work/fixtures/vlan-multiline.txt
        // 与 work/snapshots/real/vlan-real.png.raw.txt）。旧表里没有这两个词 → statusIndex=-1 →
        // 退化成"按位置切列"，VLAN 名一旦带空格就整体错位（Name="Net"、Status="Center"…）。
        "static", "dynamic",
    };

    /// <summary>解析 `show interface status`。</summary>
    public static ShowParseResult<PortStatusRecord> ParseInterfaceStatus(string raw)
    {
        var items = new List<PortStatusRecord>();
        var unparsed = new List<string>();

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0 || IsHeader(tokens, "interface", "status") || IsHeader(tokens, "port", "status"))
            {
                continue;
            }

            if (!TryReadPort(tokens, 0, out var port, out var next) || tokens.Count <= next)
            {
                unparsed.Add(line);
                continue;
            }

            // Status 可能是两个 token：真机 `admin down`（shutdown 的口）。
            // 旧实现直接取 tokens[next] 当 Status → 后面所有列整体左移一格（Vlan="down"、Duplex="100"…）。
            var status = tokens[next];
            if (string.Equals(status, "admin", StringComparison.OrdinalIgnoreCase) &&
                tokens.Count > next + 1 &&
                string.Equals(tokens[next + 1], "down", StringComparison.OrdinalIgnoreCase))
            {
                status = "admin down";
                next++;
            }

            items.Add(new PortStatusRecord
            {
                Port = port,
                Status = status,
                Vlan = TokenOrEmpty(tokens, next + 1),
                Duplex = TokenOrEmpty(tokens, next + 2),
                Speed = TokenOrEmpty(tokens, next + 3),
                Type = tokens.Count > next + 4 ? string.Join(" ", tokens.Skip(next + 4)) : string.Empty,
            });
        }

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>解析 `show vlan`。</summary>
    public static ShowParseResult<VlanInfoRecord> ParseVlan(string raw)
    {
        var items = new List<VlanInfoRecord>();
        var unparsed = new List<string>();

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (!int.TryParse(tokens[0], out var vlanId))
            {
                // 端口多的时候设备会换行：续行只有端口列表（例如某 VLAN 的第二行
                // “Te0/27, Te0/28, Ag128”），必须合并到上一条 VLAN，否则该 VLAN 端口显示不全。
                if (items.Count > 0 && LooksLikePortList(tokens))
                {
                    var previous = items[^1];
                    var extra = Join(tokens, 0);
                    // ⚠️ 拼接符必须带逗号（2026-09-22 真机暴露）：
                    // 单行里每个 token 都自带尾逗号（`Gi0/1,` → Join 后仍是 "Gi0/1, Gi0/2"），
                    // 但**行尾那个 token 没有逗号** —— 续行用空格拼就会得到
                    // `…Gi0/4 Gi0/5…`：端口列表被粘成一坨，PortCount 从 24 变成 19，
                    // 界面上"端口数"直接显示错。真机证据：work/fixtures/real-show-vlan.txt
                    // （VLAN 100 的多行续行）。
                    previous.Ports = string.IsNullOrWhiteSpace(previous.Ports)
                        ? extra
                        : previous.Ports.TrimEnd().TrimEnd(',') + ", " + extra;
                    continue;
                }

                if (!IsHeader(tokens, "vlan", string.Empty))
                {
                    unparsed.Add(line);
                }

                continue;
            }

            var statusIndex = tokens.FindIndex(1, t => VlanStatusKeywords.Contains(t.ToLowerInvariant()));
            string name;
            string status;
            string ports;

            if (statusIndex > 1)
            {
                name = string.Join(" ", tokens.Skip(1).Take(statusIndex - 1));
                status = tokens[statusIndex];
                ports = string.Join(" ", tokens.Skip(statusIndex + 1));
            }
            else
            {
                name = TokenOrEmpty(tokens, 1);
                status = TokenOrEmpty(tokens, 2);
                ports = tokens.Count > 3 ? string.Join(" ", tokens.Skip(3)) : string.Empty;
            }

            items.Add(new VlanInfoRecord
            {
                VlanId = vlanId,
                Name = name,
                Status = status,
                Ports = ports,
            });
        }

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>解析 `show int trunk`（多段式输出）。</summary>
    public static ShowParseResult<TrunkPortRecord> ParseTrunk(string raw)
    {
        var records = new Dictionary<string, TrunkPortRecord>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var unparsed = new List<string>();
        var currentSection = string.Empty;

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            // 段落标题行：以 Interface/Port 开头且第二列不是接口名
            if (IsSectionHeader(tokens))
            {
                currentSection = line.Trim().ToLowerInvariant();
                continue;
            }

            // 用更宽的接口识别：真机里 trunk 端口可能是 AggregatePort 128 / Ag128
            if (!TryReadInterface(tokens, 0, out var port, out var next))
            {
                // 续行：Allowed / Active VLAN 列表过长时设备会换行（例如 “200-202,210”），
                // 这类行没有端口名，必须并回上一条记录，否则 VLAN 列表会缺一段。
                if (order.Count > 0 && LooksLikeVlanList(tokens))
                {
                    var last = records[order[^1]];
                    var extra = Join(tokens, 0);
                    switch (SectionKind(currentSection))
                    {
                        case TrunkSection.Allowed:
                        case TrunkSection.Short:
                            last.AllowedVlans = AppendList(last.AllowedVlans, extra);
                            continue;
                        case TrunkSection.Active:
                            last.ActiveVlans = AppendList(last.ActiveVlans, extra);
                            continue;
                        case TrunkSection.Forwarding:
                            last.ForwardingVlans = AppendList(last.ForwardingVlans, extra);
                            continue;
                    }
                }

                unparsed.Add(line);
                continue;
            }

            var key = NormalizePortKey(port);
            if (!records.TryGetValue(key, out var record))
            {
                record = new TrunkPortRecord { Port = port };
                records[key] = record;
                order.Add(key);
            }

            switch (SectionKind(currentSection))
            {
                case TrunkSection.Mode:
                    record.Mode = TokenOrEmpty(tokens, next);
                    record.Encapsulation = TokenOrEmpty(tokens, next + 1);
                    record.Status = TokenOrEmpty(tokens, next + 2);
                    record.NativeVlan = TokenOrEmpty(tokens, next + 3);
                    break;
                case TrunkSection.Short:
                    // Interface | Native VLAN | VLAN lists（没有 Mode/Encap/Status 列）
                    record.NativeVlan = TokenOrEmpty(tokens, next);
                    record.AllowedVlans = Join(tokens, next + 1);
                    break;
                case TrunkSection.Allowed:
                    record.AllowedVlans = Join(tokens, next);
                    break;
                case TrunkSection.Active:
                    record.ActiveVlans = Join(tokens, next);
                    break;
                case TrunkSection.Forwarding:
                    record.ForwardingVlans = Join(tokens, next);
                    break;
                default:
                    unparsed.Add(line);
                    break;
            }
        }

        var items = order.Select(key => records[key]).ToList();
        return BuildResult(items, unparsed, raw);
    }

    /// <summary>确认输出至少包含 Trunk 表头；可区分“有效空表”和“不支持命令/输出格式未知”。</summary>
    public static bool HasTrunkTableHeader(string raw)
    {
        foreach (var line in SplitLines(raw))
        {
            var lower = line.Trim().ToLowerInvariant();
            if ((lower.Contains("interface", StringComparison.Ordinal) || lower.StartsWith("port ", StringComparison.Ordinal)) &&
                lower.Contains("native vlan", StringComparison.Ordinal) &&
                (lower.Contains("vlan lists", StringComparison.Ordinal) ||
                 lower.Contains("mode", StringComparison.Ordinal) ||
                 lower.Contains("allowed", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析 `show lldp neighbors`（块状详情或表格式）。</summary>
    public static ShowParseResult<LldpNeighborRecord> ParseLldpNeighbors(string raw)
    {
        var items = new List<LldpNeighborRecord>();
        var unparsed = new List<string>();
        var block = new StringBuilder();
        LldpNeighborRecord? current = null;
        var tableMode = false;
        var sawLocalHeader = false;

        var lines = SplitLines(raw).ToList();

        // 真机常见表格式：System Name | Local Intf | Port ID | Capability | Aging-time
        var systemTable = TryParseLldpSystemTable(lines);
        if (systemTable.Count > 0)
        {
            return BuildResult(systemTable, unparsed, raw);
        }

        foreach (var line in lines)
        {
            if (IsNoise(line))
            {
                continue;
            }

            var lower = line.ToLowerInvariant();

            if (lower.Contains("neighbor-information", StringComparison.Ordinal) ||
                lower.Contains("lldp neighbor information", StringComparison.Ordinal))
            {
                FlushBlock(current, block, items);
                current = new LldpNeighborRecord { LocalPort = ExtractLocalPort(line) };
                block.Clear();
                block.AppendLine(line.Trim());
                continue;
            }

            if (current is not null)
            {
                block.AppendLine(line.Trim());
                ApplyKeyValue(current, line);
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (!sawLocalHeader && IsHeader(tokens, "local", string.Empty))
            {
                sawLocalHeader = true;
                tableMode = true;
                continue;
            }

            if (tableMode && TryReadPort(tokens, 0, out var localPort, out var next))
            {
                items.Add(new LldpNeighborRecord
                {
                    LocalPort = localPort,
                    NeighborDevice = TokenOrEmpty(tokens, next),
                    NeighborPort = TokenOrEmpty(tokens, next + 1),
                    HoldTime = TokenOrEmpty(tokens, next + 2),
                    RawBlock = line.Trim(),
                });
                continue;
            }

            unparsed.Add(line);
        }

        FlushBlock(current, block, items);

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>解析 `show lldp neighbors interface <port> detail`。</summary>
    public static LldpNeighborDetail ParseLldpNeighborDetail(string raw)
    {
        var parsed = ParseLldpNeighbors(raw);
        var neighbor = parsed.Items.FirstOrDefault();
        if (neighbor is not null)
        {
            // 原始 Detail 输出跟着记录走：解析不全时用户仍能看到设备原文。
            neighbor.RawDetailOutput = raw;
        }

        return new LldpNeighborDetail
        {
            Neighbor = neighbor,
            RawOutput = raw,
            ParserNote = neighbor is null
                ? "未能解析出邻居详情，请参考下方原始输出。"
                : string.Empty,
        };
    }

    /// <summary>
    /// Detail 输出是否是“设备不支持这个命令”：不同固件写法不同，统一识别常见错误提示。
    /// 只用于决定“要不要继续逐个端口查下去”，不影响基础信息的展示。
    /// </summary>
    public static bool LooksLikeUnsupportedDetail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = CliOutputNormalizer.Normalize(raw);
        string[] markers =
        {
            "% invalid input",
            "% incomplete command",
            "% unrecognized",
            "% unknown",
            "invalid command",
            "unrecognized command",
            "error: invalid",
        };

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析 `show mac-address-table`（MAC → VLAN → Port）。</summary>
    public static ShowParseResult<MacAddressRecord> ParseMacAddressTable(string raw)
    {
        var items = new List<MacAddressRecord>();
        var unparsed = new List<string>();

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count < 4)
            {
                // 表头/说明行（Mac Address Table / Vlan Mac Address Type Ports）
                if (!tokens.Any(t => MacAddressHelper.Normalize(t) is not null))
                {
                    continue;
                }

                unparsed.Add(line);
                continue;
            }

            // 表头行：Vlan / Mac Address / Type / Ports
            if (tokens.Any(t => t.Equals("Vlan", StringComparison.OrdinalIgnoreCase)) &&
                tokens.Any(t => t.StartsWith("Mac", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var macIndex = tokens.FindIndex(t => MacAddressHelper.Normalize(t) is not null);
            if (macIndex < 0 || !int.TryParse(tokens[0], out var vlan))
            {
                unparsed.Add(line);
                continue;
            }

            items.Add(new MacAddressRecord
            {
                Vlan = vlan.ToString(),
                MacAddress = MacAddressHelper.Format(tokens[macIndex]),
                Type = TokenOrEmpty(tokens, macIndex + 1),
                Port = Join(tokens, macIndex + 2),
            });
        }

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>
    /// 解析 `show arp`（IP → MAC → 接口）。
    ///
    /// 设备格式（锐捷交换机原始字节样例，见
    /// `work/fixtures/real-show-arp.txt`）：
    /// <code>
    /// Protocol  Address      Age(min)  Hardware        Type   Interface
    /// Internet  203.0.113.1    2         0200.0000.0006  arpa   VLAN 100
    /// Internet  203.0.113.197  --        0200.0000.0005  arpa   VLAN 100
    /// </code>
    /// 注意两点：① Age 可能是 `--`（静态/永久项）；② Hardware 是 `xxxx.xxxx.xxxx` 写法，
    /// 交给 <see cref="MacAddressHelper"/> 归一化，别自己切字符串。
    /// 表尾那行 `Total number of ARP entries: 3` 不是数据行，会被跳过（首列不是 Internet）。
    /// </summary>
    public static ShowParseResult<ArpRecord> ParseArp(string raw)
    {
        var items = new List<ArpRecord>();
        var unparsed = new List<string>();

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count < 4)
            {
                continue;   // 表头行 / `Total number of ARP entries: N`
            }

            // 表头行：Protocol / Address / Age(min) / Hardware / Type / Interface
            if (tokens[0].StartsWith("Protocol", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 数据行首列固定是协议名（真机是 Internet，理论上也可能出现其他协议）
            if (!tokens[0].Equals("Internet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ip = tokens.FindIndex(t => IpAddressHelper.TryParse(t, out _));
            var macIndex = tokens.FindIndex(t => MacAddressHelper.Normalize(t) is not null);
            if (ip < 0 || macIndex < 0)
            {
                unparsed.Add(line);
                continue;
            }

            // 列顺序固定是 Address → Age(min) → Hardware → Type → Interface，所以：
            //   · Age 取 IP 与 MAC **之间**那一段（真机是 `2` / `8` / `--`）——
            //     不能写成 Join(tokens, ip + 1)，那会把 MAC 之后的列一起吞进来；
            //   · Interface 从 MAC 之后再往后两位开始（紧邻 MAC 的是 Type），
            //     且 `VLAN 100` 中间有空格，必须用 Join 拼回。
            var interfaceText = Join(tokens, macIndex + 2);
            var age = macIndex > ip + 1
                ? string.Join(" ", tokens.Skip(ip + 1).Take(macIndex - ip - 1)).Trim()
                : string.Empty;
            items.Add(new ArpRecord
            {
                IpAddress = tokens[ip],
                MacAddress = MacAddressHelper.Format(tokens[macIndex]),
                Age = age,
                Interface = interfaceText,
                Vlan = ExtractVlanFromInterface(interfaceText),
            });
        }

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>从 `VLAN 100` / `Vlan100` / `Vl100` 里抽出 VLAN 号；抽不到返回空串。</summary>
    private static string ExtractVlanFromInterface(string interfaceText)
    {
        if (string.IsNullOrWhiteSpace(interfaceText))
        {
            return string.Empty;
        }

        var digits = new string(interfaceText.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? string.Empty : digits;
    }

    /// <summary>解析 `show ip dhcp snooping binding`（IP → MAC → VLAN → Port）。</summary>
    public static ShowParseResult<DhcpBindingRecord> ParseDhcpSnoopingBinding(string raw)
    {
        var items = new List<DhcpBindingRecord>();
        var unparsed = new List<string>();
        DhcpBindingRecord? pendingInterface = null;

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            // RGOS 11.x can print a leading NO. column, pipe-delimited columns, and wrap
            // Interface onto the following line. Normalize those presentation differences
            // before locating the fields instead of assuming MAC is always token zero.
            var tokens = Tokenize(line.Replace("|", " ", StringComparison.Ordinal));
            if (tokens.Count == 0)
            {
                continue;
            }

            if (TryParseDhcpBindingRow(tokens, out var binding))
            {
                if (pendingInterface is not null)
                {
                    items.Add(pendingInterface);
                    pendingInterface = null;
                }

                if (string.IsNullOrWhiteSpace(binding.Port))
                {
                    pendingInterface = binding;
                }
                else
                {
                    items.Add(binding);
                }

                continue;
            }

            if (pendingInterface is not null && TryReadInterfaceContinuation(tokens, out var port))
            {
                items.Add(CopyWithPort(pendingInterface, port));
                pendingInterface = null;
                continue;
            }

            if (IsDhcpBindingHeaderOrSummary(line))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                unparsed.Add(line);
            }
        }

        if (pendingInterface is not null)
        {
            // Keep a valid IP/MAC/VLAN binding even if the model wraps or omits its port field.
            items.Add(pendingInterface);
        }

        var result = BuildResult(items, unparsed, raw);
        if (items.Count == 0 && HasExplicitZeroDhcpBindings(raw))
        {
            // A supported command with an explicitly empty binding table is not a parser failure.
            return new ShowParseResult<DhcpBindingRecord>
            {
                Items = result.Items,
                UnparsedLines = result.UnparsedLines,
                RawOutput = raw,
                ParserNote = null,
            };
        }

        return result;
    }

    private static bool TryParseDhcpBindingRow(IReadOnlyList<string> tokens, out DhcpBindingRecord binding)
    {
        binding = new DhcpBindingRecord();
        var macIndex = -1;
        string? mac = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            mac = MacAddressHelper.Normalize(tokens[i]);
            if (mac is not null)
            {
                macIndex = i;
                break;
            }
        }

        if (macIndex < 0 || mac is null)
        {
            return false;
        }

        var ipIndex = -1;
        string? ip = null;
        for (var i = macIndex + 1; i < tokens.Count; i++)
        {
            ip = IpAddressHelper.ExtractFirst(tokens[i]);
            if (ip is not null)
            {
                ipIndex = i;
                break;
            }
        }

        if (ipIndex < 0 || ip is null)
        {
            return false;
        }

        // Field order after the located MAC/IP is Lease, Type, VLAN, Interface.
        // Lease may be numeric, "infinite", or "-", so locate VLAN only after Type.
        var leaseIndex = ipIndex + 1;
        var typeIndex = ipIndex + 2;
        var vlanIndex = -1;
        for (var i = typeIndex + 1; i < tokens.Count; i++)
        {
            if (int.TryParse(tokens[i], out var vlanValue) && vlanValue is > 0 and < 4095)
            {
                vlanIndex = i;
                break;
            }
        }

        binding = new DhcpBindingRecord
        {
            MacAddress = MacAddressHelper.Format(mac),
            IpAddress = ip,
            LeaseSeconds = leaseIndex < tokens.Count ? tokens[leaseIndex] : string.Empty,
            BindingType = typeIndex < tokens.Count ? tokens[typeIndex] : string.Empty,
            Vlan = vlanIndex >= 0 ? tokens[vlanIndex] : string.Empty,
            Port = vlanIndex >= 0 && vlanIndex + 1 < tokens.Count ? Join(tokens, vlanIndex + 1) : string.Empty,
        };
        return true;
    }

    private static bool TryReadInterfaceContinuation(IReadOnlyList<string> tokens, out string port)
    {
        port = string.Empty;
        if (tokens.Count == 0 || !InterfaceNameHelper.TryParse(string.Join(" ", tokens), out _))
        {
            return false;
        }

        port = string.Join(" ", tokens);
        return true;
    }

    private static DhcpBindingRecord CopyWithPort(DhcpBindingRecord binding, string port) => new()
    {
        MacAddress = binding.MacAddress,
        IpAddress = binding.IpAddress,
        LeaseSeconds = binding.LeaseSeconds,
        BindingType = binding.BindingType,
        Vlan = binding.Vlan,
        Port = port,
    };

    private static bool IsDhcpBindingHeaderOrSummary(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("Total number of bindings", StringComparison.OrdinalIgnoreCase) ||
               (trimmed.Contains("MacAddress", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains("IpAddress", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitZeroDhcpBindings(string raw) =>
        Regex.IsMatch(raw ?? string.Empty, @"Total\s+number\s+of\s+bindings\s*:\s*0\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// 合并 LLDP 能力字段：真机 RGOS 把同一份能力写成 supported / enabled 两行，
    /// 内容相同时不要重复显示，不同时用 " / " 拼起来。
    /// </summary>
    private static string MergeCapability(string? current, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return current ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(current) || current.Equals(text, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return current.Contains(text, StringComparison.OrdinalIgnoreCase)
            ? current
            : $"{current} / {text}";
    }

    /// <summary>解析 `show ip interface brief`。</summary>
    public static ShowParseResult<IpInterfaceRecord> ParseIpInterfaceBrief(string raw)
    {
        var items = new List<IpInterfaceRecord>();
        var unparsed = new List<string>();

        // 两种表头（现场都见过）：
        //   ① 经典：Interface  IP-Address      OK? Method Status  Protocol
        //   ② 部分机型：Interface  IP-Address(Pri)  IP-Address(Sec)  Status  Protocol
        // ② 里**没有 OK?/Method 两列**，若照 ① 的位置去取，会把次地址列的 "no address" 切成
        // OK?=no / Method=address —— 设备页就出现过这两个莫名其妙的列（2026-09-24 现场）。
        // 所以先认表头、再按表头决定列位。
        var hasPrimarySecondaryHeader = SplitLines(raw).Any(line =>
            line.Contains("IP-Address(Sec)", StringComparison.OrdinalIgnoreCase)
            || line.Contains("(Pri)", StringComparison.OrdinalIgnoreCase));

        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (!TryReadInterface(tokens, 0, out var name, out var next) || tokens.Count <= next)
            {
                // 表头行（Interface IP-Address OK? Method Status Protocol）
                if (!tokens.Any(t => IpAddressHelper.ExtractFirst(t) is not null))
                {
                    continue;
                }

                unparsed.Add(line);
                continue;
            }

            var ip = IpAddressHelper.ExtractFirst(tokens[next]) ??
                     (tokens[next].Equals("unassigned", StringComparison.OrdinalIgnoreCase) ? "unassigned" : string.Empty);
            if (ip.Length == 0)
            {
                unparsed.Add(line);
                continue;
            }

            // IP 之后的**最后两个 token 一定是 Status / Protocol**（两种表头都成立），
            // 中间那段才是"OK? + Method"或"次地址"。
            // 为什么要这样切：次地址列的值可能是两个词（实机原文是 `no address`），
            // 按固定偏移取会把 `no`/`address` 当成两列，状态列就跟着错位。
            var protocolIndex = tokens.Count - 1;
            var statusIndex = tokens.Count - 2;
            var hasStatusPair = protocolIndex > next + 1;
            var middleEnd = hasStatusPair ? statusIndex : tokens.Count;   // 不含

            var middle = new List<string>();
            for (var i = next + 1; i < middleEnd; i++)
            {
                middle.Add(tokens[i]);
            }

            items.Add(new IpInterfaceRecord
            {
                Interface = name,
                IpAddress = ip,
                // 新机型：中间那一段整体就是次地址（如 `no address`）
                SecondaryAddress = hasPrimarySecondaryHeader ? string.Join(' ', middle) : string.Empty,
                // 经典机型：中间两列是 OK? / Method
                Ok = hasPrimarySecondaryHeader ? string.Empty : middle.ElementAtOrDefault(0) ?? string.Empty,
                Method = hasPrimarySecondaryHeader ? string.Empty : middle.ElementAtOrDefault(1) ?? string.Empty,
                Status = hasStatusPair ? tokens[statusIndex] : string.Empty,
                Protocol = hasStatusPair ? tokens[protocolIndex] : string.Empty,
            });
        }

        return BuildResult(items, unparsed, raw);
    }

    /// <summary>解析 `show version` 的关键字段（解析不出来的内容保留在原始输出里）。</summary>
    public static DeviceVersionInfo ParseVersion(string raw)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(raw))
        {
            if (IsNoise(line))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 0)
            {
                fields[key] = value;
            }
        }

        var description = Get(fields, "system description") ?? Get(fields, "system descr") ?? string.Empty;
        return new DeviceVersionInfo
        {
            SystemDescription = description,
            Model = ExtractModel(description),
            SoftwareVersion = Get(fields, "system software version") ?? Get(fields, "software version") ?? string.Empty,
            HardwareVersion = Get(fields, "system hardware version") ?? Get(fields, "hardware version") ?? string.Empty,
            BootVersion = Get(fields, "system boot version") ?? Get(fields, "boot version") ?? string.Empty,
            Uptime = Get(fields, "system uptime") ?? Get(fields, "uptime") ?? string.Empty,
            // 真机主字段是 `System serial number`（device-real.png.raw.txt 实测）。这里是**精确键**匹配，
            // 旧写法只找 "serial number" → 只有那些恰好带 Device-1 嵌套段（里面有 `Serial number`）的机型
            // 才侥幸取到值；没有嵌套段就整列空白。
            SerialNumber = Get(fields, "system serial number")
                           ?? Get(fields, "serial number")
                           ?? Get(fields, "device serial number")
                           ?? string.Empty,
        };
    }

    /// <summary>从 running-config 中提取 hostname。</summary>
    public static string? TryExtractHostname(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (var line in SplitLines(raw))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = trimmed["hostname ".Length..].Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// 从输出末尾的提示符（如 “Example-SW#”“Example-SW(config)#”）提取设备名。
    /// 任何 show 输出都可以顺手拿到设备名，不需要额外命令。
    /// </summary>
    public static string? TryExtractHostnameFromPrompt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = SplitLines(raw).Reverse();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var match = HostnamePromptRegex().Match(trimmed);
            if (match.Success)
            {
                return match.Groups["name"].Value;
            }

            // 只看最后几行，避免把输出内容里的 “#” 误判为提示符
            if (trimmed.Length > 0 && !trimmed.EndsWith('#') && !trimmed.EndsWith('>'))
            {
                break;
            }
        }

        return null;
    }

    private static string? Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static string ExtractModel(string description)
    {
        var open = description.LastIndexOf('(');
        var close = description.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            return description[(open + 1)..close].Trim();
        }

        return !string.IsNullOrWhiteSpace(description) && description.Length <= 32 ? description : string.Empty;
    }

    /// <summary>读取接口名：支持 Gi0/1、GigabitEthernet 0/1、VLAN 100、AggregatePort 1。</summary>
    private static bool TryReadInterface(IReadOnlyList<string> tokens, int start, out string name, out int next)
    {
        name = string.Empty;
        next = start;
        if (tokens.Count <= start)
        {
            return false;
        }

        var head = tokens[start];
        var cleaned = head.TrimEnd(',', ';');

        // 无空格写法：VLAN100 / AggregatePort128 / Ag128（设备接口列可能这么显示）
        var compact = CompactInterfaceRegex().Match(cleaned);
        if (compact.Success)
        {
            var kind = compact.Groups["kind"].Value;
            var number = compact.Groups["num"].Value;
            name = kind.StartsWith("VLAN", StringComparison.OrdinalIgnoreCase)
                ? $"VLAN {number}"
                : $"AggregatePort {number}";
            next = start + 1;
            return true;
        }

        if ((head.Equals("VLAN", StringComparison.OrdinalIgnoreCase) ||
             head.Equals("Vlan", StringComparison.OrdinalIgnoreCase) ||
             head.StartsWith("VLAN", StringComparison.OrdinalIgnoreCase)) &&
            tokens.Count > start + 1 &&
            int.TryParse(tokens[start + 1], out _))
        {
            name = $"VLAN {tokens[start + 1]}";
            next = start + 2;
            return true;
        }

        if ((head.Equals("AggregatePort", StringComparison.OrdinalIgnoreCase) || head.Equals("Ag", StringComparison.OrdinalIgnoreCase)) &&
            tokens.Count > start + 1 &&
            int.TryParse(tokens[start + 1], out _))
        {
            name = $"AggregatePort {tokens[start + 1]}";
            next = start + 2;
            return true;
        }

        return TryReadPort(tokens, start, out name, out next);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<name>[A-Za-z0-9_.\-]{1,64})(?:\([^)]*\))?[#>]$")]
    private static partial System.Text.RegularExpressions.Regex HostnamePromptRegex();

    /// <summary>无空格的聚合口 / VLAN 接口写法：AggregatePort128、Ag128、VLAN100。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<kind>VLAN|Vlan|vlan|AggregatePort|aggregateport|Ag|ag)(?<num>\d+)$")]
    private static partial System.Text.RegularExpressions.Regex CompactInterfaceRegex();

    private enum TrunkSection
    {
        Unknown,
        Mode,
        /// <summary>简表：Interface / Native VLAN / VLAN lists（真机 show int trunk 常见）。</summary>
        Short,
        Allowed,
        Active,
        Forwarding,
    }

    private static TrunkSection SectionKind(string section)
    {
        if (section.Length == 0)
        {
            return TrunkSection.Unknown;
        }

        if (section.Contains("mode", StringComparison.Ordinal))
        {
            return TrunkSection.Mode;
        }

        // 简表表头：Interface | Native VLAN | VLAN lists
        if (section.Contains("vlan lists", StringComparison.Ordinal))
        {
            return TrunkSection.Short;
        }

        if (section.Contains("allowed on trunk", StringComparison.Ordinal))
        {
            return TrunkSection.Allowed;
        }

        if (section.Contains("allowed and active", StringComparison.Ordinal))
        {
            return TrunkSection.Active;
        }

        if (section.Contains("forwarding state", StringComparison.Ordinal))
        {
            return TrunkSection.Forwarding;
        }

        return TrunkSection.Unknown;
    }

    private static bool IsSectionHeader(IReadOnlyList<string> tokens)
    {
        var first = tokens[0].ToLowerInvariant();
        if (first is not ("interface" or "port"))
        {
            return false;
        }

        return !InterfaceNameHelper.TryParse(tokens[0], out _) &&
               !(tokens.Count > 1 && InterfaceNameHelper.TryParse(tokens[0] + tokens[1], out _));
    }

    private static void FlushBlock(LldpNeighborRecord? current, StringBuilder block, ICollection<LldpNeighborRecord> items)
    {
        if (current is null)
        {
            return;
        }

        current.RawBlock = block.ToString().TrimEnd();
        items.Add(current);
        block.Clear();
    }

    private static void ApplyKeyValue(LldpNeighborRecord record, string line)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim().ToLowerInvariant();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "neighbor index":
                record.NeighborIndex = value;
                break;
            case "update time":
                record.UpdateTime = value;
                break;
            case "chassis id":
                record.ChassisId = value;
                break;
            case "chassis type":
                record.ChassisType = value;
                break;
            case "port type":
                record.PortType = value;
                break;
            case "port id":
                record.NeighborPort = value;
                break;
            case "port description":
                record.PortDescription = value;
                break;
            case "system name":
                record.NeighborDevice = value;
                break;
            case "system description":
                record.SystemDescription = value;
                break;
            case "management address":
                record.ManagementIp = IpAddressHelper.ExtractFirst(value) ?? string.Empty;
                break;
            // Capability：不同型号写法不同（Capability / System capabilities / Enabled capabilities），
            // 一律收进同一个字段；只解析出“字符串能力”时按原文展示，不做猜测映射。
            case "capability":
            case "capabilities":
            case "system capabilities":
            case "system capability":
            case "enabled capabilities":
            case "enabled capability":
            // 真机（RGOS）实际写法带后缀，旧 case 列表里没有 → 能力列恒空：
            //   System capabilities supported     : Bridge, Router
            //   System capabilities enabled       : Bridge, Router
            case "system capabilities supported":
            case "system capabilities enabled":
                record.Capability = MergeCapability(record.Capability, value);
                break;
            // 老化时间：表格式写作 Aging-time，个别固件在 detail 里带 Aging time
            case "aging time":
            case "aging-time":
                record.AgingTime = value;
                record.HoldTime = value;
                break;
        }
    }

    private static string ExtractLocalPort(string line)
    {
        var open = line.IndexOf('[', StringComparison.Ordinal);
        var close = line.IndexOf(']', StringComparison.Ordinal);
        if (open >= 0 && close > open)
        {
            var inner = line[(open + 1)..close].Trim();
            if (InterfaceNameHelper.TryParse(inner.Replace(" ", string.Empty, StringComparison.Ordinal), out _))
            {
                return inner;
            }

            return inner;
        }

        var tokens = Tokenize(line);
        var index = tokens.FindIndex(t => t.Equals("port", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && tokens.Count > index + 1)
        {
            return tokens[index + 1].TrimEnd(':');
        }

        return string.Empty;
    }

    private static bool TryReadPort(IReadOnlyList<string> tokens, int start, out string port, out int next)
    {
        port = string.Empty;
        next = start;
        if (tokens.Count <= start)
        {
            return false;
        }

        // 端口列表里常带逗号（“Te0/27, Te0/28”），解析前先去掉
        var head = tokens[start].TrimEnd(',', ';');
        if (head.Length > 0 && InterfaceNameHelper.TryParse(head, out _))
        {
            port = head;
            next = start + 1;
            return true;
        }

        if (tokens.Count > start + 1 &&
            InterfaceNameHelper.TryParse(head + tokens[start + 1].TrimEnd(',', ';'), out _))
        {
            port = $"{tokens[start]} {tokens[start + 1]}";
            next = start + 2;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析 “System Name | Local Intf | Port ID | Capability | Aging-time” 表格式 LLDP 邻居
    /// （真机 show lldp neighbors 常见输出）。列以表头位置切分，列没对齐时退回按 token 解析。
    /// </summary>
    private static List<LldpNeighborRecord> TryParseLldpSystemTable(IReadOnlyList<string> lines)
    {
        var results = new List<LldpNeighborRecord>();

        for (var i = 0; i < lines.Count; i++)
        {
            var header = lines[i];
            var nameIndex = header.IndexOf("System Name", StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0)
            {
                continue;
            }

            var localIndex = Math.Max(
                header.IndexOf("Local Intf", StringComparison.OrdinalIgnoreCase),
                header.IndexOf("Local Interface", StringComparison.OrdinalIgnoreCase));
            var portIndex = Math.Max(
                header.IndexOf("Port ID", StringComparison.OrdinalIgnoreCase),
                header.IndexOf("Neighbor Intf", StringComparison.OrdinalIgnoreCase));
            if (localIndex < 0 || portIndex < 0 || portIndex <= localIndex)
            {
                continue;
            }

            var capabilityIndex = header.IndexOf("Capability", StringComparison.OrdinalIgnoreCase);
            var agingIndex = header.IndexOf("Aging", StringComparison.OrdinalIgnoreCase);

            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line) || IsNoise(line))
                {
                    continue;
                }

                if (line.TrimStart().StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                var record = ParseLldpSystemRow(line, nameIndex, localIndex, portIndex, capabilityIndex, agingIndex);
                if (record is null)
                {
                    break;
                }

                results.Add(record);
            }

            break;
        }

        return results;
    }

    private static LldpNeighborRecord? ParseLldpSystemRow(
        string line,
        int nameIndex,
        int localIndex,
        int portIndex,
        int capabilityIndex,
        int agingIndex)
    {
        string Slice(int start, int end)
        {
            if (start < 0 || start >= line.Length)
            {
                return string.Empty;
            }

            var stop = end > start && end <= line.Length ? end : line.Length;
            return line[start..stop].Trim();
        }

        var device = Slice(nameIndex, localIndex);
        var local = Slice(localIndex, portIndex);
        var capabilityEnd = capabilityIndex > portIndex ? capabilityIndex : agingIndex;
        var port = Slice(portIndex, capabilityEnd);
        var capability = capabilityIndex > portIndex ? Slice(capabilityIndex, agingIndex) : string.Empty;
        var aging = agingIndex > portIndex ? Slice(agingIndex, -1) : string.Empty;

        if (device.Length == 0 || !TryReadInterface(Tokenize(local), 0, out _, out _))
        {
            return ParseLldpSystemRowByTokens(line);
        }

        return new LldpNeighborRecord
        {
            LocalPort = local,
            NeighborDevice = device,
            NeighborPort = port,
            Capability = capability,
            AgingTime = aging,
            HoldTime = aging,
            RawBlock = line.Trim(),
        };
    }

    /// <summary>列没对齐时的兜底：第 1 个接口名前是设备名，之后依次是邻居端口与老化时间。</summary>
    private static LldpNeighborRecord? ParseLldpSystemRowByTokens(string line)
    {
        var tokens = Tokenize(line);
        if (tokens.Count < 3)
        {
            return null;
        }

        var localIndex = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (TryReadInterface(tokens, i, out _, out _))
            {
                localIndex = i;
                break;
            }
        }

        if (localIndex <= 0)
        {
            return null;
        }

        var portIndex = -1;
        for (var i = localIndex + 1; i < tokens.Count; i++)
        {
            if (TryReadInterface(tokens, i, out _, out _))
            {
                portIndex = i;
                break;
            }
        }

        if (portIndex < 0)
        {
            return null;
        }

        // 老化时间列以数字开头，例如 “1minutes 35seconds”
        var agingIndex = -1;
        for (var i = portIndex + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Length > 0 && char.IsDigit(tokens[i][0]))
            {
                agingIndex = i;
                break;
            }
        }

        TryReadInterface(tokens, localIndex, out var local, out _);
        TryReadInterface(tokens, portIndex, out var port, out _);

        // 邻居端口与老化时间之间的 token 就是 Capability（例如 “B, R”）
        var capabilityEnd = agingIndex > portIndex ? agingIndex : tokens.Count;
        var capability = portIndex + 1 < capabilityEnd
            ? string.Join(" ", tokens.Skip(portIndex + 1).Take(capabilityEnd - portIndex - 1))
            : string.Empty;
        var aging = agingIndex > 0 ? string.Join(" ", tokens.Skip(agingIndex)) : string.Empty;

        return new LldpNeighborRecord
        {
            LocalPort = local,
            NeighborDevice = string.Join(" ", tokens.Take(localIndex)),
            NeighborPort = port,
            Capability = capability,
            AgingTime = aging,
            HoldTime = aging,
            RawBlock = line.Trim(),
        };
    }

    /// <summary>整行都是端口名（真机 VLAN 端口续行，例如 “Te0/27, Te0/28, Ag128”）。</summary>
    private static bool LooksLikePortList(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            var single = new[] { token };
            if (!TryReadInterface(single, 0, out _, out var next) || next < 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 端口在字典里的键。用 <see cref="InterfaceNameHelper.IdentityKey(string)"/> 而不是 ToString：
    /// 同一段落/不同段落把同一个聚合口写成 `AggregatePort 128` 与 `Ag128` 时必须等价，
    /// 否则会在 Trunk 表里裂成两行、字段各自缺失（详见 IdentityKey 的注释）。
    /// 展示给用户的 Port 文本仍保留设备原文。
    /// </summary>
    private static string NormalizePortKey(string port) => InterfaceNameHelper.IdentityKey(port);

    private static string Join(IReadOnlyList<string> tokens, int start) =>
        tokens.Count <= start ? string.Empty : string.Join(" ", tokens.Skip(start));

    /// <summary>整行是不是 VLAN 列表（续行判断用）。</summary>
    private static bool LooksLikeVlanList(IReadOnlyList<string> tokens) =>
        tokens.Count > 0 && tokens.All(t => VlanListTokenRegex().IsMatch(t));

    private static string AppendList(string existing, string extra) =>
        string.IsNullOrWhiteSpace(existing)
            ? extra
            : existing.TrimEnd().TrimEnd(',') + ", " + extra;

    /// <summary>形如 1 / 1-4094 / 1,100,200 / 200-202,210（可带尾逗号）。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*,?$")]
    private static partial System.Text.RegularExpressions.Regex VlanListTokenRegex();

    private static string TokenOrEmpty(IReadOnlyList<string> tokens, int index) =>
        index >= 0 && index < tokens.Count ? tokens[index] : string.Empty;

    private static bool IsHeader(IReadOnlyList<string> tokens, string firstKeyword, string secondKeyword)
    {
        if (tokens.Count == 0 || !tokens[0].Equals(firstKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (secondKeyword.Length == 0)
        {
            return true;
        }

        return tokens.Any(t => t.Contains(secondKeyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNoise(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (IsPromptOrEcho(trimmed))
        {
            return true;
        }

        return trimmed.All(c => c is '-' or '=' or '+' or '*' or ' ' or '_' or '~');
    }

    /// <summary>过滤设备提示符（Ruijie# / Ruijie(config)#）与命令回显行，避免它们被当成未识别行。</summary>
    private static bool IsPromptOrEcho(string trimmed)
    {
        // 以 # 开头的行是命令行回显/注释（例如 CommandService 的 “# show vlan” 记录格式）
        if (trimmed.StartsWith('#'))
        {
            return true;
        }

        if (trimmed.Length <= 48)
        {
            var marker = trimmed.IndexOfAny(new[] { '#', '>' });
            if (marker > 0 && !trimmed[..marker].Contains(' ', StringComparison.Ordinal))
            {
                if (marker == trimmed.Length - 1)
                {
                    return true;
                }

                var rest = trimmed[(marker + 1)..].TrimStart();
                if (rest.StartsWith("show ", StringComparison.OrdinalIgnoreCase) ||
                    rest.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    rest.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // 纯命令回显（有些终端会把用户输入一起回显到输出里）
        return trimmed.Length <= 60 && trimmed.StartsWith("show ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析入口统一走 CLI 归一化：去 ANSI 转义、处理退格、统一 CR/LF/CRLF、去掉 --More-- 残留。
    /// 原始输出不会被修改（它保存在 RawOutput 与会话文件里）。
    /// </summary>
    private static IEnumerable<string> SplitLines(string raw) =>
        CliOutputNormalizer.Normalize(raw).Split('\n');

    private static List<string> Tokenize(string line) =>
        line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static ShowParseResult<T> BuildResult<T>(List<T> items, List<string> unparsed, string raw) => new()
    {
        Items = items,
        UnparsedLines = unparsed,
        RawOutput = raw,
        ParserNote = items.Count == 0
            ? "输出格式与当前解析规则不一致：已完整保留原始输出，未做任何猜测解析。"
            : null,
    };
}
