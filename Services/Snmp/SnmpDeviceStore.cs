using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>落盘的单个 SNMP 字段（值 + 备注 + 可信度）。</summary>
public sealed class SnmpStoredField
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Confidence { get; set; } = nameof(SnmpOidConfidence.Confirmed);
}

/// <summary>落盘的表格（列 + 行 + 备注）。</summary>
public sealed class SnmpStoredSection
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public string? Note { get; set; }
    public bool Truncated { get; set; }

    /// <summary>
    /// 该表当时有多少行。**历史快照只留这个数、不留 Rows**（省空间）：
    /// 完整表格只在 <see cref="SnmpDeviceRecord.Latest"/> 里保留一份。
    /// </summary>
    public int RowCount { get; set; }
}

/// <summary>一次查询快照（**不包含 Community**，只有查询结果）。</summary>
public sealed class SnmpSnapshot
{
    public string QueriedAt { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public List<SnmpStoredField> Fields { get; set; } = new();
    public List<SnmpStoredSection> Sections { get; set; } = new();
}

/// <summary>一台设备（按 IP 作为稳定标识）的信息库记录。</summary>
public sealed class SnmpDeviceRecord
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>辅助识别字段（来自设备自身，不是 Community）：型号 / 序列号 / 软件版本。</summary>
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;

    public string LastQueryAt { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>最近一次完整结果。</summary>
    public SnmpSnapshot? Latest { get; set; }

    /// <summary>最近若干次快照（新的在前，上限 <see cref="SnmpDeviceStore.MaxSnapshotsPerDevice"/>）。</summary>
    public List<SnmpSnapshot> History { get; set; } = new();
}

/// <summary>设备信息库文件。</summary>
public sealed class SnmpDeviceDatabase
{
    public int Version { get; set; } = 1;
    public List<SnmpDeviceRecord> Devices { get; set; } = new();
}

/// <summary>
/// SNMP 设备信息库：软件查过哪些设备，就按 IP 保存查询结果与最后查询时间。
/// 安全要求：**只保存查询结果，绝不保存 Community**（Community 只存在内存里）。
/// 每个设备最多保留 <see cref="MaxSnapshotsPerDevice"/> 个历史快照，避免无限增长。
/// </summary>
public sealed class SnmpDeviceStore
{
    /// <summary>
    /// 每台设备保留多少个历史快照。界面只用到"历史快照 N 次"这个计数（SnmpViewModel），
    /// 表格内容只保留在 Latest 里，所以历史可以留得很轻：旧值 100 + 每次存完整大表，
    /// 实测把信息库文件撑到 28.8 MB（其中 61.5% 还是 JSON 缩进空白）。
    /// </summary>
    public const int MaxSnapshotsPerDevice = 20;
    public const int MaxDevices = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        // 不缩进：实测这个文件 61.5% 是缩进空白（28.8 MB → 11.7 MB 的内容）
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogService _log;
    private readonly SemaphoreSlim _sync = new(1, 1);

    /// <param name="log">日志服务。</param>
    /// <param name="filePath">自定义信息库文件路径（自动化验证用；为空时用 %LocalAppData%）。</param>
    public SnmpDeviceStore(ILogService log, string? filePath = null)
    {
        _log = log;
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(AppPaths.RootDirectory, "snmp-devices.json")
            : filePath;
    }

    /// <summary>设备信息库文件路径（设置页 / 诊断可查看）。</summary>
    public string FilePath { get; }

    public SnmpDeviceDatabase Database { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(FilePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            Database = JsonSerializer.Deserialize<SnmpDeviceDatabase>(json, Options) ?? new SnmpDeviceDatabase();
            SlimDown(_log);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // ⚠️ 读失败不能"静默重置成空库了事"（2026-09-22 改）：
            // 这个文件每次查询后都会全量重写，且 Latest 里带着整张 MAC/ARP 表，能到几十 MB。
            // 旧写法在这里把 Database 换成空库，界面不报错；用户随手点一次[查询]，
            // SaveAsync 就会把这份空库写回磁盘，**把原文件里有救的历史彻底抹掉**。
            // 现在把读不动的文件改名留档（人工还能捞回来），再以空库继续 —— 至少不销毁数据。
            QuarantineCorruptFile(ex);
            _log.Warn("读取 SNMP 设备信息库失败（已把原文件改名留档，不影响本次查询）", ex);
            Database = new SnmpDeviceDatabase();
        }
    }

