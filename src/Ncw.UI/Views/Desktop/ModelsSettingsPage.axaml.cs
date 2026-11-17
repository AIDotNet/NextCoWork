using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

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

    /// <summary>供应商卡片的「⋯」:编辑、退出登录(OAuth)、删除密钥(有密钥时)、删除供应商(标红,最后)。</summary>
    private void OnProviderMenuDataContext(object? sender, EventArgs e)
    {
        if (sender is NcwDropdownMenu { DataContext: ProviderItemViewModel row } menu)
        {
            var items = new List<NcwMenus.Item> { new(NcwTexts.Get(NcwTextKind.ProviderEdit), row.EditCommand) };
            if (row.IsOAuth)
            {
                items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderSignOut), row.SignOutCommand));
            }

            if (row.HasLast4)
            {
                items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderRemoveKey), row.RemoveKeyCommand, IsDestructive: true, SeparatorBefore: true));
            }

            items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderRemove), row.RemoveCommand, IsDestructive: true, SeparatorBefore: !row.HasLast4));
            NcwMenus.Populate(menu, items);
        }
    }
}
