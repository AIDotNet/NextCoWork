using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>「会话」标签的根页(项目列表)。点项目只是告诉导航「打开这个项目」;下拉刷新重拉会话列表。</summary>
public partial class ProjectsPage : NcwPage
{
    public ProjectsPage()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnProjectClick);
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

    private void OnProjectClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is RailProjectViewModel project)
        {
            Navigation?.OpenProject(project.Project);
        }
    }
}
