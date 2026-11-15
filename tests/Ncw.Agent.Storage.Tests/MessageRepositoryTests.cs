using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 消息部分。移植自 2.x 的 <c>db/__tests__/committed-inputs.test.ts</c>(消费回执)、<c>history-page.test.ts</c>(按页读)、
/// <c>recent-prompts.test.ts</c>(输入框补全)、<c>turn-usage.test.ts</c>(逐轮用量)和 <c>roundtrip.test.ts</c> 里
/// 「会话、完整内容块与全文索引」「lastGoalStatusMessage」两组及 replaceHistory / FTS rowid 的几条。
/// </summary>
public sealed class MessageRepositoryTests : SessionTestBase
{
    public MessageRepositoryTests()
    {
        // 2.x 的这些测试都用没有登记过的工作区 id(local 作用域下可用)
        NewSession("s1", "ws");
    }

    // ── committed-inputs.test.ts:提交用户消息留下消费回执 ──

    private long ReceiptCount(string sessionId) => Count("SELECT COUNT(*) FROM session_input_receipts WHERE session_id = $s", ("$s", sessionId));

    [Fact]
    public void User_commit_leaves_a_receipt_assistant_does_not()
    {
        Repo.Commit("s1", Ask("u1", "你好"));
        Repo.Commit("s1", Say("a1", "在的"));

        Assert.Equal(1, ReceiptCount("s1"));
        // 判定必须按 id 逐条回答:assistant 那一半绝不能跟着 user 一起被算成已提交
        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1", "a1"]));
        Assert.Empty(Repo.CommittedInputIds("s1", ["a1"]));
    }

    // ── 3.0:迁移 39 的 meta 列 ──

    [Fact]
    public void Message_meta_roundtrips_through_commit_history_and_page_and_survives_replace_history()
    {
        var meta = """{"source":{"api":"anthropic","provider":"p1","model":"m1"}}""";
        Repo.Commit("s1", Ask("u1", "hi"));
        Repo.Commit("s1", Say("a1", "yo") with { MetaJson = meta });

        Assert.Null(Repo.History("s1")[0].MetaJson); // 2.x 写的 / 用户消息:NULL = 来源未知
        Assert.Equal(meta, Repo.History("s1")[1].MetaJson);
        Assert.Equal(meta, Repo.LastAssistant("s1")!.MetaJson);
        Assert.Equal(meta, Repo.HistoryPage("s1", 10).Messages[1].MetaJson);

        // 对账时手里拿的是读出来的整段历史:meta 不能被抹掉
        Repo.ReplaceHistory("s1", Repo.History("s1"));
        Assert.Equal(meta, Repo.History("s1")[1].MetaJson);

        // meta 变了也算「变了」,会被写回
        Repo.ReplaceHistory("s1", [Repo.History("s1")[0], Repo.History("s1")[1] with { MetaJson = null }]);
        Assert.Null(Repo.History("s1")[1].MetaJson);
    }

    [Fact]
    public void Replaying_a_commit_keeps_one_receipt_with_the_first_commit_time()
    {
        Repo.Commit("s1", Ask("u1", "重放一次", At));
        // 重放时消息行会被更新(excluded.created_at 覆盖),回执不能跟着漂 —— 「什么时候第一次提交的」是它唯一的语义
        Repo.Commit("s1", Ask("u1", "重放一次", At + 5000));

        Assert.Equal(1, ReceiptCount("s1"));
        Assert.Equal(At, Count("SELECT committed_at FROM session_input_receipts WHERE message_id = 'u1'"));
        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1"]));
        Assert.Single(Repo.History("s1"));
    }

    [Fact]
    public void Unknown_ids_and_an_empty_list_get_no_answer()
    {
        Repo.Commit("s1", Ask("u1", "在的"));

        Assert.Empty(Repo.CommittedInputIds("s1", []));
        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1", "从没提交过"]));
    }

    [Fact]
    public void The_receipt_query_never_truncates_silently()
    {
        var ids = Enumerable.Range(0, 25).Select(i => $"u-{i:00}").ToList();
        foreach (var id in ids)
        {
            Repo.Commit("s1", Ask(id, $"第 {id} 条"));
        }

        // 少查一条 = 少判一条 = 可能再执行一次,所以这里必须 25 条全中
        Assert.Equal(ids.Order(), Repo.CommittedInputIds("s1", [.. ids, "不存在"]).Order());
    }

    [Fact]
    public void Receipts_of_another_session_are_never_read_across()
    {
        NewSession("s2");
        Repo.Commit("s1", Ask("m-1", "只提交在 s1"));
        Repo.Commit("s2", Ask("m-2", "s2 自己的"));

        Assert.Empty(Repo.CommittedInputIds("s2", ["m-1"]));
        Assert.Empty(Repo.CommittedInputIds("s1", ["m-2"]));
        Assert.Equal(["m-2"], Repo.CommittedInputIds("s2", ["m-2"]));
    }

    [Fact]
    public void Message_and_receipt_roll_back_together()
    {
        // ★ 注入一次回执写入失败:把回执表摘掉,提交就必然在「消息已经 INSERT 过、回执写不进去」那一步抛;
        // 如果回执是另一个事务,这一抛之后消息会留在库里,而那正是「重启后再执行一遍」的成因。
        Exec("DROP TABLE session_input_receipts");

        var error = Assert.Throws<SqliteException>(() => Repo.Commit("s1", Ask("u1", "这次提交必须整体失败")));

        Assert.Contains("session_input_receipts", error.Message);
        Assert.Empty(Repo.History("s1"));
        Assert.Equal(0, Count("SELECT count(*) FROM messages_fts"));
    }

