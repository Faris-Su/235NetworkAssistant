using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class VlanView : UserControl
{
    public VlanView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    /// <summary>小屏隐藏“端口数 / Ports”列，端口明细在选中项详情里查看。</summary>
    private void ApplyResponsiveLayout() => ResponsiveColumns.ApplyForWidth(VlanGrid, ActualWidth);
}
