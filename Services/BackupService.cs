using System.Diagnostics;
using System.IO;
using System.Text;
using RuijieNetworkAssistant.Commands;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 备份结果。**必须带"可能不完整"的标记**：配置输出撞上翻页/缓冲上限时，
/// 文件看着正常但内容可能缺开头或结尾 —— 备份的价值全在完整性上。
/// </summary>
public sealed record BackupOutcome(string FilePath, bool PossiblyIncomplete, string? IncompleteReason);

/// <summary>配置备份。备份必须是明确的用户操作，且不自动写入设备。</summary>
public sealed class BackupService
{
    private readonly CommandService _commands;
    private readonly ConnectionService _connections;
    private readonly ILogService _log;

    public BackupService(CommandService commands, ConnectionService connections, ILogService log)
    {
        _commands = commands;
        _connections = connections;
        _log = log;
    }

    public async Task<BackupOutcome> BackupRunningConfigAsync(string directory, CancellationToken cancellationToken = default)
    {
        var plan = new CommandPlan
        {
            Title = "备份 running-config",
            Description = "读取当前运行配置并保存到本地文件。",
            ImpactScope = "只读操作：只读取设备上的 running-config 存成本地文件，不修改设备配置（保存配置请用顶栏 [保存配置]）。",
            RiskLevel = CommandRiskLevel.Safe,
            Commands = new[] { ShowCommands.RunningConfig },
        };

        var result = await _commands.ExecuteAsync(plan, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("读取 running-config 失败，未生成备份文件。");
        }

        // running-config 里的 hostname 是设备名的最可靠来源：用它命名备份文件，并回填会话设备名。
        var hostname = ShowOutputParser.TryExtractHostname(result.RawText)
                       ?? ShowOutputParser.TryExtractHostnameFromPrompt(result.RawText);
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            _connections.UpdateDeviceIdentity(hostname);
        }

        Directory.CreateDirectory(directory);
        var fileName = BuildFileName(hostname ?? _connections.DeviceName, _connections.ManagementAddress);
        // 文件名只有秒级时间戳：同一秒里第二次备份（两个实例同时点 / 时钟回拨）会撞名，
        // 直接写就是**静默覆盖**上一份备份 —— 撞名就加 _2/_3… 后缀，绝不覆盖已有备份。
        var path = EnsureUniquePath(Path.Combine(directory, fileName));
        await File.WriteAllTextAsync(path, result.RawText, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

        // 截断如实上报（页面上要给用户看，不能只写日志）
        var truncations = result.Outputs
            .Where(o => o.OutputTruncated)
            .Select(o => o.TruncationReason)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToList();
        var reason = truncations.Count > 0 ? string.Join("；", truncations) : null;
        _log.Info(reason is null
            ? $"已保存备份：{path}"
            : $"已保存备份（**可能不完整**：{reason}）：{path}");
        return new BackupOutcome(path, reason is not null, reason);
    }

    public static string BuildFileName(string? deviceName, string? managementAddress)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? managementAddress : deviceName;
        name = Sanitize(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "device";
        }

        return $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.cfg";
    }

    /// <summary>同目录下不覆盖已有文件：撞名就依次尝试 `名字_2.ext`、`名字_3.ext`…</summary>
    internal static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }

    public static void OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    /// <summary>打开一个备份文件（用系统默认编辑器）。</summary>
    public static void OpenFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("备份文件不存在。", filePath);
        }

        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }

    /// <summary>列出备份目录中的备份文件（按时间倒序）。备份历史来自目录扫描，不依赖数据库。</summary>
    public static IReadOnlyList<BackupFileRecord> ListBackups(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<BackupFileRecord>();
        }

        return new DirectoryInfo(directory)
            .GetFiles("*.cfg")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(BackupFileRecord.FromFile)
            .ToList();
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(invalid.Contains(ch) || ch == ':' ? '_' : ch);
        }

        return builder.ToString().Trim();
    }
}
