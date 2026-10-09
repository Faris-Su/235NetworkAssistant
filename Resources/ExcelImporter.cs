using System.IO;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.Resources;

public enum SheetKind
{
    SwitchTable,
    VlanTable,
    LabVlanTable,
    ServiceSegmentTable,
    UsedIpTable,
    Notice,
    Unknown,
}

public static class SheetKindText
{
    public static string ToChinese(this SheetKind kind) => kind switch
    {
        SheetKind.SwitchTable => "交换机位置/IP 表",
        SheetKind.VlanTable => "VLAN 汇总表",
        SheetKind.LabVlanTable => "实验室/房间 VLAN 表",
        SheetKind.ServiceSegmentTable => "业务网段表（转置布局）",
        SheetKind.UsedIpTable => "已用 IP 记录表",
        SheetKind.Notice => "说明/图片页",
        _ => "未识别",
    };
}

public sealed class SheetAnalysis
{
    public string SheetName { get; init; } = string.Empty;

    public SheetKind Kind { get; init; }

    /// <summary>表头所在行（0 基）。</summary>
    public int HeaderRowIndex { get; init; } = -1;

    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

    public int RowCount { get; init; }

    public int NonEmptyRowCount { get; init; }
}

/// <summary>
/// Excel 导入器（基础结构）。流程：Excel → 解析 → Import Preview →（用户确认后）写入本地资源库。
/// 只读取源文件，绝不修改原始 Excel；解析失败只记录异常，不丢弃原始文本。
/// </summary>
public sealed class ExcelImporter
{
    private const int MaxPreviewItems = 5000;
    private const int HeaderSearchDepth = 8;

    /// <summary>VLAN 列可能包含多值/带前缀的写法，用数字片段提取第一个合法 VLAN。</summary>
    private static readonly System.Text.RegularExpressions.Regex VlanNumberRegex =
        new(@"\d{1,4}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ILogService _log;

    public ExcelImporter(ILogService log) => _log = log;

    /// <summary>检查 Sheet、表头与数据结构（不做任何导入动作）。</summary>
    public IReadOnlyList<SheetAnalysis> Analyze(XlsxWorkbook workbook)
    {
        var result = new List<SheetAnalysis>();
        foreach (var sheet in workbook.Sheets)
        {
            result.Add(AnalyzeSheet(sheet));
        }

        return result;
    }

    public ImportResult BuildPreview(XlsxWorkbook workbook, ResourceDatabase? existing = null)
    {
        var result = new ImportResult
        {
            Preview = new ImportPreview { SourceFile = workbook.FilePath },
        };

        // 同一份表内的重复判定必须跨工作表生效（不同 Sheet 可能记录同一台交换机/同一个 VLAN）。
        var seenSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenVlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in workbook.Sheets)
        {
            // 读取阶段就失败的工作表必须**单独报错**，不能落到下面的 Kind 分支里
            // 被当成"说明/图片页已跳过"（那样用户看到"新增 0 条"会以为这份表本来就没数据，
            // 而实际是整张表的资源都没进来；见 XlsxSheet.LoadError 的注释）。
            if (!string.IsNullOrEmpty(sheet.LoadError))
            {
                result.Preview.Sheets.Add($"{sheet.Name}（读取失败，0 行）");
                result.Preview.Warnings.Add(
                    $"工作表“{sheet.Name}”读取失败：{sheet.LoadError}。"
                    + "这张表**没有被导入**，请用 Excel 另存为 .xlsx 后重试；如果仍失败请把该文件反馈给我们。");
                _log.Warn($"Excel 工作表读取失败：{sheet.Name}（{sheet.LoadError}）");
                continue;
            }

            var analysis = AnalyzeSheet(sheet);
            result.Preview.Sheets.Add($"{sheet.Name}（{analysis.Kind.ToChinese()}，{analysis.RowCount} 行）");

            switch (analysis.Kind)
            {
                case SheetKind.SwitchTable:
                    ParseSwitchSheet(sheet, analysis, existing, result, seenSwitches);
                    break;
                case SheetKind.VlanTable:
                    ParseVlanSummarySheet(sheet, analysis, existing, result, seenVlans);
                    break;
                case SheetKind.LabVlanTable:
                    ParseLabSheet(sheet, analysis, existing, result, seenVlans);
                    break;
                case SheetKind.ServiceSegmentTable:
                    ParseServiceSegmentSheet(sheet, analysis, existing, result, seenVlans);
                    break;
                case SheetKind.UsedIpTable:
                    result.Preview.Warnings.Add(
                        $"工作表“{sheet.Name}”是现场已用 IP 记录表，字段不完整（无 VLAN/掩码），V0.1 只保留结构、不导入。");
                    break;
                case SheetKind.Notice:
                    result.Preview.Warnings.Add($"工作表“{sheet.Name}”内容为说明/图片，已跳过。");
                    break;
                default:
                    result.Preview.Warnings.Add($"工作表“{sheet.Name}”未匹配到已知表头结构，已跳过（未做任何猜测解析）。");
                    break;
            }
        }

        _log.Info($"Excel 解析完成：{Path.GetFileName(workbook.FilePath)} → {result.Preview.SummaryText}");
        return result;
    }

