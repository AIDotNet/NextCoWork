using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 会话部分。移植自 2.x 的 <c>db/__tests__/subagent-sessions.test.ts</c>、<c>ipc/__tests__/session-model.test.ts</c>、
/// <c>ipc/__tests__/session-branch.test.ts</c>(只取存储一侧)以及 <c>roundtrip.test.ts</c> 里会话相关的几条;
/// 2.x 没有直接单测的部分(创建的形状、作用域收窄、元数据读写)按 2.x 实现行为重写。
/// </summary>
public sealed class SessionRepositoryTests : SessionTestBase
{
    [Fact]
    public void New_session_has_the_2x_shape()
    {
        var session = Repo.Create(new SessionCreate("ws", RootPathAtCreation: "/work"));

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", session.Id);
        Assert.Equal("新对话", session.Title);
        Assert.Equal("default", session.TitleSource);
        Assert.Equal("code", session.Mode);
        Assert.Equal("auto", session.Thinking);
        Assert.Equal("chat", session.Origin);
        Assert.False(session.Archived);
        Assert.Equal("/work", session.RootPathAtCreation);
        // json 列和 2.x 的 JSON.stringify 一样:键顺序、status 恒为 idle、没值的字段不写
        var json = Text("SELECT json FROM sessions WHERE id = $id", ("$id", session.Id))!;
        Assert.StartsWith($$"""{"id":"{{session.Id}}","workspaceId":"ws","origin":"chat","title":"新对话","titleSource":"default","model":"","mode":"code","thinking":"auto","rootPathAtCreation":"/work","status":"idle","archived":false,"favorited":false,""", json);
        Assert.Equal("idle", Text("SELECT status FROM sessions WHERE id = $id", ("$id", session.Id)));
    }

    [Fact]
    public void A_generated_title_is_written_in_two_steps_and_never_overrides_a_name_the_user_chose()
    {
        var session = Repo.Create(new SessionCreate("ws", "s", CreatedAt: 1));
        Repo.SetArchived("s", true);
        var before = Repo.Get("s")!;

        var preview = Repo.BeginGeneratedTitle("s", "  first message  ")!;
        Assert.Equal(("first message", "generated"), (preview.Title, preview.TitleSource));
        Assert.Equal(before.UpdatedAt, preview.UpdatedAt); // 自动起名不把会话顶到最前面
        Assert.Null(Repo.BeginGeneratedTitle("s", "again")); // 已经起过名:不重复

        var final = Repo.FinishGeneratedTitle(preview, "模型起的标题")!;
        Assert.Equal(("模型起的标题", "generated", true, before.UpdatedAt), (final.Title, final.TitleSource, final.Archived, final.UpdatedAt));
        Assert.Equal("模型起的标题", Repo.Get(session.Id)!.Title);
    }

    [Fact]
    public void A_late_generated_title_loses_to_a_rename_a_delete_or_a_recreate()
    {
        Repo.Create(new SessionCreate("ws", "renamed", CreatedAt: 1));
        var renamedPreview = Repo.BeginGeneratedTitle("renamed", "preview")!;
        Repo.Rename("renamed", "新对话"); // 手动改回占位名也是用户的选择
        Assert.Null(Repo.FinishGeneratedTitle(renamedPreview, "late"));
        Assert.Equal(("新对话", "manual"), (Repo.Get("renamed")!.Title, Repo.Get("renamed")!.TitleSource));
        Assert.Null(Repo.BeginGeneratedTitle("renamed", "x")); // manual 不再被自动起名

        Repo.Create(new SessionCreate("ws", "deleted", CreatedAt: 1));
        var deletedPreview = Repo.BeginGeneratedTitle("deleted", "preview")!;
        Repo.Delete("deleted");
        Assert.Null(Repo.FinishGeneratedTitle(deletedPreview, "late"));
        Assert.Null(Repo.Get("deleted"));

        Repo.Create(new SessionCreate("ws", "again", CreatedAt: 1));
        var stale = Repo.BeginGeneratedTitle("again", "preview")!;
        Repo.Delete("again");
        Repo.Create(new SessionCreate("ws", "again", CreatedAt: 2)); // 同 id 重建:创建时间不同
        Assert.Null(Repo.FinishGeneratedTitle(stale, "late"));
        Assert.Equal("新对话", Repo.Get("again")!.Title);
    }

