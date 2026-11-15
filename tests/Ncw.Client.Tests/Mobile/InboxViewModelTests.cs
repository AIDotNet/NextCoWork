using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Mobile;

/// <summary>
/// 活动收件箱:从 Store 镜像(interactions / activeRuns / 连接状态 / 已结束的 Run)聚合(Core 的 ActivityInboxView),按设备分组、排序;
/// 先到先得的「已在另一台设备上处理」、设备断开是「未知」(规则 7)、点一条进会话并定位到卡片。
/// </summary>
public class InboxViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Ncw = new("local", "nextcowork");

    private static long Ms(int secondsAgo) => Now.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds();

    private static ToolPermissionInteractionDto Tool(string device, string id, string run, int secondsAgo, bool dangerous = false, string input = "{\"command\":\"ls\"}") => new()
    {
        Id = id, RunId = run, CallId = "c" + id, ToolName = "Bash", InternalId = "bash", InputJson = input,
        Env = new EnvRefDto(device, "local", "local", "local"), AllowedScopes = ["session"], Risk = dangerous ? "dangerous" : "unknown",
        CreatedAt = Ms(secondsAgo), Version = 5,
    };

    private static void Interactions(ClientStores stores, string device, long rev, params InteractionDto[] pending) =>
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, device, new InteractionsDto(rev, pending)));

    /// <summary>Mock 的三台设备(本机、云端、工作室)都连着:用例自己决定谁断开。</summary>
    private static ClientStores ConnectedMock()
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Connection = DeviceConnection.Connected })
                .SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Connected }),
        });
        return stores;
    }

    private static (ClientStores Stores, InboxViewModel Inbox, MobileNavigationViewModel Nav, InteractionLedger Ledger, ManualClock Clock) Make()
    {
        var stores = ConnectedMock();
        var clock = new ManualClock(Now);
        var ledger = new InteractionLedger();
        var nav = new MobileNavigationViewModel();
        return (stores, new InboxViewModel(stores, nav, ledger, clock), nav, ledger, clock);
    }

    [Fact]
    public void Nothing_pending_and_every_device_connected_is_an_empty_inbox_with_no_badge()
    {
        var (_, inbox, _, _, _) = Make();

        Assert.True(inbox.IsEmpty);
        Assert.Equal(0, inbox.BadgeCount);
        Assert.False(inbox.HasUnknown);
    }

    [Fact]
    public void Pending_interactions_are_grouped_by_device_and_resolved_to_project_and_session()
    {
        var (stores, inbox, _, _, _) = Make();
        Interactions(stores, "local", 1, Tool("local", "i1", "run-1", 30));
        Interactions(stores, "studio", 1, Tool("studio", "i2", "run-x", 10));

        Assert.Equal(["local", "studio"], inbox.Groups.Select(g => g.DeviceId));
        var local = inbox.Groups[0].Entries.Single();
        Assert.Equal(Ncw, local.Project);
        Assert.Equal("NextCoWork", local.ProjectName);
        Assert.Equal("设计 Agent Runtime 并实现骨架", local.SessionTitle);
        Assert.Equal("ls", local.Target);
        Assert.Equal("本机 · local", local.EnvLabel); // 审批必须带执行环境标识(设备 · 环境)
        Assert.Equal(2, inbox.BadgeCount);
    }

    [Fact]
    public void Entries_are_ordered_dangerous_first_then_oldest_first_and_notifications_come_last()
    {
        var (stores, inbox, _, _, _) = Make();
        Interactions(stores, "local", 1,
            Tool("local", "new", "run-1", 10),
            Tool("local", "old", "run-1", 300),
            Tool("local", "danger", "run-1", 5, dangerous: true),
            new PlanApprovalInteractionDto { Id = "plan", RunId = "run-1", CreatedAt = Ms(400), Version = 1, PlanId = "p", Path = "p.md", Plan = "# 计划\n\n第一步" });
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(new RunRef("local", "run-done"), RunView.New(new RunRef("local", "run-done"), "s-ncw-2", 1) with { Phase = RunPhase.Done, Settled = true }) });

        Assert.Equal(["i:danger", "i:old", "i:new", "i:plan", "r:run-done"], inbox.Groups.Single().Entries.Select(e => e.Key));
        Assert.Equal("第一步", inbox.Groups.Single().Entries.Single(e => e.Key == "i:plan").Detail);
        Assert.Equal(4, inbox.BadgeCount); // 通知不算待办
    }

    [Fact]
    public void A_disconnected_device_keeps_its_last_known_items_as_unknown_not_handled_and_they_stay_out_of_the_badge()
    {
        var (stores, inbox, _, _, _) = Make();
        Interactions(stores, "studio", 1, Tool("studio", "s1", "run-s", 60));
        Interactions(stores, "local", 1, Tool("local", "l1", "run-1", 60));
        Assert.Equal(2, inbox.BadgeCount);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Offline }) });

        var studio = inbox.Groups.Single(g => g.DeviceId == "studio");
        Assert.True(studio.IsUnknown);
        var entry = Assert.Single(studio.Entries);
        Assert.True(entry.IsUnknown);
        Assert.False(entry.IsHandledElsewhere); // 断线不是「已处理」(规则 7)
        Assert.Equal(1, inbox.BadgeCount);
        Assert.True(inbox.HasUnknown);

        // 重新连上:恢复成可处理,角标回来
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Connected }) });
        Assert.Equal(2, inbox.BadgeCount);
        Assert.False(inbox.Groups.Single(g => g.DeviceId == "studio").IsUnknown);
    }

    [Fact]
    public void A_pending_item_that_vanishes_without_a_local_answer_becomes_handled_on_another_device_until_dismissed()
    {
        var (stores, inbox, _, _, _) = Make();
        Interactions(stores, "local", 1, Tool("local", "i1", "run-1", 30), Tool("local", "i2", "run-1", 20));

        Interactions(stores, "local", 2, Tool("local", "i2", "run-1", 20)); // 另一台设备批准了 i1

        var tomb = inbox.Groups.Single().Entries.Single(e => e.IsHandledElsewhere);
        Assert.Equal("i:i1", tomb.Key);
        Assert.Equal(1, inbox.BadgeCount);
        Assert.True(tomb.CanDismiss);

        inbox.DismissCommand.Execute(tomb);
        Assert.DoesNotContain(inbox.Groups.Single().Entries, e => e.IsHandledElsewhere);
    }

    [Fact]
    public void An_item_this_controller_answered_itself_just_disappears_with_no_handled_elsewhere_notice()
    {
        var (stores, inbox, _, ledger, _) = Make();
        Interactions(stores, "local", 1, Tool("local", "i1", "run-1", 30));

        ledger.MarkAnswered("i1"); // ConversationActions.RespondAsync 的 Answered 结局记在这里
        Interactions(stores, "local", 2);

        Assert.True(inbox.IsEmpty);
    }

    [Fact]
    public void A_disappearance_while_the_device_is_not_connected_never_becomes_a_handled_notice()
    {
        var (stores, inbox, _, _, _) = Make();
        Interactions(stores, "studio", 1, Tool("studio", "s1", "run-s", 30));
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Offline }) });

        Interactions(stores, "studio", 2); // 假设镜像被整体替换了(断线期间不该发生,但判断不能错)

        Assert.DoesNotContain(inbox.Groups.SelectMany(g => g.Entries), e => e.IsHandledElsewhere);
    }

    [Fact]
    public void Opening_a_handled_elsewhere_notice_does_not_navigate_anywhere()
    {
        var (stores, inbox, nav, _, _) = Make();
        Interactions(stores, "local", 1, Tool("local", "i1", "run-1", 30));
        var row = inbox.Groups.Single().Entries.Single();
        Interactions(stores, "local", 2); // 另一台设备处理了它:行变成墓碑

        inbox.OpenCommand.Execute(row);

        Assert.Equal(MobileTab.Inbox, nav.SelectedTab);
        Assert.Empty(nav.CurrentStack);
    }

    [Fact]
    public void Opening_an_item_goes_to_its_session_with_the_card_as_the_focus_target()
    {
        var stores = ConnectedMock();
        var conversation = new ConversationViewModel(stores, new NoCommands());
        var nav = new MobileNavigationViewModel(conversation: conversation);
        var inbox = new InboxViewModel(stores, nav, new InteractionLedger(), new ManualClock(Now));
        Interactions(stores, "local", 1, Tool("local", "i1", "run-1", 30));

        inbox.OpenCommand.Execute(inbox.Groups.Single().Entries.Single());

        Assert.Equal(MobileTab.Sessions, nav.SelectedTab);
        Assert.Equal([new ProjectRoute(Ncw), new SessionRoute(Ncw, "s-ncw-1")], nav.StackOf(MobileTab.Sessions));
        Assert.False(nav.IsTabBarVisible);
    }

    [Fact]
    public void A_notification_is_read_by_dismissing_it_or_by_opening_its_session_and_dismiss_all_clears_them()
    {
        var (stores, inbox, nav, _, _) = Make();
        void Finish(string run, string session, RunPhase phase) =>
            stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(new RunRef("local", run), RunView.New(new RunRef("local", run), session, 1) with { Phase = phase, Settled = true }) });
        Finish("r1", "s-ncw-2", RunPhase.Done);
        Finish("r2", "s-oct-1", RunPhase.Error);
        Finish("r3", "s-api-1", RunPhase.Aborted); // 被用户中止的不通知
        var entries = inbox.Groups.Single().Entries;
        Assert.Equal(["r:r2", "r:r1"], entries.Select(e => e.Key)); // 失败的在前

        inbox.DismissCommand.Execute(entries.Single(e => e.Key == "r:r1"));
        Assert.Equal(["r:r2"], inbox.Groups.Single().Entries.Select(e => e.Key));

        // 打开那个会话 = 看过了
        stores.Selection.Update(_ => new SelectionState(new ProjectRef("local", "octopus"), "s-oct-1"));
        Assert.True(inbox.IsEmpty);
        Assert.Equal(MobileTab.Inbox, nav.SelectedTab);

        Finish("r4", "s-ncw-3", RunPhase.Done);
        inbox.DismissAllCommand.Execute(null);
        Assert.True(inbox.IsEmpty);
    }

    [Fact]
    public void Device_level_items_without_a_session_go_to_the_devices_tab()
    {
        var stores = ConnectedMock();
        var nav = new MobileNavigationViewModel();
        var inbox = new InboxViewModel(stores, nav, clock: new ManualClock(Now));
        Interactions(stores, "local", 1, new SshHostKeyInteractionDto
        {
            Id = "k1", RunId = "env:dev-server:connect", CreatedAt = Ms(5), Version = 1,
            Env = new EnvRefDto("local", "dev-server", "dev-server", "ssh"), HostName = "dev.example", Algorithm = "ssh-ed25519", Fingerprint = "abc",
        });

        var entry = inbox.Groups.Single().Entries.Single();
        Assert.Equal(InboxKind.DeviceApproval, entry.Kind);
        Assert.Null(entry.Entry.SessionId);
        Assert.Equal("本机 · dev-server", entry.EnvLabel);

        inbox.OpenCommand.Execute(entry);
        Assert.Equal(MobileTab.Devices, nav.SelectedTab);
    }

    private sealed class NoCommands : IConversationCommands
    {
        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));
        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) => Task.FromResult(new InteractionResult(InteractionOutcome.Answered));
        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }
}
