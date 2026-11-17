using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Dialogs;

/// <summary>「新建项目」窗口的内容。设备下拉框的文字是「名字 · 类型」(Arc 的 Select 用函数取文字,AOT 安全)。</summary>
public partial class NewProjectDialog : UserControl
{
    public NewProjectDialog()
    {
        InitializeComponent();
        DeviceSelect.TextSelector = item => item is DeviceChoice d ? Text(d) : null;
        DeviceSelect.ValueSelector = item => (item as DeviceChoice)?.DeviceId;
    }

    /// <summary>设备在下拉框里的文字。本机只写名字(名字本身就是「本机」或电脑名)。</summary>
    internal static string Text(DeviceChoice device) => device.Kind switch
    {
        DeviceKind.Cloud => $"{device.Name} · {NcwTexts.Get(NcwTextKind.DeviceKindCloud)}",
        DeviceKind.Remote => $"{device.Name} · {NcwTexts.Get(NcwTextKind.DeviceKindRemote)}",
        _ => device.Name,
    };

    /// <summary>窗口打开后把焦点放进路径框。</summary>
    internal void FocusPath() => PathInput.Focus();
}
