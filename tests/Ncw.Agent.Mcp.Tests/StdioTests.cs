using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// stdio 传输。移植自 2.x 的 <c>environment-transport.test.ts</c>(「走非 PTY 的环境进程,stderr 独立、密钥值显式经 env 传入」;关闭时 kill 一次)。
/// 2.x 的 <c>forwarding-native / mcp-native</c> 需要隔离 sshd(<c>it.skipIf(!integration)</c>)——3.0 的 SSH 环境由 <c>Ncw.Agent.Environments</c> 提供并在那里验证,这里只验证「经 IExecutionEnvironment」这条接缝。
/// 外加一条<b>真子进程</b>用例(<c>sh</c> 里一个读写 JSON-RPC 的小脚本)。
/// </summary>
public sealed class StdioTests
{
    private static McpOptions Opts(int stderrTail = 64, int maxMessage = 1024 * 1024) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ListToolsTimeout = TimeSpan.FromSeconds(5),
        CallTimeout = TimeSpan.FromSeconds(5),
        StderrTailBytes = stderrTail,
        MaxMessageBytes = maxMessage,
        GracefulExit = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>stdin 一写就以断管失败的进程;进程「退出」由测试在写失败<b>之后</b>手动放行(<see cref="ExitWith"/>),所以「写失败先于退出定案」是确定的,不靠时序。</summary>
    private sealed class BrokenPipeProcess(EnvironmentId environment) : IEnvProcess
    {
        private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.IO.Pipelines.Pipe _stdout = new();
        private readonly System.IO.Pipelines.Pipe _stderr = new();
        private readonly BrokenStdin _stdin = new();

        public EnvironmentId EnvironmentId { get; } = environment;

        public int Generation => 0;

        public Stream? Stdin => _stdin;

        public Stream Stdout => _stdout.Reader.AsStream();

        public Stream Stderr => _stderr.Reader.AsStream();

        public Task<ProcessExit> Exited => _exited.Task;

        public Task WriteFailed => _stdin.Failed.Task;

        public void ExitWith(int code)
        {
            _stdout.Writer.Complete();
            _stderr.Writer.Complete();
            _exited.TrySetResult(new ProcessExit(code, null, ProcessExitReason.Exited));
        }

        public ValueTask KillAsync(CancellationToken ct = default)
        {
            ExitWith(137);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => KillAsync();

        private sealed class BrokenStdin : Stream
        {
            public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => Fail();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Fail();
                return ValueTask.CompletedTask;
            }

            private void Fail()
            {
                Failed.TrySetResult();
                throw new IOException("Broken pipe");
            }
        }
    }

    /// <summary>
    /// 回归(整程序集并发时「进程一启动就退出」那条用例失败的根因):写 stdin 失败比 <c>WatchAsync</c> 的退出定案先到时,以前直接报通用的 <c>closed</c>,
    /// 同一个坏服务器有时是 <c>processExited</c>(带退出码)、有时是 <c>closed</c>。现在写失败后有界地等退出定案,按它报。这里写失败确定地先于退出。
    /// </summary>
    [Fact]
    public async Task A_write_that_fails_because_the_process_already_died_is_reported_with_the_exit_code_not_as_a_generic_close()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        var process = new BrokenPipeProcess(env.Id);
        env.RawProcessFactory = _ => process;
        var transport = new StdioTransport(env, CommandSpec.Exec("x"), Opts());
        await transport.StartAsync(CancellationToken.None);

        var send = transport.SendAsync("{}"u8.ToArray(), CancellationToken.None).AsTask();
        await process.WriteFailed.WaitAsync(TimeSpan.FromSeconds(30)); // 写已经失败了,但进程还没「退出」
        Assert.False(transport.Completion.IsCompleted);
        process.ExitWith(3);

        var error = await Assert.ThrowsAsync<McpException>(() => send);
        Assert.Equal(McpErrorKeys.ProcessExited, error.Key);
        Assert.Equal("3", error.Parameters["exitCode"]);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task A_write_that_fails_while_the_process_stays_alive_is_reported_as_closed_after_the_bounded_wait_when_the_caller_gives_up()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        var process = new BrokenPipeProcess(env.Id);
        env.RawProcessFactory = _ => process;
        var transport = new StdioTransport(env, CommandSpec.Exec("x"), Opts());
        await transport.StartAsync(CancellationToken.None);
        using var giveUp = new CancellationTokenSource();

