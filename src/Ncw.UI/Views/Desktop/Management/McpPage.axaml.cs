using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Desktop.Management;

public partial class McpPage : UserControl
{
    public McpPage() => InitializeComponent();

    /// <summary>每张服务器卡片的「⋯」菜单:次要操作都在这里,删除标红放最后(docs/design/03 §11.6)。</summary>
    private void OnRowMenuDataContext(object? sender, EventArgs e)
    {
        if (sender is NcwDropdownMenu { DataContext: McpServerRowViewModel row } menu)
        {
            NcwMenus.Populate(menu,
            [
                new NcwMenus.Item(NcwTexts.Get(NcwTextKind.McpBrowse), row.BrowseCommand),
                new NcwMenus.Item(NcwTexts.Get(NcwTextKind.McpReconnect), row.ReconnectCommand),
                new NcwMenus.Item(NcwTexts.Get(NcwTextKind.McpTest), row.TestCommand),
                new NcwMenus.Item(NcwTexts.Get(NcwTextKind.Edit), row.EditCommand, SeparatorBefore: true),
                new NcwMenus.Item(NcwTexts.Get(NcwTextKind.DeleteLabel), row.AskRemoveCommand, IsDestructive: true, SeparatorBefore: true),
            ]);
        }
    }
}
