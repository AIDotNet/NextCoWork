using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using ArcIcons = TKCode.Arc.ArcIcons;

namespace Ncw.UI.Views.Mobile;

/// <summary>项目的会话列表页。点会话行 → 导航「打开会话」(进对话页);下拉刷新重拉会话列表。</summary>
public partial class ProjectSessionsPage : NcwPage
{
    public ProjectSessionsPage()
    {
        InitializeComponent();
        AddHandler(Avalonia.Controls.Button.ClickEvent, OnRowClick);
        // 左滑:归档 / 停止(命令参数是行的数据项;设备上的结果经 Store 回来,行自己更新)
        List.AddTrailing(NcwTexts.Get(NcwTextKind.Archive), ArcIcons.Archive, new RelayCommand<SessionItemViewModel>(item => item?.ToggleArchivedCommand.Execute(null)));
        List.AddTrailing(NcwTexts.Get(NcwTextKind.MobileStopSession), ArcIcons.Square, new RelayCommand<SessionItemViewModel>(item => item?.StopCommand.Execute(null)), destructive: true);
        Refresher.RefreshRequested += async (_, e) =>
        {
            using var deferral = e.GetDeferral();
            if (RefreshAsync is { } refresh)
            {
                await refresh();
            }
        };
    }

    public MobileNavigationViewModel? Navigation { get; set; }

    public Func<Task>? RefreshAsync { get; set; }


    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is SessionItemViewModel item && Navigation is { } navigation && DataContext is ProjectPanelViewModel panel && panel.HasProject
            && navigation.StackOf(MobileTab.Sessions).OfType<ProjectRoute>().LastOrDefault() is { } route)
        {
            navigation.OpenSession(route.Project, item.Id);
        }
    }
}
