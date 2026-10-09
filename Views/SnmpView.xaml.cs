using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// SNMP 页面。Code-behind 只做 Community 输入框的"显示 / 隐藏"桥接
/// （WPF 的 PasswordBox.Password 不是依赖属性，且明文只能用 TextBox 显示）。
/// 注意：显示按钮只切换当前输入框的显示方式，不改变保存值、不写日志。
/// </summary>
public partial class SnmpView : UserControl
{
    public SnmpView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 整页滚轮：指针停在**表格/列表以外的任何位置**（标题、状态文字、空白处）时，
    /// 滚轮要滚动整页——否则用户会以为"这个页面滚不动"（现场反馈）。
    /// 指针停在内层能自己滚的表格/列表上时不动它，保持"各滚各的"。
    /// </summary>
    private void OnPagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer page)
        {
            return;
        }

        for (var node = e.OriginalSource as DependencyObject; node is not null && node != page; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not (DataGrid or ListBox))
            {
                continue;
            }

            // 内层控件确实溢出（有自己的滚动条）→ 交给它；没溢出就继续往上，由整页滚
            if (FindScrollableHeight(node) > 0)
            {
                return;
            }
        }

        if (page.ScrollableHeight <= 0)
        {
            return;
        }

        // 与系统一致：一格滚轮 ≈ 3 行
        page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>找元素内部 ScrollViewer 的可滚动高度（找不到就 0）。</summary>
    private static double FindScrollableHeight(DependencyObject root)
    {
        if (root is ScrollViewer scroller)
        {
            return scroller.ScrollableHeight;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var height = FindScrollableHeight(VisualTreeHelper.GetChild(root, i));
            if (height > 0)
            {
                return height;
            }
        }

        return 0;
    }
}
