using System.Reflection;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 产品品牌与版本信息的唯一来源（UI、日志、报告都从这里取值，避免多处硬编码不一致）。
/// 命名空间/类名保留历史内部名称（RuijieNetworkAssistant），仅用户可见信息使用新品牌。
/// </summary>
public static class AppInfo
{
    /// <summary>正式中文名称。</summary>
    public const string ChineseName = "235修网助手";

    /// <summary>正式英文名称。</summary>
    public const string EnglishName = "235 Network Assistant";

    /// <summary>内部名称 / 程序集名 / EXE 名（不含扩展名）。</summary>
    public const string InternalName = "235NetworkAssistant";

    /// <summary>开发者。</summary>
    public const string Developer = "235修网助手团队";

    /// <summary>版权。</summary>
    public const string Copyright = "© 2026 235修网助手团队";

    /// <summary>产品描述。</summary>
    public const string Description = "面向校园网络运维人员的轻量化交换机配置与运维辅助工具";

    /// <summary>兼容范围说明（避免造成“锐捷官方软件”的误解）。</summary>
    public const string CompatibilityNote =
        "支持部分锐捷交换机 CLI 操作，具体命令以设备型号及固件版本为准。本软件为第三方运维辅助工具，与设备厂商无隶属关系。";

    /// <summary>版本号（0.1.0），来自程序集 InformationalVersion。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>UI 显示用版本（V0.1.0）。</summary>
    public static string DisplayVersion => "V" + Version;

    /// <summary>概览页右下角低调标识。</summary>
    public static string WatermarkLine => $"{EnglishName} · {DisplayVersion} · Developer: {Developer}";

    /// <summary>关于信息：开发者行。</summary>
    public static string DeveloperLine => $"开发者：{Developer}";

    private static string ResolveVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉 +build 之类的后缀，保证 UI / 文档 / 元数据统一为 0.1.0
            var version = informational.Split('+')[0].Trim();
            if (version.Length > 0)
            {
                return version;
            }
        }

        var fileVersion = assembly.GetName().Version;
        return fileVersion is null ? "0.1.0" : $"{fileVersion.Major}.{fileVersion.Minor}.{fileVersion.Build}";
    }
}