    /// <summary>最后一次读盘失败时，被改名留档的原始文件路径（没发生过就是 null）。</summary>
    public string? QuarantinedFilePath { get; private set; }

    /// <summary>
    /// 把读不动的信息库文件改名成 `snmp-devices.json.corrupt-yyyyMMdd-HHmmss`，避免被后续写盘覆盖。
    /// 改不动（被占用/无权限）也只是放弃留档，不能因此让加载流程崩掉。
    /// </summary>
    private void QuarantineCorruptFile(Exception cause)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var target = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, target, overwrite: true);
            QuarantinedFilePath = target;
            _log.Info($"SNMP 信息库原文件已留档：{target}（原因：{cause.GetType().Name}）");
        }
        catch (Exception moveEx)
        {
            _log.Warn("SNMP 信息库原文件留档失败（继续用空库运行）", moveEx);
        }
    }

    /// <summary>记录一次查询结果：更新设备的最新信息 + 追加历史快照。</summary>
    public async Task SaveAsync(SnmpQueryResult result, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.TargetIp))
        {
            return;
        }

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = ToSnapshot(result);
            var device = Database.Devices.FirstOrDefault(d =>
                string.Equals(d.Ip, result.TargetIp, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                device = new SnmpDeviceRecord { Ip = result.TargetIp };
                Database.Devices.Insert(0, device);
                if (Database.Devices.Count > MaxDevices)
                {
                    Database.Devices.RemoveRange(MaxDevices, Database.Devices.Count - MaxDevices);
                }
            }

            device.LastQueryAt = result.QueriedAt.ToString("yyyy-MM-dd HH:mm:ss");
            device.LastStatus = result.StatusText;
            device.Latest = snapshot;
            device.Model = FieldValue(result, "设备型号");
            device.SerialNumber = FieldValue(result, "序列号");
            device.SoftwareVersion = FieldValue(result, "软件版本");

            // 历史只留"轻快照"：标量 + 每张表的元信息/行数，**不留表格内容**（内容在 Latest 里）
            device.History.Insert(0, ToLightSnapshot(snapshot));
            if (device.History.Count > MaxSnapshotsPerDevice)
            {
                device.History.RemoveRange(MaxSnapshotsPerDevice, device.History.Count - MaxSnapshotsPerDevice);
            }

            await SaveFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>清空设备信息库。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Database = new SnmpDeviceDatabase();
            await SaveFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>设备列表（按最后查询时间倒序）。</summary>
    public IReadOnlyList<SnmpDeviceRecord> Devices =>
        Database.Devices.OrderByDescending(d => d.LastQueryAt, StringComparer.Ordinal).ToList();

    private async Task SaveFileAsync(CancellationToken cancellationToken)
    {
        // 先写临时文件再改名（原子替换）。理由：这个文件几十 MB 且**每次查询后全量重写**，
        // 旧写法直接覆写目标文件，一旦在写的过程中断电/进程被杀/磁盘满，留下的是**半截 JSON**——
        // 下次启动 LoadAsync 解析失败，整台设备的历史就全没了（现在至少会留档，见 QuarantineCorruptFile）。
        // 改名在同目录内完成，Windows 上是原子的，读到的要么是旧的完整文件、要么是新的完整文件。
        var tempPath = FilePath + ".tmp";
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(Database, Options);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 只有"真的没写进去"才算保存失败。
            _log.Warn("保存 SNMP 设备信息库失败", ex);

            // 别把半截临时文件留在目录里：它会被用户当成"信息库文件"误读，
            // 也会让下一次 .tmp 写入与它混在一起。清不掉也只是警告。
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                _log.Warn($"清理 SNMP 信息库临时文件失败：{tempPath}", cleanupEx);
            }

            return;
        }

        // 文件已经写成功了。通知订阅者要单独兜住异常（现场 P0-6）：
        // 订阅者里改 WPF 集合会在非 UI 线程抛 NotSupportedException，
        // 以前这行放在同一个 try 里，于是"文件其实写成功了"却被报成"保存失败"。
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Warn("通知 SNMP 设备信息库更新失败（文件已保存成功）", ex);
        }
    }

    private static string FieldValue(SnmpQueryResult result, string name) =>
        result.Fields.FirstOrDefault(f => f.Name == name)?.Value ?? string.Empty;

    /// <summary>结果 → 落盘快照。这里只搬运查询结果，Community 从未进入本流程。</summary>
    private static SnmpSnapshot ToSnapshot(SnmpQueryResult result) => new()
    {
        QueriedAt = result.QueriedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        Status = result.Status.ToString(),
        StatusText = result.StatusText,
        Fields = result.Fields.Select(f => new SnmpStoredField
        {
            Name = f.Name,
            Value = f.Value,
            Note = f.Note,
            Confidence = f.Confidence.ToString(),
        }).ToList(),
        Sections = result.Sections.Select(s => new SnmpStoredSection
        {
            Title = s.Title,
            Category = s.Category.ToString(),
            Columns = s.Columns.ToList(),
            Rows = s.Rows.Select(r => r.ToList()).ToList(),
            Note = s.Note,
            Truncated = s.Truncated,
        }).ToList(),
    };

    /// <summary>
    /// 把完整快照变成"轻快照"：标量照留，表格只留标题/列名/行数，丢掉 Rows。
    /// 历史快照的唯一用途是计数（以及将来做"哪次查询有变化"的对比），完整表格Latest 里有一份就够。
    /// </summary>
    private static SnmpSnapshot ToLightSnapshot(SnmpSnapshot full) => new()
    {
        QueriedAt = full.QueriedAt,
        Status = full.Status,
        StatusText = full.StatusText,
        Fields = full.Fields,
        Sections = full.Sections.Select(s => new SnmpStoredSection
        {
            Title = s.Title,
            Category = s.Category,
            Columns = s.Columns,
            Rows = new List<List<string>>(),      // 内容不落盘
            Note = s.Note,
            Truncated = s.Truncated,
            RowCount = s.RowCount > 0 ? s.RowCount : s.Rows.Count,
        }).ToList(),
    };

    /// <summary>
    /// 给**已有**的信息库瘦身：历史条数收敛到上限、历史里的表格内容清掉。
    /// 只在加载后作用于内存，下一次保存（每次查询都会保存）就把文件改小。
    /// </summary>
    private void SlimDown(ILogService log)
    {
        var trimmedDevices = 0;
        var trimmedSnapshots = 0;
        foreach (var device in Database.Devices)
        {
            if (device.History.Count > MaxSnapshotsPerDevice)
            {
                trimmedSnapshots += device.History.Count - MaxSnapshotsPerDevice;
                device.History.RemoveRange(MaxSnapshotsPerDevice, device.History.Count - MaxSnapshotsPerDevice);
                trimmedDevices++;
            }

            for (var i = 0; i < device.History.Count; i++)
            {
                var entry = device.History[i];
                var hasRows = entry.Sections.Any(s => s.Rows.Count > 0);
                if (hasRows)
                {
                    device.History[i] = ToLightSnapshot(entry);
                }
            }
        }

        if (trimmedDevices > 0 || trimmedSnapshots > 0)
        {
            log.Info($"SNMP 信息库瘦身：{trimmedDevices} 台设备共裁掉 {trimmedSnapshots} 个超限历史快照，" +
                     $"历史里的表格内容也已清空（上限 {MaxSnapshotsPerDevice} 个/设备）");
        }
    }
}
