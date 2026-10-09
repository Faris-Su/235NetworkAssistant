using System.Text;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 终端数据管线：设备输出 → 接收缓冲（后台线程只加锁追加）→ 批量刷新 → 显示。
/// 职责：
/// 1. 绝不逐字符刷新 UI（按刷新间隔合并，缓冲越大间隔越保守）；
/// 2. 显示缓冲有上限，裁剪时整体重写（带节流），显示内容不会无限增长；
/// 3. 原始输出按原样写入 SessionRecorder，因此“保存输出”始终完整。
/// 该类型不依赖任何 UI 框架，便于自动化验证。
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private const int RewriteThrottleMs = 300;
    private const int RecorderFlushIntervalMs = 1000;
    private const int LargeBufferChars = 40_000;
    private const int HugeBufferChars = 120_000;
    private const int LargeBufferIntervalMs = 150;
    private const int HugeBufferIntervalMs = 250;

    private readonly StringBuilder _pending = new();
    private readonly object _pendingSync = new();
    private readonly ILogService? _log;
    private DateTime _lastRewriteUtc = DateTime.MinValue;
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private DateTime _lastRecorderFlushUtc = DateTime.UtcNow;
    private bool _needsRewrite;
    private bool _carryCarriageReturn;
    private bool _disposed;

    public TerminalSession(
        int bufferCapacity,
        int flushIntervalMs,
        SessionRecorder? recorder = null,
        ILogService? log = null)
    {
        Buffer = new TerminalBuffer(bufferCapacity);
        ConfiguredFlushIntervalMs = Math.Clamp(flushIntervalMs, 20, 1000);
        Recorder = recorder;
        _log = log;
    }

    public TerminalBuffer Buffer { get; private set; }

    public SessionRecorder? Recorder { get; set; }

    public ITerminalDisplay? Display { get; set; }

    public int ConfiguredFlushIntervalMs { get; set; }

    /// <summary>本会话记录的原始输出字符数。</summary>
    public long RawChars => Recorder?.TotalChars ?? 0;

    public string Text => Buffer.Text;

    /// <summary>设备输出入口：可能由读取线程调用，只做加锁追加，不触碰 UI。</summary>
    public void OnDeviceOutput(string? text)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_pendingSync)
        {
            _pending.Append(text);
        }

        Recorder?.Append(text);
    }

    /// <summary>本地生成的内容（系统提示、本地回显）：立即显示，不占用原始输出文件。</summary>
    public void AppendLocal(string? text)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        var normalized = Normalize(text);
        if (Buffer.Append(normalized))
        {
            RewriteDisplay();
        }
        else
        {
            Display?.Append(normalized);
        }
    }

    /// <summary>批量刷新。返回 true 表示本次更新了显示内容。</summary>
    public bool Flush(bool force = false)
    {
        if (_disposed)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastFlushUtc < TimeSpan.FromMilliseconds(GetEffectiveFlushIntervalMs()))
        {
            return false;
        }

        _lastFlushUtc = now;

        string chunk;
        lock (_pendingSync)
        {
            if (_pending.Length == 0)
            {
                chunk = string.Empty;
            }
            else
            {
                chunk = _pending.ToString();
                _pending.Clear();
            }
        }

        if (chunk.Length == 0)
        {
            if (_needsRewrite && now - _lastRewriteUtc >= TimeSpan.FromMilliseconds(RewriteThrottleMs))
            {
                RewriteDisplay();
                return true;
            }

            FlushRecorderIfDue(now);
            return false;
        }

        if (_carryCarriageReturn)
        {
            chunk = "\r" + chunk;
            _carryCarriageReturn = false;
        }

        // 分块边界恰好落在 CR / LF 之间时，把 CR 留到下一批，避免出现空行。
        if (chunk.EndsWith('\r'))
        {
            chunk = chunk[..^1];
            _carryCarriageReturn = true;
        }

        // 两类控制字符必须在显示层就地处理，否则会被当普通文本塞进 TextBox：
        //   ① 擦除（退格 `\b`、少数设备回 DEL 0x7F）—— 不处理就是"按了退格屏幕上没反应"；
        //   ② **单独的 CR**（不是 CRLF）—— 终端语义是"光标回行首、后面的字覆盖这一行"。
        //      设备原文样例（按 ↑/↓ 重画命令行，来源会话留档）：
        //        `Ruijie#show vlan<CR>Ruijie#         <CR>Ruijie#show vlan<CR>Ruijie#         `
        //      老实现把单独 CR 也当换行 ⇒ **每按一次方向键就多出一行**（用户看到"↑ 再 ↓ 换两行"）。
        var needsTailMerge = ContainsErase(chunk) || ContainsBareCarriageReturn(chunk);
        if (needsTailMerge)
        {
            var lineStart = Buffer.FindLastLineStart();
            var currentLine = Buffer.GetTextFrom(lineStart);
            var merged = ApplyTerminalSemantics(currentLine + chunk);

            // 只更新“尾巴”：算出要删几个字符、要补什么文本，
            // 这样不用整体重写显示（整屏重写会把光标弹回开头，用户看到的就是“光标乱跳”）。
            var keep = 0;
            while (keep < currentLine.Length && keep < merged.Length && currentLine[keep] == merged[keep])
            {
                keep++;
            }

            var eraseCount = currentLine.Length - keep;
            var appended = merged[keep..];
            // 只有缓冲被裁剪时才需要整体重写；正常情况下面显示已经增量更新过，
            // 再置 _needsRewrite 会导致下一批刷新触发整屏重写（光标/滚动位置被打断）。
            _needsRewrite = Buffer.ReplaceTail(lineStart, merged);

            if (Display is not null)
            {
                // 擦除要立刻见效，不走 300ms 重写节流，否则用户看到的是“按了没反应”。
                if (eraseCount > 0)
                {
                    Display.Backspace(eraseCount);
                }

                if (appended.Length > 0)
                {
                    Display.Append(appended);
                }
            }

            FlushRecorderIfDue(now);
            return true;
        }

        var displayText = Normalize(chunk);
        if (displayText.Length == 0)
        {
            FlushRecorderIfDue(now);
            return false;
        }

        if (Buffer.Append(displayText))
        {
            _needsRewrite = true;
        }

        var updated = false;
        if (Display is not null)
        {
            var forced = Display.TextLength > Buffer.Capacity * 2;
            if (_needsRewrite && (forced || now - _lastRewriteUtc >= TimeSpan.FromMilliseconds(RewriteThrottleMs)))
            {
                RewriteDisplay();
            }
            else
            {
                Display.Append(displayText);
            }

            updated = true;
        }

        FlushRecorderIfDue(now);
        return updated;
    }

    /// <summary>清空显示缓冲与显示内容（原始输出文件继续保留）。</summary>
    public void Clear()
    {
        Buffer.Clear();
        Display?.ClearDisplay();
        _needsRewrite = false;
    }

    /// <summary>按新设置重建缓冲（保留当前可见内容）。</summary>
    public void Resize(int bufferCapacity, int flushIntervalMs)
    {
        var resized = new TerminalBuffer(bufferCapacity);
        resized.Append(Buffer.Text);
        Buffer = resized;
        ConfiguredFlushIntervalMs = Math.Clamp(flushIntervalMs, 20, 1000);
        RewriteDisplay();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Recorder?.Dispose();
        }
        catch
        {
            // 释放阶段忽略异常。
        }

        Recorder = null;
        Display = null;
    }

    /// <summary>把设备输出规范化为 WPF TextBox 需要的换行形式（原始文本仍按原样落盘）。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>
    /// 这一批输出里是否含**单独的 CR**（不是 CRLF 的一部分）。
    ///
    /// 单独 CR 在终端里是"回到行首重画"，不是换行 —— 见 <see cref="Flush"/> 里的现场原文。
    /// 末尾那个 CR 已经被 `_carryCarriageReturn` 摘走（可能是被切开的 CRLF），所以这里不会误判。
    /// </summary>
    internal static bool ContainsBareCarriageReturn(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r')
            {
                continue;
            }

            var isCrLf = i + 1 < text.Length && text[i + 1] == '\n';
            if (!isCrLf)
            {
                return true;
            }

            i++;   // 跳过 LF
        }

        return false;
    }

    /// <summary>
    /// 按终端语义把一段输出折叠成"应该显示成什么样"（相对当前行）。
    ///
    /// 处理三种控制字符（其余原样保留）：
    ///   · `\r\n` / `\n` → 换行；
    ///   · **单独 `\r`** → 光标回行首：当前行内容清空，后续文字覆盖这一行（**不新起一行**）；
    ///   · `\b` / `0x7F` → 删掉当前行最后一个字符。
    ///
    /// 传入的 <paramref name="text"/> 通常是"当前行已有内容 + 本批新输出"，
    /// 返回值的最后一段就是新的当前行。
    /// </summary>
    internal static string ApplyTerminalSemantics(string text)
    {
        var completed = new StringBuilder(text.Length + 16);
        var line = new StringBuilder();
        // 收到过**单独 CR**（不是 CRLF）⇒ 光标回到行首，"后面的字覆盖这一行"。
        // ⚠️ 关键：CR 本身**不丢已经打印出来的内容**（2026-09-24 现场 bug）——
        // 锐捷控制台的行结束符是 `CR CR LF`，老实现把第一个 CR 当成"清空当前行"，
        // 结果用户敲的整行命令被抹掉、位置还多出一个空行（截图里那一片空白就是它）。
        // 正确语义：CR 只把光标移到行首；真正覆盖发生在**后面来的文字**上。
        var pendingLineRestart = false;
        var restartApplied = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                        completed.Append(line).Append('\n');
                        line.Clear();
                        pendingLineRestart = false;
                        restartApplied = false;
                    }
                    else
                    {
                        pendingLineRestart = true;
                        restartApplied = false;
                    }

                    break;

                case '\n':
                    completed.Append(line).Append('\n');
                    line.Clear();
                    pendingLineRestart = false;
                    restartApplied = false;
                    break;

                case '\b':
                case '\u007f':
                    if (line.Length > 0)
                    {
                        line.Length--;
                    }

                    break;

                default:
                    if (pendingLineRestart && !restartApplied)
                    {
                        // 设备开始重画这一行：整行换掉（RGOS 重画时会用空格补齐剩余宽度）
                        line.Clear();
                        restartApplied = true;
                    }

                    line.Append(ch);
                    break;
            }
        }

        return completed.Append(line).ToString();
    }

    /// <summary>设备回显里是否含擦除字符（退格 \b 或 DEL 0x7F）。</summary>
    private static bool ContainsErase(string text) =>
        text.IndexOf('\b') >= 0 || text.IndexOf('\u007f') >= 0;

    /// <summary>按终端语义处理擦除：删掉前一个字符，不跨行。</summary>
    private void RewriteDisplay()
    {
        Display?.Rewrite(Buffer.Text);
        _needsRewrite = false;
        _lastRewriteUtc = DateTime.UtcNow;
    }

    private void FlushRecorderIfDue(DateTime now)
    {
        var recorder = Recorder;
        if (recorder is null || now - _lastRecorderFlushUtc < TimeSpan.FromMilliseconds(RecorderFlushIntervalMs))
        {
            return;
        }

        _lastRecorderFlushUtc = now;
        try
        {
            recorder.Flush();
        }
        catch (Exception ex)
        {
            _log?.Warn("刷新 CLI 原始输出文件失败", ex);
        }
    }

    private int GetEffectiveFlushIntervalMs() => Buffer.Length switch
    {
        > HugeBufferChars => HugeBufferIntervalMs,
        > LargeBufferChars => LargeBufferIntervalMs,
        _ => ConfiguredFlushIntervalMs,
    };
}
