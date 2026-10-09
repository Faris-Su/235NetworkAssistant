using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 配置备份页面（Phase 4）：备份 running-config 到本地、查看备份历史、打开备份目录/文件。
/// 备份必须是用户明确的操作；备份本身**只读**（只读回 running-config，不写设备）。
/// 写设备（write）另有显式入口：顶栏 [保存配置]，且必须按住 1.2 秒确认。
/// </summary>
public sealed class BackupViewModel : ViewModelBase
{
    private readonly IShellNavigator _shell;
    private readonly ConnectionService _connections;
    private readonly Func<string?>? _folderPicker;
    private bool _isBusy;
    private BackupFileRecord? _selectedBackup;
    private string _statusText = "点击[备份 running-config]生成新备份（备份是明确的用户操作）。";
    private string _lastBackupText = "本次会话尚未备份。";
    private readonly ConfigDiffService _diffService = new();
    private ConfigDiffSource? _diffLeft;
    private ConfigDiffSource? _diffRight;
    private bool _diffOnlyChanges = true;
    private string _diffSummary = "选两侧来源后点[对比]（右侧可以选“当前设备”的 running-config）。";
    private List<ConfigDiffLine> _diffAll = new();

    /// <summary>批量备份子 VM（挂在备份页的折叠区里）。</summary>
    public BulkBackupViewModel Bulk { get; }

    /// <param name="folderPicker">
    /// 批量备份导出 CSV 时选目录。做成可注入的委托，这样本 VM（连同 <see cref="Bulk"/>）
    /// 不依赖 WPF 对话框，能进自检工程被自动化覆盖。
    /// </param>
    public BackupViewModel(IShellNavigator shell, ConnectionService connections, Func<string?>? folderPicker = null)
    {
        _shell = shell;
        _connections = connections;
        _folderPicker = folderPicker;
        Title = "备份";

        // 批量备份（按资源库选一批设备顺序备份）：挂在同一个页面上，独立 VM，互不影响。
        Bulk = new BulkBackupViewModel(
            shell,
            AppServices.ResourceRepository,
            new BulkBackupService(connections, AppServices.Backup, AppServices.Log),
            folderPicker);

        BackupCommand = new AsyncRelayCommand(BackupAsync, () => !IsBusy && IsConnected);
        RefreshListCommand = new AsyncRelayCommand(RefreshListAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenFileCommand = new RelayCommand(OpenFile, () => SelectedBackup is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedBackup is not null);
        DiffCommand = new AsyncRelayCommand(RunDiffAsync, () => !IsBusy && DiffLeft is not null && DiffRight is not null);
        ExportDiffCommand = new RelayCommand(ExportDiff, () => _diffAll.Count > 0);
        CheckSaveStateCommand = new AsyncRelayCommand(CheckSaveStateAsync, () => !IsBusy && IsConnected);

        _connections.SessionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionHint));
            BackupCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>
    /// 备份文件列表。用 <see cref="BulkObservableCollection{T}"/> 整体替换：
    /// 备份目录累积几百份 .cfg 时，旧的 `Clear()` + 逐条 `Add` 就是几百次集合通知。
    /// </summary>
    public BulkObservableCollection<BackupFileRecord> Backups { get; } = new();

    public bool IsConnected => _connections.IsConnected;

    public string ConnectionHint => IsConnected
        ? $"当前设备：{(string.IsNullOrWhiteSpace(_connections.DeviceName) ? "未获取设备名" : _connections.DeviceName)}｜{_connections.ManagementAddress}"
        : "未连接设备：备份需要先连接 Console 或 Telnet。";

    public BackupFileRecord? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                OpenFileCommand.RaiseCanExecuteChanged();
                CopyPathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                BackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastBackupText
    {
        get => _lastBackupText;
        private set => SetProperty(ref _lastBackupText, value);
    }

    public string BackupDirectoryText => $"备份目录：{AppServices.Settings.BackupDirectory}";

    public string CountText => $"备份文件 {Backups.Count} 个";

    public string WorkflowHint =>
        "推荐流程：备份 running-config → 使用 GUI 修改配置 → Command Preview → 执行 → 改完到顶栏 [保存配置] 写入设备（要按住 1.2 秒确认）。软件不会自动 write。";

