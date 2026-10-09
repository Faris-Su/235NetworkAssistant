namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 带勾选状态的设备项（DataGrid 的复选框列绑定它）。
///
/// 为什么单独一个文件：**批量备份**在用它（"勾选一批设备去备份"），
/// 而不是只有巡检页用。原来它定义在 `InspectionViewModel.cs` 里，
/// 一旦把巡检页从发布件里排除（用户要求"不打包、代码留着"），批量备份就会跟着编译不过。
/// </summary>
public sealed class SelectableDeviceItem : Helpers.ObservableObject
{
    private bool _isSelected;

    public SelectableDeviceItem(DeviceTarget target) => Target = target;

    public DeviceTarget Target { get; }

    public string Ip => Target.Ip;

    public string Name => Target.Name;

    public string Building => Target.Building;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
