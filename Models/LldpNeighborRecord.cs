namespace RuijieNetworkAssistant.Models;

using System.Text.RegularExpressions;

/// <summary>详情面板的一行（图标 + 中文标签 + 值）。</summary>
public sealed record LldpDetailField(string Icon, string Label, string Value);

/// <summary>LLDP 邻居 Detail（`show lldp neighbors interface &lt;port&gt; detail`）的获取状态。</summary>
public enum LldpDetailState
{
    /// <summary>没查过（没有本地端口，或设备不支持 Detail）。</summary>
    NotRequested,

    /// <summary>Detail 已获取并合并进本条记录。</summary>
    Ok,

    /// <summary>该端口 Detail 查询/解析失败：保留基础信息，不隐藏。</summary>
    Failed,

    /// <summary>设备不支持 Detail 命令：保留基础信息，页面照常可用。</summary>
    Unsupported,
}

/// <summary>
/// LLDP 邻居（统一模型，DataGrid 只绑定这里的字段）。
/// 数据来源分两层：
///   基础层 = `show lldp neighbors`（表格式或块状）
///   详情层 = `show lldp neighbors interface &lt;port&gt; detail`（刷新时自动逐端口补齐）
/// 原始输出分别保留在 RawBlock / RawDetailOutput，解析失败也能看到设备原文。
/// </summary>
public sealed class LldpNeighborRecord
{
    public string LocalPort { get; set; } = string.Empty;

    public string NeighborDevice { get; set; } = string.Empty;

    public string NeighborPort { get; set; } = string.Empty;

    public string ManagementIp { get; set; } = string.Empty;

    public string ChassisId { get; set; } = string.Empty;

    /// <summary>Chassis type 原始值（例如 “MAC address”），界面翻译成中文。</summary>
    public string ChassisType { get; set; } = string.Empty;

    /// <summary>Port type 原始值（例如 “Interface name”）。</summary>
    public string PortType { get; set; } = string.Empty;

    public string SystemDescription { get; set; } = string.Empty;

    public string PortDescription { get; set; } = string.Empty;

    /// <summary>能力（Capability）：B, R / Bridge, Router 等，缺失显示 N/A。</summary>
    public string Capability { get; set; } = string.Empty;

    public string NeighborIndex { get; set; } = string.Empty;

    /// <summary>老化时间（Aging-time / Holdtime）。</summary>
    public string AgingTime { get; set; } = string.Empty;

    /// <summary>兼容旧字段名（表格式的 Holdtime 列），与 <see cref="AgingTime"/> 同源。</summary>
    public string HoldTime { get; set; } = string.Empty;

    public string UpdateTime { get; set; } = string.Empty;

    public string RawBlock { get; set; } = string.Empty;

    /// <summary>该端口 Detail 查询的原始输出（设备原文，解析失败时也保留）。</summary>
    public string RawDetailOutput { get; set; } = string.Empty;

    /// <summary>Detail 获取状态。</summary>
    public LldpDetailState DetailState { get; set; } = LldpDetailState.NotRequested;

    /// <summary>Detail 失败时的原因（用于界面提示）。</summary>
    public string DetailNote { get; set; } = string.Empty;

    public bool HasDetail => DetailState == LldpDetailState.Ok;

    public bool HasManagementIp => !string.IsNullOrWhiteSpace(ManagementIp);

    public string DisplayName => string.IsNullOrWhiteSpace(NeighborDevice)
        ? Or(ChassisId)
        : NeighborDevice;

    /// <summary>邻居设备名（缺失时用 Chassis ID 兜底，再缺失显示“未命名邻居”）。</summary>
    public string DisplayDeviceName => string.IsNullOrWhiteSpace(NeighborDevice)
        ? string.IsNullOrWhiteSpace(ChassisId) ? "未命名邻居" : ChassisId
        : NeighborDevice.Trim();

    /// <summary>本端（本机）显示名：本地端口。</summary>
    public string LocalEndpointTitle => "本机";

    public string LocalEndpointPort => Or(LocalPort);

    public string RemoteEndpointTitle => DisplayDeviceName;

    public string RemoteEndpointPort => Or(NeighborPort);

