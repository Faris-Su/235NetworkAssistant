namespace RuijieNetworkAssistant.Models;

/// <summary>`show version` 解析出的设备信息（解析不出来时界面显示原始文本）。</summary>
public sealed class DeviceVersionInfo
{
    public string SystemDescription { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string SoftwareVersion { get; init; } = string.Empty;

    public string HardwareVersion { get; init; } = string.Empty;

    public string BootVersion { get; init; } = string.Empty;

    public string Uptime { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(SystemDescription) ||
        !string.IsNullOrWhiteSpace(Model) ||
        !string.IsNullOrWhiteSpace(SoftwareVersion) ||
        !string.IsNullOrWhiteSpace(Uptime);

    public string ToDisplayText()
    {
        var lines = new List<string>();
        Add(lines, "设备型号", Model);
        Add(lines, "系统描述", SystemDescription);
        Add(lines, "软件版本", SoftwareVersion);
        Add(lines, "硬件版本", HardwareVersion);
        Add(lines, "Boot 版本", BootVersion);
        Add(lines, "运行时间", Uptime);
        Add(lines, "序列号", SerialNumber);
        return string.Join(Environment.NewLine, lines);
    }

    private static void Add(ICollection<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}：{value}");
        }
    }
}
