using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>收件箱页。点一条 → 交给 <see cref="InboxViewModel.OpenCommand"/>(进会话并定位);下拉刷新重拉会话列表。这里没有业务状态。</summary>
public partial class InboxPage : NcwPage
{
    public InboxPage()
    {
        InitializeComponent();
        Refresher.RefreshRequested += async (_, e) =>
        {
            using var deferral = e.GetDeferral();
            if (RefreshAsync is { } refresh)
            {
                await refresh();
            }
        };
    }

    /// <summary>下拉刷新要做的事(外壳接到 <c>SessionActions.RefreshAllAsync</c>)。</summary>
    public Func<Task>? RefreshAsync { get; set; }

    private InboxViewModel? Vm => DataContext as InboxViewModel;

    private void OnEntryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InboxEntryViewModel entry)
        {
            Vm?.OpenCommand.Execute(entry);
        }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InboxEntryViewModel entry)
        {
            Vm?.DismissCommand.Execute(entry);
            e.Handled = true; // 不要同时触发整行的点击
        }
    }
}
