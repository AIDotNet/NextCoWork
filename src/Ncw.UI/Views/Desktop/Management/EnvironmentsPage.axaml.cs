using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Management;

public partial class EnvironmentsPage : UserControl
{
    public EnvironmentsPage() => InitializeComponent();

    /// <summary>每张 SSH 环境卡片的「⋯」菜单:测试连接、设为默认、编辑、删除(删除标红放最后,docs/design/03 §11.6)。</summary>
    private void OnRowMenuDataContext(object? sender, EventArgs e)
    {
        if (sender is NcwDropdownMenu { DataContext: EnvironmentRowViewModel row } menu)
        {
            var items = new List<NcwMenus.Item>
            {
                new(NcwTexts.Get(NcwTextKind.EnvTest), row.TestCommand) { Glyph = Lucide.Geometry(LucideIconKind.PlugZap) },
            };
            if (!row.IsDefault)
            {
                items.Add(new(NcwTexts.Get(NcwTextKind.EnvSetDefault), row.SetDefaultCommand) { Glyph = Lucide.Geometry(LucideIconKind.Star) });
            }

            items.Add(new(NcwTexts.Get(NcwTextKind.Edit), row.EditCommand, SeparatorBefore: true) { Glyph = ArcIcons.Pencil });
            items.Add(new(NcwTexts.Get(NcwTextKind.DeleteLabel), row.AskRemoveCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 });
            NcwMenus.Populate(menu, items);
        }
    }
}
