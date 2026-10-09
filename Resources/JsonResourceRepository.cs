using System.Runtime.CompilerServices;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.Resources;

public sealed class JsonResourceRepository : IResourceRepository
{
    // ---------- 搜索串预计算 ----------
    // 每行把"所有可被搜到的字段"拼成一个大写字符串（字段间用 \u0001 分隔，避免跨字段撞词误命中），
    // 查询时每行只做一次 IndexOf —— 原来是 9 个字段逐个 Contains(OrdinalIgnoreCase)。
    // 411 台设备 × 9 字段 ≈ 3700 次字符串比较 → 411 次查找；导入/重载后缓存随旧对象一起被 GC。
    private readonly ConditionalWeakTable<SwitchRecord, string> _switchSearchTexts = new();
    private readonly ConditionalWeakTable<VlanRecord, string> _vlanSearchTexts = new();
    private readonly ConditionalWeakTable<LocationRecord, string> _locationSearchTexts = new();

    private string SwitchSearchText(SwitchRecord record) => _switchSearchTexts.GetValue(record, r => Join(
        r.ManagementIp, r.Name, r.Building, r.Floor, r.MacAddress, r.SerialNumber, r.DeviceModel, r.Remark, r.SourceSheet));

    private string VlanSearchText(VlanRecord record) => _vlanSearchTexts.GetValue(record, r => Join(
        r.IpRange, r.Gateway, r.Name, r.Building, r.Room, r.LabName, r.College, r.SourceSheet));

    private string LocationSearchText(LocationRecord record) => _locationSearchTexts.GetValue(record, r => Join(
        r.Building, r.Room, r.LabName, r.Note));

    private static string Join(params string?[] parts) =>
        string.Join('\u0001', parts.Select(p => p ?? string.Empty)).ToUpperInvariant();

