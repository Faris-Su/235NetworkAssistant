namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 终端显示表面的抽象。ViewModel 通过它把缓冲内容推送到 UI，
/// 具体实现（WPF TextBox）在 Views 层，保证 ViewModel 不依赖任何 UI 类型。
/// </summary>
public interface ITerminalDisplay
{
    /// <summary>增量追加文本（批量刷新时使用，避免整体重绘）。</summary>
    void Append(string text);

    /// <summary>缓冲被裁剪后整体重写显示内容。</summary>
    void Rewrite(string fullText);

    /// <summary>
    /// 从末尾删掉 <paramref name="count"/> 个字符（设备回显擦除时用）。
    /// 之所以单独开一个方法：整体重写会把光标复位、丢掉选区、滚动位置也要重算，
    /// 而退格是高频操作 —— 只删尾巴最省也最不打扰用户。
    /// </summary>
    void Backspace(int count);

    /// <summary>清空显示。</summary>
    void ClearDisplay();

    /// <summary>当前显示文本长度，用于安全兜底（显示内容不得无限增长）。</summary>
    int TextLength { get; }
}
