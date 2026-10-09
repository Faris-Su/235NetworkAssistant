using System.Collections.ObjectModel;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// MAC / IP 页面（Phase 4）：
/// MAC → VLAN → Port → Status；IP → MAC → VLAN → Port（依赖 DHCP Snooping Binding）。
/// 一次刷新后在本地过滤，避免每次输入都向设备发命令；原始输出始终保留。
/// </summary>
public sealed class MacIpViewModel : DeviceShowPageViewModel
{
    private readonly IResourceRepository _repository;
    private string _queryText = string.Empty;
    private string _queryResultText = "输入 MAC 或 IP 后点[查询]（先用上面的刷新按钮读取设备数据）。";
    private string _macSummary = "尚未读取 MAC 地址表。";
    private string _bindingSummary = "尚未读取 DHCP Snooping 绑定表。";
    private string _arpSummary = "尚未读取 ARP 表。";
    private bool _hasQueryResult;

    public MacIpViewModel(
        IShellNavigator shell,
        ConnectionService connections,
        CommandService commands,
        IResourceRepository repository)
        : base(shell, connections, commands)
    {
        _repository = repository;
        Title = "MAC / IP";
        RegisterCachedCollection(MacTable);       // 断开/换设备时清空 MAC 表
        RegisterCachedCollection(Bindings);       // 断开/换设备时清空 DHCP 绑定表
        RegisterCachedCollection(ArpTable);       // 断开/换设备时清空 ARP 表

        RefreshMacCommand = new AsyncRelayCommand(RefreshMacAsync, () => IsConnected && !IsBusy);
        RefreshBindingCommand = new AsyncRelayCommand(RefreshBindingAsync, () => IsConnected && !IsBusy);
        RefreshArpCommand = new AsyncRelayCommand(RefreshArpAsync, () => IsConnected && !IsBusy);
        QueryCommand = new RelayCommand(Query);
        CopyRawCommand = new RelayCommand(CopyRaw);
        OpenResourceLibraryCommand = new RelayCommand(OpenResourceLibrary);

        Connections.SessionChanged += (_, _) =>
        {
            RefreshMacCommand.RaiseCanExecuteChanged();
            RefreshBindingCommand.RaiseCanExecuteChanged();
            RefreshArpCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>两个刷新命令都依赖 IsBusy：忙碌状态一变就要刷新按钮可用状态。</summary>
    protected override void OnBusyChanged()
    {
        RefreshMacCommand.RaiseCanExecuteChanged();
        RefreshBindingCommand.RaiseCanExecuteChanged();
        RefreshArpCommand.RaiseCanExecuteChanged();
    }

    // 用 BulkObservableCollection：MAC 表在核心机上能到 1 万+ 行，
    // 逐条 Add 会让 DataGrid 处理上万次通知（"读完卡几秒"），ReplaceAll 只发一次。
    public Helpers.BulkObservableCollection<MacAddressRecord> MacTable { get; } = new();

    public Helpers.BulkObservableCollection<DhcpBindingRecord> Bindings { get; } = new();

    // ARP 表：静态 IP（服务器 / 打印机 / AP / 教师机）不在 DHCP 绑定表里，只有这里有。
    public Helpers.BulkObservableCollection<ArpRecord> ArpTable { get; } = new();

    public string ArpSummary
    {
        get => _arpSummary;
        private set => SetProperty(ref _arpSummary, value);
    }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value);
    }

    public string QueryResultText
    {
        get => _queryResultText;
        private set => SetProperty(ref _queryResultText, value);
    }

    public bool HasQueryResult
    {
        get => _hasQueryResult;
        private set => SetProperty(ref _hasQueryResult, value);
    }

    public string MacSummary
    {
        get => _macSummary;
        private set => SetProperty(ref _macSummary, value);
    }

    public string BindingSummary
    {
        get => _bindingSummary;
        private set => SetProperty(ref _bindingSummary, value);
    }

    public string HintText =>
        "实时查询设备表：IP 优先匹配 ARP，也会检查 DHCP Snooping；完整原始输出可在下方展开。";

    public AsyncRelayCommand RefreshMacCommand { get; }

    public AsyncRelayCommand RefreshBindingCommand { get; }

    public AsyncRelayCommand RefreshArpCommand { get; }

    public RelayCommand QueryCommand { get; }

    public RelayCommand CopyRawCommand { get; }

    public RelayCommand OpenResourceLibraryCommand { get; }

    /// <summary>刷新（同时读取 MAC 表与 DHCP Snooping 绑定，供一次性刷新使用）。</summary>
    protected override async Task RefreshAsync()
    {
        await RefreshMacAsync();
        await RefreshArpAsync();
        await RefreshBindingAsync();
    }

    private async Task RefreshMacAsync()
    {
        var raw = await RunShowAsync(ShowCommands.MacAddressTable);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseMacAddressTable(raw);
        MacTable.ReplaceAll(parsed.Items);

        MacSummary = $"MAC 地址表 {parsed.Items.Count} 条｜{parsed.SummaryText}";
        ParseSummary = MacSummary;
        ParserNote = parsed.ParserNote ?? BuildUnsupportedNote(raw, "show mac-address-table") ?? string.Empty;
    }

