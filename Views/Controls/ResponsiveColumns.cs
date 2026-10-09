using System.Windows;
using System.Windows.Controls;

namespace RuijieNetworkAssistant.Views.Controls;

/// <summary>
/// DataGrid 小屏列控制：把次要列标记为 IsSecondary，页面在宽度不足时自动隐藏它们，
/// 重要列始终保留（列宽仍可由用户拖动调整）。只用附着属性 + Visibility，无第三方框架。
/// </summary>
public static class ResponsiveColumns
{
    /// <summary>页面可用宽度小于该值（DIP）时视为小屏：隐藏次要列。</summary>
    public const double CompactWidthThreshold = 960;

    /// <summary>标记该列为“次要列”（小屏自动隐藏）。</summary>
    public static readonly DependencyProperty IsSecondaryProperty =
        DependencyProperty.RegisterAttached(
            "IsSecondary",
            typeof(bool),
            typeof(ResponsiveColumns),
            new PropertyMetadata(false));

    public static bool GetIsSecondary(DependencyObject element) => (bool)element.GetValue(IsSecondaryProperty);

    public static void SetIsSecondary(DependencyObject element, bool value) => element.SetValue(IsSecondaryProperty, value);

    /// <summary>按紧凑模式显示/隐藏次要列。</summary>
    public static void Apply(DataGrid? grid, bool compact)
    {
        if (grid is null)
        {
            return;
        }

        foreach (var column in grid.Columns)
        {
            if (!GetIsSecondary(column))
            {
                continue;
            }

            column.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>按页面实际宽度自动判断是否紧凑（各页面统一口径）。</summary>
    public static void ApplyForWidth(DataGrid? grid, double actualWidth) =>
        Apply(grid, actualWidth > 0 && actualWidth < CompactWidthThreshold);
}
