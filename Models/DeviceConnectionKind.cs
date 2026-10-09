namespace RuijieNetworkAssistant.Models;

/// <summary>设备连接方式。SSH 为后续版本预留，V0.1 不实现。</summary>
public enum DeviceConnectionKind
{
    Serial,
    Telnet,
    Ssh,
}