    /// <summary>
    /// 空闲预热：把所有记录的搜索串先算出来（见上面 ConditionalWeakTable 的注释）。
    /// 用"非空关键字"触发一次完整遍历 —— 空关键字会跳过预计算，达不到预热目的。
    /// </summary>
    public void WarmUp()
    {
        _ = SearchSwitches("\u0001");
        _ = SearchVlans("\u0001");
        _ = SearchLocations("\u0001");
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly ILogService _log;
    private readonly string? _customFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ResourceDatabase _database = new();

    /// <param name="log">日志服务。</param>
    /// <param name="filePath">自定义资源库文件路径（自动化验证用；为空时使用 %LocalAppData%。</param>
    public JsonResourceRepository(ILogService log, string? filePath = null)
    {
        _log = log;
        _customFilePath = filePath;
    }

    public event EventHandler? Changed;

    public ResourceDatabase Database => _database;

    public bool IsLoaded { get; private set; }

    public string FilePath => string.IsNullOrWhiteSpace(_customFilePath) ? AppPaths.ResourceDatabaseFile : _customFilePath!;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(FilePath))
            {
                _database = new ResourceDatabase();
                IsLoaded = true;
                return;
            }

            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            _database = JsonSerializer.Deserialize<ResourceDatabase>(json, Options) ?? new ResourceDatabase();
            // 旧资源库里可能存有带工作表尾缀的名称；加载时规范化，避免筛选项重复。
            NormalizeBuildings(_database);
            IsLoaded = true;
            _log.Info($"资源库已加载：交换机 {_database.SwitchCount} / VLAN {_database.VlanCount} / 场所 {_database.LocationCount}");
        }
        catch (Exception ex)
        {
            _log.Warn("资源库读取失败，使用空资源库", ex);
            _database = new ResourceDatabase();
            IsLoaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(_database, Options);
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApplyImportAsync(ImportResult result, CancellationToken cancellationToken = default)
    {
        ResourceDatabase snapshot = _database;      // 默认值：万一在取快照前就抛，回滚成"没变"也安全
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 先留一份快照：落盘失败时必须把内存恢复回去。
            // 否则会出现"提示写入失败、但资源库计数/查询/导出都已经是新数据，重启后又全部消失"——
            // 用户会以为导入成功，还可能把这份"假数据"再导出成 CSV 扩散出去。
            snapshot = CloneDatabase();

            // 导入进来的楼栋先规范化（表名尾巴 "Vlan" 去掉），再合并进库
            NormalizeBuildings(result.Switches, result.Vlans, result.Locations);
            Merge(_database.Switches, result.Switches, s => s.Id);
            Merge(_database.Vlans, result.Vlans, v => v.Id);
            Merge(_database.Locations, result.Locations, l => l.Id);

            var source = result.Preview.SourceFile;
            if (!string.IsNullOrWhiteSpace(source) && !_database.SourceFiles.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                _database.SourceFiles.Add(source);
            }

            _database.LastImportedAt = DateTimeOffset.Now;
        }
        finally
        {
            _lock.Release();
        }

        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _database = snapshot;      // 回滚：内存与磁盘保持一致（都是导入前的状态）
            }
            finally
            {
                _lock.Release();
            }

            throw;
        }

        _log.Info($"资源库已更新：交换机 {_database.SwitchCount} / VLAN {_database.VlanCount} / 场所 {_database.LocationCount}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> UpdateSwitchAsync(SwitchRecord record, CancellationToken cancellationToken = default)
    {
        var updated = await MutateAsync(() =>
        {
            var index = _database.Switches.FindIndex(s => string.Equals(s.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _database.Switches[index] = record;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<bool> RemoveSwitchAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = await MutateAsync(
            () => _database.Switches.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) > 0,
            cancellationToken).ConfigureAwait(false);

        return removed;
    }

    public async Task<bool> UpdateVlanAsync(VlanRecord record, CancellationToken cancellationToken = default)
    {
        var updated = await MutateAsync(() =>
        {
            var index = _database.Vlans.FindIndex(v => string.Equals(v.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _database.Vlans[index] = record;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<bool> RemoveVlanAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = await MutateAsync(
            () => _database.Vlans.RemoveAll(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)) > 0,
            cancellationToken).ConfigureAwait(false);

        return removed;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await MutateAsync(() =>
        {
            _database.Switches.Clear();
            _database.Vlans.Clear();
            _database.Locations.Clear();
            _database.SourceFiles.Clear();
            _database.LastImportedAt = null;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        _log.Info("资源库已清空。");
    }

    public async Task<IReadOnlyList<string>> ExportCsvAsync(string directory, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<SwitchRecord> switches;
        List<VlanRecord> vlans;
        List<LocationRecord> locations;
        try
        {
            // 拿一份拷贝就放锁：导出是大 IO，不能占着写锁
            switches = _database.Switches.ToList();
            vlans = _database.Vlans.ToList();
            locations = _database.Locations.ToList();
        }
        finally
        {
            _lock.Release();
        }

        return await WriteCsvAsync(directory, switches, vlans, locations, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ExportCsvAsync(
        string directory,
        IReadOnlyList<SwitchRecord> switches,
        IReadOnlyList<VlanRecord> vlans,
        IReadOnlyList<LocationRecord> locations,
        CancellationToken cancellationToken = default) =>
        WriteCsvAsync(directory, switches, vlans, locations, cancellationToken);

    /// <summary>把给定的一批记录落成 3 个 CSV（全库导出与"导出当前筛选结果"共用这一段）。</summary>
    private async Task<IReadOnlyList<string>> WriteCsvAsync(
        string directory,
        IReadOnlyList<SwitchRecord> switches,
        IReadOnlyList<VlanRecord> vlans,
        IReadOnlyList<LocationRecord> locations,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var files = new List<string>();

        // 文件名只有秒级时间戳：同一秒里连点两次导出（例如"导出全库"再"导出当前筛选"）会撞名，
        // 直接写就是静默覆盖 —— 用与备份同一套规则加 _2/_3… 后缀。
        var switchFile = BackupService.EnsureUniquePath(Path.Combine(directory, $"resources-switches-{stamp}.csv"));
        await File.WriteAllTextAsync(
            switchFile,
            BuildCsv(
                new[] { "楼栋/位置", "楼层", "名称", "管理IP", "型号", "物理地址", "序列号", "备注", "来源表", "源行" },
                switches.Select(s => new[]
                {
                    s.Building, s.Floor, s.Name, s.ManagementIp, s.DeviceModel, s.MacAddress, s.SerialNumber, s.Remark, s.SourceSheet, s.SourceRow.ToString(),
                })),
            new UTF8Encoding(true),
            cancellationToken).ConfigureAwait(false);
        files.Add(switchFile);

        var vlanFile = BackupService.EnsureUniquePath(Path.Combine(directory, $"resources-vlans-{stamp}.csv"));
        await File.WriteAllTextAsync(
            vlanFile,
            BuildCsv(
                new[]
                {
                    "VLAN", "名称", "网关", "掩码", "IP范围", "楼栋", "楼层", "房间", "实验室", "学院",
                    "端口", "电脑数量", "IP数量", "DNS", "负责人", "联系电话", "备注", "来源表", "源行",
                },
                vlans.Select(v => new[]
                {
                    v.VlanId > 0 ? v.VlanId.ToString() : string.Empty,
                    v.Name, v.Gateway, v.Mask, v.IpRange, v.Building, v.Floor, v.Room, v.LabName, v.College,
                    v.PortInfo, v.ComputerCount, v.IpCount, v.Dns, v.Owner, v.Phone, v.Note, v.SourceSheet, v.SourceRow.ToString(),
                })),
            new UTF8Encoding(true),
            cancellationToken).ConfigureAwait(false);
        files.Add(vlanFile);

        var locationFile = BackupService.EnsureUniquePath(Path.Combine(directory, $"resources-locations-{stamp}.csv"));
        await File.WriteAllTextAsync(
            locationFile,
            BuildCsv(
                new[] { "楼栋", "楼层", "房间", "实验室/办公室", "学院/备注", "来源表" },
                locations.Select(l => new[] { l.Building, l.Floor, l.Room, l.LabName, l.Note, l.SourceSheet })),
            new UTF8Encoding(true),
            cancellationToken).ConfigureAwait(false);
        files.Add(locationFile);

        _log.Info($"资源库已导出 {files.Count} 个 CSV：{directory}");
        return files;
    }

    public IReadOnlyList<string> GetBuildings()
    {
        // 去重按 OrdinalIgnoreCase：源表里 "A11" 和 "a11"、"实训楼 " 和 "实训楼" 是同一个楼栋，
        // 否则下拉里会出现两个看着一样的选项，而只有其中一个能筛出全部行（筛选规则同理，见 ResourceBuildingFilter）
        var buildings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? raw)
        {
            var name = ResourceBuildingFilter.Normalize(raw);
            if (name.Length > 0 && !buildings.ContainsKey(name))
            {
                buildings[name] = name;
            }
        }

        foreach (var item in _database.Switches)
        {
            Consider(item.Building);
        }

        foreach (var item in _database.Locations)
        {
            Consider(item.Building);
        }

        // VLAN 资源自带位置；只有 VLAN 记录、没有交换机记录的位置也要能筛选。
        foreach (var item in _database.Vlans)
        {
            Consider(item.Building);
        }

        // Ordinal 而不是 CurrentCulture：每次导入/查询都要对全库重排，
        // 文化敏感排序要跑 ICU/区域规则，比 Ordinal 慢好几倍，而这里只需要"稳定、可预期"的顺序。
        return buildings.Keys.OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    /// <summary>整库楼栋名规范化（加载旧库时收敛一次，不用重新导入）。</summary>
    private static void NormalizeBuildings(ResourceDatabase database) =>
        NormalizeBuildings(database.Switches, database.Vlans, database.Locations);

    /// <summary>把三张表的 Building 规范化（导入时用；只动 Building，其它字段不碰）。</summary>
    private static void NormalizeBuildings(
        IEnumerable<SwitchRecord> switches,
        IEnumerable<VlanRecord> vlans,
        IEnumerable<LocationRecord> locations)
    {
        foreach (var item in switches)
        {
            item.Building = ResourceBuildingFilter.Normalize(item.Building);
        }

        foreach (var item in vlans)
        {
            item.Building = ResourceBuildingFilter.Normalize(item.Building);
        }

        foreach (var item in locations)
        {
            item.Building = ResourceBuildingFilter.Normalize(item.Building);
        }
    }

    /// <summary>统一的“修改 → 保存 → 通知”流程，避免各处遗漏 Changed 事件。</summary>
    private async Task<bool> MutateAsync(Func<bool> mutation, CancellationToken cancellationToken)
    {
        ResourceDatabase snapshot = _database;      // 同上：默认回滚成"没变"
        bool changed;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = CloneDatabase();      // 同 ApplyImportAsync：落盘失败要回滚
            changed = mutation();
        }
        finally
        {
            _lock.Release();
        }

        if (!changed)
        {
            return false;
        }

        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _database = snapshot;
            }
            finally
            {
                _lock.Release();
            }

            throw;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 深拷贝当前库（JSON 往返）。用于"落盘失败就回滚内存"——库里就几百条记录，这点开销可忽略。
    /// </summary>
    private ResourceDatabase CloneDatabase() =>
        JsonSerializer.Deserialize<ResourceDatabase>(JsonSerializer.Serialize(_database, Options), Options)
        ?? new ResourceDatabase();

    private static string BuildCsv(IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, headers);
        foreach (var row in rows)
        {
            AppendRow(builder, row);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var text = fields[i] ?? string.Empty;
            builder.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        builder.Append("\r\n");
    }

    public IReadOnlyList<SwitchRecord> SearchSwitches(string? query)
    {
        var switches = _database.Switches.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // 预计算后的"一行一个搜索串"：每行只做一次 IndexOf（原来 9 个字段逐个 Contains）。
            var upper = query.Trim().ToUpperInvariant();
            switches = switches.Where(s => SwitchSearchText(s).Contains(upper, StringComparison.Ordinal));
        }

        return switches
            .OrderBy(s => s.Building, StringComparer.Ordinal)
            .ThenBy(s => s.Floor, StringComparer.Ordinal)
            .Select(s => s.Clone())
            .ToList();
    }

    public IReadOnlyList<VlanRecord> SearchVlans(string? query)
    {
        var vlans = _database.Vlans.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            var upper = text.ToUpperInvariant();
            vlans = vlans.Where(v =>
                VlanSearchText(v).Contains(upper, StringComparison.Ordinal) ||
                (int.TryParse(text, out var vlanId) && v.VlanId == vlanId));
        }

        return vlans
            .OrderBy(v => v.VlanId)
            .ThenBy(v => v.SourceSheet, StringComparer.Ordinal)
            .Select(v => v.Clone())
            .ToList();
    }

    public IReadOnlyList<LocationRecord> SearchLocations(string? query)
    {
        var locations = _database.Locations.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var upper = query.Trim().ToUpperInvariant();
            locations = locations.Where(l => LocationSearchText(l).Contains(upper, StringComparison.Ordinal));
        }

        return locations
            .OrderBy(l => l.Display, StringComparer.Ordinal)
            .Select(l => l.Clone())
            .ToList();
    }

    public VlanIpMatch? FindVlanByIp(string ip)
    {
        if (!IpAddressHelper.TryParse(ip?.Trim(), out var address))
        {
            return null;
        }

        // 规划表里可能存在重叠/冲突的网段，因此按“范围命中 + 网关同网段 → 掩码最长前缀”排序，
        // 而不是简单取第一条记录。
        var candidates = new List<VlanIpMatch>();
        foreach (var vlan in _database.Vlans)
        {
            var inRange = IpAddressHelper.RangeContains(vlan.IpRange, address);
            var gateway = IpAddressHelper.ExtractFirst(vlan.Gateway);
            var hasMask = IpAddressHelper.TryParse(vlan.Mask, out var mask);
            var inSubnet = gateway is not null &&
                           hasMask &&
                           IpAddressHelper.TryParse(gateway, out var gatewayAddress) &&
                           IsInSameSubnet(gatewayAddress, address, mask);

            if (!inRange && !inSubnet)
            {
                continue;
            }

            var prefixLength = hasMask && IpAddressHelper.TryGetPrefixLength(mask, out var prefix) ? prefix : 0;
            candidates.Add(new VlanIpMatch(vlan, inRange, inSubnet, prefixLength));
        }

        return candidates
            .OrderByDescending(c => c.IsConsistent ? 1 : 0)
            .ThenByDescending(c => c.PrefixLength)
            .ThenByDescending(c => string.IsNullOrWhiteSpace(c.Record.Name) ? 0 : 1)
            .ThenBy(c => c.Record.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsInSameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var bytesA = a.GetAddressBytes();
        var bytesB = b.GetAddressBytes();
        var bytesM = mask.GetAddressBytes();
        if (bytesA.Length != 4 || bytesB.Length != 4 || bytesM.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((bytesA[i] & bytesM[i]) != (bytesB[i] & bytesM[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Merge<T>(List<T> target, IEnumerable<T> incoming, Func<T, string> keySelector)
    {
        foreach (var item in incoming)
        {
            var key = keySelector(item);
            var index = target.FindIndex(existing => string.Equals(keySelector(existing), key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                target[index] = item;
            }
            else
            {
                target.Add(item);
            }
        }
    }

    private static bool Contains(string? source, string text) =>
        !string.IsNullOrEmpty(source) && source.Contains(text, StringComparison.OrdinalIgnoreCase);
}
