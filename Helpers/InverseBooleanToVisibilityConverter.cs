using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>bool 取反后转 Visibility（用于“小屏隐藏次要元素”这类绑定，避免写触发器的重复代码）。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
