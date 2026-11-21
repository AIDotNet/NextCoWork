using Avalonia.Controls;
using Avalonia.Input;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>会话行。右键菜单和悬停动作都把意图接到 ViewModel 的命令上(命令再转给 SessionActions)。</summary>
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

        var pinTip = NcwTexts.Get(vm.IsFavorite ? NcwTextKind.UnpinSession : NcwTextKind.PinSession);
        TKCode.Arc.Tooltip.SetTip(PinButton, pinTip);
        Avalonia.Automation.AutomationProperties.SetName(PinButton, pinTip);
        NcwMenus.Attach(this,
        [
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.Rename), vm.RenameCommand) { Glyph = ArcIcons.Pencil },
            new NcwMenus.Item(pinTip, vm.ToggleFavoriteCommand) { Glyph = Lucide.Geometry(vm.IsFavorite ? LucideIconKind.PinOff : LucideIconKind.Pin) },
            new NcwMenus.Item(NcwTexts.Get(vm.IsArchived ? NcwTextKind.Unarchive : NcwTextKind.Archive), vm.ToggleArchivedCommand) { Glyph = ArcIcons.Archive },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.DeleteSession), vm.DeleteCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 },
        ]);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SessionItemViewModel vm)
        {
            vm.RenameCommand.Execute(null);
            e.Handled = true;
        }
    }
}
