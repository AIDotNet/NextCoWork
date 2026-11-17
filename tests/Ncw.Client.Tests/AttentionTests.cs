using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Mobile;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>桌面的提醒(docs/design/03 §11.2):规则是纯函数 <see cref="AttentionPolicy"/>,监视器只记见过的 key。</summary>
public class AttentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static InboxEntry Entry(string key, InboxKind kind, string session = "s1", bool unknown = false) =>
        new("local", key, kind, kind is InboxKind.RunCompleted or InboxKind.RunFailed ? null : key, "run-1", session, "ws", Now, false, unknown);

    [Fact]
    public void Policy_only_picks_new_wanted_and_actionable_entries()
    {
        var seen = new HashSet<string> { "i:old" };
        var entries = new[]
        {
            Entry("i:old", InboxKind.ToolApproval),
            Entry("i:new", InboxKind.ToolApproval),
            Entry("i:offline", InboxKind.Question, unknown: true),
            Entry("r:done", InboxKind.RunCompleted),
            Entry("r:fail", InboxKind.RunFailed),
        };

        var all = AttentionPolicy.Select(seen, entries, AttentionPrefs.Default, new AttentionContext(false, null));
        Assert.Equal(["i:new", "r:done", "r:fail"], all.Select(e => e.Key));

        var quiet = AttentionPolicy.Select(seen, entries, new AttentionPrefs(NotifyPending: false, NotifyCompleted: false), new AttentionContext(false, null));
        Assert.Equal(["r:fail"], quiet.Select(e => e.Key));
    }

    [Fact]
    public void Policy_skips_the_conversation_you_are_looking_at()
    {
        var entries = new[] { Entry("i:a", InboxKind.ToolApproval, "s1"), Entry("i:b", InboxKind.ToolApproval, "s2") };

        var looking = AttentionPolicy.Select(new HashSet<string>(), entries, AttentionPrefs.Default, new AttentionContext(true, ("local", "s1")));
        Assert.Equal(["i:b"], looking.Select(e => e.Key));

        // 窗口不在前台:就算打开着那个会话也要提醒
        var away = AttentionPolicy.Select(new HashSet<string>(), entries, AttentionPrefs.Default, new AttentionContext(false, ("local", "s1")));
        Assert.Equal(2, away.Length);
    }

    private sealed class RecordingSink : IAttentionSink
    {
        public List<AttentionNotice> Notices { get; } = [];

        public List<int> Badges { get; } = [];

        public AttentionSummary? Summary { get; private set; }

        public void Notify(AttentionNotice notice) => Notices.Add(notice);

        public void SetBadge(int count) => Badges.Add(count);

        public void SetSummary(AttentionSummary summary) => Summary = summary;
    }

    private static ToolPermissionInteractionDto Tool(string id, string run = "run-1") => new()
    {
        Id = id, RunId = run, CallId = "c" + id, ToolName = "Bash", InternalId = "bash", InputJson = "{\"command\":\"rm -rf build\"}",
        Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = ["session"], Risk = "unknown",
        CreatedAt = Now.ToUnixTimeMilliseconds(), Version = 1,
    };

    private static void Pending(ClientStores stores, long rev, params InteractionDto[] items) =>
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(rev, items)));

    [Fact]
    public void Monitor_baselines_after_connect_then_notifies_new_items_and_tracks_the_badge()
    {
        var stores = MockWorkspace.Create(Now);
        var clock = new ManualClock(Now);
        var shell = new ShellViewModel(stores, clock);
        var sink = new RecordingSink();
        shell.AttentionSink = sink;
        shell.IsWindowActive = () => false;

        // 刚连上:已有的待办只记基线,不弹
        Pending(stores, 1, Tool("i1"));
        Assert.Empty(sink.Notices);
        Assert.Equal(1, sink.Badges[^1]);

        clock.Advance(AttentionMonitor.SettleTime + TimeSpan.FromSeconds(1));
        Pending(stores, 2, Tool("i1"), Tool("i2"));

        var notice = Assert.Single(sink.Notices);
        Assert.Equal("i:i2", notice.Key);
        Assert.Equal(InboxKind.ToolApproval, notice.Kind);
        Assert.Equal("Bash", notice.ToolName);
        Assert.Equal(2, sink.Badges[^1]);
        Assert.Equal(2, sink.Summary!.Pending);

        // 同一条再来一遍(版本变了)不重复提醒;处理掉以后角标回落
        Pending(stores, 3, Tool("i2"));
        Assert.Single(sink.Notices);
        Assert.Equal(1, sink.Badges[^1]);

        // 关掉角标:清成 0
        stores.Prefs.Update(p => p with { Attention = p.AttentionOrDefault with { Badge = false } });
        Assert.Equal(0, sink.Badges[^1]);
    }

    [Fact]
    public void Opening_a_notification_selects_the_session_and_focuses_the_card()
    {
        var stores = MockWorkspace.Create(Now);
        var shell = new ShellViewModel(stores, new ManualClock(Now));
        // 选一个当前没选中的项目里的会话
        var session = stores.Workspace.State.Sessions.First(s => s.Project != stores.Selection.State.Project);
        var project = session.Project;

        shell.OpenAttention(project.DeviceId, project.WorkspaceId, session.Id, "i:x");

        Assert.Equal(project, stores.Selection.State.Project);
        Assert.Equal(session.Id, stores.Selection.State.SessionId);
    }
}
