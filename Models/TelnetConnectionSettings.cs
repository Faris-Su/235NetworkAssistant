using RuijieNetworkAssistant.Helpers;
using System.Text.Json.Serialization;

namespace RuijieNetworkAssistant.Models;

/// <summary>Telnet 连接参数。密码字段在界面上必须使用 PasswordBox 隐藏显示。</summary>
public sealed class TelnetConnectionSettings : ObservableObject
{
    private string _host = string.Empty;
    private int _port = 23;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private int _connectionTimeoutMs = 8000;
    private int _commandTimeoutMs = 15000;
    private int _idleQuietMs = 800;
    private bool _autoLogin = true;
    private string _lineEnding = LineEndingOption.DefaultTelnet;
    private int _terminalColumns = 200;
    private int _terminalRows = 60;
    private int _keepAliveSeconds;

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    /// 登录密码。**不落盘**（[JsonIgnore]）：
    /// settings.json 是普通配置文件，写入密码等于明文保存设备凭据，
    /// 与“密码不入库、不写日志”的既定策略冲突（2026-09-22 修正，历史版本曾写入过）。
    /// </summary>
    [JsonIgnore]
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public int ConnectionTimeoutMs
    {
        get => _connectionTimeoutMs;
        set => SetProperty(ref _connectionTimeoutMs, value);
    }

    public int CommandTimeoutMs
    {
        get => _commandTimeoutMs;
        set => SetProperty(ref _commandTimeoutMs, value);
    }

    public int IdleQuietMs
    {
        get => _idleQuietMs;
        set => SetProperty(ref _idleQuietMs, value);
    }

    /// <summary>连接后按提示符自动发送用户名/密码。</summary>
    public bool AutoLogin
    {
        get => _autoLogin;
        set => SetProperty(ref _autoLogin, value);
    }

    /// <summary>命令行结束符：Telnet(NVT) 标准是 CRLF。</summary>
    public string LineEnding
    {
        get => _lineEnding;
        set => SetProperty(ref _lineEnding, string.IsNullOrEmpty(value) ? LineEndingOption.DefaultTelnet : value);
    }

    public TelnetConnectionSettings Clone() => new()
    {
        Host = Host,
        Port = Port,
        Username = Username,
        Password = Password,
        ConnectionTimeoutMs = ConnectionTimeoutMs,
        CommandTimeoutMs = CommandTimeoutMs,
        IdleQuietMs = IdleQuietMs,
        AutoLogin = AutoLogin,
        LineEnding = LineEnding,
        TerminalColumns = TerminalColumns,
        TerminalRows = TerminalRows,
        KeepAliveSeconds = KeepAliveSeconds,
    };

    /// <summary>
    /// 终端列数（Telnet 的 NAWS 协商会把它告诉设备）。**默认 200**，比传统的 80 宽得多：
    /// 设备据此决定一行能放多少字符、以及每页多少行 —— 列宽大 = 长输出不折行、翻页次数少。
    /// 参考 PuTTY 的 "Terminal-type string" + NAWS：它会把窗口尺寸协商给设备，我们以前对所有
    /// Telnet 选项一律回绝，设备就只能按默认 80×24 处理（SSH 那条我们本来就是 200×60，两者不一致）。
    /// </summary>
    public int TerminalColumns
    {
        get => _terminalColumns;
        set => SetProperty(ref _terminalColumns, Math.Clamp(value, 40, 512));
    }

    /// <summary>终端行数（同上，默认 60 行：让"一页"能放下更多内容，减少 --More--）。</summary>
    public int TerminalRows
    {
        get => _terminalRows;
        set => SetProperty(ref _terminalRows, Math.Clamp(value, 10, 200));
    }

    /// <summary>
    /// 空闲保活间隔（秒），**0 = 关闭**。每 N 秒发一个 Telnet NOP（IAC NOP）把会话和隧道"喂"着 ——
    /// 现场最常见的掉线就是"连上后放着不动，过一会儿隧道/VTY 把它回收了"。
    /// 借鉴 PuTTY 的 "Seconds between keepalives"（默认关，长空闲场景建议 30）。
    /// </summary>
    public int KeepAliveSeconds
    {
        get => _keepAliveSeconds;
        set => SetProperty(ref _keepAliveSeconds, Math.Clamp(value, 0, 3600));
    }
}
