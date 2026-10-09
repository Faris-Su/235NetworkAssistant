using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class PortsView : UserControl
{
    public PortsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    /// <summary>小屏（页面可用宽度不足）时隐藏次要列，保留 Port / Status / VLAN。</summary>
    private void ApplyResponsiveLayout() => ResponsiveColumns.ApplyForWidth(PortsGrid, ActualWidth);
}
