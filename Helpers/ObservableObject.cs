using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// MVVM 基础：最小实现的 INotifyPropertyChanged 基类。
/// 不依赖任何第三方 MVVM 框架，保持程序体积与启动开销最小。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
