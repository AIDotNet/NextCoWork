using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Protocol = Ncw.Protocol.Methods;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>Git 页签的视图。code-behind 只管分支菜单的条目(每次分支列表 / 当前分支变了重填)和 ⌘↵ 提交这个键盘手势;一切动作都是 <see cref="GitPanelViewModel"/> 的命令。</summary>
public partial class GitPane : UserControl
{
    private GitPanelViewModel? _vm;

    public GitPane()
    {
        InitializeComponent();
        MessageBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Branches.CollectionChanged -= OnBranchesChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as GitPanelViewModel;
        if (_vm is not null)
        {
            _vm.Branches.CollectionChanged += OnBranchesChanged;
            _vm.PropertyChanged += OnViewModelChanged;
        }

        RebuildBranchMenu();
    }

    private void OnBranchesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildBranchMenu();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GitPanelViewModel.Branch))
        {
            RebuildBranchMenu();
        }
    }

    private void RebuildBranchMenu()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        List<NcwMenus.Item> items = [.. vm.Branches.Select(b => new NcwMenus.Item(b.Name, vm.CheckoutCommand, b) { IsChecked = b.Current })];
        items.Add(new NcwMenus.Item(NcwTexts.Get(NcwTextKind.GitNewBranch), vm.CreateBranchCommand, null, SeparatorBefore: items.Count > 0));
        NcwMenus.Populate(BranchMenu, items);
    }

    /// <summary>⌘↵ / Ctrl+↵ 提交(Enter 本身是换行)。</summary>
    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && _vm is { CanCommit: true } vm)
        {
            vm.CommitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
