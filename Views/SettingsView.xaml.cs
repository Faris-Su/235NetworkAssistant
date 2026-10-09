using System.Windows;
using System.Windows.Controls;

namespace RuijieNetworkAssistant.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
    }

    private void SettingsColumnsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void OpenMachineSoul_Click(object sender, RoutedEventArgs e)
    {
        var window = new MachineSoulWindow();
        var owner = Window.GetWindow(this);
        if (owner is not null)
        {
            window.Owner = owner;
        }
        window.ShowDialog();
    }

    private void ApplyResponsiveLayout()
    {
        if (SettingsColumnsGrid.ColumnDefinitions.Count < 3 || SettingsColumnsGrid.ActualWidth <= 0)
        {
            return;
        }

        var compact = ActualWidth < Controls.ResponsiveColumns.CompactWidthThreshold;
        SettingsColumnsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsColumnsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(8);
        SettingsColumnsGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(LibraryAndPreviewColumn, 0);
        Grid.SetColumn(LibraryAndPreviewColumn, 0);
        Grid.SetColumnSpan(LibraryAndPreviewColumn, compact ? 3 : 1);

        Grid.SetRow(SettingsOptionsPanel, compact ? 1 : 0);
        Grid.SetColumn(SettingsOptionsPanel, compact ? 0 : 2);
        Grid.SetColumnSpan(SettingsOptionsPanel, compact ? 3 : 1);
    }
}
