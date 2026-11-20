using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Models;

/// <summary>一个供应商的操作和模型别名。按钮的意图都在 ViewModel 里。</summary>
public partial class ProviderDetail : UserControl
{
    public ProviderDetail() => InitializeComponent();

    /// <summary>「⋯」:编辑、退出登录(OAuth)、删除密钥(有密钥时)、删除供应商(标红,最后)。</summary>
    private void OnProviderMenuDataContext(object? sender, EventArgs e)
    {
        if (sender is NcwDropdownMenu { DataContext: ProviderItemViewModel row } menu)
        {
            var items = new List<NcwMenus.Item> { new(NcwTexts.Get(NcwTextKind.ProviderEdit), row.EditCommand) { Glyph = ArcIcons.Pencil } };
            if (row.IsOAuth)
            {
                items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderSignOut), row.SignOutCommand) { Glyph = Lucide.Geometry(LucideIconKind.LogOut) });
            }

            if (row.HasLast4)
            {
                items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderRemoveKey), row.RemoveKeyCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = Lucide.Geometry(LucideIconKind.KeyRound) });
            }

            items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ProviderRemove), row.RemoveCommand, IsDestructive: true, SeparatorBefore: !row.HasLast4) { Glyph = ArcIcons.Trash2 });
            NcwMenus.Populate(menu, items);
        }
    }
}
