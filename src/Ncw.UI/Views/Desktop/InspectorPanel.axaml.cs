using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.ViewModels;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop;

public partial class InspectorPanel : UserControl
{
    private InspectorViewModel? _vm;

    public InspectorPanel()
    {
        InitializeComponent();
        Tree.ChildrenSelector = static node => (node as FileNodeViewModel)?.Children;
        Tree.ItemExpanded += (_, e) => SetExpanded(e.Item, true);
        Tree.ItemCollapsed += (_, e) => SetExpanded(e.Item, false);
    }

    /// <summary>
    /// 页签行右端让出右上角常驻按钮的宽度。Windows / Linux 上右上角还有窗口按钮,页签行更窄,
    /// 选中页签的名称照常显示(页签本来就只有选中的那个带名称)。
    /// </summary>
    public void SetTopRowTrailing(double trailing, bool compact) =>
        HeaderRow.Margin = new Thickness(8, 0, 8 + trailing, 0);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Addable.CollectionChanged -= OnAddableChanged;
            _vm.Files.Roots.CollectionChanged -= OnRootsChanged;
        }

        _vm = DataContext as InspectorViewModel;
        if (_vm is not null)
        {
            _vm.Addable.CollectionChanged += OnAddableChanged;
            _vm.Files.Roots.CollectionChanged += OnRootsChanged;
        }

        RebuildAddMenu();
        RestoreExpanded();
    }

    private void OnAddableChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAddMenu();

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(RestoreExpanded, DispatcherPriority.Loaded);

    /// <summary>「+」菜单的条目来自 ViewModel 的 Addable;点选即调用同一个 AddCommand。</summary>
    private void RebuildAddMenu()
    {
        if (_vm is null)
        {
            AddMenu.ItemsSource = null;
            return;
        }

        AddMenu.ItemsSource = _vm.Addable
            .Select(kind => new MenuEntry
            {
                Header = Converters.InspectorTabTitleConverter.Title(kind),
                Command = _vm.AddCommand,
                CommandParameter = kind,
            })
            .ToList();
    }

    /// <summary>树重建后,按 ViewModel 里记住的展开状态重新展开。</summary>
    private void RestoreExpanded()
    {
        if (_vm is null)
        {
            return;
        }

        foreach (var node in _vm.Files.Roots)
        {
            Expand(node);
        }
    }

    private void Expand(FileNodeViewModel node)
    {
        if (!node.IsExpanded)
        {
            return;
        }

        Tree.Expand(node);
        foreach (var child in node.Children)
        {
            Expand(child);
        }
    }

    private static void SetExpanded(object? item, bool expanded)
    {
        if (item is FileNodeViewModel node)
        {
            node.IsExpanded = expanded;
        }
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        Tree.CollapseAll();
        if (_vm is null)
        {
            return;
        }

        foreach (var node in _vm.Files.Roots)
        {
            Collapse(node);
        }

        static void Collapse(FileNodeViewModel node)
        {
            node.IsExpanded = false;
            foreach (var child in node.Children)
            {
                Collapse(child);
            }
        }
    }
}