    [Fact]
    public void A_session_that_was_named_by_the_user_at_creation_is_not_auto_titled()
    {
        Repo.Create(new SessionCreate("ws", "named", Title: "My project"));
        Assert.Null(Repo.BeginGeneratedTitle("named", "preview"));
        Assert.Equal("My project", Repo.Get("named")!.Title);
    }

    [Theory]
    [InlineData("", "default")]
    [InlineData("  ", "default")]
    [InlineData("New chat", "default")]
    [InlineData("修复登录", "manual")]
    public void Title_source_follows_whether_the_title_is_a_placeholder(string title, string source)
    {
        Assert.Equal(source, Repo.Create(new SessionCreate("ws", Title: title)).TitleSource);
    }

    [Theory]
    [InlineData(null, "code")]
    [InlineData("normal", "code")]
    [InlineData("goal", "code")]
    [InlineData("Plan", "plan")]
    [InlineData("  acp ", "acp")]
    [InlineData("custom-mode-1", "custom-mode-1")]
    [InlineData("Bad Mode!", "code")]
    [InlineData("-leading", "code")]
    public void Mode_ids_are_normalized_like_2x(string? input, string expected)
    {
        Assert.Equal(expected, SessionRepository.NormalizeMode(input));
        Assert.Equal(expected, Repo.Create(new SessionCreate("ws", Mode: input)).Mode);
    }

    [Fact]
    public void Ensure_does_not_overwrite_user_edits_but_backfills_the_parent()
    {
        var first = Repo.Create(new SessionCreate("ws", "s1", Title: "原名"));
        Repo.Rename("s1", "用户改的名");

        var again = Repo.Ensure(new SessionCreate("ws", "s1", Title: "另一个名字"));
        Assert.Equal("用户改的名", again.Title);
        Assert.Null(again.ParentSessionId);

        // 唯一的例外:这一行是在加上 parent_session_id 之前建的,不补的话子代理转录会永远留在列表里
        var withParent = Repo.Ensure(new SessionCreate("ws", "s1", ParentSessionId: "root"));
        Assert.Equal("root", withParent.ParentSessionId);
        Assert.Equal("用户改的名", withParent.Title);
        Assert.Equal(first.CreatedAt, withParent.CreatedAt);
    }

    [Fact]
    public void Put_normalizes_and_drops_a_self_parent_and_keeps_unknown_json_fields()
    {
        var session = Repo.Create(new SessionCreate("ws", "self"));
        Exec("UPDATE sessions SET json = $j WHERE id = 'self'", ("$j", Text("SELECT json FROM sessions WHERE id = 'self'")!.TrimEnd('}') + ",\"futureField\":{\"a\":1}}"));

        var saved = Repo.Put(session with { ParentSessionId = "self", Title = "   ", Mode = "normal" });

        Assert.Null(saved.ParentSessionId);
        Assert.Equal("新对话", saved.Title);
        Assert.Equal("code", saved.Mode);
        Assert.Null(Repo.Get("self")!.ParentSessionId);
        Assert.Equal(["self"], Repo.SubtreeIds("self"));
        Assert.Contains("\"futureField\":{\"a\":1}", Text("SELECT json FROM sessions WHERE id = 'self'"));
        Assert.Equal(["self"], Repo.List("ws").Select(s => s.Id));
    }

    [Fact]
    public void Columns_win_over_a_damaged_json_column()
    {
        Repo.Create(new SessionCreate("ws", "child", ParentSessionId: "parent", Title: "子", Model: "m1"));
        Repo.Create(new SessionCreate("ws", "parent"));
        Exec("UPDATE sessions SET json = 'not json' WHERE id = 'child'");

        var child = Repo.Get("child")!;
        Assert.Equal("parent", child.ParentSessionId); // 只认真列
        Assert.Equal("子", child.Title);
        Assert.Equal("m1", child.Model);
        Assert.Equal("chat", child.Origin);
        Assert.Null(child.ModelProviderId);
        Assert.DoesNotContain("child", Repo.List("ws").Select(s => s.Id)); // 子代理转录仍不进侧边栏
    }

