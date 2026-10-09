using System.Text.Json.Serialization;

namespace RuijieNetworkAssistant.Models;

/// <summary>应用设置。全部本地保存。</summary>
public sealed class AppSettings
{
    /// <summary>终端最大显示缓冲（字符数）。用户可配置，防止长时间运行内存增长。</summary>
    public int TerminalBufferChars { get; set; } = 200_000;

    /// <summary>批量刷新间隔（毫秒）。CLI 输出不允许逐字符刷新 UI。</summary>
    public int TerminalFlushIntervalMs { get; set; } = 80;

    /// <summary>CLI 自动滚动到最新输出。</summary>
    public bool CliAutoScroll { get; set; } = true;

    /// <summary>CLI 本地回显（默认关闭：由设备回显，避免重复显示）。</summary>
    public bool CliLocalEcho { get; set; }

    /// <summary>
    /// 退格键发给设备的字节：false = 0x08（Ctrl-H，多数网络设备）；true = 0x7F（DEL，部分设备/服务器）。
    /// 借鉴 PuTTY 的 "Backspace key" 设置 —— 这两字节各厂商习惯不同，遇到"退格没用/乱码"时换一下就好。
    /// </summary>
    public bool CliBackspaceSendsDel { get; set; }

    /// <summary>
    /// CLI 输入模式：true = 终端模式（逐字符发送、直接在终端里打字，像 Xshell，默认）；
    /// false = 行模式（在下方输入框整行输入，回车/点[发送]才发给设备）。
    /// </summary>
    public bool CliTerminalMode { get; set; } = true;

    /// <summary>命令历史条数上限。</summary>
    public int CliHistorySize { get; set; } = 200;

    /// <summary>
    /// 连接时的权限模式：普通模式 = 保持设备当前权限；管理模式（默认）= 连接后自动尝试 enable。
    /// Enable 密码**不在这里**（不落盘），见 AppServices.EnablePassword。
    /// </summary>
    public PrivilegeMode PrivilegeMode { get; set; } = PrivilegeMode.Manage;

    /// <summary>界面字体档位：小 / 标准 / 大（默认标准，可读性优先）。</summary>
    public string UiFontScale { get; set; } = "标准";

    /// <summary>CLI 字体档位：小 / 标准 / 大（与界面字体独立）。</summary>
    public string CliFontScale { get; set; } = "标准";

    /// <summary>小屏自动折叠左侧导航的窗口宽度阈值（小于该值自动折叠）。</summary>
    public double NavCollapseWidth { get; set; } = 1180;

    public string BackupDirectory { get; set; } = Helpers.AppPaths.BackupDirectory;

    public string LogLevel { get; set; } = "Info";

    /// <summary>是否把最近一次使用的连接参数保存下来。</summary>
    public bool RememberConnectionSettings { get; set; } = true;

    public SerialConnectionSettings Serial { get; set; } = new();

    public TelnetConnectionSettings Telnet { get; set; } = new();

    /// <summary>
    /// SSH 参数。**密码不落盘**：[JsonIgnore] 挂在 SshConnectionSettings.Password 上，
    /// 与“密码不入库、不写日志”的既定策略保持一致（Telnet 的历史行为见 Handoff）。
    /// </summary>
    public SshConnectionSettings Ssh { get; set; } = new();

    // ---------- SNMP 只读查询的**非敏感**设置 ----------
    // 为什么要落盘：过 UU 隧道/SnmpTunnel 时一来一回要 2~3 秒，默认 1500ms 会大量超时，
    // 用户必须把超时调到 5000ms 左右；以前这些值只在内存里，每次重启都打回默认值。
    // **Community 绝不在这里**（等同密码，永不落盘，见 SnmpSettings 注释）。
    /// <summary>是否启用概览页 SNMP 卡片。</summary>
    public bool SnmpEnabled { get; set; }

    /// <summary>最近一次查询的目标 IP（辅助识别；不含凭据）。</summary>
    public string SnmpLastHost { get; set; } = string.Empty;

    /// <summary>SNMP UDP 端口（默认 161）。</summary>
    public int SnmpPort { get; set; } = 161;

    /// <summary>单次请求超时（毫秒）。隧道场景建议 3000~8000。</summary>
    public int SnmpTimeoutMs { get; set; } = 1500;

    /// <summary>重试次数。隧道场景建议 2。</summary>
    public int SnmpRetries { get; set; } = 1;

    /// <summary>
    /// SNMP 是否"完整拉取大表"（勾一次就记住）。默认 false = 按 60 秒时间预算取一部分并标注截断。
    /// 为什么单独落盘：核心机型全量 MAC 要几分钟，用户如果就是要全量，不该每次重新勾。
    /// </summary>
    public bool SnmpFullLargeTables { get; set; }

    [JsonIgnore]
    public double UiFontSize => UiFontScale switch
    {
        "小" => 13,
        "大" => 16,
        _ => 14,
    };

    [JsonIgnore]
    public double CliFontSize => CliFontScale switch
    {
        "小" => 12.5,
        "大" => 15.5,
        _ => 13.5,
    };
}
