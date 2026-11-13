using Avalonia.Controls;
using Avalonia.Media;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop.Rail;

/// <summary>项目组。右键菜单:展开 / 收起、更改颜色、解散项目组。</summary>
public partial class RailGroupView : UserControl
{
    public RailGroupView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not RailGroupViewModel vm)
        {
            return;
        }

        var colors = vm.Palette
            .Select(color => new NcwMenus.Item(color, vm.RecolorCommand, color)
            {
                Icon = new Avalonia.Controls.Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Background = new SolidColorBrush(Color.Parse(color)),
                },
            })
            .ToList();

        NcwMenus.Attach(this,
        [
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.Rename), vm.RenameCommand) { Glyph = ArcIcons.Pencil },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ToggleGroup), vm.ToggleCommand) { Glyph = ArcIcons.FolderOpen },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.ChangeColor), null) { Glyph = ArcIcons.Palette, Children = colors },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.DissolveGroup), vm.DissolveCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 },
        ]);
    }
}
