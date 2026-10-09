namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// UI 线程调度钩子。WPF 应用启动时把 <see cref="Marshal"/> 指向 Dispatcher；
/// 命令行自检等没有 UI 的环境保持直通执行，因此本文件（以及 RelayCommand）不依赖 WPF。
///
/// 用途：命令的 CanExecuteChanged 必须回到 UI 线程触发——WPF 按钮会在事件里更新 IsEnabled，
/// 从后台线程直接触发就会抛 "The calling thread cannot access this object because a different thread owns it."。
/// </summary>
public static class UiThread
{
    /// <summary>由 App 启动时设置；为 null 表示当前环境没有 UI 线程（直接执行）。</summary>
    public static Action<Action>? Marshal { get; set; }

    public static void Post(Action action)
    {
        var marshal = Marshal;
        if (marshal is null)
        {
            action();
            return;
        }

        marshal(action);
    }
}
