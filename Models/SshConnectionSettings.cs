using System.Text.Json.Serialization;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Models;

/// <summary>
/// SSH 连接参数。密码字段在界面上必须使用 PasswordBox 隐藏显示，且**不落盘**（见 AppSettings.Ssh）。
///
/// 与 Telnet 的关键差别：
///   1. SSH 的用户名/密码是在**传输层认证**阶段发出去的（SSH 协议自己发），
///      所以这里没有“在设备提示符上打用户名/密码”这一步；
///   2. AutoLogin 的含义随之变成“连上后自动确认 CLI 提示符（必要时发一次回车唤醒）”；
///   3. 认证失败 = 连接失败（这一步等价于 Telnet 的 TCP 建连）；
///      但认证成功之后**看不到提示符只算 CLI 未就绪**，不算连接失败（与 Telnet/Serial 一致）。
/// </summary>
public sealed class SshConnectionSettings : ObservableObject
{
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private int _connectionTimeoutMs = 10_000;
    private int _commandTimeoutMs = 15_000;
    private int _idleQuietMs = 800;
    private bool _autoLogin = true;
    private string _lineEnding = LineEndingOption.DefaultSsh;

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    /// <summary>SSH 端口，锐捷设备默认 22。</summary>
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
    /// 登录密码。**不落盘**（[JsonIgnore]）：只存在内存里，不写 settings.json、不写日志。
    /// </summary>
    [JsonIgnore]
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>
    /// 连接（TCP + SSH 握手 + 认证）超时。比 Telnet 默认大一点：
    /// SSH 要做密钥交换，过隧道/跨网段时比裸 TCP 慢。
    /// </summary>
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

    /// <summary>连上后自动确认提示符（CLI 未确认时可用 <<>> 里的提示自己查原因）。</summary>
    public bool AutoLogin
    {
        get => _autoLogin;
        set => SetProperty(ref _autoLogin, value);
    }

    /// <summary>
    /// 命令行结束符。SSH 终端按“回车”发的是单个 CR（PuTTY / OpenSSH 都是这样），
    /// 所以默认 CR；个别设备/固件要 CRLF 时可在界面上改。
    /// </summary>
    public string LineEnding
    {
        get => _lineEnding;
        set => SetProperty(ref _lineEnding, string.IsNullOrEmpty(value) ? LineEndingOption.DefaultSsh : value);
    }

    public SshConnectionSettings Clone() => new()
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
    };
}
