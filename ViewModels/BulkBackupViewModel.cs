using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 批量备份：按资源库选一批设备，顺序逐台 Telnet 连上读 running-config 存文件。
///
/// 两条硬规则（都是现场教训，不是洁癖）：
///   ① **顺序执行**：`ConnectionService` 一次只持有一条连接，而且同时连几十台会把设备侧 vty 占满；
///   ② **第一台失败就整批停下**（默认开）：账号密码是整批共用的，密码填错就是拿错密码去撞几百台，
///      很多设备有登录失败锁定 —— 可能把运维账号在几百台上锁掉。停下让人先确认凭据。
/// </summary>
public sealed class BulkBackupViewModel : ViewModelBase
{
    private readonly IResourceRepository _repository;
    private readonly IShellNavigator _shell;
    private readonly BulkBackupService _service;
    private readonly Func<string?> _folderPicker;

    private string _buildingFilter = string.Empty;
    private string _keywordFilter = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _enablePassword = string.Empty;
    private int _port;
    private bool _stopOnFirstFailure = true;
    private bool _isBusy;
    private string _progressText = "尚未开始。";
    private string _summaryText = "未选择设备。";
    private string _statusHint = "勾选设备 → 填登录凭据 → 点[开始批量备份]。备份只读，不写设备。";
    private CancellationTokenSource? _cts;

