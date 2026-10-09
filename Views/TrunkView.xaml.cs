using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class TrunkView : UserControl
{
    public TrunkView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout() => ResponsiveColumns.ApplyForWidth(TrunkGrid, ActualWidth);
}
