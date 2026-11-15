using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>管理器 + 真 PTY(本机环境,<see cref="LocalTerminals"/>)的端到端:合批、游标、退出事件、整棵进程树终止、多终端、插件启动行。</summary>
public class ManagerWithPtyTests
{
    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static (TerminalManager Manager, LocalEnvironment Env, TempDir Root) Setup()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        return (new TerminalManager(), Env.NewLocal(shell: "/bin/sh"), new TempDir());
    }

    [Fact]
    public async Task A_real_shell_streams_sequenced_output_and_ends_with_an_exit_event_carrying_the_exit_code()
    {
        var (manager, env, root) = Setup();
        using var _r = root;
        await using var _m = manager;
        var request = new TerminalCreateRequest("project", env, root.Env, 100, 30, "tab-1");
        var info = await manager.CreateAsync(request, "alice");
        Assert.Equal(root.Env.FileName, info.Title);
        Assert.Equal(new EnvironmentId("dev", "local"), info.Environment.Id);

        var events = new List<TerminalEvent>();
        using var attachment = manager.Attach("tab-1", e => { lock (events) { events.Add(e); } });
        await manager.WriteAsync("tab-1", "echo mgr-$((6*7)); exit 5\n");
        await Wait.UntilAsync(() => !manager.Get("tab-1").Alive, 15_000, "exit");

        var done = manager.Get("tab-1");
        Assert.Equal(5, done.ExitCode);
        lock (events)
        {
            var exit = Assert.IsType<TerminalExit>(events[^1]);
            Assert.Equal(5, exit.ExitCode);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(i => attachment.Seq + i), events.Select(static e => e.Seq));
            Assert.Contains("mgr-42", Text(events.OfType<TerminalOutput>().SelectMany(static o => o.Data.ToArray()).ToArray()), StringComparison.Ordinal);
        }

        // 退出之后仍可回看
        Assert.Contains("mgr-42", Text(manager.GetBuffer("tab-1").Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_controller_gets_the_buffer_and_continues_from_a_cursor()
    {
        var (manager, env, root) = Setup();
        using var _r = root;
        await using var _m = manager;
        await manager.CreateAsync(new TerminalCreateRequest("project", env, root.Env, 100, 30, "tab-1"), "alice");
        await manager.WriteAsync("tab-1", "echo first-$((6*7))\n");
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1").Data).Contains("first-42", StringComparison.Ordinal), 15_000, "first output");

        // 控制端 B 晚到:快照里有第一条输出;之后的增量按序号续上
        var seen = new List<TerminalEvent>();
        using var late = manager.Attach("tab-1", e => { lock (seen) { seen.Add(e); } }, controllerId: "bob");
        Assert.Contains("first-42", Text(late.Snapshot), StringComparison.Ordinal);
        await manager.WriteAsync("tab-1", "echo second-$((6*7))\n");
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1").Data).Contains("second-42", StringComparison.Ordinal), 15_000, "second output");

        // 控制端 B 断线重连:带着它看到的最后一个序号来,只补发错过的
        long last;
        lock (seen)
        {
            last = seen[0].Seq;
        }

        using var resumed = manager.Attach("tab-1", _ => { }, sinceSeq: last, controllerId: "bob");
        Assert.True(resumed.Resumed);
        Assert.Equal(resumed.Replay.Select(static e => e.Seq), Enumerable.Range((int)last + 1, resumed.Replay.Count).Select(static i => (long)i));
    }

    [Fact]
    public async Task Killing_through_the_manager_ends_the_process_tree_and_marks_the_terminal_exited()
    {
        var (manager, env, root) = Setup();
        using var _r = root;
        await using var _m = manager;
        await manager.CreateAsync(new TerminalCreateRequest("project", env, root.Env, 100, 30, "tab-1"), "alice");
        await manager.WriteAsync("tab-1", "sleep 300 & echo BG=$!\n");
        await Wait.UntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(Text(manager.GetBuffer("tab-1").Data), @"BG=\d+"), 15_000, "background pid");
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(Text(manager.GetBuffer("tab-1").Data), @"BG=(\d+)").Groups[1].Value);
        Assert.True(Wait.ProcessAlive(pid));

        await manager.KillAsync("tab-1");
        var info = manager.Get("tab-1");
        Assert.False(info.Alive);
        Assert.Equal(ProcessExitReason.Killed, info.ExitReason);
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), 10_000, "background process to die");
    }

    [Fact]
    public async Task Several_terminals_coexist_and_a_scope_close_takes_them_all()
    {
        var (manager, env, root) = Setup();
        using var _r = root;
        await using var _m = manager;
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add((await manager.CreateAsync(new TerminalCreateRequest("project", env, root.Env, 100, 30), "alice")).Id);
        }

        Assert.Equal(4, ids.Distinct().Count());
        for (var i = 0; i < ids.Count; i++)
        {
            await manager.WriteAsync(ids[i], $"echo only-{i}-$((6*7))\n");
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var index = i;
            await Wait.UntilAsync(() => Text(manager.GetBuffer(ids[index]).Data).Contains($"only-{index}-42", StringComparison.Ordinal), 15_000, "own output");
            Assert.DoesNotContain($"only-{(index + 1) % ids.Count}-42", Text(manager.GetBuffer(ids[index]).Data), StringComparison.Ordinal);
        }

        await manager.CloseScopeAsync("project");
        Assert.Empty(manager.List("project"));
    }

    [Fact]
    public async Task A_launch_spec_starts_the_cli_inside_the_interactive_shell_with_the_injected_environment()
    {
        var (manager, env, root) = Setup();
        using var _r = root;
        await using var _m = manager;
        manager.SetLaunchSpec("tab-1", new TerminalLaunchSpec("project", new Dictionary<string, string> { ["NCW_PLUGIN_KEY"] = "s3cret value" }, ["printf", "cli-%s-%s\\n", "$NCW_PLUGIN_KEY", "it's"]));
        await manager.CreateAsync(new TerminalCreateRequest("project", env, root.Env, 100, 30, "tab-1"), "alice");
        // argv 被引号保护:$NCW_PLUGIN_KEY 不会被 shell 展开,但环境变量确实在子进程里
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1").Data).Contains("cli-$NCW_PLUGIN_KEY-it's", StringComparison.Ordinal), 15_000, "launch line output");
        await manager.WriteAsync("tab-1", "echo key=$NCW_PLUGIN_KEY-$((6*7))\n");
        await Wait.UntilAsync(() => Text(manager.GetBuffer("tab-1").Data).Contains("key=s3cret value-42", StringComparison.Ordinal), 15_000, "environment in the shell");
        // 终端仍是一个活 shell
        Assert.True(manager.Get("tab-1").Alive);
    }
}
