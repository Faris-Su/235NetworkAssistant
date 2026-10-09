using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 命令执行服务。所有命令都必须经过 CommandPlan → 用户确认 → 这里执行。
/// 断线时拒绝发送；执行结果保留原始输出。
/// </summary>
public sealed class CommandService
{
    private readonly ConnectionService _connections;
    private readonly ILogService _log;

    public CommandService(ConnectionService connections, ILogService log)
    {
        _connections = connections;
        _log = log;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        CommandPlan plan,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var connection = _connections.RequireConnected();
        var started = DateTimeOffset.Now;
        var outputs = new List<CommandOutput>();

        // 权限闸门（兜底）：明确处于普通模式时，配置类命令一律不发送。
        // 界面会在 Command Preview 里先给出[提升权限]入口；这里是最后一道防线。
        if (RequiresPrivilege(plan) && !_connections.PrivilegeLevel.CanConfigure())
        {
            var reason = "当前设备处于普通模式（>），该操作需要管理权限：请先点[提升权限]输入 Enable 密码。";
            _log.Warn($"已阻止配置命令：{plan.Title}（普通模式）");
            outputs.Add(new CommandOutput
            {
                Command = plan.Commands.FirstOrDefault() ?? plan.Title,
                RawOutput = string.Empty,
                Succeeded = false,
                Error = reason,
            });

            return new CommandExecutionResult
            {
                Plan = plan,
                Outputs = outputs,
                StartedAt = started,
                FinishedAt = DateTimeOffset.Now,
            };
        }

        _log.Info($"执行命令：{plan.Title}（{plan.Commands.Count} 条）");

        // 配置类命令必须先进入配置模式。
        // 生成器只产出"相对配置模式"的命令块（interface range … / no shutdown / exit、vlan X / name Y …），
        // 而设备如果停在特权模式的 `Host#` 下，`interface range` 会被直接判为非法命令
        // （部分设备会返回 % Invalid input detected at '^' marker）——
        // 这就是"点了 no shutdown / 建 VLAN 没生效"的根因。
        // 统一在这里包一层 configure terminal … end，所有页面（端口/VLAN/Trunk/…）一次修好。
        //
        // ⚠️ 例外：`write` / `write memory` / `copy running-config startup-config` 这类是
        // **特权模式（Host#）下的命令**，只能在 EXEC 下发；包进 configure terminal 之后
        // 就变成"(config)# write" → 设备回 % Unknown command（现场实测，就是"保存配置不生效"的那次）。
        var needsConfigMode = RequiresPrivilege(plan) && !IsExecModeOnly(plan);

        if (needsConfigMode)
        {
            try
            {
                var enter = await connection.SendAsync(ConfigureTerminalCommand, cancellationToken).ConfigureAwait(false);
                if (LooksLikeRejected(enter))
                {
                    outputs.Add(new CommandOutput
                    {
                        Command = ConfigureTerminalCommand,
                        RawOutput = enter,
                        Succeeded = false,
                        Error = "设备拒绝了 configure terminal：无法进入配置模式。",
                    });

                    return new CommandExecutionResult
                    {
                        Plan = plan,
                        Outputs = outputs,
                        StartedAt = started,
                        FinishedAt = DateTimeOffset.Now,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"进入配置模式失败：{ConfigureTerminalCommand}", ex);
                outputs.Add(new CommandOutput
                {
                    Command = ConfigureTerminalCommand,
                    RawOutput = string.Empty,
                    Succeeded = false,
                    Error = $"进入配置模式失败：{ex.Message}",
                });

                return new CommandExecutionResult
                {
                    Plan = plan,
                    Outputs = outputs,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.Now,
                };
            }
        }

        foreach (var command in plan.Commands)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(command);

            try
            {
                var raw = await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
                // 把"输出被截断"如实带出去：丢弃开头字符 / 撞上翻页上限，都意味着这份输出不完整
                var dropped = connection.LastCommandDroppedChars;
                var hitPageLimit = connection.LastCommandHitPageLimit;

                // 配置命令被设备明确拒绝时**不能算成功**。
                // 现场反馈"勾了 no shutdown 但 show 出来还是 down，软件却说完成"就是这里漏判：
                // 以前只有 `configure terminal` 那一条做了拒绝检查，后面每条命令都无条件 Succeeded = true，
                // 于是设备回 `% Invalid input detected at '^' marker` 也被记成"成功"。
                // ⚠️ 只检查**配置命令**：show 类输出是设备原文，里面出现同样字样（比如配置里有人写了这句、
                //    或备份 show running-config）属于正常内容，误判会让备份/刷新整条链路变成"失败"。
                var rejected = IsConfigCommand(command) ? MatchRejectMarker(raw) : null;
                outputs.Add(new CommandOutput
                {
                    Command = command,
                    RawOutput = raw,
                    Succeeded = rejected is null,
                    Error = rejected is null ? null : BuildRejectionMessage(marker: rejected),
                    OutputTruncated = dropped > 0 || hitPageLimit,
                    TruncationReason = dropped > 0
                        ? $"输出超过缓冲上限，开头约 {dropped} 个字符已被丢弃"
                        : hitPageLimit
                            ? $"输出超过自动翻页上限（{CliPager.MaxPages} 页），后面的内容没有取到"
                            : null,
                });

                if (rejected is not null)
                {
                    // 被拒后继续下发只会落在错误的上下文里（例如 `interface range` 被拒，
                    // 后面的 `no shutdown` 就落到全局配置模式），所以立即停止；
                    // 函数结尾仍会统一发 `end` 回到特权模式，不会把设备留在配置模式里。
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"命令失败：{command}", ex);
                outputs.Add(new CommandOutput
                {
                    Command = command,
                    RawOutput = string.Empty,
                    Succeeded = false,
                    Error = ex.Message,
                });

                // 配置类命令一旦失败立即停止，避免在半配置状态下继续下发后续命令。
                break;
            }
        }

        if (needsConfigMode)
        {
            // 回到特权模式，避免后续命令（或用户手动敲的命令）落在配置子模式里
            try
            {
                var exit = await connection.SendAsync(EndConfigCommand, cancellationToken).ConfigureAwait(false);
                outputs.Add(new CommandOutput { Command = EndConfigCommand, RawOutput = exit, Succeeded = true });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"退出配置模式失败：{EndConfigCommand}", ex);
            }
        }

