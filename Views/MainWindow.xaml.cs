using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// 主窗口：左侧导航 + 顶部设备状态栏 + 中央内容区 + 底部状态栏。
/// Code-behind 只做视图初始化，不包含任何 CLI 或设备操作逻辑。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 字体档位：启动时套用一次；用户改档位时由 AppearanceService 通知更新。
        AppearanceService.Apply();
        ViewModel?.UpdateAdaptiveLayout(ActualWidth > 0 ? ActualWidth : Width);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ViewModel?.UpdateAdaptiveLayout(e.NewSize.Width);
        }
    }

    /// <summary>全局快捷键：Ctrl+Alt+C 回 CLI、Ctrl+F 聚焦搜索框、Ctrl+L 专注模式、Esc 退出专注。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        if (ctrl && alt && e.Key == Key.C)
        {
            e.Handled = true;
            viewModel.NavigateTo("cli");
            return;
        }

        if (ctrl && !alt && e.Key == Key.F)
        {
            e.Handled = TryFocusFirstTextBox(this);
            return;
        }

        if (ctrl && !alt && e.Key == Key.L)
        {
            e.Handled = true;
            viewModel.ToggleFocusMode();
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsFocusMode)
        {
            e.Handled = true;
            viewModel.SetFocusMode(false);
        }
    }

    /// <summary>在当前页面的可视树中查找第一个可聚焦输入框（搜索框 / CLI 输入框）。</summary>
    private static bool TryFocusFirstTextBox(DependencyObject root)
    {
        var textBox = FindTextBox(root);
        if (textBox is null || !textBox.IsEnabled || !textBox.IsVisible)
        {
            return false;
        }

        textBox.Focus();
        textBox.SelectAll();
        return true;
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { IsReadOnly: false } textBox)
            {
                return textBox;
            }

            var found = FindTextBox(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
        {
            oldViewModel.CommandPreviewRequested -= OnCommandPreviewRequested;
            oldViewModel.SaveConfigRequested -= OnSaveConfigRequested;
        }

        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.CommandPreviewRequested += OnCommandPreviewRequested;
            newViewModel.SaveConfigRequested += OnSaveConfigRequested;
        }
    }

    /// <summary>打开 Command Preview 对话框：所有 GUI 配置操作的最后一道人工确认。</summary>
    private void OnCommandPreviewRequested(object? sender, CommandPlan plan)
    {
        var viewModel = new CommandPreviewViewModel(
            plan,
            Helpers.AppServices.Commands,
            Helpers.AppServices.Connections,
            (IShellNavigator)DataContext);

        var window = new CommandPreviewWindow
        {
            DataContext = viewModel,
            Owner = IsVisible ? this : null,
        };

        window.ShowDialog();
    }

    /// <summary>
    /// 打开 [保存配置]（write）对话框。这里只是"把弹窗显示出来"，
    /// 真正写设备要用户在弹窗里按住按钮 1.2 秒（见 SaveConfigViewModel）。
    /// </summary>
    private void OnSaveConfigRequested(object? sender, EventArgs e)
    {
        var viewModel = new SaveConfigViewModel(
            Helpers.AppServices.ConfigSave,
            Helpers.AppServices.Connections,
            (IShellNavigator)DataContext);

        var window = new SaveConfigWindow
        {
            DataContext = viewModel,
            Owner = IsVisible ? this : null,
        };

        window.ShowDialog();
    }
}