    public SheetAnalysis AnalyzeSheet(XlsxSheet sheet)
    {
        var headerRow = -1;
        var kind = SheetKind.Unknown;
        var nonEmpty = 0;

        for (var row = 0; row < sheet.RowCount; row++)
        {
            if (!string.IsNullOrWhiteSpace(sheet.GetJoinedRowText(row)))
            {
                nonEmpty++;
            }
        }

        for (var row = 0; row < Math.Min(HeaderSearchDepth, sheet.RowCount); row++)
        {
            var headers = sheet.GetRowText(row);
            var joined = string.Join("|", headers);
            if (string.IsNullOrWhiteSpace(joined))
            {
                continue;
            }

            var detected = DetectKind(headers, joined);
            if (detected != SheetKind.Unknown)
            {
                headerRow = row;
                kind = detected;
                break;
            }
        }

        if (kind == SheetKind.Unknown)
        {
            kind = nonEmpty <= 1 ? SheetKind.Notice : SheetKind.Unknown;
        }

        return new SheetAnalysis
        {
            SheetName = sheet.Name,
            Kind = kind,
            HeaderRowIndex = headerRow,
            Headers = headerRow >= 0 ? sheet.GetRowText(headerRow) : Array.Empty<string>(),
            RowCount = sheet.RowCount,
            NonEmptyRowCount = nonEmpty,
        };
    }

    private static SheetKind DetectKind(IReadOnlyList<string> headers, string joined)
    {
        var hasUsedIp = headers.Any(h => h.Contains("使用IP", StringComparison.OrdinalIgnoreCase));
        if (hasUsedIp)
        {
            return SheetKind.UsedIpTable;
        }

        var hasVlan = headers.Any(h => h.Contains("vlan", StringComparison.OrdinalIgnoreCase));
        var hasIpAddress = headers.Any(h => h.Contains("IP地址", StringComparison.OrdinalIgnoreCase));
        var hasIpRange = headers.Any(h => h.Contains("IP范围", StringComparison.OrdinalIgnoreCase) || h.Contains("ip范围", StringComparison.OrdinalIgnoreCase));
        var hasLab = headers.Any(h => h.Contains("实验室名称", StringComparison.OrdinalIgnoreCase));
        var hasRoom = headers.Any(h => h.Contains("房号", StringComparison.OrdinalIgnoreCase));
        var hasGateway = headers.Any(h => h.Contains("网关", StringComparison.OrdinalIgnoreCase));

        if (hasVlan && (hasLab || hasIpAddress || hasRoom))
        {
            return SheetKind.LabVlanTable;
        }

        if (hasVlan && (hasIpRange || hasGateway))
        {
            return SheetKind.VlanTable;
        }

        var serviceMarkers = new[] { "监控摄像头", "门禁", "广播", "无线网络", "网络管理交换机" };
        if (serviceMarkers.Any(m => joined.Contains(m, StringComparison.Ordinal)))
        {
            return SheetKind.ServiceSegmentTable;
        }

        // 注意：“交换机IP和端口”是实验室表的列，不能当成交换机地址表。
        var hasSwitchIp = headers.Any(h =>
            h.Contains("交换机IP", StringComparison.OrdinalIgnoreCase) &&
            !h.Contains("端口", StringComparison.Ordinal));
        if (hasSwitchIp)
        {
            return SheetKind.SwitchTable;
        }

        return SheetKind.Unknown;
    }

