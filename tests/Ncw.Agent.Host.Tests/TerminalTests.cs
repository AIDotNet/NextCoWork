using System.Diagnostics;
using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Storage;
using Ncw.Agent.Terminal;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 终端(06 §9.4):真实 Agent + 真实 PTY(Windows 10 1809 以前没有 PTY 就跳过)。方法、<c>terminal/&lt;id&gt;</c> 主题、角色、错误映射、游标续传、
/// 控制端离开、项目删除、设备停止;SSH 的逐个批准用「声称自己是 SSH、内部转发给本机环境」的包装环境测(没有真 SSH 主机可用)。
/// </summary>
public sealed class TerminalTests
{
    private static string Utf8Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string TextOf(Watch<TerminalSnapshotDto> watch) =>
        (watch.HasSnapshot ? Utf8Text(watch.Snapshot.Data) : "")
        + string.Concat(watch.Decoded<TerminalDataDto>(TerminalEvents.Data).Select(d => Utf8Text(d.Data)));

    private static byte[] Keys(string text) => Encoding.UTF8.GetBytes(text);

    private static TerminalErrorDto? ErrorOf(ProtocolException e) =>
        e.Details is { Length: > 0 } bytes ? FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<TerminalErrorDto>()) : null;

    private static async Task<(string Code, string? Key)> FailureOf<T>(Func<Task<T>> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return (e.Code, ErrorOf(e)?.Key);
    }

    private static Task<TerminalDto> CreateAsync(DeviceClient client, ProjectDto project, string? id = null, string? approval = null) =>
        client.CallAsync(TerminalMethods.Create, new TerminalsCreateParams(project.WorkspaceId, 100, 30, id, null, approval));

    private static void RequirePty() => Assert.SkipUnless(LocalTerminals.IsSupported, "no local PTY on this machine");

    /// <summary>真实 Agent + 本机环境(带 PTY)+ 一个「声称自己是 SSH」的项目。</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly FakeSshEnvironments _envs;
        private readonly string _remoteDir = Path.Combine(Path.GetTempPath(), $"ncw-term-remote-{Guid.NewGuid():N}");
        private bool _disposed;

        public Rig(TerminalManagerOptions? terminals)
        {
            Directory.CreateDirectory(_remoteDir);
            _envs = new FakeSshEnvironments();
            Conversation = new ConversationRig(llm => new AgentServices
            {
                Models = new FakeModels(llm),
                Http = new NoNetwork(),
                CatalogDelayer = new NeverDelay(),
                Delayer = new NoDelay(),
                Loop = new AgentLoopOptions { ResumeDelays = [] },
                Environments = _envs,
                Terminals = terminals,
            });
        }

        public ConversationRig Conversation { get; }

        public DeviceClient Owner { get; private set; } = null!;

        public ProjectDto Local { get; private set; } = null!;

        public ProjectDto Remote { get; private set; } = null!;

        private int _port;

        public static async Task<Rig> StartAsync(TerminalManagerOptions? terminals = null)
        {
            var rig = new Rig(terminals);
            await rig.Conversation.InitializeAsync();
            rig.Owner = await rig.Conversation.ConnectAsync("owner");
            rig.Local = await rig.Conversation.AddProjectAsync(rig.Owner);
            rig.Remote = await rig.Owner.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(rig._remoteDir, FakeSshEnvironments.RemoteProjectName));
            return rig;
        }

        /// <summary>经真实的局域网承载(TLS 1.3 + 配对)连一个指定角色的控制端;同一个身份可以重连(同一个控制端)。</summary>
        public async Task<(DeviceClient Client, Func<Task<DeviceClient>> Reconnect)> ConnectRemoteAsync(string name, string role)
        {
            if (_port == 0)
            {
                _port = (await Owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
            }

            var identity = PeerIdentity.Create(name);
            var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, Conversation.Host.Remote.PairingCode!, name);
            await Owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));

            async Task<DeviceClient> Connect()
            {
                var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
                var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
                Assert.Equal(role, client.Welcome.Role);
                return client;
            }

            return (await Connect(), Connect);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Conversation.DisposeAsync();
            await _envs.DisposeAsync();
            try { Directory.Delete(_remoteDir, recursive: true); } catch (IOException) { }
        }
    }

    // ───────────────────────── 全流程 ─────────────────────────

    [Fact]
    public async Task A_terminal_runs_a_command_streams_its_output_resizes_and_is_killed_with_a_contiguous_sequence()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync();
        var owner = rig.Owner;
        var terminal = await CreateAsync(owner, rig.Local);
        Assert.Equal(("running", "local", rig.Local.WorkspaceId), (terminal.Status, terminal.Environment.Kind, terminal.ProjectId));
        Assert.Equal((100, 30), (terminal.Columns, terminal.Rows));
        Assert.Equal("local", terminal.Environment.Environment);

        var watch = await Watch<TerminalSnapshotDto>.OpenAsync(owner, TerminalMethods.Topic(terminal.Id));
        await watch.WaitAsync(() => watch.HasSnapshot, "terminal snapshot");
        Assert.Equal(terminal.Id, watch.Snapshot.Terminal.Id);

        // 输入 → 输出(标记在输入行里是 $((…)) 的字面量,只有真的执行了才会出现 ncw42)
        await owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((20+22))\n")));
        await watch.WaitAsync(() => TextOf(watch).Contains("ncw42", StringComparison.Ordinal), "echo output");

        // 同一个幂等键重发不重复写
        await owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((1+1))x\n")), idem: "same-key");
        await watch.WaitAsync(() => TextOf(watch).Contains("ncw2x", StringComparison.Ordinal), "second echo");
        await owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((1+1))x\n")), idem: "same-key");
        await Task.Delay(400);
        Assert.Equal(1, CountOf(TextOf(watch), "ncw2x\r"));

        await owner.CallAsync(TerminalMethods.Resize, new TerminalsResizeParams(terminal.Id, 120, 40));
        var listed = (await owner.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals;
        Assert.Equal((120, 40), (listed.Single().Columns, listed.Single().Rows));

        await owner.CallAsync(TerminalMethods.Kill, new TerminalRefParams(terminal.Id));
        await watch.WaitAsync(() => watch.Events.Any(e => e.Type == TerminalEvents.Exit), "exit event");

        // 序号严格连续,退出事件也占一个
        var seqs = new List<long> { watch.SnapshotSeq };
        seqs.AddRange(watch.Events.Select(e => e.Seq));
        Assert.Equal(Enumerable.Range((int)seqs[0], seqs.Count).Select(static i => (long)i), seqs);
        Assert.Equal(TerminalEvents.Exit, watch.Events[^1].Type);
        var exit = watch.Decoded<TerminalExitDto>(TerminalEvents.Exit).Single();
        Assert.Equal("killed", exit.Reason);

        var buffer = await owner.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminal.Id));
        Assert.Contains("ncw42", Utf8Text(buffer.Data));
        Assert.Equal(watch.Events[^1].Seq, buffer.Seq);

        // 退出之后:元数据是已退出,再写 conflict
        var after = (await owner.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals.Single();
        Assert.Equal(("exited", "killed"), (after.Status, after.ExitReason));
        Assert.Equal((ErrorCodes.Conflict, TerminalErrorKeys.Exited), await FailureOf(() => owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("x")))));

        // 页签关闭:从表里移除
        await owner.CallAsync(TerminalMethods.Close, new TerminalRefParams(terminal.Id));
        Assert.Empty((await owner.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ───────────────────────── 错误 ─────────────────────────

    [Fact]
    public async Task Errors_map_to_coarse_codes_with_stable_keys()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync(new TerminalManagerOptions { MaxPerScope = 2 });
        var owner = rig.Owner;

        Assert.Equal((ErrorCodes.NotFound, TerminalErrorKeys.NotFound), await FailureOf(() => owner.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams("nope"))));
        Assert.Equal((ErrorCodes.NotFound, TerminalErrorKeys.NotFound), await FailureOf(() => owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams("nope", Keys("x")))));
        var missing = await Assert.ThrowsAsync<ProtocolException>(() => Watch<TerminalSnapshotDto>.OpenAsync(owner, TerminalMethods.Topic("nope")));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => owner.CallAsync(TerminalMethods.List, new TerminalsListParams("no-such-project")))).Code);

        var a = await CreateAsync(owner, rig.Local);
        _ = await CreateAsync(owner, rig.Local);

        // 输入超限
        Assert.Equal((ErrorCodes.TooLarge, TerminalErrorKeys.TooLarge), await FailureOf(() => owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(a.Id, new byte[64 * 1024 + 1]))));
        Assert.Equal((ErrorCodes.InvalidRequest, TerminalErrorKeys.InvalidArgument), await FailureOf(() => owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(a.Id, []))));

        // 每个项目的终端数上限(这里是 2)
        Assert.Equal((ErrorCodes.Busy, TerminalErrorKeys.LimitReached), await FailureOf(() => CreateAsync(owner, rig.Local)));

        // 目录必须在项目根里
        await owner.CallAsync(TerminalMethods.Close, new TerminalRefParams(a.Id));
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() =>
            owner.CallAsync(TerminalMethods.Create, new TerminalsCreateParams(rig.Local.WorkspaceId, 80, 24, null, "../escape")))).Code);
    }

    // ───────────────────────── 角色 ─────────────────────────

    [Fact]
    public async Task A_viewer_can_look_but_not_touch_and_a_member_can_use_local_terminals()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync();
        var terminal = await CreateAsync(rig.Owner, rig.Local);

        var (viewer, _) = await rig.ConnectRemoteAsync("viewer-1", "viewer");
        await using (viewer)
        {
            // 看:列表、缓冲、订阅主题
            Assert.Single((await viewer.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals);
            Assert.Equal(terminal.Id, (await viewer.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminal.Id))).Id);
            var watch = await Watch<TerminalSnapshotDto>.OpenAsync(viewer, TerminalMethods.Topic(terminal.Id));
            await watch.WaitAsync(() => watch.HasSnapshot, "viewer snapshot");

            // 改:每个方法都是 forbidden(整个方法被拒,不是方法里的某一步)
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => CreateAsync(viewer, rig.Local))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(rig.Local.WorkspaceId, 80, 24)))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams("i", true)))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("x"))))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Resize, new TerminalsResizeParams(terminal.Id, 90, 30)))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Kill, new TerminalRefParams(terminal.Id)))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(TerminalMethods.Close, new TerminalRefParams(terminal.Id)))).Code);
        }

        var (member, _) = await rig.ConnectRemoteAsync("member-1", "member");
        await using (member)
        {
            // 本机终端属于设备:member 看得见、写得进、杀得掉
            var watch = await Watch<TerminalSnapshotDto>.OpenAsync(member, TerminalMethods.Topic(terminal.Id));
            await member.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((3+4))\n")));
            await watch.WaitAsync(() => TextOf(watch).Contains("ncw7", StringComparison.Ordinal), "member output");
            var mine = await CreateAsync(member, rig.Local);
            Assert.Equal(2, (await member.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals.Length);
            await member.CallAsync(TerminalMethods.Kill, new TerminalRefParams(mine.Id));
            await member.CallAsync(TerminalMethods.Close, new TerminalRefParams(mine.Id));
        }
    }

    // ───────────────────────── 断线续传 ─────────────────────────

    [Fact]
    public async Task A_reconnecting_controller_resumes_from_its_cursor_and_gets_a_snapshot_when_the_cursor_is_gone()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync(new TerminalManagerOptions { BufferBytes = 2000 });
        var terminal = await CreateAsync(rig.Owner, rig.Local);
        var first = await rig.Conversation.ConnectAsync("ctrl-1");
        var watch = await Watch<TerminalSnapshotDto>.OpenAsync(first, TerminalMethods.Topic(terminal.Id));
        await watch.WaitAsync(() => watch.HasSnapshot, "snapshot");
        await first.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((10+1))\n")));
        await watch.WaitAsync(() => TextOf(watch).Contains("ncw11", StringComparison.Ordinal), "first output");
        var cursor = watch.Events[^1].Seq;
        var before = TextOf(watch);
        await first.DisposeAsync(); // 控制端掉线

        // 掉线期间终端继续产出
        await rig.Owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("echo ncw$((10+2))\n")));
        await Wait.UntilAsync(async () => Utf8Text((await rig.Owner.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminal.Id))).Data).Contains("ncw12", StringComparison.Ordinal), "missed output");

        // 带游标重连:不发快照,只补发错过的事件;序号紧接着游标、不重复
        var second = await rig.Conversation.ConnectAsync("ctrl-1");
        var resumed = await Watch<TerminalSnapshotDto>.OpenAsync(second, TerminalMethods.Topic(terminal.Id), cursor);
        await resumed.WaitAsync(() => TextOf(resumed).Contains("ncw12", StringComparison.Ordinal), "replayed output");
        Assert.False(resumed.HasSnapshot);
        Assert.Equal(cursor + 1, resumed.Events[0].Seq);
        Assert.Equal(resumed.Events.Select(e => e.Seq).Order(), resumed.Events.Select(e => e.Seq));
        Assert.DoesNotContain("ncw11\r", TextOf(resumed)); // 已经收到的不重发
        Assert.Contains("ncw11", before);

        // 游标已经被日志挤出去(缓冲只有 2000 字节):给整个快照
        await rig.Owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys("yes ncwfill | head -n 1000\n")));
        await Wait.UntilAsync(async () => (await rig.Owner.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminal.Id))).Truncated, "buffer overflow");
        var third = await rig.Conversation.ConnectAsync("ctrl-2");
        var old = await Watch<TerminalSnapshotDto>.OpenAsync(third, TerminalMethods.Topic(terminal.Id), cursor);
        await old.WaitAsync(() => old.HasSnapshot, "snapshot after a lost cursor");
        Assert.True(old.Snapshot.Truncated);
        Assert.Equal(terminal.Id, old.Snapshot.Terminal.Id);
    }

    // ───────────────────────── 生命周期 ─────────────────────────

    [Fact]
    public async Task Removing_the_project_closes_its_terminals()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync();
        var terminal = await CreateAsync(rig.Owner, rig.Local);
        await rig.Owner.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams(rig.Local.WorkspaceId));
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => rig.Owner.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminal.Id)))).Code);
    }

    [Fact]
    public async Task Stopping_the_device_leaves_no_orphan_shell()
    {
        RequirePty();
        var rig = await Rig.StartAsync();
        var pidFile = Path.Combine(rig.Conversation.WorkspaceDir, "shell.pid");
        var terminal = await CreateAsync(rig.Owner, rig.Local);
        await rig.Owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys($"echo $$ > '{pidFile}'\n")));
        await Wait.UntilAsync(() => Task.FromResult(File.Exists(pidFile) && File.ReadAllText(pidFile).EndsWith('\n')), "pid file");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.False(HasExited(pid));

        await rig.DisposeAsync();
        await Wait.UntilAsync(() => Task.FromResult(HasExited(pid)), "shell process gone", 15_000);
    }

    /// <summary>
    /// 回归:停机时控制端还连着(会话在等下一帧),取消令牌让会话以 <see cref="OperationCanceledException"/> 收场;
    /// 以前 <c>RunAsync</c> 的收尾里 <c>Task.WhenAll(会话)</c> 因此重新抛出,「终端 / Run / 后台 Shell / 库」的整条停机链被跳过,shell 成了孤儿。
    /// 这里不先断开控制端,保证每次都走这条路径(不依赖时序)。
    /// </summary>
    [Fact]
    public async Task Stopping_the_device_with_a_controller_still_connected_leaves_no_orphan_shell()
    {
        RequirePty();
        var rig = await Rig.StartAsync();
        var pidFile = Path.Combine(rig.Conversation.WorkspaceDir, "shell.pid");
        var terminal = await CreateAsync(rig.Owner, rig.Local);
        await rig.Owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Keys($"echo $$ > '{pidFile}'\n")));
        await Wait.UntilAsync(() => Task.FromResult(File.Exists(pidFile) && File.ReadAllText(pidFile).EndsWith('\n')), "pid file");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.False(HasExited(pid));

        await rig.Conversation.StopWhileConnectedAsync();
        Assert.True(HasExited(pid), "the shell must already be gone when RunAsync returns");
        await rig.DisposeAsync();
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // ───────────────────────── SSH:逐个批准 ─────────────────────────

    [Fact]
    public async Task A_remote_terminal_needs_a_per_terminal_approval_that_only_its_own_controller_can_give_or_use()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync();
        var (alice, reconnectAlice) = await rig.ConnectRemoteAsync("alice", "member");
        var (bob, _) = await rig.ConnectRemoteAsync("bob", "member");
        await using var _b = bob;

        var prepared = await alice.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(rig.Remote.WorkspaceId, 80, 24, "ssh-term"));
        Assert.Null(prepared.Terminal);
        var intent = prepared.Intent!;
        Assert.Equal(("ssh", "dev-server", "ssh-term"), (intent.Environment.Kind, intent.Environment.Environment, intent.TerminalId));
        Assert.Equal(rig.Remote.WorkspaceId, intent.ProjectId);

        // 没有批准就创建:拒绝
        Assert.Equal((ErrorCodes.Conflict, TerminalErrorKeys.ApprovalRequired), await FailureOf(() => CreateAsync(alice, rig.Remote, "ssh-term")));
        // 别的控制端不能批准别人的意图(不泄漏它是否存在)
        Assert.Equal((ErrorCodes.Conflict, TerminalErrorKeys.ApprovalExpired), await FailureOf(() => bob.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, true))));
        // 拒绝 = 丢弃意图
        var rejected = await alice.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, false));
        Assert.Null(rejected.Approval);
        Assert.Equal(TerminalErrorKeys.ApprovalExpired, (await FailureOf(() => alice.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, true)))).Key);

        // 重新来过:批准 → 凭据 → 创建
        var again = (await alice.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(rig.Remote.WorkspaceId, 80, 24, "ssh-term"))).Intent!;
        var approval = (await alice.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(again.Id, true))).Approval;
        Assert.False(string.IsNullOrEmpty(approval));
        var created = await CreateAsync(alice, rig.Remote, "ssh-term", approval);
        Assert.Equal(("ssh", "running"), (created.Environment.Kind, created.Status));

        // 只有创建它的控制端能看、写、杀、列出
        Assert.Single((await alice.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Remote.WorkspaceId))).Terminals);
        Assert.Empty((await bob.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Remote.WorkspaceId))).Terminals);
        Assert.Equal(ErrorCodes.Conflict, (await FailureOf(() => bob.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams("ssh-term")))).Code);
        Assert.Equal(ErrorCodes.Conflict, (await FailureOf(() => bob.CallAsync(TerminalMethods.Write, new TerminalsWriteParams("ssh-term", Keys("x"))))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => Watch<TerminalSnapshotDto>.OpenAsync(bob, TerminalMethods.Topic("ssh-term")))).Code);

        var watch = await Watch<TerminalSnapshotDto>.OpenAsync(alice, TerminalMethods.Topic("ssh-term"));
        await alice.CallAsync(TerminalMethods.Write, new TerminalsWriteParams("ssh-term", Keys("echo ncw$((5+5))\n")));
        await watch.WaitAsync(() => TextOf(watch).Contains("ncw10", StringComparison.Ordinal), "remote terminal output");

        // 同一个控制端的最后一条连接结束:它名下的远程终端被收掉;本机终端属于设备,不受影响
        var local = await CreateAsync(alice, rig.Local);
        await alice.DisposeAsync();
        var back = await reconnectAlice();
        await using (back)
        {
            await Wait.UntilAsync(async () => (await back.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Remote.WorkspaceId))).Terminals.Length == 0, "remote terminal released");
            Assert.Equal(local.Id, (await back.CallAsync(TerminalMethods.List, new TerminalsListParams(rig.Local.WorkspaceId))).Terminals.Single().Id);
        }
    }

    [Fact]
    public async Task A_pending_approval_expires()
    {
        RequirePty();
        await using var rig = await Rig.StartAsync(new TerminalManagerOptions { IntentTtl = TimeSpan.FromMilliseconds(200) });
        var intent = (await rig.Owner.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(rig.Remote.WorkspaceId, 80, 24))).Intent!;
        await Task.Delay(500);
        Assert.Equal((ErrorCodes.Conflict, TerminalErrorKeys.ApprovalExpired), await FailureOf(() => rig.Owner.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, true))));
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(25);
        }
    }
}

