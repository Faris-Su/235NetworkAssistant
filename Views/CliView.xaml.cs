using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RuijieNetworkAssistant.ViewModels;

namespace RuijieNetworkAssistant.Views;

/// <summary>
/// CLI 终端视图。Code-behind 只做“终端显示注入 + 按键透传”，不含任何设备或 CLI 命令逻辑：
/// 终端内容由 CliViewModel 推送到 ITerminalDisplay，按键只转换为 ViewModel 的动作。
/// </summary>
public partial class CliView : UserControl
{
    public CliView()
    {
        InitializeComponent();

        // 粘贴保护（借鉴 PuTTY）：终端区与行模式输入框都接管粘贴 —— 单行直发、多行先确认。
        DataObject.AddPastingHandler(Terminal, OnTerminalPasting);
        DataObject.AddPastingHandler(InputBox, OnTerminalPasting);

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
    }

    private CliViewModel? ViewModel => DataContext as CliViewModel;

    private void Attach()
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        Terminal.AutoScrollEnabled = viewModel.AutoScroll;
        viewModel.AttachDisplay(Terminal);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        FocusInputSurface();
    }

    private void Detach()
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.AttachDisplay(null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CliViewModel.TerminalMode))
        {
            FocusInputSurface();
        }
    }

    /// <summary>把焦点放到当前生效的输入面：终端模式=终端本身，行模式=下方输入框。</summary>
    private void FocusInputSurface()
    {
        if (ViewModel?.TerminalMode == true)
        {
            Terminal.Focus();
            Terminal.CaretIndex = Terminal.Text?.Length ?? 0;
        }
        else
        {
            InputBox.Focus();
        }
    }

    /// <summary>
    /// 终端模式：按键 → 语义（转义序列在 ViewModel 里，View 不写协议细节）。
    /// 行模式下这里什么都不做，终端保持只读。
    /// </summary>
    private async void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.TerminalMode != true)
        {
            return;
        }

        // 有选中文本时 Ctrl+C 保留复制行为（和 Xshell 一致）
        var hasSelection = !string.IsNullOrEmpty(Terminal.SelectedText);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.Enter);
                break;

            case Key.Back:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.Backspace);
                break;

            case Key.Space when !ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt:
                // 空格必须在这里处理：终端是「只读 TextBox」，空格会被 TextBox 自己吃掉
                // （不会产生 TextInput），表现为“打不出空格”，例如 conf t 变成 conft。
                // 这里直接按字符发给设备，并标记已处理，避免重复发送。
                e.Handled = true;
                await viewModel.SendTerminalTextAsync(" ");
                break;

            case Key.Tab:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.Tab);
                break;

            case Key.Escape:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.Escape);
                break;

            case Key.Up:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.HistoryPrevious);
                break;

            case Key.Down:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.HistoryNext);
                break;

            case Key.V when ctrl:
                e.Handled = true;
                if (Clipboard.ContainsText())
                {
                    await viewModel.SendTerminalTextAsync(Clipboard.GetText());
                }

                break;

            case Key.C when ctrl && !hasSelection:
                e.Handled = true;
                await viewModel.SendTerminalKeyAsync(CliViewModel.TerminalKey.Interrupt);
                break;
        }
    }

    /// <summary>终端模式：可打印字符直接发给设备（设备回显即所见）。</summary>
    private async void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.TerminalMode != true || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        await viewModel.SendTerminalTextAsync(e.Text);
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await viewModel.SendCommand.ExecuteAsync();
                InputBox.Focus();
                break;

            case Key.Up:
                e.Handled = viewModel.TryHistoryPrevious();
                break;

            case Key.Down:
                e.Handled = viewModel.TryHistoryNext();
                break;

            case Key.Tab:
                // Tab 透传给设备做命令补全，不切换焦点。
                e.Handled = true;
                await viewModel.SendTabAsync();
                InputBox.Focus();
                break;

            case Key.C:
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    string.IsNullOrEmpty(InputBox.SelectedText))
                {
                    // 无选中文本时 Ctrl+C 作为中断字符发送（0x03）；有选中文本时保留复制行为。
                    e.Handled = true;
                    await viewModel.SendCtrlCCommand.ExecuteAsync();
                    InputBox.Focus();
                }

                break;
        }
    }

    private async void OnInputPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Text != "?")
        {
            return;
        }

        // “?” 在锐捷 CLI 中立即触发帮助，不需要回车。
        e.Handled = true;
        await viewModel.SendQuestionMarkAsync();
        InputBox.Focus();
    }

    /// <summary>
    /// 粘贴处理（借鉴 PuTTY 的多行粘贴保护）：
    ///   · 单行 → 直接发给设备（等价于逐字输入）；
    ///   · 多行 → **先弹确认**，把前几行作为预览、并报出总行数，确认后才发。
    ///
    /// 为什么要拦：网络设备上"粘贴一整段配置"是最容易出事的操作 ——
    /// 段落里只要有一行不该执行（改 IP、shutdown、no 掉某个视图），就会立刻生效且不可撤销。
    /// 我们一律接管粘贴（不让 WPF 把文本塞进显示区），保证"屏幕上出现的字节=真正发给设备的字节"。
    /// </summary>
    private async void OnTerminalPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();

        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            return;
        }

        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string pasted || pasted.Length == 0)
        {
            return;
        }

        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // 统一换行，按"非空行"计数；发送时每行以 CR 结束（锐捷/思科 CLI 的标准行结束符）
        var normalized = pasted.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        if (lines.Length > 1)
        {
            var preview = string.Join(Environment.NewLine, lines.Take(8).Select(l => "  " + l));
            var more = lines.Length > 8 ? $"{Environment.NewLine}  …（还有 {lines.Length - 8} 行）" : string.Empty;
            var answer = MessageBox.Show(
                $"即将向设备发送 {lines.Length} 行命令：{Environment.NewLine}{Environment.NewLine}{preview}{more}"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "粘贴的内容会立即在设备上执行，且无法撤销 —— 确认发送？",
                "粘贴多行确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                return;   // 用户取消：一个字节都不发
            }
        }

        await viewModel.SendTerminalTextAsync(string.Join('\r', lines));
        InputBox.Focus();
    }

    private void OnAutoScrollChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } enabled })
        {
            Terminal.AutoScrollEnabled = enabled;
        }
    }

    private void OnMoreOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
