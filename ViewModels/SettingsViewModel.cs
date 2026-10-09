using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 设置：网络资源库状态、Excel 导入预览、终端缓冲、备份目录、日志等级。
/// Phase 0 只生成导入预览（写入资源库在 Phase 2 开放）。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IShellNavigator _shell;
    private readonly AppSettings _settings;
    private readonly IResourceRepository _repository;
    private readonly ExcelImporter _importer;
    private string _previewSummary = "尚未分析 Excel。";
    private string _previewFile = "—";
    private bool _isBusy;
    private ImportResult? _lastImport;
    private string _writeStateText = "尚未写入资源库。";
    private bool _hasPreview;

    public SettingsViewModel(
        IShellNavigator shell,
        AppSettings settings,
        IResourceRepository repository,
        ExcelImporter importer)
    {
        _shell = shell;
        _settings = settings;
        _repository = repository;
        _importer = importer;
        Title = "设置";

        AnalyzeExcelCommand = new AsyncRelayCommand(AnalyzeExcelAsync, () => !IsBusy);
        WriteLibraryCommand = new AsyncRelayCommand(WriteLibraryAsync, () => HasPreview && !IsBusy);
        ExportLibraryCommand = new AsyncRelayCommand(ExportLibraryAsync, () => !IsBusy);
        ClearLibraryCommand = new AsyncRelayCommand(ClearLibraryAsync, () => !IsBusy);
        SaveSettingsCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        ApplyFieldLaptopPresetCommand = new RelayCommand(ApplyFieldLaptopPreset, () => !IsBusy);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        ReloadRepositoryCommand = new AsyncRelayCommand(ReloadRepositoryAsync);

        _repository.Changed += (_, _) => RefreshCounts();
    }

    public ObservableCollection<ImportPreviewItem> PreviewItems { get; } = new();

    public ObservableCollection<string> PreviewWarnings { get; } = new();

    public ObservableCollection<string> AnalyzedSheets { get; } = new();

    public IReadOnlyList<string> LogLevelOptions { get; } = new[] { "Debug", "Info", "Warning", "Error" };

    /// <summary>字体档位（界面与 CLI 可分别设置；默认以可读性优先）。</summary>
    public IReadOnlyList<string> FontScaleOptions { get; } = new[] { "小", "标准", "大" };

    public string UiFontScale
    {
        get => _settings.UiFontScale;
        set
        {
            if (_settings.UiFontScale == value)
            {
                return;
            }

            _settings.UiFontScale = value;
            OnPropertyChanged();
            AppearanceService.Apply();
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public string CliFontScale
    {
        get => _settings.CliFontScale;
        set
        {
            if (_settings.CliFontScale == value)
            {
                return;
            }

            _settings.CliFontScale = value;
            OnPropertyChanged();
            AppearanceService.Apply();
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public string ShortcutHelp =>
        "快捷键　Ctrl+Alt+C CLI　Ctrl+F 搜索　Ctrl+L 专注　Esc 退出\n" +
        "CLI　Enter 发送　↑/↓ 历史　Tab 补全　? 帮助　Ctrl+C 中断";

    // ---------- SNMP v2c（只读） ----------

    /// <summary>是否启用概览页的 SNMP 信息卡片。</summary>
    public bool SnmpEnabled
    {
        get => AppServices.Snmp.Enabled;
        set
        {
            if (AppServices.Snmp.Enabled == value)
            {
                return;
            }

            AppServices.Snmp.Enabled = value;
            OnPropertyChanged();
            _ = AppServices.SaveSettingsAsync();      // 非敏感设置落盘（Community 除外）
        }
    }

    /// <summary>只读 Community。等同密码：不落盘、不写日志，仅本次运行有效。</summary>
    public string SnmpCommunity
    {
        get => AppServices.Snmp.Community;
        set
        {
            if (AppServices.Snmp.Community == value)
            {
                return;
            }

            AppServices.Snmp.Community = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public int SnmpPort
    {
        get => AppServices.Snmp.Port;
        set
        {
            var port = Math.Clamp(value, 1, 65535);
            if (AppServices.Snmp.Port == port)
            {
                return;
            }

            AppServices.Snmp.Port = port;
            OnPropertyChanged();
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public int SnmpTimeoutMs
    {
        get => AppServices.Snmp.TimeoutMs;
        set
        {
            var timeout = Math.Clamp(value, 300, 10000);
            if (AppServices.Snmp.TimeoutMs == timeout)
            {
                return;
            }

            AppServices.Snmp.TimeoutMs = timeout;
            OnPropertyChanged();
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public int SnmpRetries
    {
        get => AppServices.Snmp.Retries;
        set
        {
            var retries = Math.Clamp(value, 0, 5);
            if (AppServices.Snmp.Retries == retries)
            {
                return;
            }

            AppServices.Snmp.Retries = retries;
            OnPropertyChanged();
            _ = AppServices.SaveSettingsAsync();
        }
    }

    public string SnmpHint =>
        "只读查询，不会修改配置。Community 仅保存在内存中，退出程序后需重新填写。";

    public int TerminalBufferChars
    {
        get => _settings.TerminalBufferChars;
        set
        {
            if (_settings.TerminalBufferChars != value)
            {
                _settings.TerminalBufferChars = value;
                OnPropertyChanged();
            }
        }
    }

    public int TerminalFlushIntervalMs
    {
        get => _settings.TerminalFlushIntervalMs;
        set
        {
            if (_settings.TerminalFlushIntervalMs != value)
            {
                _settings.TerminalFlushIntervalMs = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 退格键发送 DEL(0x7F)？默认 false = 发 Ctrl-H(0x08)（多数交换机/路由器）；
    /// 部分设备或 Linux 服务器认 0x7F。借鉴 PuTTY 的 "Backspace key" 设置。
    /// </summary>
    public bool CliBackspaceSendsDel
    {
        get => _settings.CliBackspaceSendsDel;
        set
        {
            if (_settings.CliBackspaceSendsDel != value)
            {
                _settings.CliBackspaceSendsDel = value;
                OnPropertyChanged();
            }
        }
    }

    public string BackupDirectory
    {
        get => _settings.BackupDirectory;
        set
        {
            if (_settings.BackupDirectory != value)
            {
                _settings.BackupDirectory = value;
                OnPropertyChanged();
            }
        }
    }

    public string LogLevelSetting
    {
        get => _settings.LogLevel;
        set
        {
            if (_settings.LogLevel != value)
            {
                _settings.LogLevel = value;
                OnPropertyChanged();
            }
        }
    }

    public bool RememberConnectionSettings
    {
        get => _settings.RememberConnectionSettings;
        set
        {
            if (_settings.RememberConnectionSettings != value)
            {
                _settings.RememberConnectionSettings = value;
                OnPropertyChanged();
            }
        }
    }

    public string SwitchCountText => $"交换机资源：{_repository.Database.SwitchCount}";

    public string VlanCountText => $"VLAN 资源：{_repository.Database.VlanCount}";

    public string LocationCountText => $"场所资源：{_repository.Database.LocationCount}";

    public string DataRootText => $"数据目录：{AppPaths.RootDirectory}";

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetProperty(ref _previewSummary, value);
    }

    public string PreviewFile
    {
        get => _previewFile;
        private set => SetProperty(ref _previewFile, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                // 所有 CanExecute 里带 !IsBusy 的命令都必须在这里刷新，
                // 否则按钮的可用状态会停留在上一次判断（“预览好了但[写入资源库]点不动”就是这个原因）。
                AnalyzeExcelCommand.RaiseCanExecuteChanged();
                WriteLibraryCommand.RaiseCanExecuteChanged();
                ExportLibraryCommand.RaiseCanExecuteChanged();
                ClearLibraryCommand.RaiseCanExecuteChanged();
                SaveSettingsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand AnalyzeExcelCommand { get; }

    public AsyncRelayCommand WriteLibraryCommand { get; }

    public AsyncRelayCommand ExportLibraryCommand { get; }

    public AsyncRelayCommand ClearLibraryCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    /// <summary>
    /// 「外勤小电脑预设」：把参数朝"少刷新、够用就好"调（见方法实现）。
    /// 面向 Celeron N4120 / 8GB 这类机器 —— 那里真正的瓶颈是单核与渲染，不是内存。
    /// </summary>
    public RelayCommand ApplyFieldLaptopPresetCommand { get; }

    private void ApplyFieldLaptopPreset()
    {
        // 终端：刷新次数减半、缓冲降到 6 万（整屏重写成本与缓冲长度近似线性）
        TerminalFlushIntervalMs = 120;
        TerminalBufferChars = 60_000;
        _shell.ReportStatus(
            "已套用「外勤小电脑预设」：终端刷新 120 ms、缓冲 60,000 字符 —— 记得点[保存设置]生效"
            + "（SNMP 自动刷新建议关闭或 ≥30 秒：这台机器一次 WALK 要几十秒）。");
    }

    public RelayCommand OpenBackupFolderCommand { get; }

    public AsyncRelayCommand ReloadRepositoryCommand { get; }

    /// <summary>是否已生成可写入的导入预览。</summary>
    public bool HasPreview
    {
        get => _hasPreview;
        private set
        {
            if (SetProperty(ref _hasPreview, value))
            {
                WriteLibraryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string WriteStateText
    {
        get => _writeStateText;
        private set => SetProperty(ref _writeStateText, value);
    }

    public string SourceFilesText => _repository.Database.SourceFiles.Count == 0
        ? "来源文件：—"
        : "来源文件：" + string.Join("；", _repository.Database.SourceFiles.Select(Path.GetFileName));

    public string LastImportedText => _repository.Database.LastImportedAt is null
        ? "最近导入：—"
        : $"最近导入：{_repository.Database.LastImportedAt:yyyy-MM-dd HH:mm:ss}";

    protected override async Task OnInitializeAsync()
    {
        if (!_repository.IsLoaded)
        {
            await _repository.LoadAsync().ConfigureAwait(true);
        }

        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SwitchCountText));
        OnPropertyChanged(nameof(VlanCountText));
        OnPropertyChanged(nameof(LocationCountText));
        OnPropertyChanged(nameof(SourceFilesText));
        OnPropertyChanged(nameof(LastImportedText));
    }

    /// <summary>把导入预览写入本地资源库（用户确认后才执行；原始 Excel 不会被修改）。</summary>
    private async Task WriteLibraryAsync()
    {
        var import = _lastImport;
        if (import is null)
        {
            WriteStateText = "没有可写入的导入预览。";
            return;
        }

        var writable = import.Preview.NewCount + import.Preview.UpdateCount;
        if (writable == 0)
        {
            WriteStateText = "预览中没有需要写入的记录（全部为跳过或异常）。";
            return;
        }

        var confirm = MessageBox.Show(
            $"将把以下内容写入本地资源库：\r\n\r\n" +
            $"新增 {import.Preview.NewCount} 条 / 更新 {import.Preview.UpdateCount} 条 / " +
            $"跳过 {import.Preview.SkipCount} 条 / 异常 {import.Preview.ErrorCount} 条\r\n" +
            $"解析结果：{import.SummaryText}\r\n\r\n" +
            $"来源文件：{Path.GetFileName(import.Preview.SourceFile)}\r\n" +
            $"（原始 Excel 不会被修改，写入位置：{AppPaths.ResourceDatabaseFile}）",
            "写入资源库",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.ApplyImportAsync(import).ConfigureAwait(true);
            _lastImport = null;
            HasPreview = false;
            WriteStateText = $"已写入资源库：交换机 {_repository.Database.SwitchCount} / VLAN {_repository.Database.VlanCount} / 场所 {_repository.Database.LocationCount}";
            RefreshCounts();
            _shell.ReportStatus($"资源库已更新：{WriteStateText}");
            _shell.AddRecentOperation("写入资源库", WriteStateText);
        }
        catch (Exception ex)
        {
            WriteStateText = $"写入失败：{ex.Message}";
            _shell.ReportStatus($"写入资源库失败：{ex.Message}");
            AppServices.Log.Error("写入资源库失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportLibraryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择资源库导出目录",
            InitialDirectory = Directory.Exists(BackupDirectory) ? BackupDirectory : AppPaths.RootDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var files = await _repository.ExportCsvAsync(dialog.FolderName).ConfigureAwait(true);
            WriteStateText = $"已导出 {files.Count} 个 CSV：{dialog.FolderName}";
            _shell.ReportStatus(WriteStateText);
            _shell.AddRecentOperation("导出资源库", WriteStateText);
        }
        catch (Exception ex)
        {
            WriteStateText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearLibraryAsync()
    {
        var confirm = MessageBox.Show(
            "确定要清空本地资源库吗？\r\n\r\n" +
            "交换机 / VLAN / 场所数据与来源记录都会被删除（设置、备份、CLI 输出不受影响）。\r\n" +
            "清空后可以重新导入 Excel。",
            "清空资源库",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.ClearAsync().ConfigureAwait(true);
            WriteStateText = "资源库已清空。";
            RefreshCounts();
            _shell.ReportStatus("资源库已清空。");
            _shell.AddRecentOperation("清空资源库", "用户确认清空");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await AppServices.SaveSettingsAsync().ConfigureAwait(true);
            AppServices.Log.MinimumLevel = Enum.TryParse<LogLevel>(LogLevelSetting, ignoreCase: true, out var level)
                ? level
                : LogLevel.Info;
            _shell.ReportStatus("设置已保存。");
            _shell.AddRecentOperation("保存设置", $"终端缓冲 {TerminalBufferChars} 字符");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            BackupService.OpenDirectory(BackupDirectory);
        }
        catch (Exception ex)
        {
            _shell.ReportStatus($"打开备份目录失败：{ex.Message}");
        }
    }

    private async Task ReloadRepositoryAsync()
    {
        await _repository.LoadAsync().ConfigureAwait(true);
        RefreshCounts();
        _shell.ReportStatus("资源库已重新加载。");
    }

    /// <summary>
    /// 分析 Excel 并生成导入预览（新增/更新/跳过/异常）。
    /// 只读源文件：不修改 Excel，也不写入资源库。
    /// </summary>
    private async Task AnalyzeExcelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择学校资源表（.xlsx）",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await AnalyzeFileAsync(dialog.FileName).ConfigureAwait(true);
    }

    /// <summary>
    /// 分析指定 Excel 并生成导入预览（真正干活的部分，独立出来便于自动化验证）。
    /// 只读源文件：不修改 Excel，也不写入资源库。
    /// </summary>
    internal async Task AnalyzeFileAsync(string path)
    {
        IsBusy = true;
        PreviewItems.Clear();
        PreviewWarnings.Clear();
        AnalyzedSheets.Clear();
        PreviewSummary = "正在解析…";

        try
        {
            var result = await Task.Run(() =>
            {
                var workbook = XlsxWorkbookReader.Read(path);
                return _importer.BuildPreview(workbook, _repository.Database);
            }).ConfigureAwait(true);

            PreviewFile = path;
            foreach (var sheet in result.Preview.Sheets)
            {
                AnalyzedSheets.Add(sheet);
            }

            foreach (var warning in result.Preview.Warnings)
            {
                PreviewWarnings.Add(warning);
            }

            const int maxPreviewRows = 2000;
            foreach (var item in result.Preview.Items.Take(maxPreviewRows))
            {
                PreviewItems.Add(item);
            }

            // 明细被截断要说清楚：表格里只有前 N 条，但上面的新增/更新/跳过/异常是**全量**计数
            var listNote = result.Preview.Items.Count > maxPreviewRows
                ? $"（明细表格只显示前 {maxPreviewRows} 条，共 {result.Preview.Items.Count} 条）"
                : result.Preview.ItemsTruncated
                    ? $"（明细只保留了前 {result.Preview.Items.Count} 条，计数仍为全量）"
                    : string.Empty;
            PreviewSummary =
                $"{Path.GetFileName(path)}：{result.Preview.SummaryText}｜解析出 {result.SummaryText}{listNote}";
            _lastImport = result;
            HasPreview = true;
            WriteStateText = "预览已生成，确认无误后点[写入资源库]。";
            _shell.ReportStatus($"导入预览已生成：{result.Preview.SummaryText}");
            _shell.AddRecentOperation("生成导入预览", $"{Path.GetFileName(path)}：{result.Preview.SummaryText}");
        }
        catch (Exception ex)
        {
            PreviewSummary = $"解析失败：{ex.Message}";
            _shell.ReportStatus($"Excel 解析失败：{ex.Message}");
            AppServices.Log.Error("Excel 解析失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
