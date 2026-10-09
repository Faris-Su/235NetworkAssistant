using System.IO;

namespace RuijieNetworkAssistant.Models;

/// <summary>本地配置备份文件（备份历史列表的一行）。</summary>
public sealed class BackupFileRecord
{
    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>从文件名解析出的设备名（DeviceName_YYYYMMDD_HHmmss.cfg）。</summary>
    public string DeviceName { get; init; } = string.Empty;

    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:F1} KB";

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public static BackupFileRecord FromFile(FileInfo info)
    {
        var name = Path.GetFileNameWithoutExtension(info.Name);
        var device = name;

        // 文件名格式：设备名_YYYYMMDD_HHmmss.cfg（设备名本身可能包含下划线与点号）
        var parts = name.Split('_');
        if (parts.Length >= 3 &&
            parts[^1].Length == 6 && parts[^1].All(char.IsDigit) &&
            parts[^2].Length == 8 && parts[^2].All(char.IsDigit))
        {
            device = string.Join("_", parts[..^2]);
        }

        return new BackupFileRecord
        {
            FileName = info.Name,
            FilePath = info.FullName,
            SizeBytes = info.Length,
            CreatedAt = new DateTimeOffset(info.LastWriteTime),
            DeviceName = device,
        };
    }
}
