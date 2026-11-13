using System.ComponentModel;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop.Rail;

/// <summary>项目栏上的一个项目图标。右键菜单:新建项目组 / 移出项目组、从项目栏移除。</summary>
public partial class RailProjectTile : UserControl
{
    private RailProjectViewModel? _vm;

    public RailProjectTile() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as RailProjectViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }

        AttachMenu();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RailProjectViewModel.IsInGroup))
        {
            AttachMenu();
        }
    }

    private void AttachMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        NcwMenus.Attach(this,
        [
            vm.IsInGroup
                ? new NcwMenus.Item("移出项目组", vm.LeaveGroupCommand) { Glyph = ArcIcons.FolderOpen }
                : new NcwMenus.Item("新建项目组", vm.WrapInGroupCommand) { Glyph = ArcIcons.Folder },
            new NcwMenus.Item("从项目栏移除", vm.RemoveFromRailCommand, IsDestructive: true, SeparatorBefore: true) { Glyph = ArcIcons.Trash2 },
        ]);
    }
}
