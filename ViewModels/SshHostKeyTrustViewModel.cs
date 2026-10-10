using System.Collections.ObjectModel;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

namespace RuijieNetworkAssistant.ViewModels;

public sealed class SshHostKeyTrustViewModel : ViewModelBase
{
    private readonly SshHostKeyTrustStore _store;
    private readonly SshHostKeyProbe _probe;
    private readonly ILogService _log;
    private SshHostKeyInfo? _selectedKey;
    private string _statusText = "正在读取信任记录…";
    private string _errorText = string.Empty;
    private bool _isBusy;
    private bool _storeHealthy;

    public SshHostKeyTrustViewModel(SshHostKeyTrustStore store, SshHostKeyProbe probe, ILogService log)
    {
        _store = store;
        _probe = probe;
        _log = log;
        Title = "SSH 已信任设备";

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        UpdateSelectedCommand = new AsyncRelayCommand(UpdateSelectedAsync, () => !IsBusy && StoreHealthy && SelectedKey is not null);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => !IsBusy && StoreHealthy && SelectedKey is not null);
    }

    public event EventHandler<SshHostKeyConfirmationRequestEventArgs>? TrustConfirmationRequested;

    public event EventHandler<SshHostKeyDeleteRequestEventArgs>? DeleteConfirmationRequested;

    public ObservableCollection<SshHostKeyInfo> TrustedKeys { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand UpdateSelectedCommand { get; }

    public AsyncRelayCommand RemoveSelectedCommand { get; }

    public SshHostKeyInfo? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (SetProperty(ref _selectedKey, value))
            {
                UpdateSelectedCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                UpdateSelectedCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool StoreHealthy
    {
        get => _storeHealthy;
        private set
        {
            if (SetProperty(ref _storeHealthy, value))
            {
                UpdateSelectedCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StorePath => _store.FilePath;

    public async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var previous = SelectedKey;
            var entries = await _store.GetAllAsync().ConfigureAwait(true);
            TrustedKeys.Clear();
            foreach (var entry in entries.OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Port))
            {
                TrustedKeys.Add(entry);
            }

            StoreHealthy = true;
            SelectedKey = previous is null
                ? TrustedKeys.FirstOrDefault()
                : TrustedKeys.FirstOrDefault(x => x.Host.Equals(previous.Host, StringComparison.OrdinalIgnoreCase) && x.Port == previous.Port);
            StatusText = $"已信任 {TrustedKeys.Count} 台 SSH 设备。";
        }
        catch (Exception ex)
        {
            StoreHealthy = false;
            TrustedKeys.Clear();
            SelectedKey = null;
            ErrorText = ex.Message;
            StatusText = "信任库无法安全读取；已禁用修改操作。";
            _log.Error("读取 SSH 已信任设备列表失败。", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UpdateSelectedAsync()
    {
        var trusted = SelectedKey;
        if (trusted is null)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            StatusText = $"正在独立探测 {trusted.Host}:{trusted.Port} 的 SSH 公钥…";
            var candidate = await _probe.ProbeAsync(trusted.Host, trusted.Port, 10000).ConfigureAwait(true);
            if (SshHostKeyTrustStore.Matches(trusted, candidate))
            {
                StatusText = "设备当前公钥与已信任记录相同，无需更新。";
                return;
            }

            var request = new SshHostKeyConfirmationRequestEventArgs(candidate, trusted);
            TrustConfirmationRequested?.Invoke(this, request);
            if (TrustConfirmationRequested is null || !await request.Decision.ConfigureAwait(true))
            {
                StatusText = "已取消更新；原有信任记录保持不变。";
                return;
            }

            await _store.TrustAsync(candidate).ConfigureAwait(true);
            StatusText = "已更新主机公钥信任记录。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "更新失败；原记录未被主动删除。";
            _log.Warn("核验并更新 SSH 主机密钥失败。", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = SelectedKey;
        if (selected is null)
        {
            return;
        }

        var request = new SshHostKeyDeleteRequestEventArgs(selected.Host, selected.Port);
        DeleteConfirmationRequested?.Invoke(this, request);
        if (DeleteConfirmationRequested is null || !await request.Decision.ConfigureAwait(true))
        {
            StatusText = "已取消删除；信任记录保持不变。";
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            await _store.RemoveAsync(selected.Host, selected.Port).ConfigureAwait(true);
            StatusText = "已删除信任记录；下次连接会重新要求核验。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "删除失败；信任记录未能安全更新。";
            _log.Warn("删除 SSH 主机密钥信任记录失败。", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
