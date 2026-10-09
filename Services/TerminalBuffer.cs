using System.Text;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 终端显示缓冲：有最大长度上限，避免长时间运行导致内存无限增长。
/// 只用于“显示”，原始输出由 SessionRecorder 完整落盘（见 SaveOutput）。
/// </summary>
public sealed class TerminalBuffer
{
    /// <summary>裁剪时向后寻找换行的窗口，避免把一行切成半截。</summary>
    private const int LineBoundarySearchWindow = 400;

    private readonly StringBuilder _builder = new();
    private readonly int _capacity;

    public TerminalBuffer(int capacity)
    {
        _capacity = Math.Max(1000, capacity);
    }

    public int Capacity => _capacity;

    public int Length => _builder.Length;

    /// <summary>本次会话累计追加的字符数（不受裁剪影响）。</summary>
    public long TotalAppendedChars { get; private set; }

    /// <summary>因超出上限被裁剪掉的字符数。</summary>
    public long TrimmedChars { get; private set; }

    public string Text => _builder.ToString();

    /// <summary>追加文本；返回 true 表示发生了裁剪（调用方需要整体重写显示）。</summary>
    public bool Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        TotalAppendedChars += text.Length;
        _builder.Append(text);

        if (_builder.Length <= _capacity)
        {
            return false;
        }

        var remove = _builder.Length - _capacity;
        var cut = Math.Min(FindLineBoundary(remove), _builder.Length);
        if (cut <= 0)
        {
            return false;
        }

        _builder.Remove(0, cut);
        TrimmedChars += cut;
        return true;
    }

    /// <summary>清空显示缓冲（原始输出文件不受影响）。</summary>
    public void Clear() => _builder.Clear();

    /// <summary>当前最后一行的起始下标（用于处理退格等“只在当前行内生效”的编辑）。</summary>
    public int FindLastLineStart()
    {
        for (var i = _builder.Length - 1; i >= 0; i--)
        {
            if (_builder[i] == '\n')
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>取从 <paramref name="offset"/> 到结尾的内容（只复制这一小段，不动整块缓冲）。</summary>
    public string GetTextFrom(int offset)
    {
        offset = Math.Clamp(offset, 0, _builder.Length);
        return _builder.ToString(offset, _builder.Length - offset);
    }

    /// <summary>
    /// 把从 <paramref name="offset"/> 开始的内容替换成 <paramref name="text"/>。
    /// 用于设备回显里的擦除序列：\b 删掉屏幕上前一个字符，而不是把控制字符原样塞进显示。
    /// 返回 true 表示发生了裁剪（调用方需要整体重写显示）。
    /// </summary>
    public bool ReplaceTail(int offset, string? text)
    {
        offset = Math.Clamp(offset, 0, _builder.Length);
        _builder.Remove(offset, _builder.Length - offset);
        return Append(text);
    }

    private int FindLineBoundary(int offset)
    {
        var limit = Math.Min(_builder.Length, offset + LineBoundarySearchWindow);
        for (var i = offset; i < limit; i++)
        {
            if (_builder[i] == '\n')
            {
                return i + 1;
            }
        }

        return offset;
    }
}
