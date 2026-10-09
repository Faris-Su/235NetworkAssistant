namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 剪贴板抽象：ViewModel 不直接依赖 WPF，便于自动化验证；WPF 实现在 Helpers/WpfClipboardService.cs。
/// </summary>
public interface IClipboardService
{
    bool TrySetText(string text, out string? error);
}

/// <summary>无剪贴板环境（例如自动化脚本）下的空实现。</summary>
public sealed class NoopClipboardService : IClipboardService
{
    public static NoopClipboardService Instance { get; } = new();

    public bool TrySetText(string text, out string? error)
    {
        error = "当前环境不支持剪贴板操作。";
        return false;
    }
}