    [Fact]
    public void Old_rows_with_legacy_modes_read_back_normalized()
    {
        Exec("""
            INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, status, archived, favorited, created_at, updated_at, json)
            VALUES ('old', 'ws', '旧会话', 'm', 'normal', 'auto', '/tmp', 'running', 1, 0, 5, 6, '{}')
            """);

        var old = Repo.Get("old")!;
        Assert.Equal("code", old.Mode);
        Assert.True(old.Archived); // 状态永不从磁盘恢复,但归档照读
        Assert.Equal(6, old.UpdatedAt);
    }

    [Fact]
    public void List_is_newest_first_by_updated_at_then_id_and_filters_archived()
    {
        Repo.Put(Repo.Create(new SessionCreate("ws", "a", CreatedAt: 1)) with { UpdatedAt = 10 });
        Repo.Put(Repo.Create(new SessionCreate("ws", "b", CreatedAt: 1)) with { UpdatedAt = 30 });
        Repo.Put(Repo.Create(new SessionCreate("ws", "c", CreatedAt: 1)) with { UpdatedAt = 30, Archived = true, Favorited = true });
        Repo.Create(new SessionCreate("other", "x"));

        Assert.Equal(["c", "b", "a"], Repo.List("ws").Select(s => s.Id));
        Assert.Equal(["b", "a"], Repo.List("ws", archived: false).Select(s => s.Id));
        var archived = Assert.Single(Repo.List("ws", archived: true));
        Assert.True(archived.Archived);
        Assert.True(archived.Favorited);
        Assert.False(archived.Running);
    }

    [Fact]
    public void List_hides_scheduled_sessions_and_flags_skill_extraction()
    {
        Repo.Create(new SessionCreate("ws", "chat"));
        Repo.Create(new SessionCreate("ws", "cron", Origin: "scheduled"));
        Repo.Create(new SessionCreate("ws", "extract", SkillSourceSessionId: "chat"));

        var list = Repo.List("ws").ToDictionary(s => s.Id);
        Assert.False(list.ContainsKey("cron"));
        Assert.False(list["chat"].SkillExtraction);
        Assert.True(list["extract"].SkillExtraction);
        Assert.Equal("scheduled", Repo.Get("cron")!.Origin); // 它读得到,只是不进列表
    }

    [Fact]
    public void Rename_archive_and_favorite_persist_across_restart()
    {
        Repo.Create(new SessionCreate("ws", "s", CreatedAt: 1));
        Repo.Rename("s", "  新名字  ");
        Repo.SetArchived("s", true);
        Repo.SetFavorited("s", true);

        Restart();

        var session = Repo.Get("s")!;
        Assert.Equal("新名字", session.Title);
        Assert.Equal("manual", session.TitleSource);
        Assert.True(session.Archived);
        Assert.True(session.Favorited);
        Assert.True(session.UpdatedAt > 1);
        Assert.Equal("新对话", Repo.Get(Repo.Create(new SessionCreate("ws", "t")).Id)!.Title);
        Repo.Rename("t", " ");
        Assert.Equal("新对话", Repo.Get("t")!.Title);
    }

    [Fact]
    public void Rename_archive_favorite_on_a_missing_session_throw()
    {
        Assert.Throws<StorageException>(() => Repo.Rename("nope", "x"));
        Assert.Throws<StorageException>(() => Repo.SetArchived("nope", true));
        Assert.Throws<StorageException>(() => Repo.SetFavorited("nope", true));
    }

    // 2.x session-model.test.ts

    [Fact]
    public void Model_pick_is_remembered_per_session()
    {
        Repo.Create(new SessionCreate("w", "a", Model: "sol", ModelProviderId: "routinai"));
        Repo.Create(new SessionCreate("w", "b", Model: "claude", ModelProviderId: "anthropic"));

        Assert.True(Repo.SetModel("a", "gpt", "openai"));

        Assert.Equal(("gpt", "openai"), (Repo.Get("a")!.Model, Repo.Get("a")!.ModelProviderId));
        Assert.Equal(("claude", "anthropic"), (Repo.Get("b")!.Model, Repo.Get("b")!.ModelProviderId));
    }

