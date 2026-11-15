using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests;

public class InspectorTabsTests
{
    [Fact]
    public void Files_is_always_first_and_only_one()
    {
        Assert.Equal([InspectorTabKind.Files], InspectorTabs.Compose([], hasSubagents: false));
        Assert.Equal([InspectorTabKind.Files], InspectorTabs.Compose([InspectorTabKind.Files], hasSubagents: false));
    }

    [Fact]
    public void Added_tabs_keep_their_order_and_subagents_come_last_only_when_present()
    {
        var tabs = InspectorTabs.Compose([InspectorTabKind.History, InspectorTabKind.Git, InspectorTabKind.Git], hasSubagents: true);

        Assert.Equal([InspectorTabKind.Files, InspectorTabKind.History, InspectorTabKind.Git, InspectorTabKind.Subagents], tabs);
    }

    [Fact]
    public void Subagents_cannot_be_added_by_hand()
    {
        Assert.DoesNotContain(InspectorTabKind.Subagents, InspectorTabs.Addable);
        Assert.DoesNotContain(InspectorTabKind.Subagents, InspectorTabs.Compose([InspectorTabKind.Subagents], hasSubagents: false));
    }

    [Fact]
    public void Remaining_excludes_added()
    {
        Assert.Equal([InspectorTabKind.Review, InspectorTabKind.Terminal, InspectorTabKind.Context, InspectorTabKind.History], InspectorTabs.Remaining([InspectorTabKind.Git]));
    }
}

public class InspectorViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (ClientStores Stores, ShellViewModel Shell) Create()
    {
        var stores = MockWorkspace.Create(Now);
        return (stores, new ShellViewModel(stores, new FixedClock(Now), inspector: MockInspectorServices.Create(stores)));
    }

    [Fact]
    public void Defaults_to_file_manager_with_tree_of_selected_project()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        Assert.True(inspector.IsFiles);
        Assert.Equal("NextCoWork", inspector.Files.ProjectName);
        var src = Assert.Single(inspector.Files.Tree.Roots, n => n.Name == "src");
        Assert.True(src.IsExpanded);
        Assert.True(src.ChildrenLoaded);
        Assert.True(src.HasChangedDescendant);
        // 目录在前
        Assert.True(inspector.Files.Tree.Roots.TakeWhile(n => n.IsDirectory).Count() >= 3);
    }

    [Fact]
    public void Session_with_subagents_shows_subagent_tab_using_the_shared_derivation()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        var tab = Assert.Single(inspector.Tabs, t => t.Kind == InspectorTabKind.Subagents);
        Assert.Equal(1, tab.Count); // 只数还在运行的
        Assert.Equal(2, inspector.Subagents.Count);
        Assert.Equal(SubagentStatus.Running, inspector.Subagents[0].Status);
        Assert.Equal("Grep", inspector.Subagents[0].View.CurrentTool);
        Assert.Equal(14, inspector.Subagents[0].View.ToolCalls);
        Assert.Equal(TimeSpan.FromMinutes(2), inspector.Subagents[0].View.Elapsed);
        Assert.False(tab.IsActive); // 出现但不抢焦点
    }

    [Fact]
    public void Subagent_tab_disappears_with_the_session_and_falls_back_to_files()
    {
        var (stores, shell) = Create();
        var inspector = shell.Inspector;
        inspector.ActivateCommand.Execute(InspectorTabKind.Subagents);
        Assert.True(inspector.IsSubagents);

        shell.Actions.SelectSession("s-ncw-2");

        Assert.DoesNotContain(inspector.Tabs, t => t.Kind == InspectorTabKind.Subagents);
        Assert.True(inspector.IsFiles);
    }

    [Fact]
    public void Plus_adds_git_tab_with_changes_and_it_can_be_closed()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        inspector.AddCommand.Execute(InspectorTabKind.Git);

        Assert.True(inspector.IsGit);
        Assert.DoesNotContain(InspectorTabKind.Git, inspector.Addable);
        Assert.Contains(inspector.Git.Unstaged, c => c is { Letter: "M", Name: "InspectorPanel.axaml" });
        Assert.True(inspector.Tabs.Single(t => t.Kind == InspectorTabKind.Git).ShowsClose);

        inspector.CloseCommand.Execute(InspectorTabKind.Git);

        Assert.True(inspector.IsFiles);
        Assert.Contains(InspectorTabKind.Git, inspector.Addable);
    }

    [Fact]
    public void Switching_project_reloads_tree_and_changes()
    {
        var (_, shell) = Create();
        shell.Inspector.AddCommand.Execute(InspectorTabKind.Git);

        shell.Actions.Select(new ProjectRef("local", "api-server"));

        Assert.Equal("api-server", shell.Inspector.Files.ProjectName);
        Assert.Equal(2, shell.Inspector.Git.Unstaged.Count);
        Assert.DoesNotContain(shell.Inspector.Tabs, t => t.Kind == InspectorTabKind.Subagents);
    }

    [Fact]
    public void The_tree_and_git_overview_are_fetched_again_when_the_device_connects_after_the_first_attempt_failed()
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connecting }) });
        var files = new Inspector.FakeFiles { Offline = true }.Add("src/App.cs").Add("README.md");
        var git = new Inspector.FakeGit(stores);
        var services = new InspectorServices(files, git, new Inspector.FakeReview(), new Inspector.FakeTerminals(), null);
        var inspector = new InspectorViewModel(stores, new FixedClock(Now), services);

        Assert.Empty(inspector.Files.Tree.Roots); // 启动时设备还没连上:第一次列目录失败了
        Assert.NotNull(inspector.Files.Tree.Failure);

        files.Offline = false;
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connected }) });

        Assert.Equal(["src", "README.md"], inspector.Files.Tree.Roots.Select(n => n.Name));
        Assert.Null(inspector.Files.Tree.Failure);
        Assert.True(git.Calls.Count(c => c == "refresh") >= 2); // 连上之后概览也重新取了
    }
}
