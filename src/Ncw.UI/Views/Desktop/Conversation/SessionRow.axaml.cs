using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>会话行。右键菜单只是把几个意图接到 ViewModel 的命令上(命令再转给 SessionActions)。</summary>
public partial class SessionRow : UserControl
{
    public SessionRow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not SessionItemViewModel vm)
        {
            return;
        }

        NcwMenus.Attach(this,
        [
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.Rename), vm.RenameCommand) { Glyph = ArcIcons.Pencil },
            new NcwMenus.Item(NcwTexts.Get(vm.IsFavorite ? NcwTextKind.Unfavorite : NcwTextKind.Favorite), vm.ToggleFavoriteCommand) { Glyph = ArcIcons.Star },
            new NcwMenus.Item(NcwTexts.Get(vm.IsArchived ? NcwTextKind.Unarchive : NcwTextKind.Archive), vm.ToggleArchivedCommand) { Glyph = ArcIcons.Archive },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.DeleteSession), vm.DeleteCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 },
        ]);
    }
}
