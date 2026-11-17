using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests;

/// <summary>侧边栏项目树里会话的排序与截断(docs/design/03 §1「左侧边栏」)。</summary>
public class SidebarSessionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef P = new("d", "w");

    private static SessionSummary S(string id, int minutesAgo, bool favorite = false, bool archived = false) =>
        new(id, P, id, Now.AddMinutes(-minutesAgo), archived, favorite);

    [Fact]
    public void Pending_then_running_then_favorites_then_recent()
    {
        var sessions = new[] { S("old", 90), S("fav", 80, favorite: true), S("run", 70), S("wait", 60), S("new", 1) };
        var list = SidebarSessions.Arrange(sessions, s => s.Id == "wait" ? 2 : 0, s => s.Id == "run", null, showAll: true, showArchived: false);

        Assert.Equal(["wait", "run", "fav", "new", "old"], list.Visible.Select(s => s.Id));
        Assert.Equal(0, list.Hidden);
    }

    [Fact]
    public void Shows_four_and_counts_the_rest_but_always_keeps_the_open_session()
    {
        var sessions = Enumerable.Range(1, 7).Select(i => S($"s{i}", i)).ToList();

        var list = SidebarSessions.Arrange(sessions, _ => 0, _ => false, null, showAll: false, showArchived: false);
        Assert.Equal(["s1", "s2", "s3", "s4"], list.Visible.Select(s => s.Id));
        Assert.Equal(3, list.Hidden);

        // 打开的是第 6 条:顶替最后一条,列表不变长
        var withOpen = SidebarSessions.Arrange(sessions, _ => 0, _ => false, "s6", showAll: false, showArchived: false);
        Assert.Equal(["s1", "s2", "s3", "s6"], withOpen.Visible.Select(s => s.Id));
        Assert.Equal(3, withOpen.Hidden);

        Assert.Equal(7, SidebarSessions.Arrange(sessions, _ => 0, _ => false, null, showAll: true, showArchived: false).Visible.Count);
    }

    [Fact]
    public void Archived_are_hidden_unless_asked_and_then_go_last()
    {
        var sessions = new[] { S("a", 1, archived: true), S("b", 50) };
        Assert.Equal(["b"], SidebarSessions.Arrange(sessions, _ => 0, _ => false, null, true, showArchived: false).Visible.Select(s => s.Id));
        Assert.Equal(["b", "a"], SidebarSessions.Arrange(sessions, _ => 0, _ => false, null, true, showArchived: true).Visible.Select(s => s.Id));
    }
}

