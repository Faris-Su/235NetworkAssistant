using System.Text.RegularExpressions;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 设备提示符检测：判断一条命令的输出是否已经回到 CLI 提示符（命令结束）。
/// 支持 Host#、Host&gt;、Host(config)#、Host(config-if-GigabitEthernet 0/1)# 等形态。
/// 分页场景下“看到提示符”就是翻页结束的标志。
/// </summary>
public static partial class CliPromptDetector
{
    [GeneratedRegex(
        @"(?m)^\s*(?<name>[A-Za-z0-9_.\-]{1,64})(?:\([^)\r\n]{0,64}\))?\s*[#>]\s*$")]
    private static partial Regex PromptLine();

    /// <summary>输出（最后一屏）是否已经以设备提示符结尾。</summary>
    public static bool EndsWithPrompt(string? rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
        {
            return false;
        }

        var text = CliOutputNormalizer.TrimBlankEdges(CliOutputNormalizer.Normalize(rawOutput));
        if (text.Length == 0)
        {
            return false;
        }

        var lastLine = text[(text.LastIndexOf('\n') + 1)..].Trim();
        return lastLine.Length > 0 && PromptLine().IsMatch(lastLine);
    }

    /// <summary>从输出里取提示符中的设备名（取最后一个提示符行）。</summary>
    public static string? TryGetPromptName(string? rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
        {
            return null;
        }

        var text = CliOutputNormalizer.Normalize(rawOutput);
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var match = PromptLine().Match(line.Trim());
            if (match.Success)
            {
                name = match.Groups["name"].Value;
            }
        }

        return name;
    }

    /// <summary>
    /// 从输出**末尾的提示符**判定权限级别：
    /// 结尾是 `#`（含 `Host(config)#`）→ 特权模式；结尾是 `>` → 普通模式；没有提示符 → 未知。
    /// </summary>
    public static DevicePrivilegeLevel DetectPrivilegeLevel(string? rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
        {
            return DevicePrivilegeLevel.Unknown;
        }

        var text = CliOutputNormalizer.TrimBlankEdges(CliOutputNormalizer.Normalize(rawOutput));
        if (text.Length == 0)
        {
            return DevicePrivilegeLevel.Unknown;
        }

        var lastLine = text[(text.LastIndexOf('\n') + 1)..].Trim();
        if (lastLine.Length == 0 || !PromptLine().IsMatch(lastLine))
        {
            return DevicePrivilegeLevel.Unknown;
        }

        return lastLine[^1] switch
        {
            '#' => DevicePrivilegeLevel.Privileged,
            '>' => DevicePrivilegeLevel.User,
            _ => DevicePrivilegeLevel.Unknown,
        };
    }

    /// <summary>设备是否正在要求输入 Enable 密码（`Password:` / `密码：`）。</summary>
    public static bool IsEnablePasswordPrompt(string? rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
        {
            return false;
        }

        var text = CliOutputNormalizer.Normalize(rawOutput);
        var tail = text.Length > 60 ? text[^60..] : text;
        return PasswordPromptRegex().IsMatch(tail);
    }

    [GeneratedRegex(@"(?i)password\s*[:：]\s*$")]
    private static partial Regex PasswordPromptRegex();
}
