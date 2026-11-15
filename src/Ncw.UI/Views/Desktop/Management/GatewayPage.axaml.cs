using Avalonia.Controls;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Management;

/// <summary>网关页。开关的意图是 <c>SetEnabledCommand(bool)</c>:Switch 没有带参数的命令,所以这里把点击转成命令调用(显示的值仍以设备返回的状态为准)。</summary>
public partial class GatewayPage : UserControl
{
    public GatewayPage()
    {
        InitializeComponent();
        EnabledSwitch.IsCheckedChanged += (_, _) =>
        {
            if (DataContext is GatewayViewModel vm && EnabledSwitch.IsChecked is { } on && on != vm.IsEnabled)
            {
                vm.SetEnabledCommand.Execute(on);
            }
        };
    }
}