    [Fact]
    public void Model_pick_clears_the_provider_when_the_new_alias_comes_without_one()
    {
        Repo.Create(new SessionCreate("w", "a", Model: "sol", ModelProviderId: "routinai"));

        Repo.SetModel("a", "sol", null);

        Assert.Null(Repo.Get("a")!.ModelProviderId);
    }

    [Fact]
    public void Model_pick_leaves_the_recency_order_alone_and_is_silent_when_unchanged()
    {
        var session = Repo.Create(new SessionCreate("w", "a", Model: "sol"));
        Repo.Put(session with { UpdatedAt = 1_000 });

        Assert.True(Repo.SetModel("a", "gpt", "openai"));
        Assert.Equal(1_000, Repo.Get("a")!.UpdatedAt);
        Assert.False(Repo.SetModel("a", "gpt", "openai"));
        Assert.False(Repo.SetModel("missing", "gpt", null));
    }

    [Fact]
    public void Mode_change_moves_updated_at_and_is_a_no_op_when_unchanged_or_missing()
    {
        Repo.Create(new SessionCreate("w", "a", CreatedAt: 1));

        Assert.False(Repo.SetMode("a", "normal")); // 归一成 code,没变
        Assert.False(Repo.SetMode("missing", "plan"));
        Assert.True(Repo.SetMode("a", "plan"));
        Assert.Equal("plan", Repo.Get("a")!.Mode);
        Assert.True(Repo.Get("a")!.UpdatedAt > 1);
    }

    // 2.x subagent-sessions.test.ts —— 「modelProviderId 只活在 json 列里」

    [Fact]
    public void ModelProviderId_round_trips_and_is_null_when_absent()
    {
        Repo.Create(new SessionCreate("w", "s1", Model: "shared", ModelProviderId: "codex"));
        Repo.Create(new SessionCreate("w", "s2", Model: "shared"));

        Restart();

        Assert.Equal(("shared", "codex"), (Repo.Get("s1")!.Model, Repo.Get("s1")!.ModelProviderId));
        Assert.Null(Repo.Get("s2")!.ModelProviderId);
    }

    [Fact]
    public void Json_without_the_provider_field_still_reads()
    {
        Repo.Create(new SessionCreate("w", "s3", Model: "shared", ModelProviderId: "codex"));
        Exec("UPDATE sessions SET json = $j WHERE id = 's3'", ("$j", Text("SELECT json FROM sessions WHERE id = 's3'")!.Replace("\"modelProviderId\":\"codex\",", "")));

        var after = Repo.Get("s3")!;
        Assert.Equal("shared", after.Model);
        Assert.Null(after.ModelProviderId);
    }

    // ── 子代理转录 ──

    private const string Parent = "session-parent";
    private const string Child = "session-parent:sub:run-1:sub:1";
    private const string Grandchild = "session-parent:sub:run-1:sub:1:sub:2";

    private void SeedTree()
    {
        Repo.Ensure(new SessionCreate("w", Parent, Title: "父对话"));
        Repo.Ensure(new SessionCreate("w", Child, ParentSessionId: Parent));
        Repo.Ensure(new SessionCreate("w", Grandchild, ParentSessionId: Child));
    }

    [Fact]
    public void Subagent_transcripts_are_in_the_database_but_not_in_the_list()
    {
        SeedTree();
        Repo.Commit(Child, Say("m-child", "子代理的产出"));

        Assert.Equal([Parent], Repo.List("w").Select(s => s.Id));
        // ★ 成对断言:它没有被删掉,只是不列出来 —— 子代理面板和续跑都靠这两个。
        Assert.Equal(Parent, Repo.Get(Child)!.ParentSessionId);
        Assert.Equal(["m-child"], Ids(Repo.History(Child)));
    }

    [Fact]
    public void Search_does_not_hit_subagent_transcripts_in_either_fts_or_the_like_fallback()
    {
        SeedTree();
        Repo.Commit(Parent, Say("m-parent", "searchneedle 父"));
        Repo.Commit(Child, Say("m-child", "searchneedle 子"));

        Assert.Equal([Parent], Repo.Search("searchneedle").Select(h => h.SessionId));
        Assert.Equal([Parent], Repo.Search("searchneedle", "w").Select(h => h.SessionId));

        // ★ 兜底那条必须单独测:它平时跑不到(只在 FTS 对极端 Unicode 或旧库抛错时接手),漏加过滤也不会有任何用例变红。
        Exec("DROP TABLE messages_fts");

        Assert.Equal([Parent], Repo.Search("searchneedle").Select(h => h.SessionId));
        Assert.Equal([Parent], Repo.Search("searchneedle", "w").Select(h => h.SessionId));
    }

