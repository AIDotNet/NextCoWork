using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>
/// 右侧项目文件面板的视图。code-behind 只管树控件自己的展示:展开 / 收起事件转给 ViewModel(惰性加载由 <see cref="FileTreeViewModel"/> 做)、
/// 列表重建后把该展开的目录在控件里展开、「更多」菜单的条目。右键菜单在每一行(<see cref="FileRow"/>)。
/// </summary>
public partial class FilesPane : UserControl
{
    private FilesPanelViewModel? _vm;
    private bool _syncing;

    internal bool IsSynchronizing => _syncing;

    public FilesPane()
    {
        InitializeComponent();
        Tree.DoubleTapped += OnTreeDoubleTapped;
        AddHandler(KeyDownEvent, OnPaneKeyDown, RoutingStrategies.Tunnel);
        Tree.ChildrenSelector = static node => (node as FileNodeViewModel)?.DisplayChildren;
        Tree.HeaderSelector = static node => (node as FileNodeViewModel)?.DisplayName;
        Tree.ItemExpanded += (_, e) =>
        {
            if (!_syncing && e.Item is FileNodeViewModel node)
            {
                _ = _vm?.Tree.ExpandAsync(node);
            }
        };
        Tree.ItemCollapsed += (_, e) =>
        {
            if (!_syncing && e.Item is FileNodeViewModel node)
            {
                _vm?.Tree.Collapse(node);
            }
        };
        Tree.ItemInvoked += (_, e) =>
        {
            if (e.Item is FileNodeViewModel { IsFile: true } or FileNodeViewModel { IsMore: true })
            {
                _vm?.OpenFileCommand.Execute(e.Item);
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.UseInlineEditing = false;
            _vm.CancelInlineEditCommand.Execute(null);
            _vm.Tree.Loaded -= OnLoaded;
            _vm.Tree.Roots.CollectionChanged -= OnRootsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Tree.PropertyChanged -= OnTreeChanged;
        }

        _vm = DataContext as FilesPanelViewModel;
        if (_vm is not null)
        {
            _vm.UseInlineEditing = true;
            _vm.Tree.Loaded += OnLoaded;
            _vm.Tree.Roots.CollectionChanged += OnRootsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Tree.PropertyChanged += OnTreeChanged;
        }

        RebuildMoreMenu();
        RestoreSoon();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncing && e.PropertyName is nameof(FilesPanelViewModel.Selected))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm?.Selected is { } selected)
                {
                    Tree.ScrollIntoView(selected);
                    Dispatcher.UIThread.Post(() => Tree.GetVisualDescendants().OfType<FileRow>().FirstOrDefault(r => r.DataContext is FileNodeViewModel { IsEditing: true })?.FocusInlineEditor(), DispatcherPriority.Background);
                }
            }, DispatcherPriority.Loaded);
        }

        if (e.PropertyName is nameof(FilesPanelViewModel.CanWrite))
        {
            RebuildMoreMenu();
        }
    }

    private void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileTreeViewModel.ShowIgnored) or nameof(FileTreeViewModel.CompactFolders))
        {
            RebuildMoreMenu();
        }
    }

    private void OnLoaded(FileNodeViewModel? node) => Dispatcher.UIThread.Post(() => Sync(node), DispatcherPriority.Loaded);

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.UIThread.Post(() => Sync(null), DispatcherPriority.Loaded);

    private void RestoreSoon() => Dispatcher.UIThread.Post(() => Sync(null), DispatcherPriority.Loaded);

    /// <summary>
    /// 让树控件的展开状态跟上 ViewModel:控件不观察节点的 Children 变化(惰性加载把占位换成真实子节点之后它不会自己刷新),
    /// 所以一个目录列完之后收起再展开一次(期间的展开 / 收起事件不转给 ViewModel);根目录重载后,把 ViewModel 里记着展开的目录依次展开。
    /// </summary>
    private void Sync(FileNodeViewModel? node)
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (node is null)
            {
                // Arc 缓存子项:紧凑链改变后重设源,只影响展示。
                var selected = _vm.Selected;
                Tree.ItemsSource = null;
                Tree.ItemsSource = _vm.Tree.Roots;
                foreach (var root in _vm.Tree.Roots)
                {
                    ExpandIfNeeded(root);
                }

                Tree.SelectedItem = selected;
                if (selected is not null) Tree.ScrollIntoView(selected);
                return;
            }

            if (Tree.IsItemExpanded(node))
            {
                Tree.Collapse(node);
            }

            if (node.IsExpanded && node.ChildrenLoaded)
            {
                Tree.Expand(node);
            }

            foreach (var child in node.DisplayChildren)
            {
                ExpandIfNeeded(child);
            }
        }
        finally
        {
            _syncing = false;
            // 恢复 Arc 选中行会把焦点交回树:所有重建 / 滚动工作完成后,再交给正在编辑的输入框。
            Dispatcher.UIThread.Post(() =>
            {
                Tree.GetVisualDescendants().OfType<FileRow>().FirstOrDefault(r => r.DataContext is FileNodeViewModel { IsEditing: true })?.FocusInlineEditor();
            }, DispatcherPriority.Background);
        }
    }

    private void ExpandIfNeeded(FileNodeViewModel node)
    {
        if (!node.IsExpanded || !node.IsDirectory || !node.ChildrenLoaded)
        {
            return;
        }

        if (!Tree.IsItemExpanded(node))
        {
            Tree.Expand(node);
        }

        foreach (var child in node.DisplayChildren)
        {
            ExpandIfNeeded(child);
        }
    }

    /// <summary>「更多」菜单:新建(在根目录)、显示被忽略的文件。</summary>
    private void RebuildMoreMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        NcwMenus.Populate(MoreMenu,
        [
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.FilesNewFile), vm.NewFileCommand, null) { IsDisabled = !vm.CanWrite, Glyph = Lucide.Geometry(LucideIconKind.FilePlus) },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.FilesNewFolder), vm.NewFolderCommand, null) { IsDisabled = !vm.CanWrite, Glyph = Lucide.Geometry(LucideIconKind.FolderPlus) },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.FilesCompactFolders), vm.ToggleCompactFoldersCommand, null) { IsChecked = vm.Tree.CompactFolders, Glyph = Lucide.Geometry(LucideIconKind.Folder) },
            new NcwMenus.Item(NcwTexts.Get(NcwTextKind.FilesShowIgnored), vm.ToggleShowIgnoredCommand, null, SeparatorBefore: true) { IsChecked = vm.Tree.ShowIgnored, Glyph = Lucide.Geometry(vm.Tree.ShowIgnored ? LucideIconKind.Eye : LucideIconKind.EyeOff) },
        ]);
    }

    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (e.Key == Key.F && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            vm.IsExpanded = true;
            vm.IsFilterVisible = true;
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsFilterVisible && !Tree.GetVisualDescendants().OfType<FileRow>().Any(r => r.DataContext is FileNodeViewModel { IsEditing: true }))
        {
            vm.IsFilterVisible = false;
            vm.Search.Query = "";
            if ((vm.Selected ?? vm.Tree.Roots.FirstOrDefault()) is { } focus) Tree.FocusItem(focus);
            e.Handled = true;
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Control;
        var row = source as FileRow ?? source?.FindAncestorOfType<FileRow>();
        if (row?.DataContext is FileNodeViewModel { IsFile: true, IsDraft: false, IsEditing: false } node && _vm is not null)
        {
            _vm.PinFileCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void OnResultClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileSearchResult result)
        {
            _vm?.OpenResultCommand.Execute(result);
        }
    }

    private void OnNamesMode(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Search.Mode = FileSearchMode.Names;
        }
    }

    private void OnContentsMode(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Search.Mode = FileSearchMode.Contents;
        }
    }
}
