using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.Views.Controls;

/// <summary>
/// 终端显示控件：只读 TextBox + 增量追加 / 整体重写两种更新方式。
/// 关闭撤销栈与拼写检查，避免长文本下的额外内存与 CPU 开销。
/// </summary>
public sealed class TerminalTextBox : TextBox, ITerminalDisplay
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18));
    private static readonly Brush ForegroundBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0xD9, 0xDF));

    public TerminalTextBox()
    {
        IsReadOnly = true;
        // 终端模式下要在终端里直接打字，所以保留只读光标作为“输入位置”提示。
        IsReadOnlyCaretVisible = true;
        AcceptsReturn = false;
        IsUndoEnabled = false;
        TextWrapping = TextWrapping.NoWrap;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalContentAlignment = VerticalAlignment.Top;
        FontFamily = new FontFamily("Consolas, Courier New");
        FontSize = 12.5;
        Background = BackgroundBrush;
        Foreground = ForegroundBrush;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(6);
        SpellCheck.SetIsEnabled(this, false);
    }

    /// <summary>是否自动滚动到最新输出。</summary>
    public bool AutoScrollEnabled { get; set; } = true;

    public int TextLength => Text?.Length ?? 0;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AppendText(text);
        if (AutoScrollEnabled)
        {
            ScrollToEnd();
        }

        PinCaretToEnd();
    }

    public void Rewrite(string fullText)
    {
        Text = fullText ?? string.Empty;
        if (AutoScrollEnabled)
        {
            ScrollToEnd();
        }

        PinCaretToEnd();
    }

    /// <summary>设备回显擦除：只从末尾删字符，不整体重写（不打断光标 / 选区 / 滚动位置）。</summary>
    public void Backspace(int count)
    {
        var length = TextLength;
        if (count <= 0 || length == 0)
        {
            return;
        }

        count = Math.Min(count, length);
        var wasReadOnly = IsReadOnly;
        try
        {
            // 只读 TextBox 不允许改 SelectedText，这里临时放开一下，
            // 避免用「整段 Text = ...」的方式重写（那样光标会跳到开头）。
            IsReadOnly = false;
            Select(length - count, count);
            SelectedText = string.Empty;
        }
        finally
        {
            IsReadOnly = wasReadOnly;
        }

        PinCaretToEnd();
    }

    public void ClearDisplay() => Clear();

    /// <summary>
    /// 把光标钉在末尾：终端是只读控件，光标只是“输入位置”提示，
    /// 一旦被整体重写复位到 0（或用户点到中间），打字时光标就会跑到别处，看起来像乱跳。
    /// 有选区时不动它，避免打断用户选中文字复制。
    /// </summary>
    private void PinCaretToEnd()
    {
        if (SelectionLength == 0)
        {
            var end = TextLength;
            if (CaretIndex != end)
            {
                CaretIndex = end;
            }
        }
    }
}
