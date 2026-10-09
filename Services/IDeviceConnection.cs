using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

public sealed class DeviceOutputEventArgs : EventArgs
{
    public DeviceOutputEventArgs(string text)
    {
        Text = text;
    }

    /// <summary>设备返回的原始文本，禁止在传输层做“美化”。</summary>
    public string Text { get; }
}

public sealed class DeviceStateChangedEventArgs : EventArgs
{
    public DeviceStateChangedEventArgs(DeviceConnectionState state, string? message)
    {
        State = state;
        Message = message;
    }

    public DeviceConnectionState State { get; }

    public string? Message { get; }
}

/// <summary>
/// 统一设备连接接口。CLI 页面与命令执行只依赖本接口，不关心底层是 Serial 还是 Telnet。
/// 未来 SshDeviceConnection 也实现本接口，上层无需改动。
/// </summary>
public interface IDeviceConnection : IAsyncDisposable
{
    DeviceConnectionKind Kind { get; }

    DeviceConnectionState State { get; }

    /// <summary>设备名（可空，通常来自 show version / show running-config）。</summary>
    string? DeviceName { get; }

    /// <summary>管理地址：Telnet 为 IP，Serial 为 COM 口名。</summary>
    string? ManagementAddress { get; }

    /// <summary>设备名（通常从 show 输出的提示符或 running-config 的 hostname 得到）。</summary>
    void SetDeviceIdentity(string? deviceName);

    string? LastError { get; }

    /// <summary>
    /// 连接过程中的提示信息（例如「设备已在提示符下，已跳过自动登录」「登录后未见提示符」）。
    /// 供界面在连接成功后原样展示；没有提示时为空。不属于设备原始输出。
    /// </summary>
    string? ConnectNotice { get; }

    /// <summary>由会话层补充连接提示（例如「串口已打开但设备毫无输出」）。已有提示时不应覆盖。</summary>
    void ReportConnectNotice(string notice);

    /// <summary>会话层判定“底层已连接但 CLI 没确认可用”（例如串口打开后设备毫无输出）。</summary>
    void ReportCliNotReady(string reason);

    /// <summary>最近一条命令自动续页的次数（&gt;0 表示输出被 --More-- 分页过，已自动翻完）。</summary>
    int LastCommandPageCount { get; }

    /// <summary>最近一条命令是否在设备提示符处正常结束（false 表示可能被截断）。</summary>
    bool LastCommandEndedAtPrompt { get; }

    /// <summary>最近一条命令是否触发了分页上限保护。</summary>
    bool LastCommandHitPageLimit { get; }

    /// <summary>最近一条命令因超出输出缓冲上限而被丢弃的字符数（丢的是开头；0 = 完整）。</summary>
    long LastCommandDroppedChars { get; }

    /// <summary>当前权限级别（由提示符判定，CLI 与图形化页面共享）。</summary>
    Models.DevicePrivilegeLevel PrivilegeLevel { get; }

    /// <summary>权限相关的提示（“已进入特权模式” / “无法进入特权模式：原因”）。</summary>
    string? PrivilegeNotice { get; }

    /// <summary>权限级别变化（例如用户在 CLI 里手动 enable 成功）。</summary>
    event EventHandler? PrivilegeChanged;

    /// <summary>
    /// 尝试进入特权模式：发 enable → 若设备要求密码则发送 → 用提示符确认结果。
    /// **失败不抛异常、不影响连接**，只返回失败原因。
    /// </summary>
    Task<Models.PrivilegeElevationResult> TryEnterPrivilegedModeAsync(
        string? enablePassword,
        CancellationToken cancellationToken);

    /// <summary>
    /// CLI 是否已经确认可以交互（看到提示符 / 已完成登录）。
    /// 与 <see cref="State"/> 是两件事：底层连上但 CLI 没确认时，State 会停在 Connected。
    /// </summary>
    bool CliReady { get; }

    /// <summary>CLI 初始化维度的一句话说明（“正在自动登录”“CLI 就绪”“等待提示符超时”）。</summary>
    string? CliStateText { get; }

    event EventHandler<DeviceOutputEventArgs>? OutputReceived;

    event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    /// <summary>设备名被识别出来或发生变化时触发（例如从提示符 “Example-SW#” 解析到主机名）。</summary>
    event EventHandler? DeviceIdentityChanged;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();

    /// <summary>发送一条命令并返回本次命令的原始输出（同时仍会通过 OutputReceived 流式推送）。</summary>
    Task<string> SendAsync(string command, CancellationToken cancellationToken);

    /// <summary>发送原始数据，不做任何换行/编码加工（供 Ctrl+C、Tab、? 等透传使用）。</summary>
    Task WriteRawAsync(string rawText, CancellationToken cancellationToken);
}
