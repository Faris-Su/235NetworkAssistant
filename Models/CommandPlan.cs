namespace RuijieNetworkAssistant.Models;

public enum CommandRiskLevel
{
    Safe,
    Caution,
    Dangerous,
}

/// <summary>
/// 命令生成结果。所有 GUI 配置操作必须先产出 CommandPlan，再由 Command Preview 展示给用户确认。
/// 只有用户点击“执行”才会真正发送到设备。
/// </summary>
public sealed class CommandPlan
{
    public string Title { get; init; } = string.Empty;

    /// <summary>操作说明，例如“将 Gi0/1-3 配置到 VLAN 100”。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>影响范围说明，危险操作必须明确写出。</summary>
    public string ImpactScope { get; init; } = string.Empty;

    public CommandRiskLevel RiskLevel { get; init; } = CommandRiskLevel.Safe;

    public IReadOnlyList<string> Commands { get; init; } = Array.Empty<string>();

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;

    public bool IsDangerous => RiskLevel != CommandRiskLevel.Safe;

    public string CommandsText => string.Join(Environment.NewLine, Commands);
}

public sealed class CommandOutput
{
    public string Command { get; init; } = string.Empty;

    public string RawOutput { get; init; } = string.Empty;

    public bool Succeeded { get; init; } = true;

    public string? Error { get; init; }

    /// <summary>
    /// 本次输出**可能不完整**：超过输出缓冲上限被丢了开头，或撞上自动翻页上限。
    /// 备份/导出必须据此提示用户，不能让人拿到一份"看着正常"的残缺配置。
    /// </summary>
    public bool OutputTruncated { get; init; }

    /// <summary>截断原因（给界面直接显示）。</summary>
    public string? TruncationReason { get; init; }
}

/// <summary>执行结果。原始 CLI 输出必须完整保留，解析失败也不允许丢弃。</summary>
public sealed class CommandExecutionResult
{
    public CommandPlan Plan { get; init; } = new();

    public List<CommandOutput> Outputs { get; init; } = new();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    public bool Succeeded => Outputs.All(o => o.Succeeded);

    /// <summary>
    /// 失败时给人看的一句话原因（取第一条失败记录）。
    /// 用途：状态栏/最近操作直接显示"为什么失败"，而不是只显示"失败"两个字。
    /// 注意不要拿它当解析数据源——原始输出永远在 <see cref="Outputs"/> 里。
    /// </summary>
    public string? FailureSummary => Outputs.FirstOrDefault(o => !o.Succeeded)?.Error;

    public string RawText => string.Join(
        Environment.NewLine,
        Outputs.Select(o => $"# {o.Command}{Environment.NewLine}{o.RawOutput}"));
}
