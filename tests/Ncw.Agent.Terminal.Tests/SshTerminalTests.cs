using System.Text;
using Ncw.Agent.Environments;
using static Ncw.Agent.Terminal.Tests.SshTestKit;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>
/// 管理器 + 真实 SSH 环境(用户态 sshd,机器上没有 sshd / sftp-server / ssh-keygen 时跳过)的冒烟:
/// 本机和 SSH 走同一个 <see cref="TerminalManager"/> 接口,包括远程环境的逐个批准。
/// </summary>
public class SshTerminalTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    [Fact]
    public async Task A_remote_terminal_is_approved_per_terminal_runs_commands_resizes_and_is_killed()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        using var root = new TempDir();
        await using var manager = new TerminalManager();
        TerminalCreateRequest Request(string? approval = null) => new("project", env, EnvPath.Posix(root.Path), 100, 30, "tab-1", null, approval);

        // 逐个批准:没有凭据不能开,意图带着环境标识
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => manager.CreateAsync(Request(), "alice"));
        var intent = Assert.IsType<TerminalPreparation.ApprovalRequired>(await manager.PrepareAsync(Request(), "alice")).Intent;
        Assert.Equal(new EnvironmentId("dev", "ssh-test"), intent.Environment.Id);
        Assert.Equal(EnvironmentKind.Ssh, intent.Environment.Kind);
        var grant = manager.Approve(intent.Id, true, "alice")!;

        var info = await manager.CreateAsync(Request(grant), "alice");
        Assert.True(info.Alive);
        Assert.Equal(EnvironmentKind.Ssh, info.Environment.Kind);

        var events = new List<TerminalEvent>();
        using var subscription = manager.Attach("tab-1", events.Add, controllerId: "alice");
        await manager.WriteAsync("tab-1", "echo ssh-$((6*7)) shellpid=$$\n", "alice");
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1", "alice").Data).Contains("ssh-42", StringComparison.Ordinal), 20_000, "remote shell echo");
        var shellPid = int.Parse(System.Text.RegularExpressions.Regex.Match(Text(manager.GetBuffer("tab-1", "alice").Data), @"ssh-42 shellpid=(\d+)").Groups[1].Value);
        Assert.True(Wait.ProcessAlive(shellPid));

        await manager.ResizeAsync("tab-1", 120, 40, "alice");
        await manager.WriteAsync("tab-1", "stty size\n", "alice");
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1", "alice").Data).Contains("40 120", StringComparison.Ordinal), 20_000, "remote window size");

        // 输出事件的序号连续
        Assert.NotEmpty(events);
        Assert.Equal(Enumerable.Range(0, events.Count).Select(i => events[0].Seq + i), events.Select(static e => e.Seq));

        // 别的控制端碰不了它;自己杀掉之后标为已退出、保留输出
        await Fixture.ExpectAsync(TerminalErrorCode.ApprovalRequired, () => manager.WriteAsync("tab-1", "x", "bob"));
        await manager.KillAsync("tab-1", "alice");
        await Wait.UntilAsync(() => !manager.Get("tab-1").Alive, 20_000, "terminal to be marked exited");
        Assert.Contains("ssh-42", Text(manager.GetBuffer("tab-1", "alice").Data), StringComparison.Ordinal);
        Assert.IsType<TerminalExit>(events[^1]);
        // 关掉通道之后,远端的登录 shell 也结束了(sshd 给它发 SIGHUP)
        await Wait.UntilAsync(() => !Wait.ProcessAlive(shellPid), 10_000, "the remote shell to end");
    }

    [Fact]
    public async Task The_working_directory_is_checked_against_the_remote_root_before_anything_starts()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        using var root = new TempDir();
        await using var manager = new TerminalManager();
        var request = new TerminalCreateRequest("project", env, EnvPath.Posix(root.Path), 80, 24, "tab-2", "../outside");
        var error = await Assert.ThrowsAsync<EnvironmentException>(() => manager.PrepareAsync(request, "alice"));
        Assert.Equal(EnvironmentErrorCode.PathEscape, error.Code);
    }
}