    /// <summary>
    /// 读设备的 ARP 表。这是"IP 反查"的第二数据源：
    /// DHCP Snooping 绑定表只覆盖动态获取的地址，**静态 IP（服务器/打印机/AP/教师机）只有 ARP 表里有**，
    /// 而且不少接入交换机压根没开 DHCP Snooping。命令 `show arp` 此前已在 ShowCommands 里登记但从未被使用。
    /// </summary>
    private async Task RefreshArpAsync()
    {
        var raw = await RunShowAsync(ShowCommands.Arp);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseArp(raw);
        ArpTable.ReplaceAll(parsed.Items);

        ArpSummary = $"ARP 表 {parsed.Items.Count} 条｜{parsed.SummaryText}";
        ParseSummary = $"{MacSummary}　{ArpSummary}";
        ParserNote = parsed.ParserNote ?? BuildUnsupportedNote(raw, "show arp") ?? ParserNote;
    }

    private async Task RefreshBindingAsync()
    {
        var raw = await RunShowAsync(ShowCommands.DhcpSnoopingBinding);
        if (raw is null)
        {
            return;
        }

        var parsed = ShowOutputParser.ParseDhcpSnoopingBinding(raw);
        Bindings.ReplaceAll(parsed.Items);

        var validEmptyTable = parsed.Items.Count == 0 && parsed.ParserNote is null;
        BindingSummary = $"DHCP Snooping 绑定 {parsed.Items.Count} 条｜" +
                         (validEmptyTable ? "设备返回空表" : parsed.SummaryText);
        ParserNote = parsed.ParserNote ??
                     (parsed.Items.Count == 0 && !validEmptyTable
                         ? "未读取到 DHCP Snooping 绑定：该设备可能不支持此命令，或当前没有绑定表项（原始输出见下方）。"
                         : BuildUnsupportedNote(raw, "show ip dhcp snooping binding")) ??
                     string.Empty;
    }

    /// <summary>本地查询：先用 MAC 表，再用绑定表；IP 查询额外给出资源库规划信息。</summary>
    private void Query()
    {
        var query = QueryText?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            QueryResultText = "请输入 MAC 地址或 IP 地址。";
            HasQueryResult = true;
            return;
        }

        var mac = MacAddressHelper.Normalize(query);
        var ip = IpAddressHelper.ExtractFirst(query);

        if (mac is not null)
        {
            QueryByMac(query, mac);
            return;
        }

        if (ip is not null)
        {
            QueryByIp(ip);
            return;
        }