    private void ParseSwitchSheet(
        XlsxSheet sheet,
        SheetAnalysis analysis,
        ResourceDatabase? existing,
        ImportResult result,
        HashSet<string> seen)
    {
        var headers = analysis.Headers;
        var ipColumn = FindColumn(headers, "交换机IP");
        if (ipColumn < 0)
        {
            result.Preview.Warnings.Add($"工作表“{sheet.Name}”未找到“交换机IP”列，已跳过。");
            return;
        }

        var buildingColumn = FindColumn(headers, "宿舍或行政楼", "楼栋", "学院");
        var floorColumn = FindColumn(headers, "楼层");
        var nameColumn = FindColumn(headers, "名称");
        var macColumn = FindColumn(headers, "物理地址");
        var serialColumn = FindColumn(headers, "交换机序列号");
        if (serialColumn < 0)
        {
            serialColumn = FindColumn(headers, "序列号");
        }

        var modelColumn = FindColumn(headers, "交换机型号");
        var remarkColumn = FindColumn(headers, "其它设备型号", "其他设备型号");
        var blankRows = 0;

        for (var row = analysis.HeaderRowIndex + 1; row < sheet.RowCount; row++)
        {
            var ipRaw = Raw(sheet, row, ipColumn);

            // 空行判定必须使用“原始单元格值”：学校表大量使用纵向合并单元格，
            // 合并区域内的第 2..n 行会被填充成与首行相同的值，但它们不是数据行。
            if (string.IsNullOrWhiteSpace(ipRaw) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, nameColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, macColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, serialColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, modelColumn)))
            {
                blankRows++;
                continue;
            }

            var name = Value(sheet, row, nameColumn);
            var mac = Value(sheet, row, macColumn);
            var serial = Value(sheet, row, serialColumn);
            var model = Value(sheet, row, modelColumn);

            if (string.IsNullOrWhiteSpace(ipRaw))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Switch,
                    Key = $"{sheet.Name}#{row + 1}",
                    Display = $"{Value(sheet, row, floorColumn)} {name}".Trim(),
                    Message = "该行没有交换机IP（备注行或非交换机行），已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            var building = Value(sheet, row, buildingColumn);
            if (string.IsNullOrWhiteSpace(building) && buildingColumn < 0)
            {
                building = Value(sheet, row, 0);
            }

            if (string.IsNullOrWhiteSpace(building))
            {
                building = sheet.Name;
            }

            var ip = IpAddressHelper.ExtractFirst(ipRaw);
            if (ip is null)
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Error,
                    Target = ImportTargetKind.Switch,
                    Key = $"{sheet.Name}#{row + 1}",
                    Display = $"{building} {Value(sheet, row, floorColumn)} {name}".Trim(),
                    Message = $"交换机IP 列内容无法解析为 IPv4：\"{Truncate(ipRaw)}\"（原始内容已保留在备注）",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            if (!seen.Add(ip))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Switch,
                    Key = ip,
                    Display = $"{building} {name}".Trim(),
                    Message = $"管理 IP {ip} 在同一文件中重复出现，已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            var record = new SwitchRecord
            {
                Id = ip,
                Building = building,
                Floor = Value(sheet, row, floorColumn),
                Name = name,
                ManagementIp = ip,
                MacAddress = mac,
                SerialNumber = serial,
                DeviceModel = model,
                Remark = BuildRemark(ipRaw, Value(sheet, row, remarkColumn), ip),
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            };

