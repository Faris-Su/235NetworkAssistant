using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 保存配置（write）服务 —— 本项目里**唯一会写设备持久配置**的操作。
///
/// 防误触的三道闸（全部在服务层兜底，界面只是第一道）：
///   1. 只有用户在 [保存配置] 弹窗里**按住按钮 1.2 秒**并确认，才会走到这里；
///      服务本身没有任何自动调用点（没有定时器、没有"顺手保存"、启动/断开都不调用）。
///   2. 未连接 / 明确处于普通模式（&gt;）→ 直接拒绝，**一个字节都不发给设备**。
///   3. 真正写入成功后进入冷却期（默认 5 秒）：期间再点也不会重复下发。
///
/// 命令固定 `write`（可切 `write memory`）：不自动重试、不自动改配置、不自动替你回答设备的 y/n。
/// </summary>
public sealed class ConfigSaveService
{
    /// <summary>默认命令：锐捷 RGOS 的"保存运行配置到启动配置"。</summary>
    public const string DefaultCommand = "write";

    /// <summary>备用写法：部分型号/版本不认 `write`，要用 `write memory`。</summary>
    public const string MemoryCommand = "write memory";

    /// <summary>两次成功写入之间的冷却时间（防连点、防重复 write）。</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private readonly ConnectionService _connections;
    private readonly CommandService _commands;
    private readonly ILogService _log;
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;

    public ConfigSaveService(ConnectionService connections, CommandService commands, ILogService log)
    {
        _connections = connections;
        _commands = commands;
        _log = log;
    }

    /// <summary>本次运行里最后一次真正写入成功的时刻。</summary>
    public DateTimeOffset LastSavedAt => _lastSavedAt;

    public string LastSavedText => _lastSavedAt == DateTimeOffset.MinValue
        ? "本次运行还没有保存过配置。"
        : $"上次保存：{_lastSavedAt:HH:mm:ss}";

    /// <summary>是否处于冷却期。</summary>
    public bool IsCoolingDown =>
        _lastSavedAt != DateTimeOffset.MinValue && DateTimeOffset.Now - _lastSavedAt < Cooldown;

    /// <summary>冷却剩余秒数（向上取整，最小 1）。</summary>
    public int CooldownRemainingSeconds => Math.Max(
        1,
        (int)Math.Ceiling((Cooldown - (DateTimeOffset.Now - _lastSavedAt)).TotalSeconds));

    /// <summary>现在能否保存（按钮可用性用它）。</summary>
    public bool CanSave => _connections.IsConnected && !IsCoolingDown;

