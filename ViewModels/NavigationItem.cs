namespace RuijieNetworkAssistant.ViewModels;

public sealed class NavigationItem
{
    public required string Key { get; init; }

    public required string Icon { get; init; }

    public required string Title { get; init; }

    /// <summary>页面 ViewModel 工厂：第一次导航时才创建。</summary>
    public required Func<ViewModelBase> Factory { get; init; }

    public ViewModelBase? Instance { get; set; }
}
