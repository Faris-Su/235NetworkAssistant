using System.Windows.Input;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>通知按钮更新可用状态；跨线程调用时自动回到 UI 线程（WPF 按钮不能在后台线程更新）。</summary>
    public void RaiseCanExecuteChanged() =>
        UiThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>异步命令：避免在 UI 线程上执行串口/Telnet/Excel 等阻塞操作。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <summary>供视图按键或代码直接 await 使用（不经过 ICommand 的 async void 路径）。</summary>
    public Task ExecuteAsync(object? parameter = null) => _execute(parameter);

    /// <summary>
    /// 命令执行抛异常时的统一出口。
    /// `ICommand.Execute` 必须是 `async void`（接口规定），异常一旦逃出去就是**未处理异常**——
    /// WPF 会直接崩掉进程（本项目没有 DispatcherUnhandledException）。所以这里必须兜住所有异常，
    /// App 启动时把它接到"记日志 + 状态栏提示"，用户看到的是一句错误提示而不是程序消失。
    /// </summary>
    public static Action<Exception>? OnError { get; set; }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        IsRunning = true;
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户取消：静默处理，由调用方决定提示。
        }
        catch (Exception ex)
        {
            // 记日志（含堆栈）后交给统一出口提示；绝不 rethrow，否则 async void 会把进程带走
            AppServices.Log.Error($"命令执行失败：{ex.GetType().Name}", ex);
            OnError?.Invoke(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>通知按钮更新可用状态；跨线程调用时自动回到 UI 线程（WPF 按钮不能在后台线程更新）。</summary>
    public void RaiseCanExecuteChanged() =>
        UiThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