    /// <summary>不能保存时的一句话原因（按钮 ToolTip 用；能保存时为空）。</summary>
    public string BlockedReason
    {
        get
        {
            if (!_connections.IsConnected)
            {
                return "设备未连接：先到【连接】页连上交换机，再保存配置。";
            }

            if (IsCoolingDown)
            {
                return $"刚刚已经保存过（{_lastSavedAt:HH:mm:ss}），{CooldownRemainingSeconds} 秒内不重复写入。";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// 真正写入设备。调用方必须已经完成"按住确认"（见 <c>SaveConfigViewModel</c>）。
    /// 任何被拦截的情况都返回 <see cref="ConfigSaveResult.Sent"/> = false，且不发送任何命令。
    /// </summary>
    public async Task<ConfigSaveResult> SaveAsync(
        string command = DefaultCommand,
        CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrWhiteSpace(command) ? DefaultCommand : command.Trim();

        if (!_connections.IsConnected)
        {
            _log.Info("保存配置被拦截：设备未连接");
            return Blocked(text, "设备未连接：保存会写入设备，必须先连上交换机。");
        }

        // 兜底权限闸：明确普通模式（>）时连命令都不发（与 CommandService 的闸门一致）。
        if (!_connections.PrivilegeLevel.CanConfigure())
        {
            _log.Info("保存配置被拦截：当前处于普通模式（>）");
            return Blocked(text, "当前设备处于普通模式（>）：需要先点[提升权限]进入特权模式（#）才能保存。");
        }

        if (IsCoolingDown)
        {
            _log.Info($"保存配置被拦截：冷却中（剩余 {CooldownRemainingSeconds} 秒）");
            return Blocked(text, $"刚刚已经保存过（{_lastSavedAt:HH:mm:ss}），{CooldownRemainingSeconds} 秒内不再重复写入。");
        }

        var deviceLabel = string.IsNullOrWhiteSpace(_connections.DeviceName)
            ? "当前设备"
            : _connections.DeviceName!;

        var plan = new CommandPlan
        {
            Title = "保存配置到设备（write）",
            Description = $"把 {deviceLabel} 的运行配置写入启动配置",
            ImpactScope = "会写入设备：running-config → startup-config，重启后配置仍然生效。软件不会自动执行这条命令。",
            RiskLevel = CommandRiskLevel.Dangerous,
            Commands = new[] { text },
        };

        _log.Info($"用户已按住确认，开始保存配置：{text}（{deviceLabel}）");

        var execution = await RunAsync(plan, cancellationToken).ConfigureAwait(false);
        var output = execution.Outputs.FirstOrDefault();
        var raw = output?.RawOutput ?? string.Empty;

        // 自动识别"当前模式"：write 是**特权模式**下的命令，如果会话停在配置模式
        // （用户手动敲了 conf t，或上一次操作把它留在了 (config-if) 里），设备一定会拒绝它。
        // 这时先发一条 `end` 回到特权模式，再原样重试一次 —— 不改命令、不写配置，只是把会话带回可用状态。
        //
        // ⚠️ 判据必须是"设备明确拒绝了"（回显里有标志），**不能再要求 execution.Succeeded**：
        // 2026-09-22 起 CommandService 会把被拒的配置命令标成失败（修"拒绝却报成功"那个 bug），
        // 这里如果继续 && execution.Succeeded，那么"停在配置模式 → 自动 end → 重试"这条恢复路径
        // 就永远走不到，write 被拒反而会显示成"保存没有发出去"（自检第 401 行就是这么红的）。
        var recoveredFromConfigMode = false;
        if (output is not null && LooksLikeRejected(raw))
        {
            recoveredFromConfigMode = true;
            _log.Info("保存配置被拒绝：先发送 end 退出配置模式，然后重试");
            await LeaveConfigModeAsync(text, cancellationToken).ConfigureAwait(false);
            execution = await RunAsync(plan, cancellationToken).ConfigureAwait(false);
            output = execution.Outputs.FirstOrDefault();
            raw = output?.RawOutput ?? string.Empty;
        }

        // 命令没发出去（权限兜底 / 发送异常 / 断线）：不算写入，也不进入冷却。
        //
        // ⚠️ 判据是"命令到底有没有到设备"，不是 execution.Succeeded ——
        // 被设备拒绝时 Succeeded 同样是 false，但那一次**确实发出去了**（raw 里就是设备的拒绝回显），
        // 必须落到下面"被拒绝"分支才能给出"改用 write memory"的建议。
        // 只把"Succeeded=false 且设备一个字都没回"当成没发出去。
        var wasSent = execution.Succeeded || !string.IsNullOrEmpty(raw);
        if (!wasSent || output is null)
        {
            var reason = output?.Error ?? "未知原因";
            _log.Warn($"保存配置未发送：{reason}");
            return new ConfigSaveResult
            {
                Command = text,
                Sent = false,
                Confirmed = false,
                RawOutput = raw,
                Summary = $"保存没有发出去：{reason}",
                Hint = "按提示处理后（提权 / 重新连接）再按住确认一次。",
            };
        }

        var confirmed = LooksLikeConfirmed(raw);
        var rejected = LooksLikeRejected(raw);

        if (rejected)
        {
            // 设备明确拒绝 = 没有真的写入：不进入冷却，允许立刻换写法重试。
            _log.Warn($"保存配置被设备拒绝：{text}");
            var suggestMemory = !text.Equals(MemoryCommand, StringComparison.OrdinalIgnoreCase);
            return new ConfigSaveResult
            {
                Command = text,
                Sent = true,
                Confirmed = false,
                Rejected = true,
                RawOutput = raw,
                Summary = recoveredFromConfigMode
                    ? $"设备拒绝了 `{text}`（已自动 end 退出配置模式后重试，仍被拒绝）。"
                    : $"设备拒绝了 `{text}`（命令无效或不可用）。",
                Hint = suggestMemory
                    ? $"这台设备可能要用 `{MemoryCommand}`：点[改用 {MemoryCommand}] 后重新按住确认。"
                    : "请到【CLI】页确认这台设备支持的保存命令（不同型号/版本不一样）。",
                SuggestMemoryCommand = suggestMemory,
            };
        }

        if (confirmed)
        {
            _lastSavedAt = DateTimeOffset.Now;
            _log.Info($"保存配置成功：{text}（设备回显含成功标志）");
            return new ConfigSaveResult
            {
                Command = text,
                Sent = true,
                Confirmed = true,
                RawOutput = raw,
                Summary = recoveredFromConfigMode
                    ? $"已保存：设备回显确认成功（{_lastSavedAt:HH:mm:ss}）。" +
                      "（会话当时在配置模式，已自动 end 回到特权模式后保存）运行配置已写入启动配置，重启后仍生效。"
                    : $"已保存：设备回显确认成功（{_lastSavedAt:HH:mm:ss}）。运行配置已写入启动配置，重启后仍生效。",
                Hint = string.Empty,
            };
        }

        // 发出去了，但回显里没有认得出的成功标志：按"已写入、待人工确认"处理，同样进入冷却，
        // 避免用户以为没成功而反复 write。
        _lastSavedAt = DateTimeOffset.Now;
        _log.Warn($"保存配置结果未确认：{text}（设备回显里没有 [OK]）");
        return new ConfigSaveResult
        {
            Command = text,
            Sent = true,
            Confirmed = false,
            RawOutput = raw,
            Summary = $"已发送 `{text}`，但设备回显里没有看到成功标志（[OK]）。",
            Hint = "部分型号保存成功时不打印 [OK]：请到【CLI】页看一眼设备回显确认。若设备在等 y/n 确认，软件不会替你回答，需要手动输入。",
        };
    }

    private static ConfigSaveResult Blocked(string command, string reason) => new()
    {
        Command = command,
        Sent = false,
        Confirmed = false,
        Summary = reason,
        Hint = "设备侧没有收到任何命令，配置没有被修改。",
    };

    private Task<CommandExecutionResult> RunAsync(CommandPlan plan, CancellationToken cancellationToken) =>
        _commands.ExecuteAsync(plan, null, cancellationToken);

    /// <summary>
    /// 把会话从配置模式带回特权模式（发送 `end`）。失败只记日志、不抛出：
    /// 调用方会照常重试保存命令，重试还失败就把设备的原始回显给用户看。
    /// </summary>
    private async Task LeaveConfigModeAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var connection = _connections.Current;
            if (connection is not null)
            {
                await connection.SendAsync("end", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"保存 {command} 前发送 end 失败（继续重试保存）", ex);
        }
    }

    /// <summary>设备回显里的成功标志（不同版本措辞不同，能认出来的都算）。</summary>
    private static bool LooksLikeConfirmed(string raw) =>
        raw.Contains("[OK]", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("[  OK  ]", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("configuration saved", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("saved successfully", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("save configuration successfully", StringComparison.OrdinalIgnoreCase);

    /// <summary>设备明确拒绝的标志（% Invalid input / % Incomplete command 等）。</summary>
    private static bool LooksLikeRejected(string raw) =>
        raw.Contains("% invalid input", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("% incomplete command", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("% ambiguous command", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("% unknown command", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("% un recognized command", StringComparison.OrdinalIgnoreCase);
}
