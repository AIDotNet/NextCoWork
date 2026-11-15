using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 按 id 改写转录。移植自 2.x 的 <c>history-page.test.ts</c>「按 id 改写(共享规则)」一组和
/// <c>shared/__tests__/session-clone.test.ts</c> 的 <c>turnEndIndex</c> 一组(<c>history-edit.ts</c> 本身没有单测,按实现行为补)。
/// </summary>
public sealed class HistoryEditTests : SessionTestBase
{
    public HistoryEditTests()
    {
        NewSession("s", "w");
    }

    private static MessageRecord[] Turn(int n) =>
    [
        Ask($"u{n}", $"问题 {n}", n * 10),
        ToolCall($"a{n}", $"c{n}", "Read", $$"""{"path":"{{n}}.ts"}""", n * 10 + 1),
        ToolResult($"r{n}", $"c{n}", "…", n * 10 + 2),
        Say($"b{n}", $"回答 {n}", n * 10 + 3),
    ];

    private List<MessageRecord> Seed(int turns)
    {
        var history = Enumerable.Range(1, turns).SelectMany(Turn).ToList();
        Repo.ReplaceHistory("s", history);
        return history;
    }

    [Fact]
    public void Deleting_a_turn_by_id_keeps_the_history_outside_the_page()
    {
        var history = Seed(4);
        // 客户端只拿到最后一页 —— 旧做法拿它整段 replaceHistory,前三轮就没了
        Assert.Equal(["u4", "a4", "r4", "b4"], Ids(Repo.HistoryPage("s", 4).Messages));

        Repo.DeleteTurn("s", "u4");

        Assert.Equal(Ids(history.Take(12)), Ids(Repo.History("s")));
        Assert.Empty(Repo.Search("问题 4"));
        Assert.Single(Repo.Search("问题 3"));
    }

    [Fact]
    public void A_turn_runs_up_to_the_next_visible_prompt_and_a_reply_takes_its_trailing_tool_results()
    {
        var history = Turn(1).Concat(Turn(2)).ToList();

        Assert.Equal((0, 4), HistoryEdit.TurnSpan(history, "u1"));
        Assert.Equal((4, 8), HistoryEdit.TurnSpan(history, "u2"));
        Assert.Equal((1, 3), HistoryEdit.ReplySpan(history, "a1", "a1"));
        Assert.Equal((1, 4), HistoryEdit.ReplySpan(history, "a1", "b1"));
        Assert.Null(HistoryEdit.TurnSpan(history, "a1")); // 不是用户提问
        Assert.Null(HistoryEdit.ReplySpan(history, "u1", "a1")); // 起点不是助手回复
        Assert.Null(HistoryEdit.ReplySpan(history, "b1", "a1")); // 终点在起点之前
        Assert.Null(HistoryEdit.TurnSpan(history, "missing"));
    }

    [Fact]
    public void Deleting_only_a_reply_leaves_the_prompt_and_eats_the_orphaned_tool_result()
    {
        Seed(2);

        Repo.DeleteReply("s", "a1", "a1");

        Assert.Equal(["u1", "b1", "u2", "a2", "r2", "b2"], Ids(Repo.History("s")));
        Assert.Throws<StorageException>(() => Repo.DeleteReply("s", "a1", "a1")); // 已经不在了
    }

    [Fact]
    public void Editing_keeps_attachments_and_other_structured_parts_and_replaces_the_text()
    {
        const string Image = """{"type":"image","mime":"image/png","dataRef":"ncw://attachments/sessions/s/x.png"}""";
        Repo.ReplaceHistory("s",
        [
            new MessageRecord("u1", "user", Parts(TextPart("旧"), Image), 1, Internal: true),
            Say("b1", "答", 2),
        ]);

        Repo.EditMessage("s", "u1", "新", truncate: false);

        var edited = Repo.History("s")[0];
        Assert.Equal(Parts(TextPart("新"), Image), edited.PartsJson);
        Assert.Equal(1, edited.CreatedAt);
        Assert.False(edited.Internal); // 2.x 用 userMessage() 重新构造:internal 不保留
        Assert.Equal(["u1", "b1"], Ids(Repo.History("s")));
        Assert.Single(Repo.Search("新"));
        Assert.Empty(Repo.Search("旧"));
    }

    [Fact]
    public void Editing_to_empty_text_leaves_only_the_non_text_parts()
    {
        const string Image = """{"type":"image","mime":"image/png","dataRef":"x"}""";
        Repo.ReplaceHistory("s", [new MessageRecord("u1", "user", Parts(TextPart("旧"), Image), 1)]);

        Repo.EditMessage("s", "u1", "", truncate: false);

        Assert.Equal(Parts(Image), Repo.History("s")[0].PartsJson);
    }

