using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Converters;

namespace Ncw.UI.Views.Desktop;

/// <summary>设置 · 模型与供应商。按钮的意图都在 ViewModel 里;这里只给下拉框接上文字选择器(Arc 的 Select 用函数取显示文字,AOT 安全)。</summary>
public partial class ModelsSettingsPage : UserControl
{
    public ModelsSettingsPage()
    {
        InitializeComponent();
        DeviceSelect.TextSelector = item => (item as DeviceChoice)?.Name;
        DeviceSelect.ValueSelector = item => (item as DeviceChoice)?.DeviceId;
        ProtocolSelect.ItemsSource = ProviderEditorViewModel.Protocols;
        ProtocolSelect.TextSelector = item => ProtocolTextConverter.Text(item as string);
        // 表单和导入弹窗的按钮:保存 / 取消 / 更新列表 都是 ViewModel 的命令
        SaveProviderButton.Click += (_, _) => Vm?.SaveProviderCommand.Execute(null);
        CancelProviderButton.Click += (_, _) => Vm?.CancelEditCommand.Execute(null);
        ApplyImportButton.Click += (_, _) => Vm?.ApplyImportCommand.Execute(null);
        CloseImportButton.Click += (_, _) => Vm?.CloseImportCommand.Execute(null);
    }

    private ModelsSettingsViewModel? Vm => DataContext as ModelsSettingsViewModel;
}
