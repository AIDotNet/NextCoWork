using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Inspector;
using Ncw.UI.Views.Desktop.Tree;

namespace Ncw.UI.Tests;

public class SidebarPaneUiTests
{
    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Minimum_sidebar_width_truncates_a_long_selected_project_before_status_and_actions()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var selected = stores.Selection.State.Project ?? throw new InvalidOperationException("No selected project");
        const string name = "Selected project with a very long name that must not obscure its activity or hover commands";
        stores.Workspace.Update(state => state with { Projects = state.Projects.SetItem(selected, state.Projects[selected] with { Name = name }) });
        var vm = new ShellViewModel(stores) { SidebarWidth = UiPrefs.SidebarMinWidth };
        vm.Sidebar.CollapseAllProjectsCommand.Execute(null);
        var shell = new DesktopShell { DataContext = vm };
        var window = new Window { Width = 1500, Height = 900, Content = shell };
        window.Show();
        try
        {
            Dispatch();
            var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();
            Assert.Equal(UiPrefs.SidebarMinWidth, sidebar.Bounds.Width);
            Assert.Empty(sidebar.GetVisualDescendants().OfType<FilesPane>());
            var project = sidebar.GetVisualDescendants().OfType<SidebarProjectNode>()
                .Single(n => n.DataContext is SidebarProjectViewModel { Tile.IsSelected: true });
            var header = project.HeaderRow;
            var title = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == name);
            var status = header.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Status");
            var actions = header.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "HoverActions");
            var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            Dispatch();

            Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.True(status.IsVisible);
            Assert.True(actions.IsVisible);
            var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), header)!.Value.X;
            var statusLeft = status.TranslatePoint(default, header)!.Value.X;
            var statusRight = status.TranslatePoint(new Point(status.Bounds.Width, 0), header)!.Value.X;
            var actionsLeft = actions.TranslatePoint(default, header)!.Value.X;
            var actionsRight = actions.TranslatePoint(new Point(actions.Bounds.Width, 0), header)!.Value.X;
            Assert.True(titleRight <= statusLeft);
            Assert.True(statusRight <= actionsLeft);
            Assert.True(actionsRight <= header.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Hover_actions_keep_project_status_visible_in_a_separate_column()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now));
        var shell = new DesktopShell { DataContext = vm };
        var window = new Window { Width = 1500, Height = 900, Content = shell };
        window.Show();
        try
        {
            Dispatch();
            var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();
            var project = sidebar.GetVisualDescendants().OfType<SidebarProjectNode>()
                .First(n => n.IsEffectivelyVisible && n.DataContext is SidebarProjectViewModel { Tile.Name: "api-server" });
            var header = project.HeaderRow;
            var status = header.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Status");
            var actions = header.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "HoverActions");
            Assert.False(actions.IsVisible);
            var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;

            window.MouseMove(at);
            Dispatch();

            Assert.True(actions.IsVisible);
            Assert.True(status.IsVisible);
            var statusRight = status.TranslatePoint(new Point(status.Bounds.Width, 0), header)!.Value.X;
            var actionsLeft = actions.TranslatePoint(default, header)!.Value.X;
            Assert.True(statusRight <= actionsLeft);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Projects_use_the_remaining_sidebar_height_and_the_section_can_collapse()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now));
        var shell = new DesktopShell { DataContext = vm };
        var window = new Window { Width = 1500, Height = 820, Content = shell };
        window.Show();
        try
        {
            Dispatch();
            var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();
            var pane = sidebar.FindControl<DockPanel>("ProjectsPane")!;
            var scroll = sidebar.FindControl<ScrollViewer>("TreeScroll")!;
            Assert.Empty(sidebar.GetVisualDescendants().OfType<FilesPane>());
            Assert.Empty(sidebar.GetVisualDescendants().OfType<GridSplitter>());
            Assert.True(scroll.IsEffectivelyVisible);
            Assert.True(pane.Bounds.Height > sidebar.Bounds.Height / 2);
            Assert.Equal(pane.Bounds.Height - 28, scroll.Bounds.Height, precision: 1);
            var openHeight = scroll.Bounds.Height;

            vm.Sidebar.ToggleProjectsSectionCommand.Execute(null);
            Dispatch();
            Assert.False(scroll.IsEffectivelyVisible);
            Assert.True(sidebar.FindControl<Control>("ProjectsSectionButton")!.IsEffectivelyVisible);

            vm.Sidebar.ToggleProjectsSectionCommand.Execute(null);
            Dispatch();
            Assert.True(scroll.IsEffectivelyVisible);
            Assert.Equal(openHeight, scroll.Bounds.Height, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }
}