    public string NamingHint => "文件名格式：设备名_YYYYMMDD_HHmmss.cfg（例：Example-SW_YYYYMMDD_HHmmss.cfg）";

    public AsyncRelayCommand BackupCommand { get; }

    public AsyncRelayCommand RefreshListCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenFileCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    /// <summary>配置对比（"改完核对动了哪几行" / "上次好的配置长什么样"）。</summary>
    public AsyncRelayCommand DiffCommand { get; }

    public RelayCommand ExportDiffCommand { get; }

    /// <summary>[检查保存状态]：把设备上的 running-config 与 startup-config 对比，回答"改了是不是没保存"。</summary>
    public AsyncRelayCommand CheckSaveStateCommand { get; }

    public BulkObservableCollection<ConfigDiffLine> DiffLines { get; } = new();

    /// <summary>对比的两侧来源：本地备份文件，或"当前连接设备的 running-config"。</summary>
    public IReadOnlyList<ConfigDiffSource> DiffSources
    {
        get
        {
            var sources = new List<ConfigDiffSource>
            {
                new("（当前设备的 running-config）", null, IsCurrentDevice: true),
            };
            sources.AddRange(Backups
                .OrderByDescending(b => b.FileName, StringComparer.Ordinal)
                .Select(b => new ConfigDiffSource(b.FileName, b.FilePath, IsCurrentDevice: false)));
            return sources;
        }
    }

