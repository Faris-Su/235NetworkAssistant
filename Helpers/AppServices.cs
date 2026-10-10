using System.IO;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 极简服务定位器：替代 DI 容器，减少依赖与启动开销。
/// 所有服务在此集中构造，ViewModel 不直接 new 串口 / Socket / 文件访问。
/// </summary>
public static class AppServices
{
    private static SettingsService? _settingsService;

    public static ILogService Log { get; private set; } = new FileLogService();

    public static AppSettings Settings { get; private set; } = new();

    public static ConnectionService Connections { get; private set; } = null!;

    public static SshHostKeyTrustStore SshHostKeys { get; private set; } = null!;

    public static SshHostKeyProbe SshHostKeyProbe { get; } = new();

    public static CommandService Commands { get; private set; } = null!;

    public static BackupService Backup { get; private set; } = null!;

    /// <summary>
    /// 保存配置（write）服务：本项目里唯一会写设备持久配置的操作。
    /// 只由用户在弹窗里"按住 1.2 秒"触发，没有任何自动调用点。
    /// </summary>
    public static ConfigSaveService ConfigSave { get; private set; } = null!;

    public static IResourceRepository ResourceRepository { get; private set; } = null!;

    public static ExcelImporter ExcelImporter { get; private set; } = null!;

    /// <summary>剪贴板（默认空实现；桌面应用启动时替换为 WPF 实现）。</summary>
    public static IClipboardService Clipboard { get; set; } = NoopClipboardService.Instance;

    /// <summary>SNMP v2c 只读查询设置（Community 不落盘，仅本次运行有效）。</summary>
    public static SnmpSettings Snmp { get; } = new();

    /// <summary>
    /// SNMP 查询服务（独立模块：只依赖 IP + Community + UDP 端口，不依赖任何 CLI 连接）。
    /// </summary>
    public static SnmpQueryService SnmpQuery { get; private set; } = null!;

    /// <summary>SNMP 设备信息库（按 IP 保存最近一次结果 + 历史快照，不含 Community）。</summary>
    public static SnmpDeviceStore SnmpDevices { get; private set; } = null!;

    /// <summary>
    /// 操作台账（按天一个 CSV）。**所有"做过的事"都经过它** —— 交接班与回单要能查"今天谁在哪台设备上改了什么"。
    /// 写盘前会做密码脱敏，且写失败绝不影响主流程。
    /// </summary>
    public static OperationLedger OperationLedger { get; private set; } = null!;

    /// <summary>
    /// Enable 密码（管理模式用）。默认留空，由用户按设备配置输入；
    /// **只存在内存里**：不写设置文件、不写日志、不弹框显示。
    /// </summary>
    public static string EnablePassword { get; set; } = PrivilegeDefaults.EnablePassword;

    /// <summary>应用启动时调用一次。启动过程不等待网络，也不访问任何在线服务。</summary>
    /// <param name="resourceDatabasePath">可选：自定义资源库文件（诊断/多库场景）。</param>
    /// <param name="resourceDatabasePath">自定义资源库文件路径（自动化验证用；为空时用 %LocalAppData%）。</param>
    /// <param name="settingsPath">自定义设置文件路径（自动化验证用；为空时用 %LocalAppData%）。</param>
    public static async Task InitializeAsync(string? resourceDatabasePath = null, string? settingsPath = null)
    {
        AppPaths.EnsureCreated();

        _settingsService = new SettingsService(Log, settingsPath);
        Settings = await _settingsService.LoadAsync().ConfigureAwait(false);
        // 把落盘的非敏感 SNMP 设置读回内存（Community 从不落盘，保持为空）
        Snmp.Enabled = Settings.SnmpEnabled;
        Snmp.LastHost = Settings.SnmpLastHost;
        Snmp.Port = Settings.SnmpPort;
        Snmp.TimeoutMs = Settings.SnmpTimeoutMs;
        Snmp.Retries = Settings.SnmpRetries;
        Snmp.FullLargeTables = Settings.SnmpFullLargeTables;
        await MigrateLegacyPathsAsync().ConfigureAwait(false);
        Log.MinimumLevel = Enum.TryParse<LogLevel>(Settings.LogLevel, ignoreCase: true, out var level)
            ? level
            : LogLevel.Info;

        SshHostKeys = new SshHostKeyTrustStore(Log);
        var factory = new DeviceConnectionFactory(Log, SshHostKeys);
        Connections = new ConnectionService(factory, Log);
        Commands = new CommandService(Connections, Log);
        Backup = new BackupService(Commands, Connections, Log);
        ConfigSave = new ConfigSaveService(Connections, Commands, Log);
        ResourceRepository = new JsonResourceRepository(Log, resourceDatabasePath);
        ExcelImporter = new ExcelImporter(Log);
        SnmpQuery = new SnmpQueryService(Log);
        SnmpDevices = new SnmpDeviceStore(Log);
        OperationLedger = new OperationLedger(Log);
        await SnmpDevices.LoadAsync().ConfigureAwait(false);

        // 启动时加载本地资源库（本地 JSON，不等待网络）：保证概览、地址簿、查询页统计一致。
        await ResourceRepository.LoadAsync().ConfigureAwait(false);

        Log.Info($"{AppInfo.ChineseName}（{AppInfo.EnglishName} {AppInfo.DisplayVersion}）已启动（离线模式）。");
    }

    public static async Task SaveSettingsAsync()
    {
        if (_settingsService is not null)
        {
            // 保存前把内存里的 SNMP 非敏感设置同步进设置对象（Community 除外，永不落盘）
            Settings.SnmpEnabled = Snmp.Enabled;
            Settings.SnmpLastHost = Snmp.LastHost;
            Settings.SnmpPort = Snmp.Port;
            Settings.SnmpTimeoutMs = Snmp.TimeoutMs;
            Settings.SnmpRetries = Snmp.Retries;
            Settings.SnmpFullLargeTables = Snmp.FullLargeTables;
            await _settingsService.SaveAsync(Settings).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 品牌重命名后的一次性设置迁移：把 settings.json 里指向旧目录（RuijieNetworkAssistant）的
    /// 绝对路径改到新目录（235NetworkAssistant），避免用户既有备份目录失效。
    /// </summary>
    private static async Task MigrateLegacyPathsAsync()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyRoot = Path.Combine(localAppData, "RuijieNetworkAssistant");
        var current = Settings.BackupDirectory;

        if (string.IsNullOrWhiteSpace(current) ||
            !current.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relative = Path.GetRelativePath(legacyRoot, current);
        Settings.BackupDirectory = Path.Combine(AppPaths.RootDirectory, relative);
        Log.Info($"设置迁移：备份目录 {current} → {Settings.BackupDirectory}");
        await SaveSettingsAsync().ConfigureAwait(false);
    }
}
