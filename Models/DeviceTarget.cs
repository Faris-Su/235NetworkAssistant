namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 一台"要操作的目标设备"：管理 IP + 名称 + 楼栋。
///
/// 为什么单独一个文件、为什么叫这个中性名字：它是**批量操作共用的入参**——
/// 批量备份在用（按资源库选一批设备去备份），原先的"巡检/全网定位"也用。
/// 那两个功能按用户要求不打包了（源码保留在项目里，见 RuijieNetworkAssistant.csproj），
/// 所以这个类型不能跟着它们一起被排除。
/// </summary>
public sealed record DeviceTarget(string Ip, string Name, string Building);
