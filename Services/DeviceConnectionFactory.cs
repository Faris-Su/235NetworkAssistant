using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>按连接方式创建 IDeviceConnection，ViewModel 不直接 new 具体实现。</summary>
public sealed class DeviceConnectionFactory
{
    private readonly ILogService _log;

    public DeviceConnectionFactory(ILogService log) => _log = log;

    public IDeviceConnection Create(
        DeviceConnectionKind kind,
        SerialConnectionSettings? serial,
        TelnetConnectionSettings? telnet,
        SshConnectionSettings? ssh = null) => kind switch
    {
        DeviceConnectionKind.Serial => new SerialDeviceConnection(
            serial ?? throw new ArgumentNullException(nameof(serial), "缺少串口参数。"),
            _log),
        DeviceConnectionKind.Telnet => new TelnetDeviceConnection(
            telnet ?? throw new ArgumentNullException(nameof(telnet), "缺少 Telnet 参数。"),
            _log),
        DeviceConnectionKind.Ssh => new SshDeviceConnection(
            ssh ?? throw new ArgumentNullException(nameof(ssh), "缺少 SSH 参数。"),
            _log),
        _ => throw new NotSupportedException($"暂不支持 {kind} 连接方式。"),
    };
}