    [Fact]
    public void Truncate_removes_that_message_and_everything_after_it()
    {
        Seed(3);

        Repo.EditMessage("s", "u2", "重跑", truncate: true);

        Assert.Equal(Ids(Turn(1)), Ids(Repo.History("s")));
        Assert.Throws<StorageException>(() => Repo.EditMessage("s", "nope", "x", truncate: false));
        Assert.Throws<StorageException>(() => Repo.EditMessage("s", "a1", "x", truncate: false)); // 只能编辑用户提问
        Assert.Throws<StorageException>(() => Repo.DeleteTurn("s", "missing"));
    }

    [Fact]
    public void Editing_the_transcript_detaches_the_imported_source_in_the_same_transaction_but_renaming_does_not()
    {
        Seed(1);
        Exec("""
            INSERT INTO import_mappings (source_id, scope_key, entity_kind, source_item_id, target_id, sync_state, created_at, updated_at)
            VALUES ('src', '', 'session', 'item1', 's', 'linked', 1, 1)
            """);

        // ★ 只有内容修改算脱离:归档 / 收藏 / 改标题不是转录编辑
        Repo.Rename("s", "新名字");
        Repo.SetArchived("s", true);
        Assert.Equal("linked", Text("SELECT sync_state FROM import_mappings"));

        Repo.DeleteTurn("s", "u1");

        Assert.Equal("detached", Text("SELECT sync_state FROM import_mappings"));
    }

    [Fact]
    public void A_failed_edit_changes_nothing()
    {
        Seed(2);
        Exec("""
            INSERT INTO import_mappings (source_id, scope_key, entity_kind, source_item_id, target_id, sync_state, created_at, updated_at)
            VALUES ('src', '', 'session', 'item1', 's', 'linked', 1, 1)
            """);

        Assert.Throws<StorageException>(() => Repo.DeleteTurn("s", "missing"));

        Assert.Equal(8, Repo.CountMessages("s"));
        Assert.Equal("linked", Text("SELECT sync_state FROM import_mappings"));
    }

    // session-clone.test.ts · turnEndIndex

    private static readonly MessageRecord[] CloneTranscript =
    [
        Ask("u1", "first", 1),
        ToolCall("a1", "c1", "Read", "{}", 2),
        ToolResult("r1", "c1", "ok", 3),
        Say("a1b", "done", 4),
        Ask("u2", "second", 5),
        Say("a2", "reply", 6),
    ];

    [Fact]
    public void Turn_end_carries_the_tool_results_that_follow_the_prompt_and_stops_before_the_next_real_prompt()
    {
        Assert.Equal(4, HistoryEdit.TurnEndIndex(CloneTranscript, "u1"));
    }

    [Fact]
    public void Turn_end_runs_to_the_end_of_the_transcript_for_the_last_turn()
    {
        Assert.Equal(CloneTranscript.Length, HistoryEdit.TurnEndIndex(CloneTranscript, "u2"));
    }

    [Fact]
    public void Turn_end_is_null_for_an_unknown_id_or_a_non_user_message_id()
    {
        Assert.Null(HistoryEdit.TurnEndIndex(CloneTranscript, "missing"));
        Assert.Null(HistoryEdit.TurnEndIndex(CloneTranscript, "a1"));
    }

    // message shape(isToolResultOnly / isTurnStart / visibleText)

    [Fact]
    public void Message_shape_tells_tool_results_from_things_the_user_said()
    {
        Assert.True(MessageShape.IsToolResultOnly("user", ToolResult("r", "c", "x").PartsJson));
        Assert.False(MessageShape.IsToolResultOnly("assistant", ToolResult("r", "c", "x").PartsJson));
        Assert.False(MessageShape.IsToolResultOnly("user", "[]")); // 空数组不是回执
        Assert.False(MessageShape.IsToolResultOnly("user", Parts("""{"type":"tool_result","callId":"c","output":{"content":""},"isError":false}""", TextPart("hi"))));
        Assert.False(MessageShape.IsToolResultOnly("user", "not json"));
        Assert.True(MessageShape.IsTurnStart(Ask("u", "x")));
        Assert.False(MessageShape.IsTurnStart(ToolResult("r", "c", "x")));
        Assert.False(MessageShape.IsTurnStart(Say("a", "x")));
        Assert.Equal("ab", MessageShape.VisibleText(Parts(TextPart("a"), """{"type":"image","mime":"x","dataRef":"y"}""", TextPart("b"))));
    }
}
