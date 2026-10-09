using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Views.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout() => ResponsiveColumns.ApplyForWidth(BackupGrid, ActualWidth);
}