    public BulkBackupViewModel(
        IShellNavigator shell,
        IResourceRepository repository,
        BulkBackupService service,
        Func<string?>? folderPicker = null)
    {
        _shell = shell;
        _repository = repository;
        _service = service;
        _folderPicker = folderPicker ?? (() => null);
        Title = "批量备份";

        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => !IsBusy && Devices.Count > 0);
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy && Devices.Count > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && Devices.Any(d => d.IsSelected));
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Results.Count > 0);
        OpenFolderCommand = new RelayCommand(() => BackupService.OpenDirectory(AppServices.Settings.BackupDirectory));

        // 用户名/密码预填当前连接设置里的值（用户通常刚在【连接】页填过），但**只读进内存**：
        // 备份用的是这个 VM 自己的副本，绝不去改用户保存的地址簿/设置。
        var current = AppServices.Settings.Telnet;
        _username = current.Username;
        _password = current.Password;
        _port = current.Port;
        EnablePassword = AppServices.EnablePassword;

        RefreshDevices();
    }

    public BulkObservableCollection<SelectableDeviceItem> Devices { get; } = new();
    public BulkObservableCollection<BulkBackupItem> Results { get; } = new();

    public string BuildingFilter
    {
        get => _buildingFilter;
        set { if (SetProperty(ref _buildingFilter, value)) { RefreshDevices(); } }
    }

    public string KeywordFilter
    {
        get => _keywordFilter;
        set { if (SetProperty(ref _keywordFilter, value)) { RefreshDevices(); } }
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    /// Telnet 端口（整批共用）。默认取【连接】页保存的端口；
    /// 现场有非标准端口的设备（经端口映射时尤其常见），所以必须可改。
    /// </summary>
    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, Math.Clamp(value, 1, 65535));
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string EnablePassword
    {
        get => _enablePassword;
        set => SetProperty(ref _enablePassword, value);
    }

    /// <summary>第一台失败就整批停下（默认开）。关掉的话会拿同一套凭据把剩下的都试一遍 —— 慎用。</summary>
    public bool StopOnFirstFailure
    {
        get => _stopOnFirstFailure;
        set => SetProperty(ref _stopOnFirstFailure, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshDevicesCommand.RaiseCanExecuteChanged();
                SelectAllCommand.RaiseCanExecuteChanged();
                ClearSelectionCommand.RaiseCanExecuteChanged();
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public string DirectoryText => $"备份目录：{AppServices.Settings.BackupDirectory}";

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public IReadOnlyList<string> Buildings =>
        _repository.Database.Switches
            .Select(s => s.Building)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

    public void RefreshDevices()
    {
        var items = _repository.Database.Switches
            .Where(s => !string.IsNullOrWhiteSpace(s.ManagementIp))
            .Where(s => string.IsNullOrWhiteSpace(BuildingFilter)
                        || string.Equals(s.Building, BuildingFilter, StringComparison.Ordinal))
            .Where(s => string.IsNullOrWhiteSpace(KeywordFilter)
                        || s.ManagementIp.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains(KeywordFilter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new DeviceTarget(
                IpAddressHelper.ExtractFirst(s.ManagementIp) ?? s.ManagementIp.Trim(),
                string.IsNullOrWhiteSpace(s.Name) ? s.Floor : s.Name,
                s.Building))
            .GroupBy(t => t.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Ip, StringComparer.Ordinal)
            .ToList();

        Devices.ReplaceAll(items.Select(t => new SelectableDeviceItem(t)));
        StatusHint = items.Count == 0
            ? "资源库里没有匹配的设备（先到【资源库】页导入，或放宽筛选条件）。"
            : $"候选 {items.Count} 台。备份会**顺序**逐台连（不会同时连几十台）。";
        SelectAllCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var device in Devices)
        {
            device.IsSelected = selected;
        }

        StartCommand.RaiseCanExecuteChanged();
        SummaryText = selected ? $"已选择 {Devices.Count} 台，点[开始批量备份]" : "未选择设备。";
    }

    /// <summary>诊断/自检用：按 IP 勾选设备。</summary>
    public void SelectIps(IEnumerable<string> ips)
    {
        var wanted = ips.Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            device.IsSelected = wanted.Contains(device.Ip);
        }

        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>诊断/自检用：直接跑一轮。</summary>
    public Task RunBulkBackupAsync() => StartAsync();

    private async Task StartAsync()
    {
        var targets = Devices.Where(d => d.IsSelected).Select(d => d.Target).ToList();
        if (targets.Count == 0)
        {
            StatusHint = "请先勾选要备份的设备。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusHint = "请先填登录用户名与密码（整批共用；只存在内存里，不写入任何文件）。";
            return;
        }

        var directory = AppServices.Settings.BackupDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            StatusHint = "备份目录没有配置（到【设置】页指定）。";
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Results.ReplaceAll(Array.Empty<BulkBackupItem>());
        ProgressText = $"0 / {targets.Count}";
        StatusHint = $"正在顺序备份 {targets.Count} 台…（每台：连接 → 读 running-config → 断开）";

        try
        {
            Directory.CreateDirectory(directory);

            // 用**自己的副本**，不动用户保存的设置（每台的 Host 由服务逐个覆盖）
            var template = AppServices.Settings.Telnet;
            var settings = new TelnetConnectionSettings
            {
                Host = string.Empty,
                Port = Port,
                Username = Username,
                Password = Password,
                AutoLogin = true,
                ConnectionTimeoutMs = template.ConnectionTimeoutMs,
                CommandTimeoutMs = template.CommandTimeoutMs,
                IdleQuietMs = template.IdleQuietMs,
            };

            var done = 0;
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var item = await _service
                    .BackupOneAsync(target, settings, EnablePassword, directory, token)
                    .ConfigureAwait(true);
                Results.Add(item);
                ProgressText = $"{++done} / {targets.Count}";

                // 「先试一台」：第一台就失败，说明凭据或网络很可能整体有问题 —— 停下，别去撞剩下的
                if (done == 1 && !item.Succeeded && StopOnFirstFailure)
                {
                    var reason = item.Error.Length > 0 ? item.Error : item.Status;
                    StatusHint = $"⚠ 第一台（{target.Ip}）就失败了：{reason}。"
                                 + $"为避免用同一套凭据去试剩下的 {targets.Count - 1} 台（很多设备有登录失败锁定），"
                                 + "**已整批停止**。请确认用户名/密码/Enable 密码与该设备可达性后重试。";
                    SummaryText = $"已停止：1 台失败｜共选 {targets.Count} 台";
                    return;
                }
            }

            var ok = Results.Count(r => r.Succeeded);
            var failed = Results.Count - ok;
            SummaryText = $"共 {Results.Count} 台｜成功 {ok}｜失败 {failed}";
            StatusHint = failed == 0
                ? $"全部完成：{ok} 台已备份到 {directory}。"
                : $"完成：成功 {ok} 台、失败 {failed} 台。失败原因见下表（点[导出 CSV] 可交给同事）。";
        }
        catch (OperationCanceledException)
        {
            SummaryText = $"已停止：完成 {Results.Count(r => r.Succeeded)} 台";
            StatusHint = "已停止（已完成的备份保留在表里，文件也已经落盘）。";
        }
        finally
        {
            // 一轮批量备份要在台账里留一条**汇总**（每台记一条会淹掉台账，什么都不记又等于没做过）
            if (Results.Count > 0)
            {
                var okCount = Results.Count(r => r.Succeeded);
                var failedItems = Results.Where(r => !r.Succeeded).Take(10)
                    .Select(r => $"{r.Ip} {r.Status}")
                    .ToList();
                _shell.AddRecentOperation(
                    "批量备份",
                    $"{SummaryText}",
                    okCount == Results.Count,
                    ledgerDetail: $"成功 {okCount} 台 / 失败 {Results.Count - okCount} 台；目录 {AppServices.Settings.BackupDirectory}"
                                  // 批量操作时"设备"列是空的（每台都已断开）→ 台账正文里必须自己带上设备清单，
                                  // 否则这条记录等于没说"备份了哪几台"
                                  + $"；设备：{DescribeIps()}"
                                  + (failedItems.Count > 0 ? $"；失败：{string.Join("；", failedItems)}" : string.Empty));
            }

            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 台账正文里那份设备清单（最多列 20 台，其余给"等 N 台"）。
    /// 批量操作不写清楚"动了哪些设备"，台账对交接班就没价值。
    /// </summary>
    private string DescribeIps()
    {
        var ips = Devices.Where(d => d.IsSelected).Select(d => d.Ip).ToList();
        if (ips.Count == 0)
        {
            ips = Results.Select(r => r.Ip).ToList();
        }

        return ips.Count <= 20
            ? string.Join(",", ips)
            : string.Join(",", ips.Take(20)) + $" 等 {ips.Count} 台";
    }

    private void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        StatusHint = "正在停止（当前这台读完就停）…";
        _cts.Cancel();
    }

    private void ExportCsv()
    {
        if (Results.Count == 0)
        {
            return;
        }

        var folder = _folderPicker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusHint = "已取消导出。";
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"批量备份_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var builder = new StringBuilder();
            builder.AppendLine(BulkBackupItem.CsvHeader);
            foreach (var item in Results)
            {
                builder.AppendLine(item.ToCsvLine());
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            StatusHint = $"已导出 {Results.Count} 条：{path}";
            _shell.AddRecentOperation("批量备份导出", path);
        }
        catch (Exception ex)
        {
            StatusHint = $"导出失败：{ex.Message}";
        }
    }
}