    /// <summary>能力（Capability）的中文翻译：B, R → 网桥、路由器。</summary>
    public string CapabilityText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Capability))
            {
                return "N/A";
            }

            var items = Capability
                .Split(new[] { ',', '/', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(TranslateCapability)
                .Where(text => text.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return items.Count == 0 ? Capability.Trim() : string.Join("、", items);
        }
    }

    /// <summary>能力徽标（图形化展示用）。</summary>
    public IReadOnlyList<string> CapabilityChips =>
        string.IsNullOrWhiteSpace(Capability)
            ? Array.Empty<string>()
            : Capability
                .Split(new[] { ',', '/', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(TranslateCapability)
                .Where(text => text.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>老化时间的中文写法：1minutes 35seconds → 1 分 35 秒。</summary>
    public string AgingText => FormatDuration(AgingTime);

    /// <summary>邻居信息更新于多久之前：0 days, 0 hours, 5 minutes → 5 分钟前。</summary>
    public string UpdateAgoText
    {
        get
        {
            var text = FormatDuration(UpdateTime);
            return text == "N/A" ? text : text + "前";
        }
    }

    public string ChassisIdText => Or(ChassisId);

    public string ChassisTypeText => TranslateChassisType(ChassisType);

    public string PortTypeText => TranslatePortType(PortType);

    public string ManagementIpText => HasManagementIp ? ManagementIp.Trim() : "未获取（无法直连 Telnet）";

    public string SystemDescriptionText => Or(SystemDescription);

    public string PortDescriptionText => Or(PortDescription);

    /// <summary>详情面板的字段行（顺序即展示顺序）。</summary>
    public IReadOnlyList<LldpDetailField> DetailFields => new[]
    {
        new LldpDetailField("🏷", "邻居设备", DisplayDeviceName),
        new LldpDetailField("🔌", "本地端口", LocalEndpointPort),
        new LldpDetailField("🔗", "远端端口", RemoteEndpointPort),
        new LldpDetailField("🌐", "管理 IP", ManagementIpText),
        new LldpDetailField("🆔", "机箱 ID", $"{ChassisIdText}（{ChassisTypeText}）"),
        new LldpDetailField("🧩", "设备能力", CapabilityText),
        new LldpDetailField("📦", "设备型号描述", SystemDescriptionText),
        new LldpDetailField("📝", "端口描述", PortDescriptionText),
        new LldpDetailField("⏳", "信息老化时间", AgingText),
        new LldpDetailField("🔄", "邻居信息更新", UpdateAgoText),
        new LldpDetailField("📡", "Detail 状态", DetailStateDescription),
    };

    /// <summary>表格 Status 列（刻意用短标签，保证小屏不出现横向滚动）。</summary>
    public string DetailStatusText => DetailState switch
    {
        LldpDetailState.Ok => "已获取",
        LldpDetailState.Failed => "获取失败",
        LldpDetailState.Unsupported => "不支持",
        _ => "仅基础",
    };

    /// <summary>详情面板用的完整说明（表格里只放短标签）。</summary>
    public string DetailStateDescription => DetailState switch
    {
        LldpDetailState.Ok => "Detail 已获取",
        LldpDetailState.Failed => string.IsNullOrWhiteSpace(DetailNote)
            ? "Detail 获取失败（已保留基础信息）"
            : $"Detail 获取失败：{DetailNote}",
        LldpDetailState.Unsupported => string.IsNullOrWhiteSpace(DetailNote)
            ? "设备不支持 Detail（已保留基础信息）"
            : $"设备不支持 Detail：{DetailNote}",
        _ => "仅基础信息（未查询 Detail）",
    };

    /// <summary>[复制详情] 用的多行文本：字段名与取值都已翻译成中文（缺失字段显示 N/A）。</summary>
    public string ToDetailText() => string.Join(
        Environment.NewLine,
        DetailFields.Select(field => $"{field.Icon} {field.Label}：{field.Value}"));

    /// <summary>能力代码/全称 → 中文。</summary>
    private static string TranslateCapability(string raw)
    {
        var token = raw.Trim();
        return token.ToLowerInvariant() switch
        {
            "b" or "bridge" => "网桥",
            "r" or "router" => "路由器",
            "t" or "telephone" => "IP 电话",
            "c" or "docsis cable device" or "docsis" => "DOCSIS 设备",
            "w" or "wlan access point" or "wlan" => "无线 AP",
            "p" or "repeater" => "中继器",
            "s" or "station" => "终端设备",
            "o" or "other" => "其他",
            _ => token,
        };
    }

    private static string TranslateChassisType(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? "未提供"
        : raw.Trim().ToLowerInvariant() switch
        {
            "mac address" => "MAC 地址",
            "network address" => "网络地址",
            "interface name" or "interface alias" => "接口名",
            "locally assigned" => "本地分配",
            "chassis component" => "机箱部件",
            "port component" => "端口部件",
            _ => raw.Trim(),
        };

    private static string TranslatePortType(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? "未提供"
        : raw.Trim().ToLowerInvariant() switch
        {
            "interface name" or "interface alias" => "接口名",
            "mac address" => "MAC 地址",
            "network address" => "网络地址",
            "agent circuit id" => "代理电路号",
            "locally assigned" => "本地分配",
            _ => raw.Trim(),
        };

    /// <summary>
    /// 「1minutes 35seconds」「0 days, 0 hours, 5 minutes」「96」（纯秒数）统一翻译成
    /// 「1 分 35 秒」「5 分钟」「96 秒」。
    /// </summary>
    private static string FormatDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "N/A";
        }

        var text = raw.Trim();
        if (text.All(char.IsDigit))
        {
            return $"{text} 秒";
        }

        var matches = Regex.Matches(text, @"(\d+)\s*(days?|hours?|minutes?|seconds?|天|小时|分钟|秒)", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return text;
        }

        var parts = new List<string>();
        foreach (Match match in matches)
        {
            var value = int.Parse(match.Groups[1].Value);
            if (value == 0)
            {
                continue;
            }

            var unit = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "day" or "days" or "天" => "天",
                "hour" or "hours" or "小时" => "小时",
                "minute" or "minutes" or "分钟" => "分",
                _ => "秒",
            };
            parts.Add($"{value} {unit}");
        }

        return parts.Count == 0 ? "0 秒" : string.Join(" ", parts);
    }

    /// <summary>字段缺省统一显示 N/A。</summary>
    public static string Or(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
}