    [Fact]
    public void ListAll_counts_every_row_for_the_backup_manifest_but_export_is_top_level_only()
    {
        SeedTree();

        Assert.Equal([Parent], Repo.ListAll(topLevelOnly: true).Select(s => s.Id));
        Assert.Equal(new HashSet<string> { Parent, Child, Grandchild }, Repo.ListAll(topLevelOnly: false).Select(s => s.Id).ToHashSet());
        Assert.Equal(Db.SessionCount(), Repo.ListAll(topLevelOnly: false).Count);
    }

    [Fact]
    public void Deleting_the_parent_removes_the_whole_subtree_with_messages_runs_and_index_rows()
    {
        SeedTree();
        foreach (var id in new[] { Parent, Child, Grandchild })
        {
            Repo.Commit(id, Say($"m-{id}", $"deleteneedle {id}"));
            Repo.SetRunRecord($"run-{id}", id, "done", 1, 2);
        }

        Assert.Equal(new[] { Child, Grandchild, Parent }.Order(), Repo.Delete(Parent).Order());

        foreach (var id in new[] { Parent, Child, Grandchild })
        {
            Assert.Null(Repo.Get(id));
            Assert.Empty(Repo.History(id));
        }

        // FTS 是虚表,没有外键管得着它 —— 漏删的话搜索会一直返回幽灵结果
        Assert.Empty(Repo.Search("deleteneedle"));
        Assert.Equal(0, Count("SELECT count(*) FROM messages_fts"));
        Assert.Equal(0, Count("SELECT count(*) FROM messages_fts_rowid"));
        Assert.Equal(0, Count("SELECT count(*) FROM runs"));
        Assert.Equal(0, Count("SELECT count(*) FROM session_input_receipts"));
    }