/// <summary>侧边栏的 ViewModel:项目树跟着布局走、展开规则、状态来自 Core 的派生函数、意图转给已有入口。</summary>
public class SidebarViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private sealed class Host : ISidebarHost
    {
        public List<string> Calls { get; } = [];

        public void OpenPalette() => Calls.Add("palette");

        public void OpenScheduledTasks() => Calls.Add("tasks");

        public void OpenAttention(InboxEntry entry) => Calls.Add("attention:" + entry.Key);

        public void NewTerminal(ProjectRef project) => Calls.Add("terminal:" + project.WorkspaceId);

        public void OpenSettings() => Calls.Add("settings");

        public ILocalFiles LocalFiles => NoLocalFiles.Instance;
    }

    private static (SidebarViewModel Vm, ClientStores Stores, Host Host) Create()
    {
        var stores = MockWorkspace.Create(Now);
        var host = new Host();
        return (new SidebarViewModel(stores, new LayoutActions(stores), host), stores, host);
    }

    private static IEnumerable<SidebarProjectViewModel> Projects(SidebarViewModel vm) =>
        vm.Items.SelectMany(i => i switch
        {
            SidebarProjectViewModel p => [p],
            SidebarGroupViewModel g => g.Members,
            _ => Enumerable.Empty<SidebarProjectViewModel>(),
        });

    private static SidebarProjectViewModel Project(SidebarViewModel vm, string workspaceId) => Projects(vm).Single(p => p.Project.WorkspaceId == workspaceId);

    [Fact]
    public void The_tree_follows_the_rail_layout_and_only_the_current_project_starts_expanded()
    {
        var (vm, stores, _) = Create();

        Assert.Equal(stores.Layout.State.Rail.Projects().ToList(), Projects(vm).Select(p => p.Project).ToList());
        Assert.Equal(2, vm.Items.OfType<SidebarGroupViewModel>().Count());
        var personal = vm.Items.OfType<SidebarGroupViewModel>().Single(g => g.Group.Name == "个人");
        Assert.False(personal.IsOpen); // 折叠状态就是布局里的 Collapsed

        var ncw = Project(vm, "nextcowork");
        Assert.True(ncw.IsExpanded);
        Assert.True(ncw.Tile.IsSelected);
        Assert.All(Projects(vm).Where(p => p != ncw), p => Assert.False(p.IsExpanded));
        Assert.Empty(Project(vm, "octopus").Sessions); // 收起的项目不建会话行
    }

    [Fact]
    public void Session_rows_carry_pending_and_running_from_the_active_runs_broadcast()
    {
        var (vm, _, _) = Create();
        var ncw = Project(vm, "nextcowork");

        var first = ncw.Sessions[0];
        Assert.Equal("s-ncw-1", first.Id); // 有待处理的排第一
        Assert.Equal(2, first.PendingCount);
        Assert.True(first.IsRunning);
        Assert.True(first.IsSelected);
        Assert.All(ncw.Sessions.Skip(1), s => Assert.False(s.HasPending));

        // 收起时项目行尾巴上是待处理数;展开时交给会话行
        Assert.False(ncw.ShowsPending);
        ncw.ToggleExpandedCommand.Execute(null);
        Assert.True(ncw.ShowsPending);
        Assert.Empty(ncw.Sessions);

        // 只在跑、没有待处理的项目:收起时是运行点
        var api = Project(vm, "api-server");
        Assert.True(api.ShowsRunning);
        Assert.False(api.ShowsPending);
    }

    [Fact]
    public void Clicking_another_project_selects_and_expands_it_clicking_the_current_one_toggles()
    {
        var (vm, stores, _) = Create();
        var octopus = Project(vm, "octopus");

        octopus.ActivateCommand.Execute(null);
        Assert.Equal(octopus.Project, stores.Selection.State.Project);
        octopus = Project(vm, "octopus");
        Assert.True(octopus.IsExpanded);
        Assert.Single(octopus.Sessions);
        Assert.True(Project(vm, "nextcowork").IsExpanded); // 别的项目不会被收起

        octopus.ActivateCommand.Execute(null); // 已经是当前项目:收起
        Assert.False(Project(vm, "octopus").IsExpanded);
        Assert.Equal(octopus.Project, stores.Selection.State.Project);

        // 用别的方式(⌘1…⌘9)选中一个收起的项目:自动展开
        new LayoutActions(stores).Select(new ProjectRef("local", "nextcowork"));
        Project(vm, "nextcowork").ToggleExpandedCommand.Execute(null);
        Assert.False(Project(vm, "nextcowork").IsExpanded);
        new LayoutActions(stores).Select(new ProjectRef("local", "arc"));
        new LayoutActions(stores).Select(new ProjectRef("local", "nextcowork"));
        Assert.True(Project(vm, "nextcowork").IsExpanded);
    }

    [Fact]
    public void Show_more_and_archived_are_per_project_ui_state()
    {
        var stores = MockWorkspace.Create(Now);
        var ncw = new ProjectRef("local", "nextcowork");
        stores.Workspace.Update(w => w with
        {
            Sessions = w.Sessions.AddRange(Enumerable.Range(1, 4).Select(i => new SessionSummary($"extra-{i}", ncw, $"旧会话 {i}", Now.AddDays(-10 - i))))
                .Add(new SessionSummary("archived", ncw, "已归档的", Now, Archived: true)),
        });
        var vm = new SidebarViewModel(stores, new LayoutActions(stores), new Host());
        var node = Project(vm, "nextcowork");

        Assert.Equal(SidebarSessions.Limit, node.Sessions.Count);
        Assert.Equal(3, node.HiddenCount); // 3 个 mock + 4 个旧的 = 7,显示 4
        node.ToggleShowAllCommand.Execute(null);
        Assert.Equal(7, node.Sessions.Count);
        Assert.True(node.CanShowLess);
        Assert.DoesNotContain(node.Sessions, s => s.IsArchived);

        node.ToggleArchivedCommand.Execute(null);
        Assert.True(node.ShowsArchived);
        Assert.True(node.Sessions[^1].IsArchived);
    }

    [Fact]
    public void Global_entries_and_project_menu_go_to_the_existing_entry_points()
    {
        var (vm, _, host) = Create();

        vm.SearchCommand.Execute(null);
        vm.OpenScheduledTasksCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
        Project(vm, "octopus").NewTerminalCommand.Execute(null);
        Assert.Equal(["palette", "tasks", "settings", "terminal:octopus"], host.Calls);

        Assert.True(vm.HasProjects);
        Assert.True(vm.HasCurrentProject);
        Assert.False(vm.IsInboxOpen);
        vm.ToggleInboxCommand.Execute(null);
        Assert.True(vm.IsInboxOpen);
    }

    [Fact]
    public void Device_label_is_only_written_when_the_device_dimension_shows()
    {
        var (vm, _, _) = Create();

        // Mock 里有云端和另一台电脑:本机项目不写设备,别的设备上的项目写设备名
        Assert.Null(Project(vm, "nextcowork").DeviceLabel);
        Assert.Equal("工作室 Mac mini", Project(vm, "infra").DeviceLabel);
        Assert.True(Project(vm, "nextcowork").IsLocal);
        Assert.False(Project(vm, "infra").IsLocal);
    }

    [Fact]
    public void Removing_a_project_from_the_layout_removes_its_row()
    {
        var (vm, stores, _) = Create();
        var octopus = Project(vm, "octopus");

        octopus.Tile.RemoveFromRailCommand.Execute(null);
        Assert.DoesNotContain(Projects(vm), p => p.Project == octopus.Project);
        Assert.DoesNotContain(octopus.Project, stores.Layout.State.Rail.Projects());
    }
}
