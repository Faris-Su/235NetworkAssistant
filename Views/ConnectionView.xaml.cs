using System.Windows;
using System.Windows.Controls;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// 连接页面。Code-behind 只做 PasswordBox 与 ViewModel 的桥接
/// （WPF 的 PasswordBox.Password 不是依赖属性，无法直接绑定），不含任何设备/CLI 逻辑。
/// </summary>
public partial class ConnectionView : UserControl
{
    private ConnectionViewModel? _viewModel;

    public ConnectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachViewModel(e.NewValue as ConnectionViewModel);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as ConnectionViewModel);
        SyncPasswordFromViewModel();
    }

    private void AttachViewModel(ConnectionViewModel? next)
    {
        if (ReferenceEquals(_viewModel, next))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.SshHostKeyConfirmationRequested -= OnSshHostKeyConfirmationRequested;
            _viewModel.SshHostKeyManagerRequested -= OnSshHostKeyManagerRequested;
        }

        _viewModel = next;
        if (_viewModel is not null)
        {
            _viewModel.SshHostKeyConfirmationRequested += OnSshHostKeyConfirmationRequested;
            _viewModel.SshHostKeyManagerRequested += OnSshHostKeyManagerRequested;
        }

        SyncPasswordFromViewModel();
    }

    private void SyncPasswordFromViewModel()
    {
        // 密码框已换成 PasswordRevealBox（内部自带 TwoWay 绑定与 👁 显示/隐藏），
        // 这里不再需要手工桥接；保留方法是为了数据上下文切换时立即刷新显示。
        _ = TelnetPasswordBox;
        _ = SshPasswordBox;
        _ = EnablePasswordBox;
    }

    private void OnSshHostKeyConfirmationRequested(object? sender, SshHostKeyConfirmationRequestEventArgs e)
    {
        // SshHostKeyTrustWorkflow resumes on a pool thread after its network probe.
        // Never call WPF MessageBox from that thread: the old catch converted the
        // resulting dispatcher/STA exception into a silent "user cancelled" result.
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.Invoke(() => OnSshHostKeyConfirmationRequested(sender, e));
            }
            catch (Exception ex)
            {
                AppServices.Log.Error("无法切回 UI 线程显示 SSH 主机密钥确认框。", ex);
                e.Fail(ex);
            }

            return;
        }

        try
        {
            var key = e.Candidate;
            var message =
                $"首次连接 SSH 设备：{key.Host}:{key.Port}\n\n" +
                $"主机密钥算法：{key.Algorithm}（{key.KeyLength} 位）\n" +
                $"SHA256 指纹：\n{key.FingerprintSha256}\n\n" +
                "请通过 Console、设备标签/管理平台或其他可信渠道核对该指纹。不要只根据当前网络连接本身判断。\n\n" +
                "只有确认指纹一致后才选择“是”。确认后会保存该设备公钥，并继续 SSH 登录；选择“否”不会认证，也不会保存。\n\n" +
                "是否已通过可信渠道核验并信任此主机密钥？";
            var answer = MessageBox.Show(
                Window.GetWindow(this) ?? Application.Current?.MainWindow,
                message,
                "核验 SSH 主机密钥",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            e.Complete(answer == MessageBoxResult.Yes);
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("显示 SSH 主机密钥确认框失败。", ex);
            e.Fail(ex);
        }
    }

    private void OnSshHostKeyManagerRequested(object? sender, EventArgs e)
    {
        var window = new SshHostKeyTrustWindow
        {
            Owner = Window.GetWindow(this),
            DataContext = new SshHostKeyTrustViewModel(
                AppServices.SshHostKeys,
                AppServices.SshHostKeyProbe,
                AppServices.Log),
        };
        window.ShowDialog();
    }

}
