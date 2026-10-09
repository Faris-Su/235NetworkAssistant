using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// Show Center（Phase 4）：常用 show 命令集中入口。
/// 能可靠解析的做结构化显示，其余直接显示原始文本（不做脆弱解析）。
/// </summary>
public sealed class DeviceInfoViewModel : DeviceShowPageViewModel
{
    private ShowCenterCategory _selectedCategory;
    private string _structuredText = "选择命令后点[刷新]，结构化结果会显示在这里。";
    private string _modeNote = "可解析的命令显示为表格，不可靠解析的命令直接显示原始文本。";

    public DeviceInfoViewModel(IShellNavigator shell, ConnectionService connections, CommandService commands)
        : base(shell, connections, commands)
    {
        Title = "设备";
        Categories = ShowCenterCategory.All;
        _selectedCategory = Categories[0];

        // 断开 / 连到别的设备时，这四张表要立刻清空（现场要求：设备页也算"上一台设备的数据"）
        RegisterCachedCollection(Ports);
        RegisterCachedCollection(Vlans);
        RegisterCachedCollection(MacTable);
        RegisterCachedCollection(IpInterfaces);

        CopyRawCommand = new RelayCommand(CopyRaw);
        CopyStructuredCommand = new RelayCommand(CopyStructured);
    }

    public IReadOnlyList<ShowCenterCategory> Categories { get; }

