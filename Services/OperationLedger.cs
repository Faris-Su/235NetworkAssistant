using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 操作台账：把界面上每一件"做过的事"追加到**按天一个 CSV** 文件里。
///
/// 为什么需要：交接班和回单都要回答"今天谁在哪台设备上改了什么"，
/// 而界面上那个"最近操作"列表只在内存里、最多 50 条、关掉程序就没了。
///
/// 三条硬规则：
///   ① **绝不落密码**：写盘前做一次脱敏（`password xxx` / `密码：xxx` 这类），
///      和项目里"Community / 密码不落盘"那条铁律保持一致；
///   ② **绝不因为台账写不了而影响主流程**：整个 Append 不抛异常，失败只记日志；
///   ③ 按天分文件，一天一个 `operations-yyyyMMdd.csv`，方便直接交给同事或存档。
/// </summary>
public sealed partial class OperationLedger
{
    private readonly ILogService? _log;
    private readonly string? _directoryOverride;
    private readonly object _sync = new();

    /// <param name="directoryOverride">自定义目录（自动化验证用；为空时用 %LocalAppData% 下的 operations）。</param>
    public OperationLedger(ILogService? log = null, string? directoryOverride = null)
    {
        _log = log;
        _directoryOverride = directoryOverride;
    }

    public string DirectoryPath => _directoryOverride ?? AppPaths.OperationLedgerDirectory;

    /// <summary>今天这份台账的文件路径（还没写过也可能不存在）。</summary>
    public string TodayFilePath => Path.Combine(DirectoryPath, $"operations-{DateTime.Now:yyyyMMdd}.csv");

    /// <summary>
    /// 追加一条。
    /// <paramref name="device"/> 是操作发生时的设备（主机名或管理地址）；没连设备时传空。
    /// </summary>
    public void Append(string category, string detail, bool succeeded, string? device = null)
    {
        try
        {
            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                var path = TodayFilePath;
                var isNew = !File.Exists(path);

                var builder = new StringBuilder();
                if (isNew)
                {
                    builder.AppendLine("时间,设备,操作,结果,内容");
                }

                builder.AppendLine(string.Join(
                    ',',
                    Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(device ?? string.Empty),
                    Csv(category),
                    Csv(succeeded ? "成功" : "失败"),
                    Csv(Redact(detail))));

                // 追加写：UTF-8 带 BOM，Excel 打开中文不乱码
                File.AppendAllText(path, builder.ToString(), new UTF8Encoding(true));
            }
        }
        catch (Exception ex)
        {
            // 台账写不了（磁盘满、目录被策略锁住、文件被占用）绝不能让主流程失败
            _log?.Warn("写操作台账失败（不影响本次操作）", ex);
        }
    }

    /// <summary>列出已有的台账文件（新的在前）。</summary>
    public IReadOnlyList<string> ListFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(DirectoryPath))
            {
                return Array.Empty<string>();
            }

            return System.IO.Directory
                .GetFiles(DirectoryPath, "operations-*.csv")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _log?.Warn("读取操作台账目录失败", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 脱敏：把"密码"后面跟的值抹成 `***`。
    /// 项目里 GUI 生成的命令不含密码，但**用户可能手工在 CLI 里敲 `password xxx`**，
    /// 而那条命令也会经 AddRecentOperation 进台账 —— 宁可多抹，不可漏写。
    /// </summary>
    internal static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return PasswordPattern().Replace(text, "$1***");
    }

    /// <summary>匹配 `password 值` / `密码 值` / `密码：值`（值到空白或行尾为止）。</summary>
    [GeneratedRegex(
        @"(?i)(\bpassword\s*[:=]?\s*|\bpasswd\s*[:=]?\s*|密码\s*[:：=]?\s*)\S+",
        RegexOptions.None)]
    private static partial Regex PasswordPattern();

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? text
            : '"' + text.Replace("\"", "\"\"") + '"';
    }
}
