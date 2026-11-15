using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>本机 PTY(POSIX)的真实测试:起真的 sh,走真的内核终端。Windows 的 ConPTY 见 <see cref="WindowsConPtyTests"/>。</summary>
public class PosixPtyTests
{
    private static async Task<(ITerminal Terminal, Collector Output)> OpenAsync(TerminalSpec? spec = null, LocalEnvironment? env = null)
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        env ??= Env.NewLocal(shell: "/bin/sh");
        var terminal = await env.OpenTerminalAsync(spec ?? new TerminalSpec());
        return (terminal, new Collector(terminal));
    }

    /// <summary>从输出里取 <c>tag=数字</c>:要的是命令执行后打印的那个,而不是终端回显的命令文本(那里 <c>tag=</c> 后面是 <c>$!</c>)。</summary>
    private static int Pid(Collector output, string tag)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output.Text, tag + @"=(\d+)");
        Assert.True(match.Success, $"no {tag}=<pid> in: {output.Text}");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public async Task A_shell_runs_commands_and_echoes_the_output()
    {
        var (_, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("echo ncw-$((40+2))");
        await output.WaitForAsync("ncw-42");
    }

    [Fact]
    public async Task The_working_directory_environment_and_term_are_applied()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "ncw-cwd-marker"), "x");
        var (_, output) = await OpenAsync(new TerminalSpec(dir.Env, 100, 30, new Dictionary<string, string> { ["NCW_T"] = "it's \"x\" $HOME" }));
        await using var _o = output;
        await output.SendLineAsync("test -f ncw-cwd-marker && printf 'cwd-ok env=%s term=%s\\n' \"$NCW_T\" \"$TERM\"");
        await output.WaitForAsync("term=xterm-256color");
        Assert.Contains("cwd-ok env=it's \"x\" $HOME term=", output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Injected_child_environment_does_not_override_the_users_own_but_explicit_values_win()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        Environment.SetEnvironmentVariable("NCW_USER_OWN", "user");
        try
        {
            var env = Env.NewLocal(new LocalPtyOptions
            {
                ChildEnvironment = () => new Dictionary<string, string> { ["NCW_USER_OWN"] = "injected", ["NCW_INJECTED"] = "proxy", ["NCW_BOTH"] = "injected" },
                KillGrace = TimeSpan.FromMilliseconds(500),
            }, shell: "/bin/sh");
            await using var terminal = await env.OpenTerminalAsync(new TerminalSpec(Environment: new Dictionary<string, string> { ["NCW_BOTH"] = "explicit", ["TERM"] = "vt100" }));
            await using var output = new Collector(terminal);
            await output.SendLineAsync("echo own=$NCW_USER_OWN inj=$NCW_INJECTED both=$NCW_BOTH term=$TERM");
            await output.WaitForAsync("own=user inj=proxy both=explicit term=vt100");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCW_USER_OWN", null);
        }
    }

    [Fact]
    public async Task The_initial_size_and_resize_reach_the_kernel_terminal()
    {
        var (terminal, output) = await OpenAsync(new TerminalSpec(Columns: 100, Rows: 30));
        await using var _o = output;
        await output.SendLineAsync("stty size");
        await output.WaitForAsync("30 100");
        await terminal.ResizeAsync(120, 40);
        await output.SendLineAsync("stty size");
        await output.WaitForAsync("40 120");
    }

    [Fact]
    public async Task Resize_delivers_SIGWINCH_to_the_foreground_job()
    {
        // dash 不认 WINCH 陷阱:这个用例要 bash(macOS 的 /bin/bash、多数 Linux 都有)
        Assert.SkipUnless(Env.Posix && File.Exists("/bin/bash"), "needs /bin/bash");
        var (terminal, output) = await OpenAsync(env: Env.NewLocal(shell: "/bin/bash"));
        await using var _o = output;
        await output.SendLineAsync("trap 'echo WINCH-$(stty size)' WINCH; echo ready-for-winch-$((6*7))");
        await output.WaitForAsync("ready-for-winch-42");
        await terminal.ResizeAsync(91, 33);
        await output.WaitForAsync("WINCH-33 91");
    }

    [Fact]
    public async Task Ctrl_C_interrupts_the_foreground_job_but_not_the_shell()
    {
        var (_, output) = await OpenAsync();
        await using var _o = output;
        // sleep 60 占着前台;ETX 经终端行规程变成前台进程组的 SIGINT。sleep 没被打断的话,下面的命令要等 60 秒才会执行
        await output.SendLineAsync("echo started-$((6*7)); sleep 60");
        await output.WaitForAsync("started-42");
        await Task.Delay(300);
        var started = DateTime.UtcNow;
        await output.SendAsync("\u0003");
        await output.SendLineAsync("echo shell-still-alive-$((6*7))");
        await output.WaitForAsync("shell-still-alive-42", 10_000);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Exit_code_is_reported_and_exited_completes_after_the_output_is_drained()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("echo last-words-$((6*7)); exit 7");
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, exit.ExitCode);
        Assert.Null(exit.Signal);
        Assert.Equal(ProcessExitReason.Exited, exit.Reason);
        await output.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("last-words-42", output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signalled_shell_reports_the_signal()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("kill -9 $$");
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(9, exit.Signal);
        Assert.Equal(137, exit.ExitCode);
    }

    [Fact]
    public async Task Killing_ends_the_whole_process_tree_including_jobs_in_other_process_groups()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        // 前台 sleep、后台 sleep(sh 非交互式没有作业控制,但仍是同一个进程组)、再经子 shell 的孙进程
        await output.SendLineAsync("set +H 2>/dev/null; true"); // 交互式 bash 把 `$!;` 里的 `!` 当历史展开
        await output.SendLineAsync("sleep 300 & echo BG=$!; (sleep 301 & echo GRAND=$!; wait) & echo SUB=$!; echo ready-tree-$((6*7))");
        await output.WaitForAsync("ready-tree-42");
        var pids = new[] { Pid(output, "BG"), Pid(output, "GRAND") };
        Assert.All(pids, pid => Assert.True(Wait.ProcessAlive(pid)));

        await terminal.KillAsync();
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ProcessExitReason.Killed, exit.Reason);
        await Wait.UntilAsync(() => pids.All(pid => !Wait.ProcessAlive(pid)), 10_000, "background processes to die");
    }

    [Fact]
    public async Task A_shell_that_ignores_SIGHUP_and_SIGTERM_is_force_killed_after_the_grace_period()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("trap '' HUP TERM; echo trapped-$((6*7))");
        await output.WaitForAsync("trapped-42");
        var started = DateTime.UtcNow;
        await terminal.KillAsync();
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(9, exit.Signal);
        Assert.Equal(ProcessExitReason.Killed, exit.Reason);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300), "SIGKILL must wait for the grace period");
    }

    [Fact]
    public async Task Large_output_arrives_complete_and_in_order()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        const int Count = 3_000_000;
        await output.SendLineAsync($"head -c {Count} /dev/zero | tr '\\0' \"$(printf '\\170')\"; echo; echo END-MARK-$((1+1))");
        await output.WaitForAsync("END-MARK-2", 60_000);
        Assert.Equal(Count, output.Text.Count(c => c == 'x'));
        await terminal.KillAsync();
    }

    [Fact]
    public async Task A_slow_reader_applies_back_pressure_instead_of_buffering_without_bound()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        var env = Env.NewLocal(shell: "/bin/sh");
        await using var terminal = (PtyTerminal)await env.OpenTerminalAsync(new TerminalSpec());
        // 不读:一个会无限输出的命令。托管侧的通道只缓存有限的块(64 块),满了读线程就停下,其余由内核 PTY 缓冲和被阻塞的子进程承担
        await terminal.Stream.WriteAsync("yes ncw-flood\n"u8.ToArray());
        var reader = terminal.OutputReader;
        Assert.True(reader.CanCount);
        await Wait.UntilAsync(() => reader.Count >= 64, 10_000, "the channel to fill up");
        await Task.Delay(1000);
        Assert.Equal(64, reader.Count); // 一秒之后仍是 64:没有继续增长
        Assert.False(terminal.Exited.IsCompleted); // 子进程只是被反压,没有死

        // 一旦开始读,输出继续流动
        var buffer = new byte[16 * 1024];
        for (var i = 0; i < 200; i++)
        {
            Assert.True(await terminal.Stream.ReadAsync(buffer) > 0);
        }

        await terminal.KillAsync();
    }

    [Fact]
    public async Task Multibyte_text_survives_arbitrary_chunk_boundaries()
    {
        var (_, output) = await OpenAsync();
        await using var _o = output;
        // 20000 个三字节字符 + 一个四字节的表情,总长远大于单次读取块,必然有字符被切在块边界上
        await output.SendLineAsync("i=0; while [ $i -lt 2000 ]; do printf '你好世界你好世界你好😀'; i=$((i+1)); done; echo; echo UTF8-DONE-$((6*7))");
        await output.WaitForAsync("UTF8-DONE-42");
        var expected = string.Concat(Enumerable.Repeat("你好世界你好世界你好😀", 2000));
        Assert.Contains(expected, output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', output.Text);
    }

    [Fact]
    public async Task Raw_bytes_including_escape_sequences_pass_through()
    {
        var (_, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("printf '\\033[31mred\\033[0m-%s\\n' $((6*7))");
        await output.WaitForAsync("\u001b[31mred\u001b[0m-42");
    }

    [Fact]
    public async Task Input_split_in_the_middle_of_a_multibyte_character_is_reassembled_by_the_shell()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        var bytes = Encoding.UTF8.GetBytes("echo 世界-$((6*7))\n");
        await terminal.Stream.WriteAsync(bytes.AsMemory(0, 7)); // "echo " + 世 的前两个字节
        await Task.Delay(100);
        await terminal.Stream.WriteAsync(bytes.AsMemory(7));
        await output.WaitForAsync("世界-42");
    }

    [Fact]
    public async Task Several_terminals_run_concurrently_without_crossing_output()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        var env = Env.NewLocal(shell: "/bin/sh");
        var terminals = new List<(ITerminal Terminal, Collector Output)>();
        for (var i = 0; i < 8; i++)
        {
            var terminal = await env.OpenTerminalAsync(new TerminalSpec());
            terminals.Add((terminal, new Collector(terminal)));
        }

        try
        {
            await Task.WhenAll(terminals.Select((t, i) => t.Output.SendLineAsync($"echo mark-{i}-$((i=0)){i}")));
            for (var i = 0; i < terminals.Count; i++)
            {
                await terminals[i].Output.WaitForAsync($"mark-{i}-0{i}");
                for (var j = 0; j < terminals.Count; j++)
                {
                    if (j != i)
                    {
                        Assert.DoesNotContain($"mark-{j}-0{j}", terminals[i].Output.Text, StringComparison.Ordinal);
                    }
                }
            }
        }
        finally
        {
            foreach (var (_, output) in terminals)
            {
                await output.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_background_process_holding_the_terminal_does_not_keep_the_terminal_alive_forever()
    {
        var (terminal, output) = await OpenAsync();
        await using var _o = output;
        await output.SendLineAsync("sleep 30 & echo BG=$!");
        await Wait.UntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(output.Text, @"BG=\d+"), 10_000, "BG=<pid>");
        var pid = Pid(output, "BG");
        await output.SendLineAsync("exit 0");
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exit.ExitCode);
        using var killer = System.Diagnostics.Process.Start("/bin/kill", ["-9", pid.ToString()]);
        killer?.WaitForExit();
    }

    [Fact]
    public async Task Resolving_the_launch_validates_the_directory_the_shell_and_the_size()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        using var dir = new TempDir();
        var env = Env.NewLocal(shell: "/bin/sh");
        var missing = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.OpenTerminalAsync(new TerminalSpec(dir.Env.Join("nope"))));
        Assert.Equal(EnvironmentErrorCode.NotFound, missing.Code);

        File.WriteAllText(Path.Combine(dir.Path, "file"), "x");
        var notDirectory = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.OpenTerminalAsync(new TerminalSpec(dir.Env.Join("file"))));
        Assert.Equal(EnvironmentErrorCode.NotDirectory, notDirectory.Code);

        var badSize = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.OpenTerminalAsync(new TerminalSpec(Columns: 0)));
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, badSize.Code);
        var badEnv = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.OpenTerminalAsync(new TerminalSpec(Environment: new Dictionary<string, string> { ["A=B"] = "x" })));
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, badEnv.Code);

        var noShell = await Assert.ThrowsAsync<EnvironmentException>(async () => await Env.NewLocal(shell: "/nonexistent/shell").OpenTerminalAsync(new TerminalSpec()));
        Assert.Equal(EnvironmentErrorCode.ProcessStartFailed, noShell.Code);
    }

    [Fact]
    public async Task Disposing_a_running_terminal_kills_it_and_leaves_no_process_behind()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        var env = Env.NewLocal(shell: "/bin/sh");
        var terminal = await env.OpenTerminalAsync(new TerminalSpec());
        var output = new Collector(terminal);
        await output.SendLineAsync("echo me=$$; sleep 300 & echo BG=$!");
        await output.WaitForAsync("BG=");
        await Wait.UntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(output.Text, @"BG=\d+"));
        var shellPid = Pid(output, "me");
        var bgPid = Pid(output, "BG");
        await output.DisposeAsync();
        await Wait.UntilAsync(() => !Wait.ProcessAlive(shellPid) && !Wait.ProcessAlive(bgPid), 10_000, "processes to die");
    }

    [Fact]
    public async Task The_default_shell_is_started_as_a_login_shell_where_supported()
    {
        Assert.SkipUnless(Env.Posix, "POSIX PTY");
        Assert.Equal(["-l"], PtyLaunch.ShellArgs("/bin/bash"));
        Assert.Equal(["-l"], PtyLaunch.ShellArgs("/usr/bin/zsh"));
        Assert.Empty(PtyLaunch.ShellArgs("/usr/bin/nu"));
        await Task.CompletedTask;
    }
}
