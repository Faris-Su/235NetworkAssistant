using System.Windows;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

public partial class SshHostKeyTrustWindow : Window
{
    private SshHostKeyTrustViewModel? _viewModel;

    public SshHostKeyTrustWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += async (_, _) =>
        {
            if (_viewModel is not null)
            {
                await _viewModel.RefreshCommand.ExecuteAsync();
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.TrustConfirmationRequested -= OnTrustConfirmationRequested;
            _viewModel.DeleteConfirmationRequested -= OnDeleteConfirmationRequested;
        }

        _viewModel = e.NewValue as SshHostKeyTrustViewModel;
        if (_viewModel is not null)
        {
            _viewModel.TrustConfirmationRequested += OnTrustConfirmationRequested;
            _viewModel.DeleteConfirmationRequested += OnDeleteConfirmationRequested;
        }
    }

    private void OnTrustConfirmationRequested(object? sender, SshHostKeyConfirmationRequestEventArgs e)
    {
        try
        {
            var key = e.Candidate;
            var message =
                $"设备：{key.Host}:{key.Port}\n" +
                $"算法：{key.Algorithm}（{key.KeyLength} 位）\n" +
                $"新 SHA256 指纹：\n{key.FingerprintSha256}\n\n";

            if (e.Previous is { } previous)
            {
                message +=
                    $"原 SHA256 指纹：\n{previous.FingerprintSha256}\n\n" +
                    "只有在已通过 Console 或其他可信渠道核实新指纹后，才应替换记录。\n" +
                    "确认更新后，后续 SSH 连接将信任新公钥并拒绝旧公钥。\n\n" +
                    "我已核验新指纹，确认更新？";
            }
            else
            {
                message +=
                    "首次信任必须先通过 Console、设备管理平台等独立可信渠道核验。\n" +
                    "确认后才会保存记录并允许后续认证。\n\n" +
                    "我已核验指纹，确认信任？";
            }

            var result = MessageBox.Show(
                this,
                message,
                e.Previous is null ? "首次信任 SSH 主机密钥" : "更新 SSH 主机密钥",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            e.Complete(result == MessageBoxResult.Yes);
        }
        catch
        {
            e.Complete(false);
        }
    }

    private void OnDeleteConfirmationRequested(object? sender, SshHostKeyDeleteRequestEventArgs e)
    {
        try
        {
            var result = MessageBox.Show(
                this,
                $"确定删除 {e.Host}:{e.Port} 的 SSH 主机密钥信任记录吗？\n\n删除后下次连接会重新显示指纹并要求核验。",
                "删除 SSH 信任记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            e.Complete(result == MessageBoxResult.Yes);
        }
        catch
        {
            e.Complete(false);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