            var isUpdate = existing?.Switches.Any(s => string.Equals(s.Id, record.Id, StringComparison.OrdinalIgnoreCase)) == true;
            result.Switches.Add(record);
            AddItem(result, new ImportPreviewItem
            {
                Category = isUpdate ? ImportItemCategory.Update : ImportItemCategory.New,
                Target = ImportTargetKind.Switch,
                Key = record.Id,
                Display = $"{record.LocationText} {record.Name} {record.ManagementIp}".Trim(),
                Message = isUpdate ? "资源库中已存在同一管理 IP，导入时更新。" : "新增交换机记录。",
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            });
        }

        if (blankRows > 0)
        {
            result.Preview.Warnings.Add($"工作表“{sheet.Name}”有 {blankRows} 个空行/合并行，已按原表结构跳过。");
        }
    }

    private void ParseVlanSummarySheet(
        XlsxSheet sheet,
        SheetAnalysis analysis,
        ResourceDatabase? existing,
        ImportResult result,
        HashSet<string> seen)
    {
        var headers = analysis.Headers;
        var vlanColumn = FindColumn(headers, "vlan");
        var nameColumn = FindColumn(headers, "名字", "名称");
        var gatewayColumn = FindColumn(headers, "网关");
        var maskColumn = FindColumn(headers, "掩码");
        var rangeColumn = FindColumn(headers, "ip范围", "IP范围", "ip 范围");

        if (vlanColumn < 0)
        {
            result.Preview.Warnings.Add($"工作表“{sheet.Name}”未找到 VLAN 列，已跳过。");
            return;
        }

        for (var row = analysis.HeaderRowIndex + 1; row < sheet.RowCount; row++)
        {
            if (string.IsNullOrWhiteSpace(Raw(sheet, row, vlanColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, nameColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, gatewayColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, maskColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, rangeColumn)))
            {
                continue;
            }

            var vlanText = Value(sheet, row, vlanColumn);
            var joined = sheet.GetJoinedRowText(row);

            if (string.IsNullOrWhiteSpace(vlanText))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Vlan,
                    Key = $"{sheet.Name}#{row + 1}",
                    Display = Truncate(joined),
                    Message = "该行没有 VLAN 号（备注行或补充说明），已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            if (!int.TryParse(vlanText?.Trim(), out var vlanId) || vlanId <= 0 || vlanId > 4094)
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Error,
                    Target = ImportTargetKind.Vlan,
                    Key = $"{sheet.Name}#{row + 1}",
                    Display = Truncate(joined),
                    Message = $"VLAN 列内容无法解析为 1-4094 的数字：\"{Truncate(vlanText)}\"",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            var record = new VlanRecord
            {
                Id = $"vlan-{vlanId}",
                VlanId = vlanId,
                Name = Value(sheet, row, nameColumn),
                Gateway = Value(sheet, row, gatewayColumn),
                Mask = Value(sheet, row, maskColumn),
                IpRange = Value(sheet, row, rangeColumn),
                Building = sheet.Name,
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            };

            if (!seen.Add(record.Id))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Vlan,
                    Key = record.Id,
                    Display = $"VLAN {record.VlanId} {record.Name}".Trim(),
                    Message = $"VLAN {record.VlanId} 在同一文件的其他工作表已出现，已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            result.Vlans.Add(record);
            AddItem(result, new ImportPreviewItem
            {
                Category = existing?.Vlans.Any(v => v.Id == record.Id) == true ? ImportItemCategory.Update : ImportItemCategory.New,
                Target = ImportTargetKind.Vlan,
                Key = record.Id,
                Display = $"VLAN {record.VlanId} {record.Name} {record.Gateway}".Trim(),
                Message = "VLAN 资源。",
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            });
        }
    }

    private void ParseLabSheet(
        XlsxSheet sheet,
        SheetAnalysis analysis,
        ResourceDatabase? existing,
        ImportResult result,
        HashSet<string> seen)
    {
        var headers = analysis.Headers;
        var collegeColumn = FindColumn(headers, "学院");
        var roomColumn = FindColumn(headers, "房号");
        var labColumn = FindColumn(headers, "实验室名称");
        var portColumn = FindColumn(headers, "端口");
        var computerColumn = FindColumn(headers, "电脑数量");
        var ipCountColumn = FindColumn(headers, "IP数量");
        var vlanColumn = FindColumn(headers, "vlan");
        var ipColumn = FindIpColumn(headers);
        var maskColumn = FindColumn(headers, "掩码");
        var gatewayColumn = FindColumn(headers, "网关");
        var dnsColumn = FindColumn(headers, "DNS");
        var switchPortColumn = FindColumn(headers, "交换机IP和端口");
        var ownerColumn = FindColumn(headers, "负责人");
        var phoneColumn = FindColumn(headers, "联系电话");

        for (var row = analysis.HeaderRowIndex + 1; row < sheet.RowCount; row++)
        {
            // 同样：空行判定只看原始单元格，避免把合并单元格的填充值当成新数据行。
            if (string.IsNullOrWhiteSpace(Raw(sheet, row, roomColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, labColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, ipColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, vlanColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, maskColumn)) &&
                string.IsNullOrWhiteSpace(Raw(sheet, row, gatewayColumn)))
            {
                continue;
            }

            var room = Value(sheet, row, roomColumn);
            var lab = Value(sheet, row, labColumn);
            var ipRange = Value(sheet, row, ipColumn);
            var vlanText = Value(sheet, row, vlanColumn);
            var mask = Value(sheet, row, maskColumn);
            var gateway = Value(sheet, row, gatewayColumn);

            var vlanId = ParseVlanId(vlanText);
            var building = sheet.Name;
            // 用内容生成稳定主键：同一份表重复导入时按“更新”处理，不会产生重复记录。
            var id = $"lab|{sheet.Name}|{room}|{lab}|{vlanId?.ToString() ?? "na"}|{ipRange}";

            var record = new VlanRecord
            {
                Id = id,
                VlanId = vlanId ?? 0,
                Name = string.IsNullOrWhiteSpace(lab) ? room : lab,
                Gateway = gateway,
                Mask = mask,
                IpRange = ipRange,
                Building = building,
                Room = room,
                LabName = lab,
                College = Value(sheet, row, collegeColumn),
                PortInfo = Value(sheet, row, portColumn),
                ComputerCount = Value(sheet, row, computerColumn),
                IpCount = Value(sheet, row, ipCountColumn),
                Dns = Value(sheet, row, dnsColumn),
                Owner = Value(sheet, row, ownerColumn),
                Phone = Value(sheet, row, phoneColumn),
                Note = Value(sheet, row, switchPortColumn),
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            };

            if (!seen.Add(record.Id))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Vlan,
                    Key = record.Id,
                    Display = $"VLAN {record.VlanId} {record.Room} {record.LabName}".Trim(),
                    Message = "同一条实验室 VLAN 记录在文件中重复出现，已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = row + 1,
                });
                continue;
            }

            result.Vlans.Add(record);
            AddItem(result, new ImportPreviewItem
            {
                Category = existing?.Vlans.Any(v => v.Id == record.Id) == true ? ImportItemCategory.Update : ImportItemCategory.New,
                Target = ImportTargetKind.Vlan,
                Key = record.Id,
                Display = $"{(vlanId is null ? "VLAN 未知" : $"VLAN {vlanId}")} {record.Room} {record.LabName} {record.IpRange}".Trim(),
                Message = $"来源：{sheet.Name}（{analysis.Kind.ToChinese()}）",
                SourceSheet = sheet.Name,
                SourceRow = row + 1,
            });

            if (!string.IsNullOrWhiteSpace(room) || !string.IsNullOrWhiteSpace(lab))
            {
                var location = new LocationRecord
                {
                    Id = $"{building}|{room}|{lab}",
                    Building = building,
                    Room = room,
                    LabName = lab,
                    Note = record.College,
                    SourceSheet = sheet.Name,
                };

                if (result.Locations.All(l => l.Id != location.Id))
                {
                    result.Locations.Add(location);
                }
            }
        }
    }

    /// <summary>“业务类型横向、VLAN/IP/掩码/网关竖向”的转置表。</summary>
    private void ParseServiceSegmentSheet(
        XlsxSheet sheet,
        SheetAnalysis analysis,
        ResourceDatabase? existing,
        ImportResult result,
        HashSet<string> seen)
    {
        var headerRow = -1;
        for (var row = 0; row < Math.Min(HeaderSearchDepth, sheet.RowCount); row++)
        {
            var headers = sheet.GetRowText(row);
            if (headers.Any(h => new[] { "监控摄像头", "门禁", "广播", "无线网络", "网络管理交换机" }
                    .Any(m => h.Contains(m, StringComparison.Ordinal))))
            {
                headerRow = row;
                break;
            }
        }

        if (headerRow < 0)
        {
            result.Preview.Warnings.Add($"工作表“{sheet.Name}”未识别出业务类型表头，已跳过。");
            return;
        }

        var serviceHeaders = sheet.GetRowText(headerRow);
        var maxColumn = serviceHeaders.Count;
        var vlanRow = headerRow + 1;
        var ipRow = headerRow + 2;
        var maskRow = headerRow + 3;
        var gatewayRow = headerRow + 4;

        for (var column = 0; column < maxColumn; column++)
        {
            var service = Value(sheet, headerRow, column);
            if (string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            var vlanText = Value(sheet, vlanRow, column);
            var ipRange = Value(sheet, ipRow, column);
            var mask = Value(sheet, maskRow, column);
            var gateway = Value(sheet, gatewayRow, column);
            if (string.IsNullOrWhiteSpace(vlanText) && string.IsNullOrWhiteSpace(ipRange))
            {
                continue;
            }

            var vlanId = ParseVlanId(vlanText) ?? 0;
            var id = $"segment|{sheet.Name}|{service}";
            var record = new VlanRecord
            {
                Id = id,
                VlanId = vlanId,
                Name = $"{sheet.Name}-{service}",
                Gateway = gateway,
                Mask = mask,
                IpRange = ipRange,
                Building = sheet.Name,
                LabName = service,
                Note = "业务网段表（原表为转置布局）",
                SourceSheet = sheet.Name,
                SourceRow = headerRow + 1,
            };

            if (!seen.Add(record.Id))
            {
                AddItem(result, new ImportPreviewItem
                {
                    Category = ImportItemCategory.Skip,
                    Target = ImportTargetKind.Vlan,
                    Key = record.Id,
                    Display = record.Name,
                    Message = "同一业务网段已出现，已跳过。",
                    SourceSheet = sheet.Name,
                    SourceRow = headerRow + 1,
                });
                continue;
            }

            result.Vlans.Add(record);
            AddItem(result, new ImportPreviewItem
            {
                Category = existing?.Vlans.Any(v => v.Id == record.Id) == true ? ImportItemCategory.Update : ImportItemCategory.New,
                Target = ImportTargetKind.Vlan,
                Key = record.Id,
                Display = $"{record.Name} VLAN {vlanId} {record.IpRange}".Trim(),
                Message = "业务网段（转置表解析）。",
                SourceSheet = sheet.Name,
                SourceRow = headerRow + 1,
            });
        }
    }

    private static void AddItem(ImportResult result, ImportPreviewItem item)
    {
        // 计数**先加**：明细列表有上限（MaxPreviewItems），但"新增/更新/跳过/异常"这些数字
        // 必须是真实的——大表导入时用户就是靠它们判断要不要写入。被截断时明确标出来。
        var preview = result.Preview;
        switch (item.Category)
        {
            case ImportItemCategory.New:
                preview.TotalNew = Math.Max(0, preview.TotalNew) + 1;
                break;
            case ImportItemCategory.Update:
                preview.TotalUpdate = Math.Max(0, preview.TotalUpdate) + 1;
                break;
            case ImportItemCategory.Skip:
                preview.TotalSkip = Math.Max(0, preview.TotalSkip) + 1;
                break;
            case ImportItemCategory.Error:
                preview.TotalError = Math.Max(0, preview.TotalError) + 1;
                break;
        }

        if (result.Preview.Items.Count >= MaxPreviewItems)
        {
            preview.ItemsTruncated = true;
            return;
        }

        result.Preview.Items.Add(item);
    }

    private static int? ParseVlanId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 原表里 VLAN 列存在多值写法（如 “1480/1481”、“vlan1623”），取其中第一个合法 VLAN。
        foreach (System.Text.RegularExpressions.Match match in VlanNumberRegex.Matches(text))
        {
            if (int.TryParse(match.Value, out var value) && value is > 0 and < 4095)
            {
                return value;
            }
        }

        return null;
    }

    private static string Value(XlsxSheet sheet, int row, int column) =>
        column < 0 ? string.Empty : (sheet.GetValue(row, column) ?? string.Empty).Trim();

    /// <summary>原始单元格值（不做合并单元格填充），用于判断该行是否真的存在数据。</summary>
    private static string Raw(XlsxSheet sheet, int row, int column) =>
        column < 0 ? string.Empty : (sheet.GetValue(row, column, fillMerged: false) ?? string.Empty).Trim();

    /// <summary>IP 地址列：优先“IP地址”，其次恰好等于“IP”的列（避免误取“IP数量”列）。</summary>
    private static int FindIpColumn(IReadOnlyList<string> headers)
    {
        var exact = FindColumn(headers, "IP地址");
        if (exact >= 0)
        {
            return exact;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Trim().Equals("IP", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindColumn(IReadOnlyList<string> headers, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(headers[i]) &&
                    headers[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string BuildRemark(string? rawIp, string? otherDevice, string ip)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawIp) && !string.Equals(rawIp.Trim(), ip, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"原表交换机IP列：{rawIp.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(otherDevice))
        {
            parts.Add($"其它设备：{otherDevice.Trim()}");
        }

        return string.Join("；", parts);
    }

    private static string Truncate(string? text, int max = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(空)";
        }

        var normalized = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }
}
