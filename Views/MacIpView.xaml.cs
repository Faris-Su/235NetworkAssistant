using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class MacIpView : UserControl
{
    public MacIpView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var compact = ActualWidth > 0 && ActualWidth < ResponsiveColumns.CompactWidthThreshold;
        ResponsiveColumns.Apply(MacGrid, compact);
        ResponsiveColumns.Apply(BindingGrid, compact);
    }
}
