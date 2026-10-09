using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// QuickPing 页面。Code-behind 只做一件事：图形模式的格子被点中时，
/// 通知 ViewModel 切回表格模式并选中那一行，然后把该行滚到可视区域。
/// </summary>
public partial class QuickPingView : UserControl
{
    public QuickPingView()
    {
        InitializeComponent();
    }

    private void OnGraphCellClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QuickPingViewModel viewModel
            || (sender as FrameworkElement)?.DataContext is not QuickPingGraphItem item)
        {
            return;
        }

        viewModel.ShowInTable(item);

        // 切模式 + 选中之后布局才生效，滚动要等下一个 dispatcher 周期
        if (viewModel.SelectedResult is { } row)
        {
            Dispatcher.BeginInvoke(
                new Action(() => QuickPingGrid.ScrollIntoView(row)),
                DispatcherPriority.Background);
        }
    }
}