        var send = transport.SendAsync("{}"u8.ToArray(), giveUp.Token).AsTask();
        await process.WriteFailed.WaitAsync(TimeSpan.FromSeconds(30));
        await giveUp.CancelAsync(); // 调用方不想再等进程的定案

        var error = await Assert.ThrowsAsync<McpException>(() => send);
        Assert.Equal(McpErrorKeys.Closed, error.Key);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task The_server_runs_as_a_non_pty_environment_process_with_piped_stdin_the_working_directory_and_explicit_env_values()
    {
        var config = McpRig.Stdio() with { EnvNames = ["TOKEN"], Cwd = "sub" };
        var projectEnv = new TestEnvironment(new LocalEnvironment("dev-1"), EnvironmentKind.Ssh) { ProcessFactory = _ => new FakeProcess(new EnvironmentId("dev-1", "ssh-1"), ServiceTests.ThreeTools()) };
        await using var projectRig = new McpRig(projects: id => projectEnv);
        projectRig.Secrets.Set("mcp:demo:env", """{"TOKEN":"test-value"}""");

        await projectRig.Service.UpsertAsync(config with { WorkspaceId = "proj-a" });

        var spec = Assert.Single(projectEnv.Spawned);
        Assert.Equal("fake-mcp", spec.Program);
        Assert.Equal(StdinMode.Pipe, spec.Stdin);
        Assert.Equal("sub", spec.WorkingDirectory!.Value.FileName); // 本机风格的路径:Windows 上分隔符是反斜杠
        Assert.True(spec.WorkingDirectory!.Value.IsAbsolute);
        Assert.Equal("test-value", spec.Environment!["TOKEN"]);
        Assert.Single(spec.Environment); // 只有声明过的键
    }

    [Fact]
    public async Task Server_stderr_is_read_separately_kept_as_a_bounded_tail_and_never_becomes_protocol_input()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        var server = ServiceTests.ThreeTools();
        env.ProcessFactory = _ => new FakeProcess(env.Id, server);
        var options = Opts(stderrTail: 32);
        var transport = new StdioTransport(env, CommandSpec.Exec("x"), options);
        await using var connection = await McpConnection.OpenAsync(transport, options, "t", null, CancellationToken.None);

        await env.Processes[0].WriteStderrAsync("not protocol, and never exposed as tool content\n");
        await env.Processes[0].WriteStderrAsync(new string('e', 100) + "THE-END");

