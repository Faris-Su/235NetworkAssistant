using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Resources;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 资源库（Phase 2）：VLAN / IP / 楼栋 / 房间查询，以及资源记录的查看、编辑、删除与导出。
/// 所有数据来自本地资源库（学校网络规划资料），必须始终提示“不是设备实时状态”。
/// </summary>
public sealed class ResourceLibraryViewModel : ViewModelBase, INavigationAware
{
    public const string AllBuildings = ResourceBuildingFilter.AllBuildings;

    private readonly IShellNavigator _shell;
    private readonly IResourceRepository _repository;
    private string _queryText = string.Empty;
    private string _querySummary = "输入 VLAN ID、IP、楼栋、房间或关键词后查询。";
    private string _statusText = "双击表格单元格可修改内容，修改后点[保存修改]。";
    private VlanRecord? _vlanMatch;
    private VlanIpMatch? _vlanIpMatch;
    private VlanRecord? _selectedVlan;
    private SwitchRecord? _selectedSwitch;
    private string _selectedBuilding = AllBuildings;

    /// <summary>单个列表最多往界面塞多少行（超出只影响显示；导出走全量，不受这个限制）。</summary>
    private const int MaxRowsPerList = 500;

    /// <summary>本次查询/筛选命中的真实条数（可能大于列表里显示的行数）。</summary>
    private int _filteredSwitchCount;
    private int _filteredVlanCount;
    private int _filteredLocationCount;

    /// <summary>是否已经跑过一次查询（没跑过时 chip 显示全库数量）。</summary>
    private bool _searchHasRun;
    private bool _isBusy;