    public ShowCenterCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(SelectedCommandText));
                StatusHint = $"已选择：{value.Title}（{value.Command}），点[刷新]执行。";
            }
        }
    }

    public string SelectedCommandText => $"将执行：{SelectedCategory.Command}";

    // 这几个大表都改成 BulkObservableCollection：设备页一次刷新的行数可能上万
    //（核心机 MAC 表 1 万+），逐条 Add 的通知量全落在 UI 线程上。
    public Helpers.BulkObservableCollection<PortStatusRecord> Ports { get; } = new();

    public Helpers.BulkObservableCollection<VlanInfoRecord> Vlans { get; } = new();

    public Helpers.BulkObservableCollection<MacAddressRecord> MacTable { get; } = new();

    public Helpers.BulkObservableCollection<IpInterfaceRecord> IpInterfaces { get; } = new();

    public string StructuredText
    {
        get => _structuredText;
        private set => SetProperty(ref _structuredText, value);
    }

    public string ModeNote
    {
        get => _modeNote;
        private set => SetProperty(ref _modeNote, value);
    }

    public bool ShowPortsGrid => Ports.Count > 0;

    public bool ShowVlanGrid => Vlans.Count > 0;

    public bool ShowMacGrid => MacTable.Count > 0;

    public bool ShowIpInterfaceGrid => IpInterfaces.Count > 0;

    public bool ShowStructuredText => !string.IsNullOrWhiteSpace(StructuredText) && SelectedCategory.Mode != ShowCenterMode.Grid;

    public RelayCommand CopyRawCommand { get; }

    public RelayCommand CopyStructuredCommand { get; }

    protected override Task RefreshAsync()
    {
        var category = SelectedCategory;
        StatusHint = $"正在执行 {category.Command} …";
        return category.Key switch
        {
            ShowCenterCategoryKeys.Basic => RefreshBasicAsync(),
            ShowCenterCategoryKeys.InterfaceStatus => RefreshInterfaceStatusAsync(),
            ShowCenterCategoryKeys.Vlan => RefreshVlanAsync(),
            ShowCenterCategoryKeys.Mac => RefreshMacAsync(),
            ShowCenterCategoryKeys.IpInterface => RefreshIpInterfaceAsync(),
            _ => RefreshRawOnlyAsync(category),
        };
    }

    private void ResetCollections()
    {
        Ports.Clear();
        Vlans.Clear();
        MacTable.Clear();
        IpInterfaces.Clear();
        NotifyGrids();
    }

    private void NotifyGrids()
    {
        OnPropertyChanged(nameof(ShowPortsGrid));
        OnPropertyChanged(nameof(ShowVlanGrid));
        OnPropertyChanged(nameof(ShowMacGrid));
        OnPropertyChanged(nameof(ShowIpInterfaceGrid));
        OnPropertyChanged(nameof(ShowStructuredText));
    }

    private async Task RefreshBasicAsync()
    {
        var raw = await RunShowAsync(ShowCommands.Version);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        var info = ShowOutputParser.ParseVersion(raw);
        StructuredText = info.HasAny
            ? info.ToDisplayText()
            : "未解析出结构化信息（设备输出格式与预期不同），请查看下方原始输出。";
        ModeNote = info.HasAny
            ? "show version 关键字段（设备名可在【设备】页刷新运行配置或任意 show 后自动回填到顶部状态栏）。"
            : "解析失败：已完整保留原始输出。";
        ParseSummary = info.HasAny ? $"设备信息 {CountFields(info)} 项" : "未解析出结构化信息";
        ParserNote = info.HasAny ? string.Empty : "show version 输出格式与预期不同，已保留原始输出。";
        NotifyGrids();
    }

    private async Task RefreshInterfaceStatusAsync()
    {
        var raw = await RunShowAsync(ShowCommands.InterfaceStatus);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        var parsed = ShowOutputParser.ParseInterfaceStatus(raw);
        Ports.ReplaceAll(parsed.Items);

        StructuredText = parsed.Items.Count == 0 ? string.Empty : $"接口状态 {parsed.Items.Count} 行。";
        ModeNote = "端口状态也可在【端口】页进行批量配置。";
        ParseSummary = parsed.SummaryText;
        ParserNote = parsed.ParserNote ?? string.Empty;
        NotifyGrids();
    }

    private async Task RefreshVlanAsync()
    {
        var raw = await RunShowAsync(ShowCommands.Vlan);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        var parsed = ShowOutputParser.ParseVlan(raw);
        Vlans.ReplaceAll(parsed.Items);

        StructuredText = parsed.Items.Count == 0 ? string.Empty : $"VLAN {parsed.Items.Count} 个。";
        ModeNote = "VLAN 的创建 / 删除请在【VLAN】页操作（会生成命令预览）。";
        ParseSummary = parsed.SummaryText;
        ParserNote = parsed.ParserNote ?? string.Empty;
        NotifyGrids();
    }

    private async Task RefreshMacAsync()
    {
        var raw = await RunShowAsync(ShowCommands.MacAddressTable);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        var parsed = ShowOutputParser.ParseMacAddressTable(raw);
        MacTable.ReplaceAll(parsed.Items);

        StructuredText = parsed.Items.Count == 0 ? string.Empty : $"MAC 表项 {parsed.Items.Count} 条。";
        ModeNote = "按 MAC / IP 查询请到【MAC/IP】页。";
        ParseSummary = parsed.SummaryText;
        ParserNote = parsed.ParserNote ?? string.Empty;
        NotifyGrids();
    }

    private async Task RefreshIpInterfaceAsync()
    {
        var raw = await RunShowAsync(ShowCommands.InterfaceBrief);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        var parsed = ShowOutputParser.ParseIpInterfaceBrief(raw);
        IpInterfaces.ReplaceAll(parsed.Items);

        // 把读到的管理 IP 回填给会话：概览页「管理 IP」会显示它（配置线场景就靠这个 ——
        // 用户明确要求"别自动发命令"，所以只在**用户自己读了这一页**之后才回填）。
        var managementIp = PickManagementIp(parsed.Items);
        if (managementIp is not null)
        {
            Connections.UpdateManagementAddress(managementIp);
        }

        StructuredText = parsed.Items.Count == 0 ? string.Empty : $"三层接口 {parsed.Items.Count} 个。";
        ModeNote = "本页只读：三层接口 / SVI 的配置修改不在本工具范围内（要改请到 CLI 或设备 Web 界面）。";
        ParseSummary = parsed.SummaryText;
        ParserNote = parsed.ParserNote ?? string.Empty;
        NotifyGrids();
    }

    /// <summary>
    /// 从三层接口表里挑一个"这台设备的管理 IP"（没有就返回 null）。
    ///
    /// 实现已挪到 <see cref="ManagementAddressReader.PickIp"/>：同一个动作现在有两个入口
    /// （【设备】页读「三层接口」、【概览】页管理 IP 的[获取]按钮），挑法必须只有一份。
    /// 这个方法保留成薄壳，是为了不改动既有自检与调用点。
    /// </summary>
    internal static string? PickManagementIp(IReadOnlyList<IpInterfaceRecord> rows)
        => ManagementAddressReader.PickIp(rows);

    private async Task RefreshRawOnlyAsync(ShowCenterCategory category)
    {
        var raw = await RunShowAsync(category.Command);
        if (raw is null)
        {
            return;
        }

        ResetCollections();
        StructuredText = string.Empty;
        ModeNote = category.RawOnlyNote;
        ParseSummary = $"原始输出 {raw.Length:N0} 字符（该命令不做结构化解析）";
        ParserNote = string.Empty;

        if (string.Equals(category.Command, ShowCommands.RunningConfig, StringComparison.OrdinalIgnoreCase))
        {
            var hostname = ShowOutputParser.TryExtractHostname(raw);
            if (hostname is not null)
            {
                Connections.UpdateDeviceIdentity(hostname);
                StructuredText = $"已从 running-config 读取设备名：{hostname}";
                StatusHint = $"已执行 {category.Command}，设备名已更新为 {hostname}";
            }
        }

        NotifyGrids();
    }

    private static int CountFields(DeviceVersionInfo info) =>
        info.ToDisplayText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length;

    private void CopyRaw()
    {
        TryCopy(RawOutput ?? string.Empty, "原始输出");
    }

    private void CopyStructured()
    {
        var text = SelectedCategory.Mode == ShowCenterMode.Grid
            ? BuildGridText()
            : StructuredText;
        TryCopy(text, "结构化结果");
    }

    private string BuildGridText()
    {
        if (Ports.Count > 0)
        {
            return string.Join(Environment.NewLine, Ports.Select(p => $"{p.Port}\t{p.Status}\t{p.Vlan}\t{p.Duplex}\t{p.Speed}\t{p.Type}"));
        }

        if (Vlans.Count > 0)
        {
            return string.Join(Environment.NewLine, Vlans.Select(v => $"{v.VlanId}\t{v.Name}\t{v.Status}\t{v.Ports}"));
        }

        if (MacTable.Count > 0)
        {
            return string.Join(Environment.NewLine, MacTable.Select(m => $"{m.Vlan}\t{m.MacAddress}\t{m.Type}\t{m.Port}"));
        }

        // 两种表头都照顾到：新机型有"次地址"列、没有 OK?/Method；老机型反之。
        var hasSecondary = IpInterfaces.Any(i => !string.IsNullOrWhiteSpace(i.SecondaryAddress));
        return string.Join(
            Environment.NewLine,
            IpInterfaces.Select(i => hasSecondary
                ? $"{i.Interface}\t{i.IpAddress}\t{i.SecondaryText}\t{i.Status}\t{i.Protocol}"
                : $"{i.Interface}\t{i.IpAddress}\t{i.OkMethodText}\t{i.Status}\t{i.Protocol}"));
    }

    private void TryCopy(string text, string what)
    {
        StatusHint = AppServices.Clipboard.TrySetText(text, out var error)
            ? $"{what}已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}

public enum ShowCenterMode
{
    Grid,
    Text,
    RawOnly,
}

public static class ShowCenterCategoryKeys
{
    public const string Basic = "basic";
    public const string InterfaceStatus = "interface-status";
    public const string Vlan = "vlan";
    public const string Mac = "mac";
    public const string IpInterface = "ip-interface";
    public const string Cpu = "cpu";
    public const string Logs = "logs";
    public const string RunningConfig = "running-config";
}

/// <summary>Show Center 的一个条目（命令来自 ShowCommands，集中定义）。</summary>
public sealed class ShowCenterCategory
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Command { get; init; }

    public ShowCenterMode Mode { get; init; } = ShowCenterMode.Text;

    public string RawOnlyNote { get; init; } = "该命令输出结构随型号/固件变化较大，直接显示原始文本（不做脆弱解析）。";

    public static IReadOnlyList<ShowCenterCategory> All { get; } = new[]
    {
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.Basic,
            Title = "基本信息",
            Command = ShowCommands.Version,
            Mode = ShowCenterMode.Text,
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.InterfaceStatus,
            Title = "接口状态",
            Command = ShowCommands.InterfaceStatus,
            Mode = ShowCenterMode.Grid,
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.Vlan,
            Title = "VLAN",
            Command = ShowCommands.Vlan,
            Mode = ShowCenterMode.Grid,
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.Mac,
            Title = "MAC 地址表",
            Command = ShowCommands.MacAddressTable,
            Mode = ShowCenterMode.Grid,
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.IpInterface,
            Title = "三层接口",
            Command = ShowCommands.InterfaceBrief,
            Mode = ShowCenterMode.Grid,
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.Cpu,
            Title = "CPU",
            Command = ShowCommands.Cpu,
            Mode = ShowCenterMode.RawOnly,
            RawOnlyNote = "CPU 使用率输出格式差异较大，直接显示原始文本。",
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.Logs,
            Title = "日志",
            Command = ShowCommands.Logging,
            Mode = ShowCenterMode.RawOnly,
            RawOnlyNote = "日志内容不固定，直接显示原始文本（大输出不会阻塞界面）。",
        },
        new ShowCenterCategory
        {
            Key = ShowCenterCategoryKeys.RunningConfig,
            Title = "运行配置",
            Command = ShowCommands.RunningConfig,
            Mode = ShowCenterMode.RawOnly,
            RawOnlyNote = "运行配置原样显示；备份请在【备份】页执行；要把配置写入设备用顶栏 [保存配置]（需按住确认，软件不会自动 write）。",
        },
    };
}