        return new CommandExecutionResult
        {
            Plan = plan,
            Outputs = outputs,
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>进入全局配置模式的命令（锐捷/Cisco 同形）。</summary>
    public const string ConfigureTerminalCommand = "configure terminal";

    /// <summary>
    /// 整条计划都是"EXEC 模式命令"（不需要、也不能进配置模式）时为 true。
    /// 目前只有保存类命令属于这一类。
    /// </summary>
    private static bool IsExecModeOnly(CommandPlan plan)
    {
        var commands = plan.Commands
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();

        return commands.Count > 0 && commands.All(IsExecModeCommand);
    }

    private static bool IsExecModeCommand(string command) =>
        command.StartsWith("write", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("copy running-config", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("save", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("reload", StringComparison.OrdinalIgnoreCase)
        || command.Equals("end", StringComparison.OrdinalIgnoreCase);

    /// <summary>回到特权模式的命令。</summary>
    public const string EndConfigCommand = "end";

    /// <summary>
    /// 设备"明确拒绝这条命令"时回显里的标记（锐捷 RGOS 实测口径）。
    ///
    /// 这里**只放"命令没被接受"的标记**，不放业务性提示：列表越宽越容易把正常输出误判成失败。
    /// 每条都标了出处，加新标记前请先确认真机/模拟设备确实回这句。
    /// </summary>
    private static readonly string[] RejectMarkers =
    {
        "% invalid input",          // 现场实测：特权模式下发 interface range → % Invalid input detected at '^' marker.
        "% invalid command",
        "% incomplete command",     // 少参数，例如只敲了 switchport
        "% ambiguous command",      // 简写有歧义
        "% unknown command",        // 现场实测：(config)# write → % Unknown command（所以 write 必须走 EXEC）
        "% unrecognized command",
        "% not enough parameters",
        "% too many parameters",
        "% permission denied",      // 权限不足（普通模式 > 下敲配置类命令）
        "% authorization failed",   // 提权/授权被拒
    };

    /// <summary>设备明确拒绝（% Invalid input 之类）的判定。</summary>
    private static bool LooksLikeRejected(string raw) => MatchRejectMarker(raw) is not null;

    /// <summary>
    /// 命中"被拒绝"时返回命中的标记（用于写进报错），没命中返回 null。
    /// 返回标记而不是 bool，是为了让用户看到设备到底回了哪一句。
    /// </summary>
    private static string? MatchRejectMarker(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var marker in RejectMarkers)
        {
            if (raw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return marker;
            }
        }

        return null;
    }

    /// <summary>
    /// 是否是配置类命令（非 show）。只有这类命令才做"被拒绝"判定，理由见调用处注释。
    /// </summary>
    private static bool IsConfigCommand(string command) =>
        !command.TrimStart().StartsWith("show", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把"设备拒绝了这条命令"翻译成用户看得懂、并且知道下一步该干什么的一句话。
    /// （用户明确要求：报错必须指明原因，看到报错就知道怎么修。）
    /// </summary>
    private static string BuildRejectionMessage(string marker) =>
        $"设备拒绝了这条命令（回显含「{marker}」），这条命令没有生效，后续命令已停止下发。"
        + "常见原因：① 该型号不支持这条命令；② 命令层级不对（例如没进到接口视图就往里发）；"
        + "③ 当前权限不足（普通模式 > 需要先在连接页选“管理模式”或点“提升权限”）。"
        + "请到下方结果面板看设备原始回显：其中 '^' 指向设备不认识的那一段。";

    /// <summary>
    /// 判断计划里是否含需要管理权限的命令：本项目里除 `show` 之外的命令都是配置命令。
    /// 这样不必逐个改动命令生成器。
    /// </summary>
    private static bool RequiresPrivilege(CommandPlan plan) =>
        plan.Commands.Any(command =>
            !string.IsNullOrWhiteSpace(command) &&
            !command.TrimStart().StartsWith("show", StringComparison.OrdinalIgnoreCase));

    /// <summary>执行单条 show 命令并返回原始输出。</summary>
    public async Task<string> RunShowCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var result = await RunShowResultAsync(command, cancellationToken).ConfigureAwait(false);
        var output = result.Outputs.FirstOrDefault();
        if (output is null)
        {
            return string.Empty;
        }

        // 只返回设备原始输出：不要在这里加 “# 命令” 之类的前缀，否则解析器会把它当成未识别行。
        return output.Succeeded
            ? output.RawOutput
            : $"执行失败：{output.Error}{Environment.NewLine}{output.RawOutput}";
    }

    /// <summary>
    /// 执行单条 show 命令并返回完整结果（成功/失败/原始输出）。
    /// 需要按“这条命令到底成没成功”做分支的场景用这个（例如 LLDP 逐端口取 Detail）。
    /// </summary>
    public async Task<CommandExecutionResult> RunShowResultAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var plan = new CommandPlan
        {
            Title = $"查看：{command}",
            Description = command,
            ImpactScope = "只读命令，不修改设备配置",
            RiskLevel = CommandRiskLevel.Safe,
            Commands = new[] { command },
        };

        return await ExecuteAsync(plan, null, cancellationToken).ConfigureAwait(false);
    }
}