    public ConfigDiffSource? DiffLeft
    {
        get => _diffLeft;
        set
        {
            if (SetProperty(ref _diffLeft, value))
            {
                DiffCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ConfigDiffSource? DiffRight
    {
        get => _diffRight;
        set
        {
            if (SetProperty(ref _diffRight, value))
            {
                DiffCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>只看变更行（默认开）：一份 2600 行的配置全列出来没法看，真正要看的只有改动的几行。</summary>
    public bool DiffOnlyChanges
    {
        get => _diffOnlyChanges;
        set
        {
            if (SetProperty(ref _diffOnlyChanges, value))
            {
                ApplyDiffFilter();
            }
        }
    }

    public string DiffSummary
    {
        get => _diffSummary;
        private set => SetProperty(ref _diffSummary, value);
    }

    private async Task RunDiffAsync()
    {
        if (DiffLeft is null || DiffRight is null)
        {
            DiffSummary = "请先选好左右两侧的来源。";
            return;
        }

        IsBusy = true;
        try
        {
            var left = await ReadSourceAsync(DiffLeft).ConfigureAwait(true);
            if (left is null)
            {
                return;
            }

            var right = await ReadSourceAsync(DiffRight).ConfigureAwait(true);
            if (right is null)
            {
                return;
            }

            var result = _diffService.Compare(left, right);
            _diffAll = result.Lines.ToList();
            ApplyDiffFilter();
            DiffSummary = $"{DiffLeft.Label}  →  {DiffRight.Label}｜{result.Summary}";
            if (result.IgnoredSamples.Count > 0)
            {
                DiffSummary += $"｜忽略样例：{string.Join(" / ", result.IgnoredSamples.Take(3))}";
            }
        }
        finally
        {
            IsBusy = false;
            ExportDiffCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// [检查保存状态]：读设备上的 running-config 与 startup-config 做对比 ——
    /// 回答现场最常问的一句话："我改完是不是忘了保存（write）？"
    /// 全是只读命令，不写设备。
    /// </summary>
    private async Task CheckSaveStateAsync()
    {
        if (!IsConnected)
        {
            StatusText = "需要先连接设备（在【连接】页连上 Console 或 Telnet）。";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "正在读取 running-config 与 startup-config…（只读）";
            var running = await AppServices.Commands
                .RunShowCommandAsync(Commands.ShowCommands.RunningConfig)
                .ConfigureAwait(true);
            var startup = await AppServices.Commands
                .RunShowCommandAsync(Commands.ShowCommands.StartupConfig)
                .ConfigureAwait(true);

            // startup-config 可能不存在（新设备还没存过盘）或该型号不认这条命令 —— 如实说，别当成"不一致"
            if (string.IsNullOrWhiteSpace(startup)
                || startup.Contains("Invalid input", StringComparison.OrdinalIgnoreCase)
                || startup.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                || startup.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || startup.Contains("no such", StringComparison.OrdinalIgnoreCase))
            {
                DiffSummary = "读不到 startup-config：这台设备可能还没有启动配置（从没保存过），"
                              + "或者该型号不支持 show startup-config。**这与「改没改过」是两回事**，"
                              + "请改用【备份】里的备份文件做对比。";
                _diffAll = new List<ConfigDiffLine>();
                ApplyDiffFilter();
                StatusText = DiffSummary;
                return;
            }

            // ⚠️ 左右顺序不能反：左 = running、右 = startup（DescribeSaveState 的文案与这个顺序绑定）
            var diff = _diffService.Compare(running, startup);
            _diffAll = diff.Lines.ToList();
            ApplyDiffFilter();
            DiffSummary = "运行配置 vs 启动配置｜" + ConfigDiffService.DescribeSaveState(diff) + "｜" + diff.Summary;
            StatusText = DiffSummary;
        }
        catch (Exception ex)
        {
            StatusText = $"检查保存状态失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ExportDiffCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 诊断/自检用：刷新备份列表 → 选好左右两侧 → 跑一次对比。
    /// 默认左 = 最新那份备份、右 = 当前设备的 running-config（"我改了配置，跟上一份比一比"）。
    /// </summary>
    public async Task RunDiffForDiagnosticsAsync()
    {
        await RefreshListAsync().ConfigureAwait(true);
        var sources = DiffSources;
        DiffLeft = sources.FirstOrDefault(s => !s.IsCurrentDevice) ?? sources.FirstOrDefault();
        DiffRight = sources.FirstOrDefault(s => s.IsCurrentDevice) ?? sources.Skip(1).FirstOrDefault();
        await RunDiffAsync().ConfigureAwait(true);
    }

    /// <summary>诊断/自检用：直接跑一次"检查保存状态"。</summary>
    public Task RunSaveStateForDiagnosticsAsync() => CheckSaveStateAsync();

    /// <summary>读一侧的内容：本地文件直接读；"当前设备"走 CLI 读 running-config。</summary>
    private async Task<string?> ReadSourceAsync(ConfigDiffSource source)
    {
        if (!source.IsCurrentDevice)
        {
            if (source.FilePath is null || !File.Exists(source.FilePath))
            {
                DiffSummary = $"备份文件不存在：{source.FilePath}";
                return null;
            }

            return await File.ReadAllTextAsync(source.FilePath).ConfigureAwait(true);
        }

        if (!IsConnected)
        {
            DiffSummary = "右侧选了“当前设备”，但当前没有连接。请先在【连接】页连上设备，或把右侧改成某个备份文件。";
            return null;
        }

        var raw = await AppServices.Commands
            .RunShowCommandAsync(Commands.ShowCommands.RunningConfig)
            .ConfigureAwait(true);
        return raw;
    }

    private void ApplyDiffFilter()
    {
        var lines = DiffOnlyChanges
            ? _diffAll.Where(l => l.Kind is ConfigDiffKind.Added or ConfigDiffKind.Removed).ToList()
            : _diffAll;
        DiffLines.ReplaceAll(lines);
    }

    private void ExportDiff()
    {
        if (_diffAll.Count == 0)
        {
            return;
        }

        var folder = _folderPicker?.Invoke();
        if (string.IsNullOrWhiteSpace(folder))
        {
            // 没注入目录选择器（自检环境）或用户取消：退化成"存到备份目录"
            folder = AppServices.Settings.BackupDirectory;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"配置对比_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var builder = new StringBuilder();
            builder.AppendLine($"# 配置对比 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"# 左：{DiffLeft?.Label}");
            builder.AppendLine($"# 右：{DiffRight?.Label}");
            builder.AppendLine($"# {DiffSummary}");
            builder.AppendLine();
            foreach (var line in _diffAll)
            {
                builder.AppendLine(line.ToDiffText());
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            StatusText = $"已导出对比结果：{path}";
            _shell.AddRecentOperation("配置对比导出", path);
        }
        catch (Exception ex)
        {
            StatusText = $"导出对比结果失败：{ex.Message}";
        }
    }

    protected override Task OnInitializeAsync()
    {
        return RefreshListAsync();
    }

    private async Task BackupAsync()
    {
        if (!_connections.IsConnected)
        {
            StatusText = "设备未连接：请先在【连接】页连接 Console 或 Telnet。";
            return;
        }

        IsBusy = true;
        StatusText = "正在读取 running-config 并保存…";
        try
        {
            var outcome = await AppServices.Backup.BackupRunningConfigAsync(AppServices.Settings.BackupDirectory)
                .ConfigureAwait(true);
            var path = outcome.FilePath;
            LastBackupText = $"最近备份：{path}";
            // 截断了就明说（文件里也有一行 `!` 警告，但用户多数不会去打开看）
            StatusText = outcome.PossiblyIncomplete
                ? $"⚠ 备份已保存，但**可能不完整**：{outcome.IncompleteReason}｜{path}"
                : $"备份完成：{path}";
            await RefreshListAsync().ConfigureAwait(true);
            _shell.ReportStatus(outcome.PossiblyIncomplete
                ? $"配置备份已保存（可能不完整：{outcome.IncompleteReason}）：{path}"
                : $"配置备份已保存：{path}");
            _shell.AddRecentOperation("备份 running-config",
                outcome.PossiblyIncomplete ? $"{path}（可能不完整）" : path,
                succeeded: !outcome.PossiblyIncomplete);
        }
        catch (Exception ex)
        {
            StatusText = $"备份失败：{ex.Message}";
            _shell.ReportStatus($"备份失败：{ex.Message}");
            _shell.AddRecentOperation("备份 running-config", ex.Message, succeeded: false);
            AppServices.Log.Warn("备份 running-config 失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 刷新备份列表。目录扫描**放到后台线程**：备份目录可能被改成网络盘/已拔出的移动盘，
    /// 这时 `Directory.Exists` / `GetFiles` 会一直等到 SMB 超时（几十秒）——放在 UI 线程上就是
    /// "打开【备份】页直接卡死"。失败时如实提示，并保留空列表（不是"备份全没了"的假象）。
    /// </summary>
    private async Task RefreshListAsync()
    {
        var selectedPath = SelectedBackup?.FilePath;
        var directory = AppServices.Settings.BackupDirectory;

        IReadOnlyList<BackupFileRecord> records;
        try
        {
            records = await Task.Run(() => BackupService.ListBackups(directory)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Backups.ReplaceAll(Array.Empty<BackupFileRecord>());
            OnPropertyChanged(nameof(CountText));
            StatusText = $"读取备份目录失败：{ex.Message}";
            AppServices.Log.Warn("读取备份目录失败", ex);
            return;
        }

        Backups.ReplaceAll(records);   // 一次 Reset 通知

        OnPropertyChanged(nameof(CountText));
        SelectedBackup = Backups.FirstOrDefault(b => string.Equals(b.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                         ?? Backups.FirstOrDefault();

        // 配置对比的下拉是从备份列表算出来的 → 列表一变要通知刷新；
        // 并给出默认：左 = 最新那份备份，右 = 当前设备（"我改了配置，跟上一份备份比一比"这个最常用的动作）
        OnPropertyChanged(nameof(DiffSources));
        var sources = DiffSources;
        if (DiffLeft is null || !sources.Contains(DiffLeft))
        {
            DiffLeft = sources.FirstOrDefault(s => !s.IsCurrentDevice) ?? sources.FirstOrDefault();
        }

        if (DiffRight is null || !sources.Contains(DiffRight))
        {
            DiffRight = sources.FirstOrDefault(s => s.IsCurrentDevice) ?? sources.FirstOrDefault();
        }
    }

    private void OpenFolder()
    {
        try
        {
            BackupService.OpenDirectory(AppServices.Settings.BackupDirectory);
            StatusText = $"已打开备份目录：{AppServices.Settings.BackupDirectory}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    private void OpenFile()
    {
        var record = SelectedBackup;
        if (record is null)
        {
            return;
        }

        try
        {
            BackupService.OpenFile(record.FilePath);
            StatusText = $"已打开备份：{record.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开备份失败：{ex.Message}";
        }
    }

    private void CopyPath()
    {
        var record = SelectedBackup;
        if (record is null)
        {
            return;
        }

        StatusText = AppServices.Clipboard.TrySetText(record.FilePath, out var error)
            ? "备份路径已复制到剪贴板。"
            : $"复制失败：{error}";
    }
}
