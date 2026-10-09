using System.Windows;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 外观（字体档位）应用与通知。只使用原生 WPF，不引入任何依赖；
/// 不用定时器、不做持续重排：仅在用户切换字体档位时执行一次。
/// </summary>
public static class AppearanceService
{
    /// <summary>字体档位变化（界面字体 / CLI 字体）。</summary>
    public static event EventHandler? AppearanceChanged;

    public static double UiFontSize => AppServices.Settings.UiFontSize;

    public static double CliFontSize => AppServices.Settings.CliFontSize;

    /// <summary>把当前设置应用到主窗口并与各页面同步。</summary>
    public static void Apply()
    {
        if (Application.Current?.MainWindow is { } window)
        {
            window.FontSize = UiFontSize;
        }

        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }
}
