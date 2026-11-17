using Avalonia.Controls;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop;

/// <summary>设置 · 模型与供应商的整页形式(手机)。这里只给设备下拉框接上文字选择器(Arc 的 Select 用函数取显示文字,AOT 安全)。</summary>
public partial class ModelsSettingsPage : UserControl
{
    public ModelsSettingsPage()
    {
        InitializeComponent();
        DeviceSelect.TextSelector = item => (item as DeviceChoice)?.Name;
        DeviceSelect.ValueSelector = item => (item as DeviceChoice)?.DeviceId;
    }
}
