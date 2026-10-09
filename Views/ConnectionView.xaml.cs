using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// 连接页面。Code-behind 只做 PasswordBox 与 ViewModel 的桥接
/// （WPF 的 PasswordBox.Password 不是依赖属性，无法直接绑定），不含任何设备/CLI 逻辑。
/// </summary>
public partial class ConnectionView : UserControl
{
    public ConnectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncPasswordFromViewModel();

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncPasswordFromViewModel();

    private void SyncPasswordFromViewModel()
    {
        // 密码框已换成 PasswordRevealBox（内部自带 TwoWay 绑定与 👁 显示/隐藏），
        // 这里不再需要手工桥接；保留方法是为了数据上下文切换时立即刷新显示。
        _ = TelnetPasswordBox;
        _ = SshPasswordBox;
        _ = EnablePasswordBox;
    }

}