        await ServiceTests.Eventually(() => transport.Diagnostics?.EndsWith("THE-END", StringComparison.Ordinal) == true, "the stderr tail");
        Assert.True(transport.Diagnostics!.Length <= 32);
        Assert.Equal(3, (await connection.ListToolsAsync(CancellationToken.None)).Count); // 协议照常
    }

    [Fact]
    public async Task Disposing_closes_stdin_first_so_a_well_behaved_server_exits_by_itself_without_being_killed()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        env.ProcessFactory = _ => new FakeProcess(env.Id, ServiceTests.ThreeTools(), exitOnEof: true);
        var options = Opts();
        var connection = await McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("x"), options), options, "t", null, CancellationToken.None);

        await connection.DisposeAsync();

        var process = env.Processes[0];
        Assert.True(process.Exited.IsCompleted);
        Assert.Equal(0, (await process.Exited).ExitCode); // 自己退出的(EOF),不是被杀
    }

    [Fact]
    public async Task A_server_that_ignores_eof_is_killed_after_the_grace_period()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        env.ProcessFactory = _ => new FakeProcess(env.Id, ServiceTests.ThreeTools(), exitOnEof: false);
        var options = Opts();
        var connection = await McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("x"), options), options, "t", null, CancellationToken.None);

        await connection.DisposeAsync();

        Assert.True(env.Processes[0].Killed);
    }

    [Fact]
    public async Task A_command_that_cannot_be_started_maps_to_a_stable_key_and_a_lost_environment_to_another()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1")) { StartFailure = new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "ENOENT") };
        var options = Opts();

        var missing = await Assert.ThrowsAsync<McpException>(() => McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("nope"), options), options, "t", null, CancellationToken.None));
        env.StartFailure = new EnvironmentException(EnvironmentErrorCode.Disconnected);
        var lost = await Assert.ThrowsAsync<McpException>(() => McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("nope"), options), options, "t", null, CancellationToken.None));

        Assert.Equal(McpErrorKeys.CommandNotFound, missing.Key);
        Assert.Equal(McpErrorKeys.EnvironmentUnavailable, lost.Key);
    }

    [Fact]
    public async Task A_single_line_over_the_size_limit_closes_the_connection_with_a_protocol_error_instead_of_buffering_forever()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        env.ProcessFactory = _ => new FakeProcess(env.Id, ServiceTests.ThreeTools());
        var options = Opts(maxMessage: 4096);
        var transport = new StdioTransport(env, CommandSpec.Exec("x"), options);
        await using var connection = await McpConnection.OpenAsync(transport, options, "t", null, CancellationToken.None);

        await env.Processes[0].WriteRawAsync(new string('x', 10_000)); // 没有换行的超长输出

        var e = await Assert.ThrowsAsync<McpException>(() => transport.Completion);
        Assert.Equal(McpErrorKeys.Protocol, e.Key);
    }

    [Fact]
    public async Task The_last_message_a_process_wrote_before_exiting_is_not_lost()
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        var server = ServiceTests.ThreeTools();
        env.ProcessFactory = _ => new FakeProcess(env.Id, server, exitOnEof: false);
        var options = Opts();
        await using var connection = await McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("x"), options), options, "t", null, CancellationToken.None);
        var changed = 0;
        connection.ToolsListChanged += () => Interlocked.Increment(ref changed);

        await server.NotifyAsync("notifications/tools/list_changed");
        env.Processes[0].ExitNow(0);

        await ServiceTests.Eventually(() => Volatile.Read(ref changed) == 1, "the final notification");
    }

    // ───────────────────────── 真子进程 ─────────────────────────

    private const string ShellServer = """
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
          case "$line" in
            *'"method":"initialize"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"sh-server","version":"1"}}}\n' "$id";;
            *'"method":"tools/list"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"echo","description":"e","inputSchema":{"type":"object"}}]}}\n' "$id";;
            *'"method":"tools/call"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"token=%s"}]}}\n' "$id" "$MCP_TEST_TOKEN";;
          esac
        done
        """;

    [Fact]
    public async Task A_real_shell_script_server_connects_gets_its_secret_through_the_environment_and_ends_when_stdin_closes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The shell-script server needs a POSIX shell.");
        }

        await using var rig = new McpRig();
        // 用真实的本机环境(不换进程工厂):McpRig 的 Local 是个 TestEnvironment,没设 ProcessFactory 时就走真进程
        rig.Secrets.Set("mcp:sh:env", """{"MCP_TEST_TOKEN":"tok-REAL-123456"}""");

        var status = await rig.Service.UpsertAsync(McpRig.Stdio("sh") with { Command = "sh", Args = ["-c", ShellServer], EnvNames = ["MCP_TEST_TOKEN"] });

        Assert.Equal(McpState.Connected, status.State);
        Assert.Equal("sh-server", status.ServerName);
        var result = await McpRig.CallAsync(rig.Tool("mcp__sh__echo"));
        // 密钥确实到了子进程的环境里;而它回显给我们的结果里,值被抹掉了(脱敏)
        Assert.Equal("token=***", McpRig.TextOf(result));

        await rig.Service.SetEnabledAsync("sh", false);
        Assert.Empty(rig.Tools.List());
    }

    [Fact]
    public async Task A_real_process_that_exits_immediately_reports_its_exit_code_and_the_scrubbed_stderr_tail_only_to_the_owner_facing_test_result()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        await using var rig = new McpRig();
        rig.Secrets.Set("mcp:boom:env", """{"MCP_TEST_TOKEN":"tok-REAL-123456"}""");
        var config = McpRig.Stdio("boom") with { Command = "sh", Args = ["-c", "echo \"missing token tok-REAL-123456 at /secret/path\" >&2; exit 3"], EnvNames = ["MCP_TEST_TOKEN"] };
        rig.Configs.Put(config);

        var test = await rig.Service.TestConnectionAsync("boom");

        Assert.False(test.Ok);
        Assert.Equal(McpErrorKeys.ProcessExited, test.ErrorKey);
        Assert.Equal("3", test.ErrorParams["exitCode"]);
        Assert.Contains("/secret/path", test.Detail); // 给 owner 排错
        Assert.DoesNotContain("tok-REAL-123456", test.Detail);
        Assert.Contains("***", test.Detail);

        // 主题里的状态不带错误原文(路径、命令输出):只有 key + 结构化参数
        var status = await rig.Service.UpsertAsync(config with { Enabled = true });
        Assert.Equal(McpErrorKeys.ProcessExited, status.ErrorKey);
        Assert.DoesNotContain("/secret/path", string.Concat(status.ErrorParams.Values));
        Assert.DoesNotContain("tok-REAL-123456", rig.Log.All);
    }
}