/// <summary>本机环境(带 PTY),其中叫 <c>remote</c> 的项目换成「声称自己是 SSH、内部转发给本机环境」的包装环境:没有真 SSH 主机时测批准流程。</summary>
internal sealed class FakeSshEnvironments : IProjectEnvironments, IAsyncDisposable
{
    public const string RemoteProjectName = "remote";

    private readonly LocalEnvironment _local = new("dev-1", new LocalEnvironmentOptions { TerminalFactory = LocalTerminals.CreateFactory() });
    private readonly FakeSshEnvironment _ssh;

    public FakeSshEnvironments() => _ssh = new FakeSshEnvironment(_local);

    public IExecutionEnvironment Resolve(WorkspaceRecord project) => project.Name == RemoteProjectName ? _ssh : _local;

    public ValueTask DisposeAsync() => _local.DisposeAsync();
}

internal sealed class FakeSshEnvironment(IExecutionEnvironment inner) : IExecutionEnvironment
{
    public EnvironmentId Id { get; } = new(inner.Id.DeviceId, "dev-server");

    public EnvironmentKind Kind => EnvironmentKind.Ssh;

    public string DisplayName => "dev-server";

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add => inner.StatusChanged += value;
        remove => inner.StatusChanged -= value;
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => inner.Paths;

    public IEnvironmentFs Fs => inner.Fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
