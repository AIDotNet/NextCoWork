using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 改动审查(<c>review.*</c>):真实 Agent + <see cref="DeviceClient"/> + 按会话分派脚本的假上游 + 真实 Write / Read 工具 / 权限 / SQLite。
/// Run 结束时设备把工具采集到的改动落库(顶层 Run 聚合,子代理的改动并进父 Run);变更集 / 文件 diff 的读取、撤销前预检、撤销(force)、重做、
/// 角色(viewer 只读、member 可撤销 / 重做)、库里被篡改的越界路径不被碰、幂等重放。
/// </summary>
public sealed class ReviewTests : IAsyncLifetime
{
    private readonly ScriptedLlm _llm = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;
    private SessionDto _session = null!;

    public ReviewTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Models = new TwoModels(_llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
        _session = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId, Model: "sonnet"));
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private string Abs(string relative) => Path.Combine(_project.Path, relative);

    private static string ReadJson(string path) => new JsonObject { ["file_path"] = path }.ToJsonString();

    /// <summary>一个 Run:读 existing.txt 再覆盖它(modified),并新建 new.txt(created)。</summary>
    private async Task<string> RunWritingAsync(string runId = "p1")
    {
        File.WriteAllText(Abs("existing.txt"), "v0\n");
        _llm.On(_session.Id,
            S.Call("r1", "Read", ReadJson(Abs("existing.txt"))),
            S.Call("w1", "Write", ConversationRig.WriteJson(Abs("existing.txt"), "v1\n")),
            S.Call("w2", "Write", ConversationRig.WriteJson(Abs("new.txt"), "hello\nworld\n")),
            S.Say("done"));
        await StartAsync(_owner, _session.Id, runId);
        await (await RunWatch.OpenAsync(_owner, runId)).SettledAsync();
        return runId;
    }

    private static Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string runId) =>
        client.CallAsync(
            ConversationMethods.RunsStart,
            new RunsStartParams(runId, sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, "write some files")], new SendOptionsDto("sonnet", PermissionMode: "full")),
            idem: $"start:{runId}");

    private static Task<ReviewMutationDto> UndoAsync(DeviceClient client, string runId, bool force = false, string? idem = null) =>
        client.CallAsync(ReviewMethods.Undo, new ReviewUndoParams(runId, force), idem: idem ?? $"undo:{Guid.NewGuid():N}");

    private static Task<ReviewMutationDto> RedoAsync(DeviceClient client, string runId) =>
        client.CallAsync(ReviewMethods.Redo, new ReviewRunParams(runId), idem: $"redo:{Guid.NewGuid():N}");

    // ───────────────────────── 变更集 ─────────────────────────

    [Fact]
    public async Task A_finished_run_has_a_change_set_with_the_created_and_the_modified_file_and_their_diffs()
    {
        var runId = await RunWritingAsync();

        var set = (await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId))).ChangeSet!;

        Assert.Equal((runId, _session.Id, _project.WorkspaceId, ReviewStates.Applied, 2), (set.RunId, set.SessionId, set.WorkspaceId, set.State, set.FileCount));
        Assert.Equal(["existing.txt", "new.txt"], set.Files.Select(f => f.Path)); // 项目相对路径、按路径排序
        Assert.Equal((ReviewChangeKinds.Modified, 1, 1), (set.Files[0].ChangeKind, set.Files[0].Additions, set.Files[0].Deletions));
        Assert.Equal((ReviewChangeKinds.Created, 2, 0), (set.Files[1].ChangeKind, set.Files[1].Additions, set.Files[1].Deletions));
        Assert.All(set.Files, f => Assert.True(f.InWorkspace && !f.Oversize));
        Assert.Equal((3, 1), (set.Additions, set.Deletions));

        var modified = (await _owner.CallAsync(ReviewMethods.FileDiff, new ReviewFileParams(runId, "existing.txt"))).Diff!;
        Assert.Equal(("v0\n", "v1\n"), (modified.Before, modified.After));
        var created = (await _owner.CallAsync(ReviewMethods.FileDiff, new ReviewFileParams(runId, "new.txt"))).Diff!;
        Assert.Equal((ReviewChangeKinds.Created, "", "hello\nworld\n"), (created.ChangeKind, created.Before, created.After));

        // 按会话列出;没有的是 null / 空,不是错误
        Assert.Equal(runId, Assert.Single((await _owner.CallAsync(ReviewMethods.List, new ReviewSessionParams(_session.Id))).ChangeSets).RunId);
        Assert.Null((await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams("nope"))).ChangeSet);
        Assert.Null((await _owner.CallAsync(ReviewMethods.FileDiff, new ReviewFileParams(runId, "nope.txt"))).Diff);
        Assert.Empty((await _owner.CallAsync(ReviewMethods.List, new ReviewSessionParams("nope"))).ChangeSets);
    }

    [Fact]
    public async Task A_run_that_changed_nothing_has_no_change_set()
    {
        _llm.On(_session.Id, S.Say("just talking"));
        await StartAsync(_owner, _session.Id, "p0");
        await (await RunWatch.OpenAsync(_owner, "p0")).SettledAsync();

        Assert.Null((await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams("p0"))).ChangeSet);
    }

    [Fact]
    public async Task A_subagents_writes_roll_up_into_the_parent_runs_change_set()
    {
        var child = S.ChildSession(_session.Id, "p1");
        _llm.On(_session.Id,
                S.Call("w1", "Write", ConversationRig.WriteJson(Abs("parent.txt"), "from parent\n")),
                S.Call("t1", "Task", S.TaskArgs("write sub.txt")),
                S.Say("all done"))
            .On(child, S.Call("c1", "Write", ConversationRig.WriteJson(Abs("sub.txt"), "from child\n")), S.Say("child done"));

        await StartAsync(_owner, _session.Id, "p1");
        await (await RunWatch.OpenAsync(_owner, "p1")).SettledAsync();

        var set = (await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams("p1"))).ChangeSet!;
        Assert.Equal(["parent.txt", "sub.txt"], set.Files.Select(f => f.Path));
        Assert.Equal(_session.Id, set.SessionId); // 记在父会话名下,不是子会话
        // 子 Run 自己的 id 下没有单独的一份
        Assert.Null((await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(S.ChildRun("p1")))).ChangeSet);
    }

    // ───────────────────────── 撤销 / 重做 ─────────────────────────

    [Fact]
    public async Task Undo_restores_the_before_state_and_redo_writes_the_after_state_back()
    {
        var runId = await RunWritingAsync();
        Assert.Empty((await _owner.CallAsync(ReviewMethods.PrecheckUndo, new ReviewRunParams(runId))).Conflicts);

        var undone = await UndoAsync(_owner, runId);

        Assert.Equal(ReviewStates.Reverted, undone.State);
        Assert.All(undone.Files, f => Assert.Equal(ReviewFileStatuses.Ok, f.Status));
        Assert.Equal("v0\n", File.ReadAllText(Abs("existing.txt")));
        Assert.False(File.Exists(Abs("new.txt"))); // 撤销新建 = 删除
        Assert.Equal(ReviewStates.Reverted, (await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId))).ChangeSet!.State);

        var redone = await RedoAsync(_owner, runId);

        Assert.Equal(ReviewStates.Applied, redone.State);
        Assert.Equal("v1\n", File.ReadAllText(Abs("existing.txt")));
        Assert.Equal("hello\nworld\n", File.ReadAllText(Abs("new.txt")));
        Assert.Equal(ReviewStates.Applied, (await _owner.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId))).ChangeSet!.State);
    }

    [Fact]
    public async Task A_hand_edit_after_the_run_is_reported_by_precheck_and_skipped_by_undo_until_forced()
    {
        var runId = await RunWritingAsync();
        File.WriteAllText(Abs("existing.txt"), "my own change\n");

        Assert.Equal(["existing.txt"], (await _owner.CallAsync(ReviewMethods.PrecheckUndo, new ReviewRunParams(runId))).Conflicts);

        var plain = await UndoAsync(_owner, runId);
        Assert.Equal(ReviewFileStatuses.Conflict, plain.Files.Single(f => f.Path == "existing.txt").Status);
        Assert.Equal(ReviewFileStatuses.Ok, plain.Files.Single(f => f.Path == "new.txt").Status);
        Assert.Equal("my own change\n", File.ReadAllText(Abs("existing.txt")));
        Assert.False(File.Exists(Abs("new.txt")));

        var forced = await UndoAsync(_owner, runId, force: true);
        Assert.Equal(ReviewFileStatuses.Ok, forced.Files.Single(f => f.Path == "existing.txt").Status);
        Assert.Equal("v0\n", File.ReadAllText(Abs("existing.txt")));
    }

    [Fact]
    public async Task Undo_is_idempotent_for_the_same_key_and_a_missing_run_is_a_no_op()
    {
        var runId = await RunWritingAsync();

        var first = await UndoAsync(_owner, runId, idem: "same-key");
        File.WriteAllText(Abs("existing.txt"), "touched in between");
        var again = await UndoAsync(_owner, runId, idem: "same-key");

        // 重发拿到同一个结果,没有再执行一次
        Assert.Equal(first.State, again.State);
        Assert.Equal(first.Files.Select(f => (f.Path, f.Status)), again.Files.Select(f => (f.Path, f.Status)));
        Assert.Equal("touched in between", File.ReadAllText(Abs("existing.txt")));

        var none = await UndoAsync(_owner, "no-such-run");
        Assert.Empty(none.Files);
        Assert.Equal(ReviewStates.Applied, none.State);
        Assert.Empty((await _owner.CallAsync(ReviewMethods.PrecheckUndo, new ReviewRunParams("no-such-run"))).Conflicts);
    }

    [Fact]
    public async Task Recorded_paths_that_were_tampered_to_point_outside_the_project_are_never_touched()
    {
        var runId = await RunWritingAsync();
        var victim = Path.Combine(Path.GetTempPath(), $"ncw-victim-{Guid.NewGuid():N}.txt");
        File.WriteAllText(victim, "keep me");
        try
        {
            // 库里被人塞了两条「在项目内」的记录,实际指向项目外(一条 .. 逃逸,一条绝对路径)
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, AgentDatabase.FileName)};Pooling=False"))
            {
                connection.Open();
                foreach (var path in new[] { "../" + Path.GetFileName(victim), victim })
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO file_snapshots (run_id, file_path, source_run_id, abs_path, change_kind, before_content, after_content, before_hash, after_hash, additions, deletions, oversize, in_workspace, created_at)
                        VALUES ($run, $path, $run, $path, 'created', NULL, 'keep me', NULL, $hash, 1, 0, 0, 1, 1)
                        """;
                    command.Parameters.AddWithValue("$run", runId);
                    command.Parameters.AddWithValue("$path", path);
                    command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("keep me"u8.ToArray())));
                    command.ExecuteNonQuery();
                }
            }

            var undone = await UndoAsync(_owner, runId, force: true);

            Assert.Equal("keep me", File.ReadAllText(victim));
            Assert.Equal(2, undone.Files.Count(f => f.Status == ReviewFileStatuses.Skipped && f.Reason == "outsideWorkspace"));
            Assert.Equal(2, undone.Files.Count(f => f.Status == ReviewFileStatuses.Ok)); // 正常的两个文件照常撤销
        }
        finally
        {
            File.Delete(victim);
        }
    }

    // ───────────────────────── 角色 ─────────────────────────

    private async Task<DeviceClient> ConnectRemoteAsync(string name, string role)
    {
        var status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        var port = status.Port!.Value;
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<string> CodeOf<T>(Func<Task<T>> call) => (await Assert.ThrowsAsync<ProtocolException>(call)).Code;

    [Fact]
    public async Task A_viewer_can_read_the_review_but_not_undo_or_redo_and_a_member_can()
    {
        var runId = await RunWritingAsync();
        await using var viewer = await ConnectRemoteAsync("viewer-1", "viewer");

        Assert.Equal(2, (await viewer.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId))).ChangeSet!.FileCount);
        Assert.Single((await viewer.CallAsync(ReviewMethods.List, new ReviewSessionParams(_session.Id))).ChangeSets);
        Assert.Equal("v1\n", (await viewer.CallAsync(ReviewMethods.FileDiff, new ReviewFileParams(runId, "existing.txt"))).Diff!.After);
        Assert.Empty((await viewer.CallAsync(ReviewMethods.PrecheckUndo, new ReviewRunParams(runId))).Conflicts);

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => UndoAsync(viewer, runId)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => RedoAsync(viewer, runId)));
        Assert.Equal("v1\n", File.ReadAllText(Abs("existing.txt")));

        await using var member = await ConnectRemoteAsync("member-1", "member");
        Assert.Equal(ReviewStates.Reverted, (await UndoAsync(member, runId)).State);
        Assert.Equal("v0\n", File.ReadAllText(Abs("existing.txt")));
        Assert.Equal(ReviewStates.Applied, (await RedoAsync(member, runId)).State);
        Assert.Equal("v1\n", File.ReadAllText(Abs("existing.txt")));
    }
}