        QueryResultText = $"无法识别的查询内容：{query}（请输入 MAC 或 IPv4 地址）";
        HasQueryResult = true;
    }

    private void QueryByMac(string query, string normalizedMac)
    {
        var lines = new List<string> { $"查询 MAC：{MacAddressHelper.Format(query)}" };

        var macRows = MacTable.Where(m => MacAddressHelper.IsMatch(m.MacAddress, normalizedMac)).ToList();
        if (macRows.Count == 0)
        {
            lines.Add("MAC 地址表：未找到（可能未学习到、已被老化，或未刷新 MAC 表）");
        }
        else
        {
            foreach (var row in macRows)
            {
                lines.Add($"MAC 地址表：VLAN {row.Vlan}｜Port {row.Port}｜Type {row.Type}");
            }
        }

        var bindings = Bindings.Where(b => MacAddressHelper.IsMatch(b.MacAddress, normalizedMac)).ToList();
        foreach (var binding in bindings)
        {
            lines.Add($"DHCP 绑定：IP {binding.IpAddress}｜VLAN {binding.Vlan}｜Port {binding.Port}｜Lease {binding.LeaseSeconds}s");
            AppendPlanningInfo(lines, binding.IpAddress);
        }

        if (bindings.Count == 0)
        {
            lines.Add("DHCP 绑定：无对应记录（该设备可能不支持 DHCP Snooping Binding）");
        }

        // 反向：ARP 表能告诉我们这个 MAC 现在用的是哪个 IP（静态地址的设备只有这里有）
        var arpRows = ArpTable.Where(a => MacAddressHelper.IsMatch(a.MacAddress, normalizedMac)).ToList();
        foreach (var arp in arpRows)
        {
            lines.Add($"ARP 表：IP {arp.IpAddress}｜接口 {arp.Interface}｜老化 {arp.Age}");
            AppendPlanningInfo(lines, arp.IpAddress);
        }

        if (arpRows.Count == 0)
        {
            lines.Add(ArpTable.Count == 0
                ? "ARP 表：尚未读取（点[刷新 ARP 表]可查该 MAC 对应的 IP）"
                : "ARP 表：没有这个 MAC（它当前不在设备 ARP 缓存里）");
        }

        FinishQuery(lines, query);
    }

    private void QueryByIp(string ip)
    {
        var lines = new List<string> { $"查询 IP：{ip}" };

        // ① ARP 表：静态 IP 的唯一来源（服务器/打印机/AP）。先看它，因为 DHCP 表覆盖不到这类设备。
        var arpRows = ArpTable
            .Where(a => string.Equals(a.IpAddress, ip, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (arpRows.Count > 0)
        {
            foreach (var arp in arpRows)
            {
                lines.Add($"ARP 表：MAC {arp.MacAddress}｜接口 {arp.Interface}｜老化 {arp.Age}");

                // ARP 给出 MAC 之后，再去 MAC 地址表定位**具体物理端口**（ARP 只能到 VLAN 接口级）
                var ports = MacTable
                    .Where(m => MacAddressHelper.IsMatch(m.MacAddress, arp.MacAddress))
                    .ToList();
                if (ports.Count > 0)
                {
                    foreach (var row in ports)
                    {
                        lines.Add($"MAC 地址表：VLAN {row.Vlan}｜Port {row.Port}｜Type {row.Type}");
                    }
                }
                else
                {
                    lines.Add("MAC 地址表：未找到该 MAC（可先点[刷新 MAC 表]；" +
                              "若这台设备是网关/本机条目，MAC 表里本来就不会有）");
                }
            }
        }
        else if (ArpTable.Count == 0)
        {
            lines.Add("ARP 表：尚未读取（点[刷新 ARP 表]可查静态 IP 的设备 —— 服务器 / 打印机 / AP 等）");
        }
        else
        {
            lines.Add($"ARP 表：{ArpTable.Count} 条里没有这个 IP（该地址当前不在设备的 ARP 缓存里，可能已老化）");
        }

        // ② DHCP Snooping 绑定表：动态获取的地址走这条，能看到租约与接入端口
        var bindings = Bindings.Where(b => string.Equals(b.IpAddress, ip, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bindings.Count == 0)
        {
            lines.Add(Bindings.Count == 0
                ? "DHCP Snooping 绑定：尚未读取（点[刷新 DHCP Snooping]）"
                : "DHCP Snooping 绑定：未找到该 IP（说明它不是通过 DHCP 获取的，看上面的 ARP 表结果）");
        }

        foreach (var binding in bindings)
        {
            lines.Add($"DHCP 绑定：MAC {binding.MacAddress}｜VLAN {binding.Vlan}｜Port {binding.Port}｜Type {binding.BindingType}");

            var macRows = MacTable.Where(m => MacAddressHelper.IsMatch(m.MacAddress, binding.MacAddress)).ToList();
            if (macRows.Count > 0)
            {
                foreach (var row in macRows)
                {
                    lines.Add($"MAC 地址表：VLAN {row.Vlan}｜Port {row.Port}｜Type {row.Type}");
                }
            }
            else
            {
                lines.Add("MAC 地址表：未找到该 MAC（可先刷新 MAC 表）");
            }
        }

        AppendPlanningInfo(lines, ip);
        FinishQuery(lines, ip);
    }

    /// <summary>结合本地资源库（规划数据）给出该 IP 所属 VLAN / 网关 / 掩码 / 位置。</summary>
    private void AppendPlanningInfo(ICollection<string> lines, string ip)
    {
        var match = _repository.FindVlanByIp(ip);
        if (match is null)
        {
            lines.Add($"资源库规划信息：未命中（本地库共 {_repository.Database.VlanCount} 条 VLAN 记录）");
            return;
        }

        var record = match.Record;
        lines.Add(
            $"资源库规划信息：VLAN {record.VlanId}｜网关 {Text(record.Gateway)}｜掩码 {Text(record.Mask)}｜" +
            $"位置 {Text(record.LocationText)}｜来源 {record.SourceSheet}");

        if (!match.IsConsistent)
        {
            lines.Add("注意：该结果仅由原表 IP 范围命中，与网关/掩码推算的网段不一致，请核对原表。");
        }
    }

    private void FinishQuery(List<string> lines, string query)
    {
        QueryResultText = string.Join(Environment.NewLine, lines);
        HasQueryResult = true;
        Shell.ReportStatus($"MAC/IP 查询：{query}");
        Shell.AddRecentOperation("MAC/IP 查询", query);
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string? BuildUnsupportedNote(string raw, string command)
    {
        var lowered = raw.ToLowerInvariant();
        if (lowered.Contains("invalid input") || lowered.Contains("unknown command") || lowered.Contains("unrecognized"))
        {
            return $"设备未接受命令 {command}（原始输出见下方），可能是该型号/固件不支持。";
        }

        return null;
    }

    private void OpenResourceLibrary()
    {
        var ip = IpAddressHelper.ExtractFirst(QueryText);
        Shell.NavigateTo("resources", ip);
    }

    private void CopyRaw()
    {
        StatusHint = AppServices.Clipboard.TrySetText(RawOutput ?? string.Empty, out var error)
            ? "原始输出已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}
