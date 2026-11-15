using System.Text.Json.Nodes;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 草稿与排队输入的落盘。移植自 2.x <c>shared/__tests__/queued-input.test.ts</c> 的「isValidSessionInput」与「序列化往返」两组
/// (队列的 pickNextBatch / mergeBatch 等纯规则属于 Kernel 的 <c>RunInputQueues</c>,不在存储层)。
/// </summary>
public sealed class SessionInputStoreTests : SessionTestBase
{
    private static readonly long Now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private sealed class FixedTime(long ms) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    private SessionInputStore At_(long ms) => new(Db, new FixedTime(ms));

    private static QueuedInput Queued(string id, string text, string status = "pending", long? promotedAt = null) => new()
    {
        Id = id,
        Text = text,
        Status = status,
        EnqueuedAt = 1,
        PromotedAt = promotedAt,
        Attachments = [new QueuedAttachment { Kind = "image", Name = "n", Url = "ncw://attachments/sessions/S1/p.png" }],
        Options = new JsonObject { ["workspaceId"] = "w1", ["depth"] = 0, ["mode"] = "code", ["model"] = "claude", ["skillIds"] = new JsonArray() },
    };

    [Fact]
    public void A_full_draft_and_queue_round_trip_every_field()
    {
        var store = At_(Now);
        store.Write("s1", new SessionInputState { Draft = "半句话", Queued = [Queued("a", "一"), Queued("b", "二", "promoted", 5)] });

        Restart();
        var read = At_(Now + 1000).Read("s1")!;

        Assert.Equal("半句话", read.Draft);
        Assert.Equal(Now, read.SavedAt);
        Assert.Equal(["a", "b"], read.Queued!.Select(q => q.Id));
        var promoted = read.Queued![1];
        Assert.Equal(("promoted", 5L), (promoted.Status, promoted.PromotedAt));
        Assert.Equal("ncw://attachments/sessions/S1/p.png", promoted.Attachments![0].Url);
        Assert.Equal("claude", promoted.Options!["model"]!.GetValue<string>());
        Assert.Equal("二", promoted.Text);
    }

    [Fact]
    public void Only_live_entries_are_persisted_terminal_ones_leave_the_array()
    {
        At_(Now).Write("s1", new SessionInputState { Queued = [Queued("a", "一"), Queued("done", "消费过", "consumed"), Queued("gone", "丢弃", "dropped")] });

        Assert.Equal(["a"], At_(Now).Read("s1")!.Queued!.Select(q => q.Id));
    }

    [Fact]
    public void Unknown_fields_survive_a_read_write_cycle()
    {
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.s1', $j)", ("$j", $$$"""{"v":1,"draft":"d","queued":[],"savedAt":{{{Now}}},"future":{"a":[1,2]}}"""));
        var store = At_(Now);

        store.Write("s1", store.Read("s1")!);

        Assert.Contains("\"future\":{\"a\":[1,2]}", Text("SELECT json FROM kv WHERE key = 'session.input.s1'"));
    }

    private void Put(string json) => Exec("INSERT OR REPLACE INTO kv (key, json) VALUES ('session.input.s1', $j)", ("$j", json));

    [Fact]
    public void A_wrong_version_is_discarded_whole_not_repaired()
    {
        Put($$"""{"v":2,"draft":"d","queued":[],"savedAt":{{Now}}}""");

        Assert.Null(At_(Now).Read("s1"));
    }

    [Fact]
    public void An_archive_older_than_the_ttl_is_discarded()
    {
        Put($$"""{"v":1,"draft":"d","queued":[],"savedAt":{{Now - (long)SessionInputState.Ttl.TotalMilliseconds - 1}}}""");
        Assert.Null(At_(Now).Read("s1"));

        Put($$"""{"v":1,"draft":"d","queued":[],"savedAt":{{Now - (long)SessionInputState.Ttl.TotalMilliseconds}}}""");
        Assert.NotNull(At_(Now).Read("s1"));
    }

    [Fact]
    public void A_terminal_entry_in_the_queue_makes_the_whole_archive_invalid()
    {
        // 终态本就不该被落盘
        Put($$"""{"v":1,"draft":"d","queued":[{"id":"z","text":"僵尸","attachments":[],"status":"consumed","options":{},"enqueuedAt":1}],"savedAt":{{Now}}}""");

        Assert.Null(At_(Now).Read("s1"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"{}\"")]
    [InlineData("not json")]
    [InlineData("""{"v":1,"draft":"d"}""")]
    [InlineData("""{"v":1,"draft":"d","queued":[{"id":"a"}],"savedAt":1}""")]
    public void Non_object_or_incomplete_archives_do_not_crash_and_are_simply_absent(string json)
    {
        Put(json);

        Assert.Null(At_(Now).Read("s1"));
    }

    [Fact]
    public void Absent_row_reads_as_null_and_remove_reports_whether_anything_was_there()
    {
        Assert.Null(Inputs.Read("s1"));
        Assert.False(Inputs.Remove("s1"));
        Inputs.Write("s1", new SessionInputState());
        Assert.True(Inputs.Remove("s1"));
        Assert.Null(Inputs.Read("s1"));
    }

    [Fact]
    public void A_draft_may_exist_before_its_session_row_and_follows_the_session_into_deletion()
    {
        Inputs.Write("not-created-yet", new SessionInputState { Draft = "先写草稿" });
        Assert.Equal("先写草稿", Inputs.Read("not-created-yet")!.Draft);

        NewSession("s1");
        Inputs.Write("s1", new SessionInputState { Draft = "x" });
        Repo.Delete("s1");

        Assert.Equal(0, Count("SELECT count(*) FROM kv WHERE key = 'session.input.s1'"));
    }

    [Fact]
    public void Another_account_scope_cannot_read_write_or_remove_the_input_of_a_session_it_cannot_see()
    {
        Workspace("ws-local");
        NewSession("local-1", "ws-local");
        Inputs.Write("local-1", new SessionInputState { Draft = "本地的草稿" });
        SetScope("acct-a");

        Assert.Null(Inputs.Read("local-1"));
        Assert.Throws<StorageException>(() => Inputs.Write("local-1", new SessionInputState { Draft = "x" }));
        Assert.False(Inputs.Remove("local-1"));

        SetScope("local");
        Assert.Equal("本地的草稿", Inputs.Read("local-1")!.Draft);
    }
}
