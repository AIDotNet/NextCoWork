using System.ComponentModel;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Tree;

/// <summary>
/// 侧边栏里的一个项目。右键菜单和「⋯」是同一份(docs/design/03 §1「项目菜单」):
/// 新建会话、新建终端、新建文件、新建文件夹、显示 / 隐藏已归档、在文件管理器中显示 / 复制路径、新建 / 移出项目组、更改图标…、从侧边栏移除。
/// </summary>
public partial class SidebarProjectNode : UserControl
{
    private SidebarProjectViewModel? _vm;

    public SidebarProjectNode() => InitializeComponent();

    /// <summary>拖放命中用:只有标题行算这个项目(展开出来的会话不算)。</summary>
    public Control HeaderRow => Header;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Tile.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as SidebarProjectViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Tile.PropertyChanged += OnViewModelChanged;
        }

        BuildMenus();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RailProjectViewModel.IsInGroup) or nameof(SidebarProjectViewModel.ShowsArchived) or nameof(SidebarProjectViewModel.IsLocal))
        {
            BuildMenus();
        }
    }

    private void BuildMenus()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        var newItems = new List<NcwMenus.Item>
        {
            new(NcwTexts.Get(NcwTextKind.NewSession), vm.NewSessionCommand) { Glyph = ArcIcons.SquarePen },
            new(NcwTexts.Get(NcwTextKind.NewTerminal), vm.NewTerminalCommand) { Glyph = ArcIcons.Terminal },
            new(NcwTexts.Get(NcwTextKind.FilesNewFile), vm.NewFileCommand) { Glyph = Lucide.Geometry(LucideIconKind.FilePlus) },
            new(NcwTexts.Get(NcwTextKind.FilesNewFolder), vm.NewFolderCommand) { Glyph = Lucide.Geometry(LucideIconKind.FolderPlus) },
        };
        NcwMenus.Populate(NewMenu, newItems);

        var tile = vm.Tile;
        var items = new List<NcwMenus.Item>(newItems)
        {
            new(NcwTexts.Get(vm.ShowsArchived ? NcwTextKind.SidebarHideArchived : NcwTextKind.ShowArchived), vm.ToggleArchivedCommand, SeparatorBefore: true) { Glyph = ArcIcons.Archive },
        };
        if (vm.IsLocal)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesReveal), vm.RevealCommand, SeparatorBefore: true) { Glyph = ArcIcons.FolderOpen });
        }

        items.Add(new(NcwTexts.Get(NcwTextKind.FilesCopyPath), vm.CopyPathCommand, SeparatorBefore: !vm.IsLocal) { Glyph = ArcIcons.Copy });
        items.Add(tile.IsInGroup
            ? new(NcwTexts.Get(NcwTextKind.LeaveGroup), tile.LeaveGroupCommand, SeparatorBefore: true) { Glyph = ArcIcons.FolderOpen }
            : new(NcwTexts.Get(NcwTextKind.NewGroup), tile.WrapInGroupCommand, SeparatorBefore: true) { Glyph = ArcIcons.Folder });
        items.Add(new(NcwTexts.Get(NcwTextKind.ChangeProjectIcon), tile.EditIconCommand) { Glyph = ArcIcons.Image });
        items.Add(new(NcwTexts.Get(NcwTextKind.RemoveFromRail), tile.RemoveFromRailCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 });

        NcwMenus.Attach(Header, items);
        NcwMenus.Populate(MoreMenu, items);
    }
}
