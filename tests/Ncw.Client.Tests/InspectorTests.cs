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
        Assert.DoesNotContain(InspectorTabKind.Files, InspectorTabs.Addable);
        Assert.Contains(InspectorTabKind.Review, InspectorTabs.Addable);
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
    public void Defaults_to_files_and_shares_selected_project_tree_with_sidebar()
    {
        var (_, shell) = Create();
        var inspector = shell.Inspector;

        Assert.True(inspector.IsFiles);
        Assert.Same(inspector.Files, shell.Sidebar.Files);
        Assert.Equal("NextCoWork", inspector.Files.ProjectName);
        var src = Assert.Single(inspector.Files.Tree.Roots, n => n.Name == "src");
        Assert.True(src.IsExpanded);
        Assert.True(src.ChildrenLoaded);
        Assert.True(src.HasChangedDescendant);
        // 目录在前
        Assert.True(inspector.Files.Tree.Roots.TakeWhile(n => n.IsDirectory).Count() >= 3);
    }

    [Fact]
    public void Files_base_cannot_be_closed_or_duplicated()
    {
        var (_, shell) = Create();
        shell.Inspector.AddCommand.Execute(InspectorTabKind.Files);
        shell.Inspector.CloseCommand.Execute(InspectorTabKind.Files);

        Assert.True(shell.Inspector.IsFiles);
        Assert.False(Assert.Single(shell.Inspector.Tabs, t => t.Kind == InspectorTabKind.Files).IsClosable);
        Assert.DoesNotContain(InspectorTabKind.Files, shell.Stores.Prefs.State.InspectorTabs);
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

    [Fact]
    public void Context_tab_reflects_live_session_usage_segments_and_todos()
    {
        var (stores, shell) = Create();
        var inspector = shell.Inspector;
        var run = new RunRef("local", "run-live");
        var meta = new Ncw.Protocol.Methods.SessionDto("s-ncw-1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        var todoCall = new Ncw.Protocol.Methods.MessageDto("m1", "assistant", 1, false,
            "[{\"type\":\"tool_call\",\"callId\":\"tc1\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[{\"content\":\"Write tests\",\"status\":\"in_progress\",\"activeForm\":\"Writing tests\"}]}}]");
        var todoDone = new Ncw.Protocol.Methods.MessageDto("m2", "user", 2, true,
            "[{\"type\":\"tool_result\",\"callId\":\"tc1\",\"output\":{\"content\":\"ok\"},\"isError\":false}]");
        var sessionState = new Ncw.Protocol.Methods.SessionTopicState(
            new Ncw.Protocol.Methods.SessionPageDto(meta, [todoCall, todoDone], false, [], [], []),
            new Ncw.Protocol.Methods.QueueDto("s-ncw-1", 1, []),
            "run-live");
        stores.Conversations.Update(c =>
        {
            var next = ConversationReducer.ApplySession(c, "local", sessionState);
            return next with
            {
                Runs = next.Runs.SetItem(run, RunView.New(run, "s-ncw-1", 1) with
                {
                    Context = new Ncw.Protocol.Methods.ContextUsageEventDto(102_000, 200_000, false,
                    [
                        new Ncw.Protocol.Methods.ContextSegmentDto("system", 20_000),
                        new Ncw.Protocol.Methods.ContextSegmentDto("messages", 82_000),
                    ]),
                }),
            };
        });

        Assert.True(inspector.HasContext);
        Assert.Equal(51, inspector.ContextPercent);
        Assert.Equal("102K", inspector.ContextUsedText);
        Assert.Equal("200K", inspector.ContextWindowText);
        Assert.Equal(2, inspector.ContextSegments.Count);
        Assert.True(inspector.HasTodo);
        Assert.True(inspector.Todo!.Items.Count > 0);
    }

    [Fact]
    public async Task History_tab_loads_review_runs_and_opening_a_run_switches_to_review()
    {
        var (stores, shell) = Create();
        stores.Review.Update(m => ReviewReducer.Apply(m, "local", "run-1",
            new Ncw.Protocol.Methods.ReviewChangeSetDto("run-1", "s-ncw-1", "nextcowork", Ncw.Protocol.Methods.ReviewStates.Applied, 1, 5, 1,
                [new Ncw.Protocol.Methods.ReviewFileEntryDto("src/a.cs", Ncw.Protocol.Methods.ReviewChangeKinds.Modified, 5, 1, false, true)], 100)));
        var inspector = shell.Inspector;
        inspector.AddCommand.Execute(InspectorTabKind.History);
        await Task.Yield();

        Assert.True(inspector.IsHistory);
        Assert.True(inspector.Review.HasRuns);
        var run = inspector.Review.Runs[0];
        inspector.OpenReviewRunCommand.Execute(run);

        Assert.True(inspector.IsReview);
        Assert.Equal(run.RunId, inspector.Review.Selected?.RunId);
    }
}