    [Fact]
    public void Replacing_history_without_the_user_message_keeps_its_receipt()
    {
        Repo.Commit("s1", Ask("u1", "要被编辑掉的提问"));
        Repo.Commit("s1", Say("a1", "回答"));

        Repo.ReplaceHistory("s1", [Say("a1", "回答")]);

        Assert.Equal(["a1"], Ids(Repo.History("s1")));
        // ★ 消息没了,但「这份输入已经执行过」这件事发生过 —— 抹掉它就是允许重放
        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1"]));
    }

    [Fact]
    public void Deleting_message_rows_directly_does_not_take_the_receipt_with_it()
    {
        Repo.Commit("s1", Ask("u1", "被压缩掉的提问"));

        Exec("DELETE FROM messages WHERE id = 'u1'"); // 转录压缩的形状:回执没有指向 messages 的外键

        Assert.Empty(Repo.History("s1"));
        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1"]));
    }

    [Fact]
    public void Deleting_a_session_reclaims_its_receipts_only()
    {
        NewSession("s2");
        Repo.Commit("s1", Ask("u1", "在 s1"));
        Repo.Commit("s2", Ask("u2", "在 s2"));

        Repo.Delete("s1");

        Assert.Equal(0, ReceiptCount("s1"));
        Assert.Empty(Repo.CommittedInputIds("s1", ["u1"]));
        Assert.Equal(["u2"], Repo.CommittedInputIds("s2", ["u2"]));
    }

    [Fact]
    public void Receipts_survive_a_restart()
    {
        Repo.Commit("s1", Ask("u1", "跨重启"));

        Restart();

        Assert.Equal(["u1"], Repo.CommittedInputIds("s1", ["u1"]));
        Assert.Equal(["u1"], Ids(Repo.History("s1")));
    }

