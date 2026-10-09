using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// Command Preview 对话框：显示操作说明、影响范围与 CLI 命令，
/// 只有用户点击[执行]才会把命令发送到设备。
/// </summary>
public partial class CommandPreviewWindow : Window
{
    public CommandPreviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 小屏适配：窗口不超过工作区（含 125%/150% DPI 缩放后的可用区域），
    /// 命令区内部滚动、说明与按钮固定。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 24);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 24);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    /// <summary>Esc 关闭；Enter 只在操作不危险时触发[执行]，危险操作必须显式点击按钮。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Enter && DataContext is CommandPreviewViewModel { Plan.IsDangerous: false } viewModel)
        {
            e.Handled = true;
            if (viewModel.ExecuteCommand.CanExecute(null))
            {
                viewModel.ExecuteCommand.Execute(null);
            }
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CommandPreviewViewModel oldViewModel)
        {
            oldViewModel.CloseRequested -= OnCloseRequested;
        }

        if (e.NewValue is CommandPreviewViewModel newViewModel)
        {
            newViewModel.CloseRequested += OnCloseRequested;
            EnablePasswordBox.Password = newViewModel.EnablePassword;
        }
    }

    /// <summary>Enable 密码：只进内存（ViewModel → AppServices），不落盘、不写日志。</summary>
    private void OnEnablePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommandPreviewViewModel viewModel)
        {
            viewModel.EnablePassword = EnablePasswordBox.Password;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
