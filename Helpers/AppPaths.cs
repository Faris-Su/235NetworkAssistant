using System.IO;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 应用数据路径。全部位于本地磁盘，保证离线可用；不使用任何云端目录。
/// </summary>
public static class AppPaths
{
    private const string LegacyDirectoryName = "RuijieNetworkAssistant";

    private const string DirectoryName = "235NetworkAssistant";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DirectoryName);

    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public static string ResourceDatabaseFile => Path.Combine(RootDirectory, "resources.json");

    public static string BackupDirectory => Path.Combine(RootDirectory, "backups");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string WorkingDirectory => Path.Combine(RootDirectory, "work");

    /// <summary>
    /// 操作台账目录（按天一个 CSV：`operations-yyyyMMdd.csv`）。
    /// 用途：交接班 / 回单要的"今天谁在哪台设备上改了什么"—— 界面上的最近操作只在内存、上限 50 条、关程序就没了。
    /// </summary>
    public static string OperationLedgerDirectory => Path.Combine(RootDirectory, "operations");

    public static void EnsureCreated()
    {
        MigrateLegacyDirectory();
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(WorkingDirectory);
        Directory.CreateDirectory(OperationLedgerDirectory);
    }

    /// <summary>
    /// 品牌重命名后的一次性数据迁移：旧目录（RuijieNetworkAssistant）改名到新目录（235NetworkAssistant），
    /// 保证用户已导入的资源库、备份与设置不丢；迁移失败时保留旧目录并继续使用新目录。
    /// </summary>
    private static void MigrateLegacyDirectory()
    {
        if (Directory.Exists(RootDirectory))
        {
            return;
        }

        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyDirectoryName);

        if (!Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, RootDirectory);
        }
        catch
        {
            // 迁移失败（例如文件被占用）：保留旧目录，本次使用新目录。
        }
    }
}
