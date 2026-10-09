using System.Windows;
using System.Windows.Controls;

namespace RuijieNetworkAssistant.Views.Controls;

/// <summary>
/// 带"显示 / 隐藏"按钮的密码输入框（Telnet 密码 / Enable 密码 / SNMP Community 共用）。
/// 只负责显示方式切换与值同步；**不**写日志、不落盘、不改变任何密码策略。
/// </summary>
public partial class PasswordRevealBox : UserControl
{
    /// <summary>密码值（TwoWay 绑定到 ViewModel；明文/隐藏两种模式下都同步）。</summary>
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password),
        typeof(string),
        typeof(PasswordRevealBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPasswordPropertyChanged));

    private bool _syncing;

    public PasswordRevealBox()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncToInputs();
    }

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    /// <summary>当前是否显示明文（诊断/自检用）。</summary>
    public bool IsRevealed => PlainBox.Visibility == Visibility.Visible;

    private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordRevealBox box)
        {
            box.SyncToInputs();
        }
    }

    private void SyncToInputs()
    {
        if (_syncing)
        {
            return;
        }

        var value = Password ?? string.Empty;
        _syncing = true;
        try
        {
            if (!string.Equals(SecretBox.Password, value, StringComparison.Ordinal))
            {
                SecretBox.Password = value;
            }

            if (!string.Equals(PlainBox.Text, value, StringComparison.Ordinal))
            {
                PlainBox.Text = value;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e) => Push(SecretBox.Password);

    private void OnPlainChanged(object sender, TextChangedEventArgs e) => Push(PlainBox.Text);

    private void Push(string value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (!string.Equals(SecretBox.Password, value, StringComparison.Ordinal))
            {
                SecretBox.Password = value;
            }

            if (!string.Equals(PlainBox.Text, value, StringComparison.Ordinal))
            {
                PlainBox.Text = value;
            }
        }
        finally
        {
            _syncing = false;
        }

        Password = value;
    }

    /// <summary>👁：在 •••• 与明文之间切换（只影响显示）。</summary>
    private void OnToggle(object sender, RoutedEventArgs e) => Reveal(!IsRevealed);

    public void Reveal(bool reveal)
    {
        if (reveal)
        {
            PlainBox.Text = SecretBox.Password;
            PlainBox.Visibility = Visibility.Visible;
            SecretBox.Visibility = Visibility.Collapsed;
            PlainBox.Focus();
            PlainBox.CaretIndex = PlainBox.Text.Length;
        }
        else
        {
            SecretBox.Password = PlainBox.Text;
            SecretBox.Visibility = Visibility.Visible;
            PlainBox.Visibility = Visibility.Collapsed;
        }
    }
}
