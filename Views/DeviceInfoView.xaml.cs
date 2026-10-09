using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class DeviceInfoView : UserControl
{
    public DeviceInfoView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var compact = ActualWidth > 0 && ActualWidth < ResponsiveColumns.CompactWidthThreshold;
        ResponsiveColumns.Apply(DevicePortsGrid, compact);
        ResponsiveColumns.Apply(DeviceVlanGrid, compact);
        ResponsiveColumns.Apply(DeviceMacGrid, compact);
        ResponsiveColumns.Apply(DeviceIpGrid, compact);
    }
}
