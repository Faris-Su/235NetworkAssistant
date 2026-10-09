using RuijieNetworkAssistant.Helpers;

using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.ViewModels;

/// <summary>
/// 页面 ViewModel 基类。页面采用“按需初始化”：第一次打开页面时才加载数据，
/// 避免启动时一次性初始化所有页面（低配外勤电脑的性能要求）。
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _initialized;

    public string Title { get; protected set; } = string.Empty;


    public bool IsInitialized => _initialized;

    public async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await OnInitializeAsync().ConfigureAwait(true);
    }

    protected virtual Task OnInitializeAsync() => Task.CompletedTask;
}

/// <summary>Shell 提供的跨页面能力：导航、状态栏提示、最近操作记录。</summary>
public interface IShellNavigator
{
    void NavigateTo(string key);

    /// <summary>带参数的导航（例如从 LLDP 邻居页跳到端口页并预选端口）。</summary>
    void NavigateTo(string key, object? parameter);

    void ReportStatus(string message);

    /// <param name="ledgerDetail">
    /// 只写进操作台账的详细内容（例如实际下发的命令原文）；界面列表仍用 <paramref name="detail"/> 的短版本。
    /// </param>
    void AddRecentOperation(string title, string detail, bool succeeded = true, string? ledgerDetail = null);

    /// <summary>打开 Command Preview 对话框。所有配置操作必须经过该组件。</summary>
    void ShowCommandPreview(CommandPlan plan);

    /// <summary>
    /// 打开 [保存配置] 对话框（write）。这是唯一会写设备持久配置的入口，
    /// 需要用户在弹窗里按住按钮 1.2 秒才会真正发送。
    /// </summary>
    void ShowSaveConfig();

    /// <summary>专注 CLI 模式（隐藏导航与状态栏，把空间让给终端）。</summary>
    bool IsFocusMode { get; }

    void SetFocusMode(bool enabled);

    event EventHandler? FocusModeChanged;
}

/// <summary>页面需要接收导航参数时实现该接口。</summary>
public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);

    /// <summary>
    /// 离开本页时调用（默认什么都不做）。页面 VM 会被 MainViewModel 缓存复用，
    /// 所以这里**不是**释放资源的地方，而是"停掉只应该在页面上跑的东西"（例如自动刷新定时器）。
    /// </summary>
    void OnNavigatedFrom()
    {
    }
}
