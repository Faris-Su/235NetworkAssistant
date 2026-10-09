using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// [保存配置] 对话框：把 running-config 写入设备启动配置（write）。
///
/// 防误触在这里落地：**鼠标按住 1.2 秒**（进度条走满）才会调用 ViewModel 的写入；
/// 中途松开、鼠标移出按钮、窗口失去捕获都算取消。按钮是 Border 而不是 Button，
/// 并且 <c>Focusable=False</c>，所以回车 / 空格 / Tab 都不可能触发写入。
/// </summary>
public partial class SaveConfigWindow : Window
{
    private readonly DispatcherTimer _holdTimer;

    public SaveConfigWindow()
    {
        InitializeComponent();

        _holdTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _holdTimer.Tick += OnHoldTick;

        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _holdTimer.Stop();
    }

    private SaveConfigViewModel? ViewModel => DataContext as SaveConfigViewModel;

    /// <summary>小屏适配：窗口不超过工作区（含 125% / 150% 缩放后的可用区域）。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 24);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 24);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SaveConfigViewModel viewModel)
        {
            EnablePasswordBox.Password = viewModel.EnablePassword;
        }
    }

    /// <summary>Enable 密码只进内存（ViewModel → AppServices），不落盘、不写日志。</summary>
    private void OnEnablePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.EnablePassword = EnablePasswordBox.Password;
        }
    }

    private void OnHoldStarted(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.BeginHold();
        if (!viewModel.IsHolding)
        {
            // 不满足条件（未连接 / 普通模式 / 冷却中）：不进入按住状态，状态行已说明原因。
            return;
        }

        HoldSurface.CaptureMouse();
        _holdTimer.Start();
        e.Handled = true;
    }

    private void OnHoldEnded(object sender, MouseButtonEventArgs e)
    {
        StopHold();
        e.Handled = true;
    }

    private void OnHoldLostCapture(object sender, MouseEventArgs e) => StopHold();

    private void StopHold()
    {
        _holdTimer.Stop();
        if (HoldSurface.IsMouseCaptured)
        {
            HoldSurface.ReleaseMouseCapture();
        }

        ViewModel?.CancelHold();
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            _holdTimer.Stop();
            return;
        }

        // 鼠标移出按钮（小范围容差）也算放弃：避免"按下去就跑去干别的"。
        var position = Mouse.GetPosition(HoldSurface);
        var outside = position.X < -6 || position.Y < -6
            || position.X > HoldSurface.ActualWidth + 6
            || position.Y > HoldSurface.ActualHeight + 6;
        if (outside || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            StopHold();
            return;
        }

        if (!viewModel.UpdateHold())
        {
            return;
        }

        // 按满 1.2 秒：停止计时、释放捕获，然后才真正写入设备。
        _holdTimer.Stop();
        if (HoldSurface.IsMouseCaptured)
        {
            HoldSurface.ReleaseMouseCapture();
        }

        _ = viewModel.WriteAsync();
    }
}
