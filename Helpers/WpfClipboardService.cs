using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>WPF 剪贴板实现（只在桌面应用中使用）。</summary>
public sealed class WpfClipboardService : IClipboardService
{
    public bool TrySetText(string text, out string? error)
    {
        try
        {
            System.Windows.Clipboard.SetText(text ?? string.Empty);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
