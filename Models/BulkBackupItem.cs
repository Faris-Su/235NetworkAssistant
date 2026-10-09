namespace RuijieNetworkAssistant.Models;

/// <summary>批量备份里一台设备的结果。</summary>
public sealed class BulkBackupItem
{
    public string Ip { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Building { get; init; } = string.Empty;

    /// <summary>成功 / 认证失败 / 连不上 / 读取失败 / 已跳过。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>落盘的备份文件路径（成功时才有）。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>失败原因（给用户看的原话）。</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>备份内容是否可能不完整（撞上翻页/缓冲上限）——**必须显示出来**，否则用户以为备份是完整的。</summary>
    public string IncompleteReason { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }

    public bool Succeeded => Status == "成功";

    public const string CsvHeader = "IP,名称,楼栋,结果,文件,失败原因,可能不完整,耗时(ms)";

    public string ToCsvLine() => string.Join(
        ',',
        Csv(Ip), Csv(Name), Csv(Building), Csv(Status), Csv(FilePath), Csv(Error), Csv(IncompleteReason), ElapsedMs.ToString());

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? text
            : '"' + text.Replace("\"", "\"\"") + '"';
    }
}
