namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 设备权限级别。由设备提示符判定（`#` = 特权、`>` = 普通），
/// CLI 页面与所有图形化页面共享同一份状态，禁止各页面各自维护。
/// </summary>
public enum DevicePrivilegeLevel
{
    /// <summary>还没拿到提示符，无法判定。</summary>
    Unknown,

    /// <summary>普通用户模式（提示符 `Host>`）。</summary>
    User,

    /// <summary>特权模式（提示符 `Host#`、`Host(config)#`）。</summary>
    Privileged,
}

/// <summary>连接时的权限模式。</summary>
public enum PrivilegeMode
{
    /// <summary>普通模式：连接后保持设备当前权限，不自动 enable。</summary>
    Normal,

    /// <summary>管理模式（默认）：连接成功后自动尝试进入特权模式。</summary>
    Manage,
}

/// <summary>一次权限提升请求（连接时或图形化操作前触发）。</summary>
public sealed record PrivilegeRequest(PrivilegeMode Mode, string? EnablePassword);

/// <summary>权限提升结果。失败不代表连接失败，只代表不能改配置。</summary>
public sealed record PrivilegeElevationResult(bool Succeeded, string Reason)
{
    public static PrivilegeElevationResult Success(string reason) => new(true, reason);

    public static PrivilegeElevationResult Failure(string reason) => new(false, reason);
}

public static class DevicePrivilegeText
{
    public static string ToChinese(this DevicePrivilegeLevel level) => level switch
    {
        DevicePrivilegeLevel.Privileged => "特权模式 #",
        DevicePrivilegeLevel.User => "普通模式 >",
        _ => "权限未知",
    };

    public static string ToIcon(this DevicePrivilegeLevel level) => level switch
    {
        DevicePrivilegeLevel.Privileged => "🟢",
        DevicePrivilegeLevel.User => "🟡",
        _ => "⚪",
    };

    /// <summary>
    /// 能否执行需要管理权限的操作。只有**明确判定为普通模式**时才拦截；
    /// 未知（没拿到提示符）按可尝试处理，避免误挡本来能用的设备。
    /// </summary>
    public static bool CanConfigure(this DevicePrivilegeLevel level) =>
        level != DevicePrivilegeLevel.User;
}

/// <summary>权限相关的默认值。</summary>
public static class PrivilegeDefaults
{
    /// <summary>
    /// Enable 密码的默认值留空，避免在源码中预置设备凭据；用户需要自行输入。
    /// 输入内容只存在内存里，不写入配置文件、不进日志。
    /// </summary>
    public const string EnablePassword = "";
}
