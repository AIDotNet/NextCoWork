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
        Assert.Equal([InspectorTabKind.Context, InspectorTabKind.History], InspectorTabs.Remaining([InspectorTabKind.Git]));
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
        return (stores, new ShellViewModel(stores, new FixedClock(Now)));
    }

    [Fact]
    public void Defaults_to_file_manager_with_tree_of_selected_project()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        Assert.True(inspector.IsFiles);
        Assert.Equal("NextCoWork", inspector.Files.ProjectName);
        var src = Assert.Single(inspector.Files.Roots, n => n.Name == "src");
        Assert.True(src.IsExpanded);
        Assert.True(src.HasChangedDescendant);
        // 目录在前
        Assert.True(inspector.Files.Roots.TakeWhile(n => n.IsDirectory).Count() >= 3);
    }

    [Fact]
    public void Session_with_subagents_shows_subagent_tab_using_the_shared_derivation()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        var tab = Assert.Single(inspector.Tabs, t => t.Kind == InspectorTabKind.Subagents);
        Assert.Equal(1, tab.Count); // 只数还在运行的
        Assert.Equal(2, inspector.Subagents.Count);
        Assert.Equal("运行中", inspector.Subagents[0].StatusText);
        Assert.Equal("Grep · 14 次工具调用 · 2:00", inspector.Subagents[0].Detail);
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
        Assert.Contains(inspector.GitChanges, c => c is { Letter: "M", Name: "InspectorPanel.axaml" });
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
        Assert.Equal(2, shell.Inspector.GitChanges.Count);
        Assert.DoesNotContain(shell.Inspector.Tabs, t => t.Kind == InspectorTabKind.Subagents);
    }
}
