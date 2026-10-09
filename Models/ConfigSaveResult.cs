namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 保存配置（write）的结果。
/// 这是一条**真正会写设备**的命令，所以除了"发没发出去"，还必须区分：
/// 设备是否回了成功标志（[OK]）、还是明确拒绝了这条命令、还是发了但结果看不出来。
/// 原始回显永远保留，解析不出来时以设备原文为准。
/// </summary>
public sealed class ConfigSaveResult
{
    /// <summary>实际发送的命令（`write` 或 `write memory`）。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>命令是否真的发到了设备（false = 被连接/权限/冷却拦截，设备侧一个字节都没收到）。</summary>
    public bool Sent { get; init; }

    /// <summary>设备回显里是否看到成功标志（[OK] 等）。</summary>
    public bool Confirmed { get; init; }

    /// <summary>设备是否明确拒绝了这条命令（% Invalid input 之类）。</summary>
    public bool Rejected { get; init; }

    /// <summary>一句话结论（界面与状态栏直接用）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>失败 / 未确认时"该怎么修"。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>设备原始回显。</summary>
    public string RawOutput { get; init; } = string.Empty;

    /// <summary>是否建议改用 `write memory`（设备提示命令无效时）。</summary>
    public bool SuggestMemoryCommand { get; init; }

    public DateTimeOffset RanAt { get; init; } = DateTimeOffset.Now;
}