    public ResourceLibraryViewModel(IShellNavigator shell, IResourceRepository repository)
    {
        _shell = shell;
        _repository = repository;
        Title = "资源库";

        SearchCommand = new RelayCommand(Search);
        ClearQueryCommand = new RelayCommand(() =>
        {
            QueryText = string.Empty;
            if (HasBuildingFilter)
            {
                SelectedBuilding = AllBuildings;   // setter 里会触发 ApplyBuildingFilter → Search
            }
            else
            {
                Search();
            }
        });

        SaveSwitchCommand = new AsyncRelayCommand(SaveSwitchAsync, () => SelectedSwitch is not null && !IsBusy);
        DeleteSwitchCommand = new AsyncRelayCommand(DeleteSwitchAsync, () => SelectedSwitch is not null && !IsBusy);
        SaveVlanCommand = new AsyncRelayCommand(SaveVlanAsync, () => SelectedVlan is not null && !IsBusy);
        DeleteVlanCommand = new AsyncRelayCommand(DeleteVlanAsync, () => SelectedVlan is not null && !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
        GoToSettingsCommand = new RelayCommand(() => _shell.NavigateTo("settings"));

        // 仓库是在 ConfigureAwait(false) 之后触发 Changed 的（保存/删除/导入都走线程池续体），
        // 直接刷新会从后台线程去改绑定到界面的 ObservableCollection → WPF 抛 NotSupportedException。
        // 必须回 UI 线程（SnmpViewModel 对 SnmpDevices.Changed 也是这么处理的）。
        _repository.Changed += (_, _) => UiThread.Post(RefreshFromRepository);
    }

    public BulkObservableCollection<VlanRecord> VlanResults { get; } = new();

    public BulkObservableCollection<SwitchRecord> SwitchResults { get; } = new();

    public BulkObservableCollection<LocationRecord> LocationResults { get; } = new();

    public ObservableCollection<string> BuildingOptions { get; } = new();

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value);
    }

    public string QuerySummary
    {
        get => _querySummary;
        private set => SetProperty(ref _querySummary, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>IP 反查到的规划网段（验收场景 6）。</summary>
    public VlanRecord? VlanMatch
    {
        get => _vlanMatch;
        private set
        {
            if (SetProperty(ref _vlanMatch, value))
            {
                OnPropertyChanged(nameof(HasVlanMatch));
                OnPropertyChanged(nameof(VlanMatchLines));
            }
        }
    }

    /// <summary>匹配依据提示：范围与网关网段一致才可信。</summary>
    public string VlanMatchConfidence => _vlanIpMatch is null
        ? string.Empty
        : _vlanIpMatch.IsConsistent
            ? "匹配依据：原表 IP 范围命中，且与网关/掩码推算的网段一致。"
            : "注意：仅原表 IP 范围命中，与网关/掩码推算的网段不一致（原表该行 IP 范围可能写错），请核对来源工作表。";

    public bool HasVlanMatch => VlanMatch is not null;

    public string VlanMatchLines => VlanMatch is null
        ? string.Empty
        : $"VLAN：{VlanMatch.VlanId}\r\n" +
          $"名字：{(string.IsNullOrWhiteSpace(VlanMatch.Name) ? "—" : VlanMatch.Name)}\r\n" +
          $"网关：{(string.IsNullOrWhiteSpace(VlanMatch.Gateway) ? "—" : VlanMatch.Gateway)}\r\n" +
          $"掩码：{(string.IsNullOrWhiteSpace(VlanMatch.Mask) ? "—" : VlanMatch.Mask)}\r\n" +
          $"IP 范围：{(string.IsNullOrWhiteSpace(VlanMatch.IpRange) ? "—" : VlanMatch.IpRange)}\r\n" +
          $"位置：{(string.IsNullOrWhiteSpace(VlanMatch.LocationText) ? "—" : VlanMatch.LocationText)}\r\n" +
          $"来源：{VlanMatch.SourceSheet}（{VlanMatch.College}{(string.IsNullOrWhiteSpace(VlanMatch.College) ? string.Empty : "｜")}{VlanMatch.SourceRow} 行）";

    public VlanRecord? SelectedVlan
    {
        get => _selectedVlan;
        set
        {
            if (SetProperty(ref _selectedVlan, value))
            {
                SaveVlanCommand.RaiseCanExecuteChanged();
                DeleteVlanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SwitchRecord? SelectedSwitch
    {
        get => _selectedSwitch;
        set
        {
            if (SetProperty(ref _selectedSwitch, value))
            {
                SaveSwitchCommand.RaiseCanExecuteChanged();
                DeleteSwitchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>交换机/场所列表的楼栋筛选。</summary>
    public string SelectedBuilding
    {
        get => _selectedBuilding;
        set
        {
            if (SetProperty(ref _selectedBuilding, value))
            {
                ApplyBuildingFilter();
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
                ExportCommand.RaiseCanExecuteChanged();
                SaveSwitchCommand.RaiseCanExecuteChanged();
                DeleteSwitchCommand.RaiseCanExecuteChanged();
                SaveVlanCommand.RaiseCanExecuteChanged();
                DeleteVlanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // 三个 chip 也跟着筛选走：以前永远显示全库数量，跟下面的"命中 N 条"打架，
    // 用户判断"筛选到底生效没有"看的就是这三个数。筛选/查询生效时显示"筛选后 / 全库"。
    public string SwitchCountText =>
        CountText("交换机资源", _filteredSwitchCount, _repository.Database.SwitchCount);

    public string VlanCountText =>
        CountText("VLAN 资源", _filteredVlanCount, _repository.Database.VlanCount);

    public string LocationCountText =>
        CountText("场所资源", _filteredLocationCount, _repository.Database.LocationCount);

    private string CountText(string title, int filtered, int total) =>
        !_searchHasRun || filtered == total ? $"{title}：{total}" : $"{title}：{filtered} / {total}";

    public string LastImportText => _repository.Database.LastImportedAt is null
        ? "尚未导入数据：请到【设置】页导入学校资源表。"
        : $"最近导入：{_repository.Database.LastImportedAt:yyyy-MM-dd HH:mm:ss}｜来源：{string.Join("；", _repository.Database.SourceFiles.Select(Path.GetFileName))}";

    public bool IsEmpty => _repository.Database.SwitchCount == 0
        && _repository.Database.VlanCount == 0
        && _repository.Database.LocationCount == 0;

    public string DataSourceNote => "本页为学校网络规划/资源信息（静态资料），不代表设备实时状态。";

    public RelayCommand SearchCommand { get; }

    public RelayCommand ClearQueryCommand { get; }

    public AsyncRelayCommand SaveSwitchCommand { get; }

    public AsyncRelayCommand DeleteSwitchCommand { get; }

    public AsyncRelayCommand SaveVlanCommand { get; }

    public AsyncRelayCommand DeleteVlanCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public RelayCommand GoToSettingsCommand { get; }

    protected override async Task OnInitializeAsync()
    {
        if (!_repository.IsLoaded)
        {
            await _repository.LoadAsync().ConfigureAwait(true);
        }

        RefreshFromRepository();
    }

    /// <summary>从 MAC/IP 页带 IP 跳转过来时自动查询。</summary>
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not string text || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 带条件跳过来时不要继承上一次停留时的楼栋筛选：否则查询结果会莫名少一截，
        // 而屏幕上只有"楼栋：xxx"那几个小字能解释（IP 反查面板更是一句话都不说）。
        if (HasBuildingFilter)
        {
            _selectedBuilding = AllBuildings;
            OnPropertyChanged(nameof(SelectedBuilding));
        }

        QueryText = text;
        Search();
        StatusText = $"已带入查询条件：{text}（来自 MAC/IP 页面）";
    }

    /// <summary>资源库变化（导入/编辑/删除/清空）后刷新统计、筛选与查询结果。</summary>
    private void RefreshFromRepository()
    {
        var buildings = _repository.GetBuildings();
        BuildingOptions.Clear();
        BuildingOptions.Add(AllBuildings);
        foreach (var building in buildings)
        {
            BuildingOptions.Add(building);
        }

        if (!BuildingOptions.Contains(SelectedBuilding))
        {
            _selectedBuilding = AllBuildings;
            OnPropertyChanged(nameof(SelectedBuilding));
        }

        OnPropertyChanged(nameof(SwitchCountText));
        OnPropertyChanged(nameof(VlanCountText));
        OnPropertyChanged(nameof(LocationCountText));
        OnPropertyChanged(nameof(LastImportText));
        OnPropertyChanged(nameof(IsEmpty));

        Search();
    }

    private void ApplyBuildingFilter()
    {
        if (IsEmpty)
        {
            return;
        }

        Search();
    }

    /// <summary>是否处于"某个楼栋"的筛选状态（"全部"或空 = 不筛）。规则见 <see cref="ResourceBuildingFilter"/>。</summary>
    private bool HasBuildingFilter => ResourceBuildingFilter.IsActive(SelectedBuilding);

    /// <summary>
    /// 该记录是否属于当前筛选的楼栋。交换机 / 场所 / VLAN-IP 三张表共用同一套规则
    /// （VLAN 那张表以前漏了这一步：选了楼栋它不跟着变，用户看到的就是"没反应"）。
    /// </summary>
    private bool MatchesBuilding(string? building) =>
        ResourceBuildingFilter.Matches(building, SelectedBuilding);

    private void Search()
    {
        var query = QueryText?.Trim() ?? string.Empty;

        // 整体替换（各只发一次 Reset 通知）：原来是 Clear() + 逐条 Add，
        // 三张表最多 3×500 行 = 1500 次 CollectionChanged —— 弱 CPU 上是纯开销。
        VlanMatch = null;

        if (query.Length == 0 && IsEmpty)
        {
            QuerySummary = "资源库为空：请到【设置】页导入本地设备清单和网络资源工作簿。";
            return;
        }

        // 楼栋筛选对三张表一视同仁：统一走 ResourceBuildingFilter（VLAN/IP 资源以前漏了这一步；
        // 统一调用还保证"规则改了/加第四张表"时不会再各写各的）
        var vlanRows = ResourceBuildingFilter
            .Apply(_repository.SearchVlans(query), SelectedBuilding, v => v.Building)
            .ToList();
        var switchRows = ResourceBuildingFilter
            .Apply(_repository.SearchSwitches(query), SelectedBuilding, s => s.Building)
            .ToList();
        var locationRows = ResourceBuildingFilter
            .Apply(_repository.SearchLocations(query), SelectedBuilding, l => l.Building)
            .ToList();

        VlanResults.ReplaceAll(vlanRows.Take(MaxRowsPerList));
        SwitchResults.ReplaceAll(switchRows.Take(MaxRowsPerList));
        LocationResults.ReplaceAll(locationRows.Take(MaxRowsPerList));

        _filteredVlanCount = vlanRows.Count;
        _filteredSwitchCount = switchRows.Count;
        _filteredLocationCount = locationRows.Count;
        _searchHasRun = true;
        OnPropertyChanged(nameof(SwitchCountText));
        OnPropertyChanged(nameof(VlanCountText));
        OnPropertyChanged(nameof(LocationCountText));

        // IP → VLAN → 网关 → 掩码 → 位置（验收场景 6）
        if (IpAddressHelper.TryParse(query, out _))
        {
            _vlanIpMatch = _repository.FindVlanByIp(query);
            VlanMatch = _vlanIpMatch?.Record;
        }
        else
        {
            _vlanIpMatch = null;
        }

        OnPropertyChanged(nameof(VlanMatchConfidence));

        // 选中楼栋时把范围写进摘要，用户一眼能看出筛选生效了；条数用**真实命中数**（不是被截断的列表长度）
        var scope = HasBuildingFilter ? $"（楼栋：{SelectedBuilding}）" : string.Empty;
        var truncated = vlanRows.Count > MaxRowsPerList ||
                        switchRows.Count > MaxRowsPerList ||
                        locationRows.Count > MaxRowsPerList;
        var truncNote = truncated ? $"（仅显示前 {MaxRowsPerList} 条）" : string.Empty;
        QuerySummary = VlanMatch is not null
            ? $"IP {query} 命中规划网段{scope}：VLAN {VlanMatch.VlanId}｜网关 {VlanMatch.Gateway}｜掩码 {VlanMatch.Mask}｜位置 {VlanMatch.LocationText}"
            : query.Length == 0
                ? $"资源库{scope}：VLAN {vlanRows.Count} 条 / 交换机 {switchRows.Count} 条 / 场所 {locationRows.Count} 条{truncNote}"
                : $"查询“{query}”{scope}：VLAN {vlanRows.Count} 条 / 交换机 {switchRows.Count} 条 / 场所 {locationRows.Count} 条{truncNote}";

        if (query.Length > 0)
        {
            _shell.ReportStatus($"资源库查询：{query} → {QuerySummary}");
        }
    }

    private async Task SaveSwitchAsync()
    {
        var record = SelectedSwitch;
        if (record is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _repository.UpdateSwitchAsync(record).ConfigureAwait(true);
            StatusText = ok
                ? $"已保存交换机记录：{record.DisplayName}"
                : $"保存失败：资源库中找不到 {record.Id}";
            _shell.ReportStatus(StatusText);
            _shell.AddRecentOperation("编辑交换机记录", StatusText, ok);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSwitchAsync()
    {
        var record = SelectedSwitch;
        if (record is null)
        {
            return;
        }

        if (MessageBox.Show(
                $"确定从资源库删除交换机记录吗？\r\n\r\n{record.DisplayName}（{record.LocationText}）",
                "删除交换机记录",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _repository.RemoveSwitchAsync(record.Id).ConfigureAwait(true);
            SelectedSwitch = null;
            StatusText = ok ? $"已删除：{record.DisplayName}" : "删除失败：记录不存在";
            _shell.ReportStatus(StatusText);
            _shell.AddRecentOperation("删除交换机记录", StatusText, ok);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveVlanAsync()
    {
        var record = SelectedVlan;
        if (record is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _repository.UpdateVlanAsync(record).ConfigureAwait(true);
            StatusText = ok
                ? $"已保存 VLAN 记录：VLAN {record.VlanId} {record.LocationText}"
                : $"保存失败：资源库中找不到 {record.Id}";
            _shell.ReportStatus(StatusText);
            _shell.AddRecentOperation("编辑 VLAN 记录", StatusText, ok);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteVlanAsync()
    {
        var record = SelectedVlan;
        if (record is null)
        {
            return;
        }

        if (MessageBox.Show(
                $"确定从资源库删除该 VLAN 记录吗？\r\n\r\nVLAN {record.VlanId} {record.LocationText}",
                "删除 VLAN 记录",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _repository.RemoveVlanAsync(record.Id).ConfigureAwait(true);
            SelectedVlan = null;
            StatusText = ok ? "已删除该 VLAN 记录。" : "删除失败：记录不存在";
            _shell.ReportStatus(StatusText);
            _shell.AddRecentOperation("删除 VLAN 记录", StatusText, ok);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择资源库导出目录",
            InitialDirectory = Directory.Exists(AppPaths.BackupDirectory) ? AppPaths.BackupDirectory : AppPaths.RootDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 导出**当前筛选/查询的结果**（而不是静默导出全库）：屏幕上是子集、文件里也是子集。
            // 用重新检索的全量结果，不用列表里那 500 条上限的显示数据。
            var query = QueryText?.Trim() ?? string.Empty;
            var exportVlans = ResourceBuildingFilter
                .Apply(_repository.SearchVlans(query), SelectedBuilding, v => v.Building)
                .ToList();
            var exportSwitches = ResourceBuildingFilter
                .Apply(_repository.SearchSwitches(query), SelectedBuilding, s => s.Building)
                .ToList();
            var exportLocations = ResourceBuildingFilter
                .Apply(_repository.SearchLocations(query), SelectedBuilding, l => l.Building)
                .ToList();

            var files = await _repository
                .ExportCsvAsync(dialog.FolderName, exportSwitches, exportVlans, exportLocations)
                .ConfigureAwait(true);

            var scope = HasBuildingFilter ? $"楼栋「{SelectedBuilding}」" : string.Empty;
            if (query.Length > 0)
            {
                scope = scope.Length > 0 ? $"{scope} + 查询“{query}”" : $"查询“{query}”";
            }

            var scopeText = scope.Length > 0
                ? $"（范围：{scope} → VLAN {exportVlans.Count} / 交换机 {exportSwitches.Count} / 场所 {exportLocations.Count}）"
                : "（全库）";
            StatusText = $"已导出 {files.Count} 个 CSV {scopeText} 到 {dialog.FolderName}";
            _shell.ReportStatus(StatusText);
            _shell.AddRecentOperation("导出资源库", StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
