using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// <see cref="ObservableCollection{T}"/> + 一次性的"整体替换"。
///
/// 为什么需要：刷新大表时常见写法是 `Clear()` 再逐条 `Add()`，这会给每一行发一次
/// <c>CollectionChanged</c>（Clear 一次 + Add N 次）。DataGrid 每收到一次就要动一次行容器，
/// 上万行时 UI 线程要处理上万次通知 —— 现场表现是"读完 show mac-address-table 卡好几秒"，
/// 明明解析本身很快、也已经放到后台线程了，卡的是最后贴结果这一步。
///
/// <see cref="ReplaceAll"/> 只发一次 Reset，把 N 次通知压成 1 次。
/// 注意：Reset 会让 DataGrid 重建可见行并丢失选中项 —— 这正是"刷新"想要的行为
///（旧数据已经不成立，保留选中反而会指到别的行上）。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>用 <paramref name="items"/> 整体替换当前内容，只发一次 Reset 通知。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        // 手动补上集合属性通知，再发一次 Reset：与 ObservableCollection 自己的行为对齐，
        // 但把 N 次变成 1 次。
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