    [Fact]
    public void Migration_34_backfills_receipts_for_committed_user_messages_only()
    {
        // 升级第 33 版的旧库:已提交过的用户消息被回填成回执,assistant 不被回填。
        // ★ 不回填的话,升级后第一次重启就会把这些老输入当成「还没提交」再执行一遍 —— 而这个 bug 只出现在升级路径上。
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        var legacyRoot = Path.Combine(Path.GetTempPath(), $"ncwv33-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            using (var raw = new SqliteConnection($"Data Source={Path.Combine(legacyRoot, AgentDatabase.FileName)};Pooling=False"))
            {
                raw.Open();
                Raw(raw, "CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");
                foreach (var m in AgentDatabase.Migrations.Where(m => m.Version <= 33))
                {
                    Raw(raw, m.Sql);
                    Raw(raw, $"INSERT INTO migrations VALUES ({m.Version}, '{m.Name}', 0)");
                }

                Raw(raw, """
                    INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, status, archived, favorited, created_at, updated_at, json)
                    VALUES ('s-old', 'w', '旧对话', '', 'normal', 'auto', '', 'idle', 0, 0, 1, 1, '{}')
                    """);
                Raw(raw, "INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('m-user', 's-old', 0, 'user', '[]', 1, 555)");
                Raw(raw, "INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('m-assistant', 's-old', 1, 'assistant', '[]', 1, 556)");
            }

            using var upgraded = AgentDatabase.Open(legacyRoot);
            var repo = new SessionRepository(upgraded);

            Assert.Equal(["m-user"], repo.CommittedInputIds("s-old", ["m-user", "m-assistant"]));
            // committed_at 取的是消息原来的 created_at,不是升级那一刻的时间
            using var command = upgraded.Connection.CreateCommand();
            command.CommandText = "SELECT committed_at FROM session_input_receipts WHERE message_id = 'm-user'";
            Assert.Equal(555L, command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(legacyRoot, recursive: true);
            Db = AgentDatabase.Open(Root);
            Repo = new SessionRepository(Db);
        }
    }

    // ── 提交的基本规则 ──

    [Fact]
    public void Commit_assigns_increasing_ordinals_and_moves_the_session_forward()
    {
        Repo.Create(new SessionCreate("ws", "s9", CreatedAt: 1));
        Repo.Commit("s9", Ask("u1", "a", At));
        Repo.Commit("s9", Say("a1", "b", At + 1));
        Repo.Commit("s9", Ask("u2", "c", At + 2));
        Repo.Commit("s9", Ask("u1", "a2", At)); // 重放:原位覆盖,不新增

        Assert.Equal(["u1", "a1", "u2"], Ids(Repo.History("s9")));
        Assert.Equal(At + 2, Repo.Get("s9")!.UpdatedAt);
        Assert.Equal(3, Repo.CountMessages("s9"));
        Assert.Contains("a2", Repo.History("s9")[0].PartsJson);
    }

    [Fact]
    public void Commit_rejects_a_message_id_owned_by_another_session_and_a_missing_session()
    {
        NewSession("s2");
        Repo.Commit("s1", Ask("owned", "x"));

        Assert.Throws<StorageException>(() => Repo.Commit("s2", Ask("owned", "x")));
        Assert.Throws<StorageException>(() => Repo.Commit("missing", Ask("m", "x")));
        Assert.Throws<StorageException>(() => Repo.Commit("s1", new MessageRecord("bad", "user", "{\"not\":\"an array\"}", At)));
        Assert.Empty(Repo.History("s2"));
    }

    [Fact]
    public void The_run_id_survives_a_rewrite_without_one_and_internal_round_trips()
    {
        Repo.Commit("s1", Say("a1", "原文", 1), "run-1");
        Repo.Commit("s1", Say("a1", "改过的原文", 1)); // 没带 runId:COALESCE 保留原值
        Repo.Commit("s1", Say("a2", "内部", 2) with { Internal = true });

        Assert.Equal(new Dictionary<string, string> { ["a1"] = "run-1" }, Repo.MessageRuns("s1"));
        Assert.True(Repo.History("s1")[1].Internal);
        Assert.False(Repo.History("s1")[0].Internal);
    }

    [Fact]
    public void Bad_or_non_array_parts_read_as_an_empty_array()
    {
        Exec("INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('broken', 's1', 0, 'user', '{broken', 1, 1)");
        Exec("INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('obj', 's1', 1, 'user', '{}', 1, 2)");

        Assert.Equal(["[]", "[]"], Repo.History("s1").Select(m => m.PartsJson));
    }

    // ── history-page.test.ts:转录按页读 ──

    /// <summary>一轮:提问 → 带 tool_call 的助手 → 工具回执 → 收尾的助手</summary>
    private static MessageRecord[] Turn(int n) =>
    [
        Ask($"u{n}", $"问题 {n}", n * 10),
        ToolCall($"a{n}", $"c{n}", "Read", $$"""{"path":"{{n}}.ts"}""", n * 10 + 1),
        ToolResult($"r{n}", $"c{n}", "…", n * 10 + 2),
        Say($"b{n}", $"回答 {n}", n * 10 + 3),
    ];

    private List<MessageRecord> SeedTurns(int turns)
    {
        var history = Enumerable.Range(1, turns).SelectMany(Turn).ToList();
        Repo.ReplaceHistory("s1", history);
        return history;
    }

    [Fact]
    public void Page_start_is_extended_to_a_turn_start_never_a_tool_result()
    {
        SeedTurns(5);

        // 最新 3 条落在第 5 轮中间:r5 / b5 前面还有 u5、a5
        var page = Repo.HistoryPage("s1", 3);

        Assert.Equal(["u5", "a5", "r5", "b5"], Ids(page.Messages));
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Paging_backwards_joins_up_without_overlap_or_gaps()
    {
        var history = SeedTurns(4);
        var seen = new List<string>();
        var page = Repo.HistoryPage("s1", 4);
        seen.InsertRange(0, Ids(page.Messages));
        while (page.HasMore)
        {
            page = Repo.HistoryPage("s1", 4, page.Messages[0].Id);
            seen.InsertRange(0, Ids(page.Messages));
        }

        Assert.Equal(Ids(history), seen);
    }

    [Fact]
    public void An_anchor_outside_the_session_gives_an_empty_page()
    {
        SeedTurns(2);

        var page = Repo.HistoryPage("s1", 10, "nope");

        Assert.Empty(page.Messages);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void A_short_session_is_read_in_one_page()
    {
        var history = SeedTurns(1);

        var page = Repo.HistoryPage("s1", 200);

        Assert.Equal(Ids(history), Ids(page.Messages));
        Assert.False(page.HasMore);
    }

    [Fact]
    public void A_turn_with_hundreds_of_tool_messages_caps_the_extension_and_still_pages_without_gaps()
    {
        var history = new List<MessageRecord> { Ask("long-question", "Long task", 0) };
        history.AddRange(Enumerable.Range(0, 720).Select(i => Say($"step-{i}", $"Step {i}", i + 1)));
        Repo.ReplaceHistory("s1", history);

        var latest = Repo.HistoryPage("s1", 60);
        Assert.Equal(560, latest.Messages.Count);
        Assert.Equal("step-719", latest.Messages[^1].Id);
        Assert.True(latest.HasMore);

        var earlier = Repo.HistoryPage("s1", 60, latest.Messages[0].Id);
        Assert.False(earlier.HasMore);
        Assert.Equal(Ids(history), Ids([.. earlier.Messages, .. latest.Messages]));
        // 提前跳出补齐循环必须释放读取器:接下来的读仍然可用
        Assert.Equal(560, Repo.HistoryPage("s1", 60).Messages.Count);
    }

    [Fact]
    public void Message_runs_cover_only_the_requested_page_and_do_not_leak_across_sessions()
    {
        var history = SeedTurns(3);
        foreach (var message in history)
        {
            Repo.Commit("s1", message, "run-s");
        }

        Repo.Commit("s1", Ask("legacy", "Legacy", 100));
        NewSession("other");
        Repo.Commit("other", Ask("foreign", "Other", 1), "run-other");

        Assert.Equal(new Dictionary<string, string> { ["u3"] = "run-s", ["b3"] = "run-s" },
            Repo.MessageRuns("s1", ["u3", "b3", "legacy", "foreign", "missing"]));
        Assert.Empty(Repo.MessageRuns("s1", []));
        Assert.Equal(history.Count, Repo.MessageRuns("s1").Count);
    }

    // ── recent-prompts.test.ts ──

    [Fact]
    public void Recent_session_prompts_are_only_what_the_user_typed_newest_first()
    {
        Repo.Commit("s1", Ask("u1", "第一句", At));
        Repo.Commit("s1", Say("a1", "回答", At + 1));
        Repo.Commit("s1", ToolResult("t1", "c1", "ok", At + 2));
        Repo.Commit("s1", Ask("i1", "内部注入", At + 3) with { Internal = true });
        Repo.Commit("s1", new MessageRecord("img", "user", Parts("""{"type":"image","mime":"image/png","dataRef":"ref"}"""), At + 4));
        Repo.Commit("s1", Ask("u2", "第二句", At + 5));

        Assert.Equal(["u2", "u1"], Ids(Repo.RecentPrompts("s1", 10)));
        Assert.Equal(["u2"], Ids(Repo.RecentPrompts("s1", 1)));
    }

    [Fact]
    public void Last_visible_assistant_skips_internal_ones()
    {
        Repo.Commit("s1", Say("a1", "给人看的回复", At));
        Repo.Commit("s1", Say("a2", "内部", At + 1) with { Internal = true });

        Assert.Equal("a1", Repo.LastVisibleAssistant("s1")!.Id);
        Assert.Equal("a2", Repo.LastAssistant("s1")!.Id); // 不挑 internal
        Assert.Null(Repo.LastVisibleAssistant("nope"));
    }

    [Fact]
    public void Recent_workspace_prompts_are_newest_first_deduped_and_top_level_only()
    {
        NewSession("s2");
        NewSession("other", "ws2");
        Repo.Create(new SessionCreate("ws", "s1:sub:x", ParentSessionId: "s1"));
        Repo.Commit("s1", Ask("u1", "跑一下测试", At));
        Repo.Commit("s2", Ask("u2", "提交并推送", At + 1));
        Repo.Commit("s2", Ask("u3", "跑一下测试", At + 2));
        Repo.Commit("other", Ask("o1", "别的工作区", At + 3));
        Repo.Commit("s1:sub:x", Ask("sub1", "子代理的任务说明", At + 4));

        Assert.Equal(["跑一下测试", "提交并推送"], Repo.RecentWorkspacePrompts("ws"));
    }

    [Fact]
    public void Recent_workspace_prompts_drop_long_or_multi_line_expanded_templates_and_honor_the_limit()
    {
        Repo.Commit("s1", Ask("long", new string('x', 501), At));
        Repo.Commit("s1", Ask("lines", "a\nb\nc\nd", At + 1));
        Repo.Commit("s1", Ask("ok", "看一下 CI 为什么红了", At + 2));
        Assert.Equal(["看一下 CI 为什么红了"], Repo.RecentWorkspacePrompts("ws"));

        for (var i = 0; i < 8; i++)
        {
            Repo.Commit("s1", Ask($"u{i}", $"指令 {i}", At + 10 + i));
        }

        Assert.Equal(["指令 7", "指令 6", "指令 5"], Repo.RecentWorkspacePrompts("ws", new RecentPromptLimits(Limit: 3)));
    }

    [Fact]
    public void Recent_prompts_survive_a_row_with_unparseable_parts()
    {
        Exec("INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('broken', 's1', 0, 'user', '{broken', 1, 1)");
        Repo.Commit("s1", Ask("u1", "还能读到", At));

        Assert.Equal(["u1"], Ids(Repo.RecentPrompts("s1", 10)));
    }

    // ── roundtrip.test.ts:lastGoalStatusMessage ──

    [Fact]
    public void Last_goal_status_is_the_last_assistant_message_that_really_has_the_part()
    {
        static MessageRecord Assistant(string id, params string[] parts) => new(id, "assistant", Parts(parts), 1);
        const string TextGood = """{"type":"text","text":"好"}""";
        static string Goal(string condition) => $$"""{"type":"goal_status","met":false,"condition":"{{condition}}"}""";
        Repo.ReplaceHistory("s1",
        [
            Assistant("a1", TextGood, Goal("旧目标")),
            Assistant("a2", TextGood, Goal("新目标")),
            // 转义后的引号让它不会命中;下划线是 LIKE 通配,换成别的字符的那一条会命中候选但要被解析排除
            Assistant("a3", """{"type":"text","text":"\"type\":\"goal_status\""}"""),
            Assistant("a4", TextGood, """{"type":"goal-status"}"""),
            Ask("u1", "继续", 2),
        ]);

        Assert.Equal("a2", Repo.LastGoalStatus("s1")!.Id);
        Assert.Null(Repo.LastGoalStatus("no-such-session"));
    }

    // ── roundtrip.test.ts:会话、完整内容块与全文索引 ──

    private static MessageRecord[] Transcript() =>
    [
        new("m-assistant", "assistant", Parts(
            """{"type":"thinking","text":"private reasoning","opaque":{"signature":"sig-1"}}""",
            """{"type":"text","text":"persistneedle answer"}""",
            """{"type":"tool_call","callId":"call-1","name":"Read","input":{"path":"README.md"}}""",
            """{"type":"subagent","callId":"call-2","childRunId":"child-1","summary":"child summary"}""",
            """{"type":"image","mime":"image/png","dataRef":"/missing/imported-image.png"}""",
            """{"type":"error","error":{"code":"tool_failed","message":"tool failed safely","retryable":false}}"""), 1_700_000_000_200),
        new("m-tool-result", "user", Parts(
            """{"type":"tool_result","callId":"call-1","output":{"content":"tool output","truncated":true,"originalBytes":100000},"isError":false}"""), 1_700_000_000_100),
    ];

    [Fact]
    public void Every_content_part_round_trips_in_commit_order_not_timestamp_order()
    {
        NewSession("session-parts", "workspace-1", "OriginalTitle");
        foreach (var message in Transcript())
        {
            Repo.Commit("session-parts", message);
        }

        Repo.SetRunRecord("run-1", "session-parts", "completed", 10, 20);
        Restart();

        Assert.Equal(Transcript(), Repo.History("session-parts"));
        Assert.Equal(Transcript(), Repo.Detail("session-parts")!.Messages);
        Assert.Equal("completed", Text("SELECT status FROM runs WHERE id = 'run-1'"));
        Assert.Equal(20L, Count("SELECT ended_at FROM runs WHERE id = 'run-1'"));
    }

    [Fact]
    public void Body_and_new_title_are_searchable_and_deleting_leaves_no_phantom_hits()
    {
        NewSession("session-search", "workspace-1", "OriginalTitle");
        foreach (var message in Transcript())
        {
            Repo.Commit("session-search", message);
        }

        Assert.Equal(["session-search"], Repo.Search("persistneedle").Select(h => h.SessionId));
        Repo.Rename("session-search", "RenamedOrbit");
        Assert.Equal(["session-search"], Repo.Search("RenamedOrbit").Select(h => h.SessionId).Distinct());
        Assert.Empty(Repo.Search("OriginalTitle"));

        Repo.Delete("session-search");

        Assert.Empty(Repo.Search("persistneedle"));
        Assert.Empty(Repo.Search("RenamedOrbit"));
    }

    [Fact]
    public void Search_hits_carry_the_mark_snippet_and_the_workspace()
    {
        Repo.Commit("s1", Say("a1", "the quick brown fox jumps", At));

        var hit = Assert.Single(Repo.Search("brown fox"));

        Assert.Equal(("s1", "ws", "a1"), (hit.SessionId, hit.WorkspaceId, hit.MessageId));
        Assert.Contains("<mark>", hit.Snippet);
        Assert.Equal(At, hit.CreatedAt);
        // 用户输入里的引号和 MATCH 运算符不会破坏语法(短语查询)
        Assert.Empty(Repo.Search("\"fox\" OR *"));
        Assert.Empty(Repo.Search("   "));
    }

    [Fact]
    public void Session_search_is_scoped_to_one_session_and_limited()
    {
        NewSession("s2");
        Repo.Commit("s1", Say("a1", "ctxneedle one", At));
        Repo.Commit("s1", Say("a2", "ctxneedle two", At + 1));
        Repo.Commit("s2", Say("b1", "ctxneedle elsewhere", At + 2));

        var hits = Repo.SearchSession("s1", "ctxneedle");
        Assert.Equal(["a2", "a1"], hits.Select(h => h.MessageId));
        Assert.Equal("assistant", hits[0].Role);
        Assert.Single(Repo.SearchSession("s1", "ctxneedle", 1));
        Exec("DROP TABLE messages_fts"); // LIKE 兜底
        Assert.Equal(["a2", "a1"], Repo.SearchSession("s1", "ctxneedle").Select(h => h.MessageId));
    }

    [Fact]
    public void Archive_and_favorite_persist_and_listing_flags_follow()
    {
        Repo.Commit("s1", Transcript()[0]);
        Repo.SetArchived("s1", true);
        Repo.SetFavorited("s1", true);

        Restart();

        Assert.Equal((true, true), (Repo.Get("s1")!.Archived, Repo.Get("s1")!.Favorited));
        var item = Assert.Single(Repo.List("ws", archived: true));
        Assert.Equal(("s1", true, true), (item.Id, item.Archived, item.Favorited));
    }

    // ── roundtrip.test.ts:replaceHistory 与全文索引 ──

    [Fact]
    public void Identical_history_writes_nothing_and_edits_leave_unchanged_search_rows_intact()
    {
        var messages = Enumerable.Range(0, 6).Select(i => Say($"delta-{i}", $"needle{i}", i + 1)).ToList();
        foreach (var message in messages)
        {
            Repo.Commit("s1", message, "owner-run");
        }

        var before = Count("SELECT total_changes()");
        Repo.ReplaceHistory("s1", messages);
        Assert.Equal(before, Count("SELECT total_changes()"));

        Exec("CREATE TEMP TABLE body_updates (id TEXT)");
        Exec("CREATE TEMP TRIGGER track_body_updates AFTER UPDATE OF parts ON messages BEGIN INSERT INTO body_updates VALUES (new.id); END");
        var indexedBefore = FtsRowids("s1");
        var edited = messages[3] with { PartsJson = Parts(TextPart("replacementneedle")) };

        Repo.ReplaceHistory("s1", [messages[5], messages[0], edited, messages[2]]);

        Assert.Equal("delta-3", Text("SELECT id FROM body_updates"));
        Assert.Equal(1, Count("SELECT count(*) FROM body_updates"));
        Assert.Equal(["delta-5", "delta-0", "delta-3", "delta-2"], Ids(Repo.History("s1")));
        foreach (var id in new[] { "delta-5", "delta-0", "delta-2" })
        {
            Assert.Equal(indexedBefore[id], FtsRowids("s1")[id]);
        }

        Assert.Empty(Repo.Search("needle1"));
        Assert.Equal("delta-3", Repo.Search("replacementneedle")[0].MessageId);
        Assert.Equal(new Dictionary<string, string> { ["delta-5"] = "owner-run", ["delta-0"] = "owner-run", ["delta-3"] = "owner-run", ["delta-2"] = "owner-run" },
            Repo.Detail("s1")!.MessageRuns);
        Restart();
        Assert.Equal(["delta-5", "delta-0", "delta-3", "delta-2"], Ids(Repo.History("s1")));
    }

    private Dictionary<string, long> FtsRowids(string session)
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = "SELECT message_id, rowid FROM messages_fts WHERE session_id = $s";
        command.Parameters.AddWithValue("$s", session);
        using var reader = command.ExecuteReader();
        var map = new Dictionary<string, long>();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    [Fact]
    public void Duplicate_and_foreign_message_ids_are_rejected_before_changing_the_history()
    {
        NewSession("foreign-owner");
        var message = Ask("owned", "original", 1);
        Repo.Commit("s1", message);
        Repo.Commit("foreign-owner", Ask("foreign", "original", 1));

        Assert.Throws<StorageException>(() => Repo.ReplaceHistory("s1", [message, message]));
        Assert.Throws<StorageException>(() => Repo.ReplaceHistory("s1", [message with { Id = "foreign" }]));
        Assert.Equal([message], Repo.History("s1"));

        Repo.ReplaceHistory("s1", []);

        Assert.Empty(Repo.History("s1"));
        Assert.Equal(["foreign-owner"], Repo.Search("original").Select(h => h.SessionId));
    }

    [Fact]
    public void Search_titles_are_rewritten_only_when_the_normalized_title_changes()
    {
        Repo.Rename("s1", "OriginalTitle");
        var messages = Enumerable.Range(0, 20).Select(i => Say($"title-cost-{i}", "searchable content", i + 1)).ToList();
        foreach (var message in messages)
        {
            Repo.Commit("s1", message);
        }

        var before = Count("SELECT total_changes()");
        Repo.Put(Repo.Get("s1")! with { Title = "  OriginalTitle  " });
        Repo.ReplaceHistory("s1", messages);
        // 只有 sessions 那一行被写;20 条消息的索引标题没动
        Assert.True(Count("SELECT total_changes()") - before <= 2);

        Repo.Rename("s1", "RenamedTitle");

        Assert.Empty(Repo.Search("OriginalTitle"));
        Assert.Equal(20, Repo.Search("RenamedTitle").Count);
        Restart();
        Assert.Equal(20, Repo.Search("RenamedTitle").Count);
    }

    [Fact]
    public void One_fts_row_per_message_addressed_by_rowid_through_rewrites_and_deletes()
    {
        var before = Count("SELECT COUNT(*) FROM messages_fts");
        Repo.Commit("s1", Say("fts-a", "alphaword", 1));
        Repo.Commit("s1", Say("fts-b", "betaword", 2));
        Repo.ReplaceHistory("s1", [Say("fts-a", "gammaword", 1), Say("fts-b", "betaword", 2)]);

        Assert.Equal(2, Count("SELECT COUNT(*) FROM messages_fts") - before);
        Assert.Equal(Count("SELECT COUNT(*) FROM messages_fts"), Count("SELECT COUNT(*) FROM messages_fts_rowid"));
        Assert.Empty(Repo.Search("alphaword"));
        Assert.Single(Repo.Search("gammaword"));

        Repo.ReplaceHistory("s1", [Say("fts-a", "gammaword", 1)]);

        Assert.Empty(Repo.Search("betaword"));
        Assert.Equal(Count("SELECT COUNT(*) FROM messages_fts"), Count("SELECT COUNT(*) FROM messages_fts_rowid"));
        Repo.Delete("s1");
        Assert.Equal(before, Count("SELECT COUNT(*) FROM messages_fts"));
        Assert.Equal(before, Count("SELECT COUNT(*) FROM messages_fts_rowid"));
    }

    [Fact]
    public void After_a_pre_v37_version_touched_the_same_database_new_messages_still_write_and_old_ones_are_found()
    {
        Repo.Commit("s1", Say("legacy-a", "firstword", 1));
        // 旧版写法:按 message_id 删、不带 rowid 插,不碰映射表
        void LegacyWrite(string id, string text)
        {
            Exec("DELETE FROM messages_fts WHERE message_id = $m", ("$m", id));
            Exec("INSERT INTO messages_fts (message_id, session_id, title, content) VALUES ($m, 's1', 'LegacyTitle', $c)", ("$m", id), ("$c", text));
        }

        Exec("INSERT INTO messages (id, session_id, ordinal, role, parts, created_at, schema_version) VALUES ('legacy-b', 's1', 100, 'assistant', '[]', 2, 1)");
        Exec("INSERT INTO messages (id, session_id, ordinal, role, parts, created_at, schema_version) VALUES ('legacy-c', 's1', 101, 'assistant', '[]', 3, 1)");
        LegacyWrite("legacy-a", "rewrittenword");
        LegacyWrite("legacy-b", "legacyword");
        LegacyWrite("legacy-c", "thirdword");

        Restart(); // 开库时对账:把无映射的 FTS 行补进映射
        Repo.Commit("s1", Say("legacy-d", "newword", 4)); // 不许被 FTS5 拒成 constraint failed

        Assert.Single(Repo.Search("rewrittenword"));
        Assert.Single(Repo.Search("legacyword"));
        Assert.Single(Repo.Search("thirdword"));
        Assert.Single(Repo.Search("newword"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM messages_fts WHERE rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid)"));
    }

    [Fact]
    public void Replace_parts_if_unchanged_is_a_compare_and_swap_that_keeps_ordinal_run_and_time()
    {
        Repo.Commit("s1", Ask("u1", "a", 5), "run-1");
        var expected = Repo.History("s1")[0].PartsJson;
        var replacement = Parts(TextPart("replacedword"));

        Assert.False(Repo.ReplacePartsIfUnchanged("s1", "u1", Parts(TextPart("stale")), replacement)); // 读到之后被别人改了
        Assert.False(Repo.ReplacePartsIfUnchanged("s1", "missing", expected, replacement));
        Assert.True(Repo.ReplacePartsIfUnchanged("s1", "u1", expected, replacement));

        var message = Repo.History("s1")[0];
        Assert.Equal((replacement, 5L), (message.PartsJson, message.CreatedAt));
        Assert.Equal("run-1", Repo.MessageRuns("s1")["u1"]);
        Assert.Single(Repo.Search("replacedword")); // 全文索引走同一条写路径
    }

    // ── turn-usage.test.ts:逐轮用量 ──

    private void Usage(string id, string runId, string session = "s1", string alias = "assistant", long input = 100, long output = 20,
        long cacheRead = 300, long cacheWrite = 50, long cacheWrite1h = 10, long? thinking = 5, long latency = 1_200, bool ok = true,
        long? cost = 1_000, string? currency = "USD")
    {
        Exec("""
            INSERT INTO usage_records (id, at, run_id, provider_id, alias, upstream_model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
              cache_write_1h_tokens, latency_ms, ok, cost_micros, currency, session_id, thinking_tokens)
            VALUES ($id, 1000, $run, 'provider-1', $alias, 'model-a', $in, $out, $cr, $cw, $cw1, $lat, $ok, $cost, $cur, $s, $think)
            """, ("$id", id), ("$run", runId), ("$alias", alias), ("$in", input), ("$out", output), ("$cr", cacheRead), ("$cw", cacheWrite),
            ("$cw1", cacheWrite1h), ("$lat", latency), ("$ok", ok ? 1 : 0), ("$cost", cost), ("$cur", currency), ("$s", session), ("$think", thinking));
    }

    [Fact]
    public void Per_run_usage_is_aggregated_back_after_a_restart()
    {
        Repo.Commit("s1", Say("m-1", "第一轮", 1), "run-1");
        Repo.Commit("s1", Say("m-2", "第二轮", 2), "run-2");
        Usage("u-1", "run-1");
        Usage("u-2", "run-2", input: 7, output: 3);

        Restart();

        var detail = Repo.Detail("s1")!;
        Assert.Equal(new Dictionary<string, string> { ["m-1"] = "run-1", ["m-2"] = "run-2" }, detail.MessageRuns);
        Assert.Equal(new RunUsageRecord(100, 20, 300, 50, 10, 5, 1_200, new RunCost(1_000, "USD")), detail.RunUsage["run-1"]);
        Assert.Equal((7L, 3L), (detail.RunUsage["run-2"].InputTokens, detail.RunUsage["run-2"].OutputTokens));
    }

    [Fact]
    public void Retries_within_a_run_merge_into_one_charge_and_failed_attempts_count()
    {
        Repo.Commit("s1", Say("m-1", "被限流后重试成功的一轮", 1), "run-1");
        // 第一次被打回:prompt 已经发上去了,照样计费。排除它,界面上的数字就比账单小。
        Usage("u-1", "run-1", ok: false, output: 0, cacheRead: 0, cacheWrite: 0, cacheWrite1h: 0, thinking: null, cost: 400);
        Usage("u-2", "run-1");

        var usage = Repo.RunUsage("s1")["run-1"];

        Assert.Equal((200L, 20L, 300L), (usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens));
        Assert.Equal(new RunCost(1_400, "USD"), usage.Cost); // 失败那次的钱也算
    }

    [Fact]
    public void One_unpriced_attempt_makes_the_whole_run_cost_null_not_zero()
    {
        Repo.Commit("s1", Say("m-1", "中途切到了没有定价的模型", 1), "run-1");
        Usage("u-1", "run-1", cost: 1_000);
        Usage("u-2", "run-1", cost: null, currency: null);

        var usage = Repo.RunUsage("s1")["run-1"];

        Assert.Null(usage.Cost); // 显式「算不齐」:会话总额随之作废,而不是漏掉未定价的那一轮
        Assert.Equal(200, usage.InputTokens); // token 照常 —— 不给的只有钱
    }

    [Fact]
    public void Mixed_currencies_in_one_run_give_no_cost()
    {
        Repo.Commit("s1", Say("m-1", "故障切换跨了国别", 1), "run-1");
        Usage("u-1", "run-1", cost: 1_000, currency: "USD");
        Usage("u-2", "run-1", cost: 9_000, currency: "CNY");

        Assert.Null(Repo.RunUsage("s1")["run-1"].Cost); // 没有汇率源:不把 ¥ 和 $ 加成一个错数
    }

    [Fact]
    public void Upstream_time_counts_only_successful_attempts()
    {
        Repo.Commit("s1", Say("m-1", "超时一次之后才答上来的一轮", 1), "run-1");
        // 30s 才被打回、一个 token 都没产出:Token 照样计费,但把它算进 TPS 的分母,就是在报告一件没发生的事
        Usage("u-1", "run-1", ok: false, output: 0, latency: 30_000);
        Usage("u-2", "run-1", latency: 1_200);

        var usage = Repo.RunUsage("s1")["run-1"];

        Assert.Equal((20L, 1_200L), (usage.OutputTokens, usage.UpstreamMs));
    }

    [Fact]
    public void A_run_without_any_successful_request_has_no_upstream_ms_and_no_reasoning_tokens_when_unreported()
    {
        Repo.Commit("s1", Say("m-1", "全军覆没的一轮", 1), "run-1");
        Usage("u-1", "run-1", ok: false, output: 0, latency: 8_000, thinking: null);

        var usage = Repo.RunUsage("s1")["run-1"];

        Assert.Null(usage.UpstreamMs);
        Assert.Null(usage.ReasoningTokens);
    }

    [Fact]
    public void Rewriting_the_whole_history_does_not_wipe_run_attribution()
    {
        Repo.Commit("s1", Say("m-1", "原文", 1), "run-1");
        Usage("u-1", "run-1");

        // replaceHistory 手上只有消息,没有 run 归属 —— 它必须保留原值
        Repo.ReplaceHistory("s1", [Say("m-1", "改过的原文", 1)]);
        Restart();

        var detail = Repo.Detail("s1")!;
        Assert.Contains("改过的原文", detail.Messages[0].PartsJson);
        Assert.Equal(new Dictionary<string, string> { ["m-1"] = "run-1" }, detail.MessageRuns);
        Assert.Equal(100, detail.RunUsage["run-1"].InputTokens);
    }

    [Fact]
    public void Messages_from_before_migration_12_have_no_run_attribution_and_no_placeholder()
    {
        Repo.Commit("s1", Say("m-old", "老对话", 1));

        Restart();

        Assert.Empty(Repo.Detail("s1")!.MessageRuns);
        Assert.Empty(Repo.Detail("s1")!.RunModel);
    }

    [Fact]
    public void Another_sessions_usage_does_not_leak_in()
    {
        NewSession("s2");
        Repo.Commit("s1", Say("m-1", "一", 1), "run-1");
        Repo.Commit("s2", Say("m-2", "二", 2), "run-2");
        Usage("u-1", "run-1");
        Usage("u-2", "run-2", session: "s2");

        Assert.Equal(["run-1"], Repo.Detail("s1")!.RunUsage.Keys);
        Assert.Equal(["run-2"], Repo.Detail("s2")!.RunUsage.Keys);
    }

    [Fact]
    public void Each_run_remembers_the_alias_it_was_sent_with_and_retries_share_it()
    {
        Repo.Commit("s1", Say("m-1", "第一轮用 deepseek", 1), "run-1");
        Repo.Commit("s1", Say("m-2", "第二轮换成 claude", 2), "run-2");
        Usage("u-1", "run-1", alias: "deepseek-v4.1-flash-expires-on-0910");
        Usage("u-1b", "run-1", alias: "deepseek-v4.1-flash-expires-on-0910", ok: false);
        Usage("u-2", "run-2", alias: "claude-fable-5");

        Restart();

        var model = Repo.Detail("s1")!.RunModel;
        Assert.Equal("deepseek-v4.1-flash-expires-on-0910", model["run-1"]);
        Assert.Equal("claude-fable-5", model["run-2"]);
    }

    [Fact]
    public void A_database_stuck_at_version_11_upgrades_and_old_messages_stay_unattributed()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        var legacyRoot = Path.Combine(Path.GetTempPath(), $"ncwv11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            using (var raw = new SqliteConnection($"Data Source={Path.Combine(legacyRoot, AgentDatabase.FileName)};Pooling=False"))
            {
                raw.Open();
                Raw(raw, "CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");
                foreach (var m in AgentDatabase.Migrations.Where(m => m.Version <= 11))
                {
                    Raw(raw, m.Sql);
                    Raw(raw, $"INSERT INTO migrations VALUES ({m.Version}, '{m.Name}', 0)");
                }

                Raw(raw, "INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, created_at, updated_at, json) VALUES ('session-1', 'workspace-1', '旧会话', 'model-a', 'normal', 'auto', '/tmp', 1, 1, '{}')");
                Raw(raw, """INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('m-old', 'session-1', 0, 'assistant', '[{"type":"text","text":"升级前就存在"}]', 1, 1)""");
            }

            using var upgraded = AgentDatabase.Open(legacyRoot);
            var repo = new SessionRepository(upgraded);
            repo.Commit("session-1", Say("m-new", "升级后的一轮", 2), "run-1");

            var detail = repo.Detail("session-1")!;
            Assert.Equal(["m-old", "m-new"], Ids(detail.Messages));
            Assert.Equal(new Dictionary<string, string> { ["m-new"] = "run-1" }, detail.MessageRuns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(legacyRoot, recursive: true);
            Db = AgentDatabase.Open(Root);
            Repo = new SessionRepository(Db);
        }
    }

    // ── 摘要、页与详情 ──

    [Fact]
    public void Summary_has_the_count_without_reading_bodies_and_page_has_run_attribution_for_the_page_only()
    {
        var history = SeedTurns(3);
        foreach (var message in history)
        {
            Repo.Commit("s1", message, $"run-{message.Id[^1]}");
        }

        Usage("u-1", "run-1");
        var summary = Repo.Summary("s1")!;
        Assert.Equal((12L, "s1"), (summary.MessageCount, summary.Session.Id));
        Assert.Null(Repo.Summary("missing"));

        var page = Repo.Page("s1", 4)!;
        Assert.Equal(["u3", "a3", "r3", "b3"], Ids(page.Messages));
        Assert.True(page.HasMore);
        Assert.Equal(["a3", "b3", "r3", "u3"], page.MessageRuns.Keys.Order()); // 只覆盖本页
        Assert.Equal(["run-1"], page.RunUsage.Keys); // 用量与模型是整段会话的汇总
        Assert.Null(Repo.Page("missing", 10));
    }

    // ── 孤儿清理 ──

    [Fact]
    public void Sweeping_orphans_removes_index_rows_of_vanished_messages_and_stale_unreadable_inputs_only()
    {
        Repo.Commit("s1", Say("a1", "orphanword keepword", 1));
        Repo.Commit("s1", Say("a2", "orphanword goneword", 2));
        Exec("DELETE FROM messages WHERE id = 'a2'"); // 压缩 / 外部改库之后的幽灵索引
        // session.input.*:会话还在的留着;会话已不在但存档没过期的留着(草稿可以早于会话行);过期 / 结构对不上的清掉
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.s1', 'garbage')");
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.draft-fresh', $j)", ("$j", $$"""{"v":1,"draft":"x","queued":[],"savedAt":{{now}}}"""));
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.draft-old', $j)", ("$j", """{"v":1,"draft":"x","queued":[],"savedAt":1}"""));
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.draft-broken', 'garbage')");

        var result = Repo.SweepOrphans();

        Assert.Equal(new OrphanSweepResult(1, 2), result);
        Assert.Single(Repo.Search("orphanword"));
        Assert.Empty(Repo.Search("goneword"));
        Assert.Equal(["session.input.draft-fresh", "session.input.s1"], Keys());
        Assert.Equal(new OrphanSweepResult(0, 0), Repo.SweepOrphans());
    }

    private List<string> Keys()
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = "SELECT key FROM kv WHERE key LIKE 'session.input.%' ORDER BY key";
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static void Raw(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