    [Fact]
    public void Delete_cleans_draft_attachments_kv_rows_and_tombstones_imported_mappings()
    {
        Repo.Create(new SessionCreate("w", "s1"));
        Repo.Create(new SessionCreate("w", "s2"));
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, checksum, created_at, scope, status, owner_id) VALUES ('draft', NULL, NULL, '/a/draft.png', 1, NULL, 1, 'session', 'draft', 's1')");
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, checksum, created_at, scope, status, owner_id) VALUES ('other', NULL, NULL, '/a/other.png', 1, NULL, 1, 'session', 'draft', 's2')");
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, checksum, created_at, scope, status, owner_id) VALUES ('theme', NULL, NULL, '/a/theme.png', 1, NULL, 1, 'theme', 'committed', 's1')");
        Exec("INSERT INTO kv (key, json) VALUES ('session.input.s1', '{}'), ('session.widget.s1', '{}'), ('session.input.s2', '{}')");
        Exec("""
            INSERT INTO import_mappings (source_id, scope_key, entity_kind, source_item_id, target_id, sync_state, created_at, updated_at)
            VALUES ('src', '', 'session', 'item1', 's1', 'linked', 1, 1)
            """);

        // 级联会带走这些行,所以路径要在删之前取
        Assert.Equal(["/a/draft.png"], Repo.AttachmentPaths("s1"));
        Repo.Delete("s1");

        Assert.Equal(["other", "theme"], Texts("SELECT id FROM attachments ORDER BY id"));
        Assert.Equal(["session.input.s2"], Texts("SELECT key FROM kv ORDER BY key"));
        // ★ 墓碑而不是删映射行:删掉的话同步器会把用户明确删掉的会话复活
        Assert.Equal("suppressed", Text("SELECT sync_state FROM import_mappings"));
        Assert.Equal("", Text("SELECT target_id FROM import_mappings"));
    }

    private List<string> Texts(string sql)
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    [Fact]
    public void Attachment_paths_include_the_subtree_and_drafts_for_a_session_that_does_not_exist_yet()
    {
        SeedTree();
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, created_at, scope, status, owner_id) VALUES ('a1', NULL, NULL, '/p/parent.png', 1, 1, 'session', 'draft', $o)", ("$o", Parent));
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, created_at, scope, status, owner_id) VALUES ('a2', NULL, NULL, '/p/child.png', 1, 1, 'session', 'draft', $o)", ("$o", Child));
        Exec("INSERT INTO attachments (id, session_id, message_id, path, size, created_at, scope, status, owner_id) VALUES ('a3', NULL, NULL, '/p/future.png', 1, 1, 'session', 'draft', 'not-created-yet')");

        Assert.Equal(["/p/child.png", "/p/parent.png"], Repo.AttachmentPaths(Parent).Order());
        Assert.Equal(["/p/future.png"], Repo.AttachmentPaths("not-created-yet"));
    }

    // ── 作用域 ──

    [Fact]
    public void Another_account_scope_cannot_see_or_touch_the_session()
    {
        Workspace("ws-a", owner: "acct-a");
        Workspace("ws-local");
        Repo.Create(new SessionCreate("ws-local", "local-1", Title: "本地的"));
        Repo.Commit("local-1", Ask("u1", "scopeneedle"));
        SetScope("acct-a");
        Repo.Create(new SessionCreate("ws-a", "acct-1", Title: "账户 A 的"));
        Repo.Commit("acct-1", Ask("u2", "scopeneedle"));

        Assert.Equal(["acct-1"], Repo.List("ws-a").Select(s => s.Id));
        Assert.Empty(Repo.List("ws-local")); // 看不见的工作区就是空列表
        Assert.Null(Repo.Get("local-1"));
        Assert.False(Repo.ScopeVisible("local-1"));
        Assert.Empty(Repo.History("local-1"));
        Assert.Equal(0, Repo.CountMessages("local-1"));
        Assert.Null(Repo.Summary("local-1"));
        Assert.Empty(Repo.SearchSession("local-1", "scopeneedle"));
        // ★ 全局搜索只给本作用域的(收窄在 SQL 里)
        Assert.Equal(["acct-1"], Repo.Search("scopeneedle").Select(h => h.SessionId));
        Assert.Empty(Repo.Search("scopeneedle", "ws-local"));
        Assert.Empty(Repo.Delete("local-1"));
        Assert.Equal(1, Count("SELECT count(*) FROM sessions WHERE id = 'local-1'"));
        Assert.DoesNotContain(Repo.ListAll(topLevelOnly: false), s => s.Id == "local-1");
        Assert.Empty(Repo.RecentWorkspacePrompts("ws-local"));

        SetScope("local");

        Assert.Equal(["local-1"], Repo.Search("scopeneedle").Select(h => h.SessionId));
    }

    [Fact]
    public void Writing_into_another_scope_throws_instead_of_overwriting()
    {
        Workspace("ws-a", owner: "acct-a");
        Workspace("ws-local");
        Repo.Create(new SessionCreate("ws-local", "local-1", Title: "本地的"));
        SetScope("acct-a");

        // 撞上别的作用域已有的 id:拒绝,不顶替
        Assert.Throws<StorageException>(() => Repo.Create(new SessionCreate("ws-a", "local-1", Title: "抢占")));
        // 往别的作用域的工作区里建
        Assert.Throws<StorageException>(() => Repo.Create(new SessionCreate("ws-local", "x")));
        // 别的账户下没有登记过的工作区 id,只有 local 能用
        Assert.Throws<StorageException>(() => Repo.Create(new SessionCreate("never-registered", "y")));
        Assert.Throws<StorageException>(() => Repo.Create(new SessionCreate("", "z")));
        Assert.Throws<StorageException>(() => Repo.Commit("local-1", Ask("u", "x")));
        Assert.Throws<StorageException>(() => Repo.ReplaceHistory("local-1", []));
        Assert.Throws<StorageException>(() => Repo.Rename("local-1", "x"));
        Assert.Throws<StorageException>(() => Repo.SetRunRecord("r", "local-1", "done", 1));

        SetScope("local");
        Assert.Equal("本地的", Repo.Get("local-1")!.Title);
        // local 作用域下没登记过的工作区 id(包括空串)可用:它们就是本地测试和界面先生成 id 的兜底
        Assert.Equal("never-registered", Repo.Create(new SessionCreate("never-registered", "y")).WorkspaceId);
        Assert.Equal("", Repo.Create(new SessionCreate("", "z")).WorkspaceId);
    }

    [Fact]
    public void A_run_record_can_be_read_back_and_listed_by_session_the_runs_registry_uses_both()
    {
        var a = Repo.Create(new SessionCreate("ws", "a"));
        var b = Repo.Create(new SessionCreate("ws", "b"));
        Repo.SetRunRecord("r1", a.Id, "running", 100);
        Repo.SetRunRecord("r2", a.Id, "done", 200, 250);
        Repo.SetRunRecord("r3", b.Id, "aborted", 300, 310);
        Repo.SetRunRecord("r1", a.Id, "error", 100, 120); // 状态与结束时间可更新,开始时间不变

        Assert.Equal(new RunRecordRow("r1", "a", "error", 100, 120), Repo.GetRunRecord("r1"));
        Assert.Equal(new RunRecordRow("r3", "b", "aborted", 300, 310), Repo.GetRunRecord("r3"));
        Assert.Null(Repo.GetRunRecord("nope"));
        Assert.Equal(["r1", "r2"], Repo.RunIds("a"));
        Assert.Equal(["r3"], Repo.RunIds("b"));
        Assert.Empty(Repo.RunIds("missing"));
    }

    [Fact]
    public void A_scope_switch_is_read_fresh_every_time()
    {
        Workspace("ws-a", owner: "acct-a");
        SetScope("acct-a");
        Repo.Create(new SessionCreate("ws-a", "a1"));

        SetScope("local");
        Assert.Null(Repo.Get("a1"));
        SetScope("acct-a");
        Assert.NotNull(Repo.Get("a1"));
        SetScope("acct-b");
        Assert.Null(Repo.Get("a1"));
    }

    // ── 复制与分支 ──

    private void SeedTwoTurns()
    {
        Repo.Create(new SessionCreate("ws", "src", Title: "Source", Model: "alias", ModelProviderId: "provider-b", Mode: "code"));
        Repo.ReplaceHistory("src", [Ask("u1", "first", 1), Say("a1", "one", 2), Ask("u2", "second", 3), Say("a2", "two", 4)]);
    }

    [Fact]
    public void Branch_copies_only_up_to_the_chosen_turn_with_fresh_ids_and_the_source_provider()
    {
        SeedTwoTurns();

        var branched = Repo.Branch("src", "u1", "Source · branch");

        Assert.Equal(("Source · branch", "alias", "provider-b", "code"), (branched.Title, branched.Model, branched.ModelProviderId, branched.Mode));
        var history = Repo.History(branched.Id);
        Assert.Equal([TextPart("first"), TextPart("one")], history.Select(m => m.PartsJson[1..^1]));
        Assert.DoesNotContain(history, m => m.Id is "u1" or "a1");
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", history[0].Id);
        Assert.Equal(4, Repo.History("src").Count);
        // ★ 分支是独立的顶层会话(与设计稿原先的「parent_session_id 指回原会话」不同):否则它会从列表里消失
        Assert.Null(branched.ParentSessionId);
        Assert.Contains(branched.Id, Repo.List("ws").Select(s => s.Id));
    }

    [Fact]
    public void Branch_carries_the_tool_results_that_follow_the_prompt()
    {
        Repo.Create(new SessionCreate("ws", "src"));
        Repo.ReplaceHistory("src",
        [
            Ask("u1", "first", 1),
            ToolCall("a1", "c1", "Read", "{}", 2),
            ToolResult("r1", "c1", "ok", 3),
            Say("a1b", "done", 4),
            Ask("u2", "second", 5),
            Say("a2", "reply", 6),
        ]);

        Assert.Equal(4, Repo.History(Repo.Branch("src", "u1", "b").Id).Count);
        Assert.Equal(6, Repo.History(Repo.Branch("src", "u2", "b").Id).Count);
    }

    [Fact]
    public void Branch_rejects_an_unknown_turn_without_leaving_an_empty_session_behind()
    {
        SeedTwoTurns();

        Assert.Throws<StorageException>(() => Repo.Branch("src", "a1", "b")); // 不是用户提问
        Assert.Throws<StorageException>(() => Repo.Branch("src", "missing", "b"));

        Assert.Equal(["src"], Repo.List("ws").Select(s => s.Id));
    }

    [Fact]
    public void Duplicate_keeps_the_whole_transcript_and_lets_the_caller_rewrite_each_message()
    {
        SeedTwoTurns();
        Repo.Create(new SessionCreate("ws", "ext", Title: "extract", SkillSourceSessionId: "src"));
        Repo.ReplaceHistory("ext", [Ask("e1", "x", 1)]);

        var copy = Repo.Duplicate("src", "Source copy", m => m with { PartsJson = m.PartsJson.Replace("first", "FIRST") });

        Assert.Equal("provider-b", copy.ModelProviderId);
        var history = Repo.History(copy.Id);
        Assert.Equal(4, history.Count);
        Assert.Contains("FIRST", history[0].PartsJson);
        Assert.Contains("first", Repo.History("src")[0].PartsJson);
        Assert.Equal("src", Repo.Duplicate("ext", "ext copy").SkillSourceSessionId); // 提炼会话复制出来仍是提炼会话
        Assert.Throws<StorageException>(() => Repo.Duplicate("missing", "x"));
        // 克隆走 replaceHistory:不带 run 归属、不留消费回执
        Assert.Equal(0, Count("SELECT count(*) FROM session_input_receipts WHERE session_id = $s", ("$s", copy.Id)));
    }

    [Fact]
    public void A_failed_clone_rolls_back_completely()
    {
        SeedTwoTurns();
        var before = Count("SELECT count(*) FROM sessions");

        Assert.Throws<InvalidOperationException>(() => Repo.Duplicate("src", "boom", _ => throw new InvalidOperationException("rewrite failed")));

        Assert.Equal(before, Count("SELECT count(*) FROM sessions"));
    }

    // ── 升级路径 ──

    /// <summary>2.x 第 10 条迁移:把已经泄漏进侧边栏的存量脏行清掉(升级一个第 9 版的真实库)。</summary>
    [Fact]
    public void Migration_10_removes_leaked_subagent_rows_with_their_messages_and_index_rows()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(Root, AgentDatabase.FileName));
        foreach (var f in Directory.GetFiles(Root, "nextcowork.db-*"))
        {
            File.Delete(f);
        }

        var path = Path.Combine(Root, AgentDatabase.FileName);
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Raw(raw, "CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");
            foreach (var m in AgentDatabase.Migrations.Where(m => m.Version <= 9))
            {
                Raw(raw, m.Sql);
                Raw(raw, $"INSERT INTO migrations VALUES ({m.Version}, '{m.Name}', 0)");
            }

            // 第 9 版没有 parent_session_id,子会话和顶层长得一模一样 —— 这正是那个 bug。
            foreach (var id in new[] { Parent, Child, Grandchild })
            {
                Raw(raw, $$"""
                    INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, status, archived, favorited, created_at, updated_at, json)
                    VALUES ('{{id}}', 'w', '新对话', '', 'normal', 'auto', '', 'idle', 0, 0, 1, 1, '{}')
                    """);
                Raw(raw, $$"""INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at) VALUES ('m-{{id}}', '{{id}}', 0, 'assistant', '[{"type":"text","text":"leakneedle"}]', 1, 1)""");
                Raw(raw, $"INSERT INTO messages_fts (message_id, session_id, title, content) VALUES ('m-{id}', '{id}', '新对话', 'leakneedle')");
            }
        }

        Db = AgentDatabase.Open(Root);
        Repo = new SessionRepository(Db);
        Inputs = new SessionInputStore(Db);

        Assert.Equal([Parent], Repo.List("w").Select(s => s.Id));
        Assert.NotNull(Repo.Get(Parent));
        Assert.Null(Repo.Get(Child));
        Assert.Null(Repo.Get(Grandchild));
        Assert.Empty(Repo.History(Child));
        Assert.Equal([Parent], Repo.Search("leakneedle").Select(h => h.SessionId));
        Assert.Equal("code", Repo.Get(Parent)!.Mode);
    }

    private static void Raw(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
