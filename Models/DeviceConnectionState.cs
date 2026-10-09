namespace RuijieNetworkAssistant.Models;

public enum DeviceConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在建立底层连接（TCP 握手 / 打开串口）。</summary>
    Connecting,

    /// <summary>底层已连上，但 CLI 还没确认可用（例如自动登录未见提示符）。</summary>
    Connected,

    /// <summary>底层已连上，正在自动登录。</summary>
    Authenticating,

    /// <summary>底层已连上，并且已确认 CLI 可交互（看到了提示符）。</summary>
    Ready,

    /// <summary>底层连接失败。只有“连不上”才是 Error；登录/提示符失败不算。</summary>
    Error,
}

public static class DeviceConnectionStateText
{
    public static string ToChinese(this DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Disconnected => "未连接",
        DeviceConnectionState.Connecting => "连接中",
        DeviceConnectionState.Connected => "已连接（CLI 未确认）",
        DeviceConnectionState.Authenticating => "已连接（正在登录）",
        DeviceConnectionState.Ready => "已连接",
        // Error 只代表“底层连不上”，登录/提示符问题不再落到这个状态。
        DeviceConnectionState.Error => "连接失败",
        _ => state.ToString(),
    };

    /// <summary>
    /// 底层连接是否已经建立。建立之后的状态都允许发命令、也允许进 CLI 手动输入，
    /// 与“CLI 是否已经确认可用”分开判断。
    /// </summary>
    public static bool IsTransportUp(this DeviceConnectionState state) =>
        state is DeviceConnectionState.Connected
            or DeviceConnectionState.Authenticating
            or DeviceConnectionState.Ready;
}
