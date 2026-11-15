using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>
/// 远程(SSH)环境的终端逐个批准,移植自 2.x 的 <c>main/__tests__/terminal-host.test.ts</c>「SSH terminal page approval」6 条,
/// 并补上 3.0 要求的:审批意图带执行环境标识(设备 + 环境)。「窗口 / 页签」在 3.0 里是「控制端 + 终端 id」。
/// </summary>
public class TerminalApprovalTests
{
    private static Fixture Remote() => new(kind: EnvironmentKind.Ssh);

    [Fact]
    public async Task Nothing_is_spawned_before_an_explicit_approval()
    {
        await using var f = Remote();
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice));
        var prepared = await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice);
        var intent = Assert.IsType<TerminalPreparation.ApprovalRequired>(prepared).Intent;
        // 意图 id 不是凭据
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: intent.Id), Fixture.Alice));
        Assert.Null(f.Manager.Approve(intent.Id, false, Fixture.Alice));
        Assert.Equal(0, f.Env.OpenCount);
        // 拒绝之后意图就没了
        Fixture.Expect(TerminalErrorCode.ApprovalExpired, () => f.Manager.Approve(intent.Id, true, Fixture.Alice));
    }

    [Fact]
    public async Task The_intent_carries_the_execution_environment_identity_the_shell_and_the_directory()
    {
        await using var f = Remote();
        Directory.CreateDirectory(Path.Combine(f.Dir.Path, "app"));
        var prepared = await f.Manager.PrepareAsync(f.Request(id: "tab", cwd: "app"), Fixture.Alice);
        var intent = Assert.IsType<TerminalPreparation.ApprovalRequired>(prepared).Intent;
        Assert.Equal(new EnvironmentId("dev", "dev-server"), intent.Environment.Id);
        Assert.Equal("dev-server", intent.Environment.Name);
        Assert.Equal(EnvironmentKind.Ssh, intent.Environment.Kind);
        Assert.Equal("/bin/sh", intent.Shell);
        Assert.EndsWith("app", intent.Cwd, StringComparison.Ordinal);
        Assert.Equal("tab", intent.TerminalId);
        Assert.Equal("ws", intent.ScopeId);
        Assert.Equal(f.Time.GetUtcNow() + TimeSpan.FromSeconds(60), intent.ExpiresAt);
        Assert.Equal(0, f.Env.OpenCount); // 准备阶段什么都没有启动
    }

    [Fact]
    public async Task A_grant_is_consumed_once_creation_is_coalesced_and_only_the_owner_can_touch_a_live_remote_terminal()
    {
        await using var f = Remote();
        var grant = await f.GrantAsync("tab");
        var request = f.Request(id: "tab", approval: grant);
        var both = await Task.WhenAll(f.Manager.CreateAsync(request, Fixture.Alice), f.Manager.CreateAsync(request, Fixture.Alice));
        Assert.Equal(both[0], both[1]);
        Assert.Equal(1, f.Env.OpenCount);

        // 同一个活着的终端切回来免批准
        Assert.IsType<TerminalPreparation.Ready>(await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice));
        // 别的控制端:不能重连、不能看、不能写、列表里也没有
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(request, Fixture.Bob));
        Fixture.Expect(TerminalErrorCode.ApprovalRequired, () => f.Manager.Attach("tab", _ => { }, controllerId: Fixture.Bob));
        Fixture.Expect(TerminalErrorCode.ApprovalRequired, () => f.Manager.GetBuffer("tab", Fixture.Bob));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.WriteAsync("tab", "command", Fixture.Bob));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.KillAsync("tab", Fixture.Bob));
        Assert.Empty(f.Manager.List("ws", Fixture.Bob));
        Assert.Single(f.Manager.List("ws", Fixture.Alice));

        // 退出之后:凭据已用掉,重开必须重新批准
        f.Env.Terminals[0].Finish(0);
        await f.WaitExitedAsync("tab");
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(request, Fixture.Alice));
        Assert.IsType<TerminalPreparation.ApprovalRequired>(await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice));
    }

    [Fact]
    public async Task Expired_cross_terminal_cross_scope_other_controller_and_disconnected_grants_are_rejected()
    {
        await using var f = Remote();
        var prepared = await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice);
        var intent = Assert.IsType<TerminalPreparation.ApprovalRequired>(prepared).Intent;
        // 别的控制端批不了别人的意图,而且不泄漏它存在
        Fixture.Expect(TerminalErrorCode.ApprovalExpired, () => f.Manager.Approve(intent.Id, true, Fixture.Bob));
        var grant = f.Manager.Approve(intent.Id, true, Fixture.Alice)!;
        Assert.Equal(grant, f.Manager.Approve(intent.Id, true, Fixture.Alice)); // 重复批准:同一个凭据

        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "other", approval: grant), Fixture.Alice));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(scope: "other-ws", id: "tab", approval: grant), Fixture.Alice));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Bob));

        f.Time.Advance(TimeSpan.FromSeconds(61));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalExpired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice));

        var next = await f.GrantAsync("tab");
        f.Env.State = EnvironmentState.Failed;
        var error = await Assert.ThrowsAsync<EnvironmentException>(() => f.Manager.CreateAsync(f.Request(id: "tab", approval: next), Fixture.Alice));
        Assert.Equal(EnvironmentErrorCode.Disconnected, error.Code);
        Assert.Equal(0, f.Env.OpenCount);
    }

    [Fact]
    public async Task A_grant_does_not_survive_the_connection_being_replaced()
    {
        await using var f = Remote();
        var grant = await f.GrantAsync("tab");
        f.Env.Generation++; // 重连了:批准的是上一条连接
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice));
        Assert.Equal(0, f.Env.OpenCount);
    }

    [Fact]
    public async Task Pending_approvals_are_invalidated_when_the_controller_leaves()
    {
        await using var f = Remote();
        var prepared = await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice);
        var intent = Assert.IsType<TerminalPreparation.ApprovalRequired>(prepared).Intent;
        await f.Manager.ReleaseController(Fixture.Alice);
        Fixture.Expect(TerminalErrorCode.ApprovalExpired, () => f.Manager.Approve(intent.Id, true, Fixture.Alice));
        Assert.Equal(0, f.Env.OpenCount);
    }

    [Theory]
    [InlineData("tab")]
    [InlineData("controller")]
    [InlineData("shutdown")]
    public async Task A_late_PTY_is_killed_after_the_tab_the_controller_or_the_device_goes_away(string closure)
    {
        await using var f = Remote();
        f.Env.OpenGate = new TaskCompletionSource();
        var grant = await f.GrantAsync("tab");
        var creation = f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice);
        await Wait.UntilAsync(() => f.Env.OpenCount == 1, 5000, "the open to start");

        switch (closure)
        {
            case "tab":
                await f.Manager.KillAsync("tab", Fixture.Alice);
                break;
            case "controller":
                await f.Manager.ReleaseController(Fixture.Alice);
                break;
            default:
                var shutdown = f.Manager.ShutdownAsync();
                f.Env.OpenGate.SetResult();
                await shutdown;
                break;
        }

        if (closure != "shutdown")
        {
            f.Env.OpenGate.SetResult();
        }

        await Fixture.ExpectAsync(TerminalErrorCode.Cancelled, () => creation);
        // 迟到才开出来的 PTY 必须被释放(释放 = 终止),而且没有留下会话
        await Wait.UntilAsync(() => f.Env.Terminals.Count == 1 && f.Env.Terminals[0].Disposals >= 1, 5000, "the late PTY to be disposed");
        Assert.Empty(f.Manager.List("ws", Fixture.Alice));
    }

    [Fact]
    public async Task A_different_directory_is_not_coalesced_into_an_approved_creation()
    {
        await using var f = Remote();
        Directory.CreateDirectory(Path.Combine(f.Dir.Path, "elsewhere"));
        f.Env.OpenGate = new TaskCompletionSource();
        var grant = await f.GrantAsync("tab");
        var creation = f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice);
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: grant, cwd: "elsewhere"), Fixture.Alice));
        f.Env.OpenGate.SetResult();
        await creation;
    }

    [Fact]
    public async Task A_grant_for_one_directory_cannot_open_another()
    {
        await using var f = Remote();
        Directory.CreateDirectory(Path.Combine(f.Dir.Path, "elsewhere"));
        var grant = await f.GrantAsync("tab");
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.CreateAsync(f.Request(id: "tab", approval: grant, cwd: "elsewhere"), Fixture.Alice));
        Assert.Equal(0, f.Env.OpenCount);
    }

    [Fact]
    public async Task Pending_intents_are_capped_and_expired_ones_make_room()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, MaxIntents = 2 }, EnvironmentKind.Ssh);
        await f.Manager.PrepareAsync(f.Request(id: "a"), Fixture.Alice);
        await f.Manager.PrepareAsync(f.Request(id: "b"), Fixture.Alice);
        await Fixture.ExpectAsync(TerminalErrorCode.Conflict, () => f.Manager.PrepareAsync(f.Request(id: "c"), Fixture.Alice));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await f.Manager.PrepareAsync(f.Request(id: "c"), Fixture.Alice);
    }

    [Fact]
    public async Task Preparing_the_same_terminal_again_replaces_the_previous_intent_but_never_another_controllers()
    {
        await using var f = Remote();
        var first = Assert.IsType<TerminalPreparation.ApprovalRequired>(await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice)).Intent;
        var second = Assert.IsType<TerminalPreparation.ApprovalRequired>(await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice)).Intent;
        Assert.NotEqual(first.Id, second.Id);
        Fixture.Expect(TerminalErrorCode.ApprovalExpired, () => f.Manager.Approve(first.Id, true, Fixture.Alice));
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Bob));
        Assert.NotNull(f.Manager.Approve(second.Id, true, Fixture.Alice));
    }

    [Fact]
    public async Task A_local_environment_needs_no_approval_and_prepare_creates_it()
    {
        await using var f = new Fixture();
        var prepared = await f.Manager.PrepareAsync(f.Request(id: "tab"), Fixture.Alice);
        var ready = Assert.IsType<TerminalPreparation.Ready>(prepared);
        Assert.True(ready.Terminal.Alive);
        Assert.Equal(1, f.Env.OpenCount);
    }

    [Fact]
    public async Task A_dropped_controller_takes_its_remote_terminals_with_it()
    {
        await using var f = Remote();
        var grant = await f.GrantAsync("tab");
        await f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice);
        await f.Manager.WriteAsync("tab", Encoding.UTF8.GetBytes("hostname\n"), Fixture.Alice);
        Assert.Equal("hostname\n", f.Env.Terminals[0].WrittenText);
        await f.Manager.ReleaseController(Fixture.Alice);
        Assert.Equal(1, f.Env.Terminals[0].Kills);
        Assert.Empty(f.Manager.List("ws", Fixture.Alice));
    }
}
