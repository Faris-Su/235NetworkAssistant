using System.Windows.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class ResourceLibraryView : UserControl
{
    public ResourceLibraryView()
    {
        InitializeComponent();
        // 资源库三张表**不按宽度隐藏列**（2026-09-25 用户要求）：
        // 现场反馈"列拖了还是显示不全"—— 根因是小屏时 Views/Controls/ResponsiveColumns 会把次要列整列
        // Collapsed 掉，用户拖列宽也找不回来。现在列全在，装不下就出横向滚动条
        // （XAML 每张表都写了 ScrollViewer.HorizontalScrollBarVisibility="Auto"）。
        // 要恢复"小屏隐藏次要列"，就用 ResponsiveColumns.Apply(<grid>, compact) 挂在 SizeChanged 上。
    }
}
