using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>插件终端的一次性启动规格(移植自 2.x <c>terminal-host.test.ts</c> 的「terminal launch specs」5 条)。</summary>
public class LaunchSpecTests
{
    private static TerminalLaunchSpec Spec(string scope = "ws", Dictionary<string, string>? env = null, params string[] argv) =>
        new(scope, env ?? [], argv);

    [Fact]
    public async Task A_matching_spec_injects_its_environment_and_writes_the_launch_line_into_the_shell()
    {
        await using var f = new Fixture();
        f.Manager.SetLaunchSpec("tab", Spec(env: new() { ["ANTHROPIC_BASE_URL"] = "https://relay" }, argv: "claude"));
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Equal("https://relay", Assert.Single(f.Env.Specs).Environment!["ANTHROPIC_BASE_URL"]);
        Assert.Equal("claude\n", f.Env.Terminals[0].WrittenText);
    }

    [Fact]
    public async Task The_spec_is_one_shot_creating_again_on_the_live_terminal_does_not_replay_the_launch_line()
    {
        await using var f = new Fixture();
        f.Manager.SetLaunchSpec("tab", Spec(argv: "claude"));
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Equal(1, f.Env.OpenCount);
        Assert.Single(f.Env.Terminals[0].Written);
        // 终端退出后用同一个 id 重新开:规格已经用掉了,不会再写
        f.Env.Terminals[0].Finish(0);
        await f.WaitExitedAsync("tab");
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Empty(f.Env.Terminals[1].Written);
    }

    [Fact]
    public async Task Spaces_and_quotes_in_argv_are_quoted_for_the_shell_before_they_reach_the_launch_line()
    {
        await using var f = new Fixture();
        f.Manager.SetLaunchSpec("tab", Spec(argv: ["codex", "-m", "gpt 5 it's"]));
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Equal("codex -m 'gpt 5 it'\\''s'\n", f.Env.Terminals[0].WrittenText);
    }

    [Fact]
    public async Task A_remote_environment_never_consumes_a_spec_so_the_secrets_in_it_are_not_lost_or_written()
    {
        await using var f = new Fixture(kind: EnvironmentKind.Ssh);
        f.Manager.SetLaunchSpec("tab", Spec(env: new() { ["KEY"] = "secret" }, argv: "claude"));
        var grant = await f.GrantAsync("tab");
        await f.Manager.CreateAsync(f.Request(id: "tab", approval: grant), Fixture.Alice);
        Assert.Null(Assert.Single(f.Env.Specs).Environment);
        Assert.Empty(f.Env.Terminals[0].Written);
    }

    [Fact]
    public async Task A_spec_for_another_scope_is_ignored_and_shutdown_clears_unclaimed_specs()
    {
        await using var f = new Fixture();
        f.Manager.SetLaunchSpec("tab", Spec(scope: "other", argv: "claude"));
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Empty(f.Env.Terminals[0].Written);

        f.Manager.SetLaunchSpec("tab2", Spec(argv: "claude"));
        await f.Manager.ShutdownAsync();
        await f.Manager.CreateAsync(f.Request(id: "tab2"), Fixture.Alice);
        Assert.Empty(f.Env.Terminals[1].Written);
    }

    [Fact]
    public async Task An_unclaimed_spec_expires()
    {
        await using var f = new Fixture();
        f.Manager.SetLaunchSpec("tab", Spec(argv: "claude"));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await f.Manager.CreateAsync(f.Request(id: "tab"), Fixture.Alice);
        Assert.Empty(f.Env.Terminals[0].Written);
    }

    [Theory]
    [InlineData(ShellDialect.Posix, "it's", "'it'\\''s'")]
    [InlineData(ShellDialect.PowerShell, "it's", "'it''s'")]
    [InlineData(ShellDialect.Cmd, "a b", "\"a b\"")]
    [InlineData(ShellDialect.Posix, "plain-arg_1.0/x:y", "plain-arg_1.0/x:y")]
    [InlineData(ShellDialect.Posix, "", "''")]
    [InlineData(ShellDialect.Posix, "$(rm -rf ~)", "'$(rm -rf ~)'")]
    public void The_launch_line_quotes_by_shell_dialect(ShellDialect dialect, string argument, string expected) =>
        Assert.Equal("tool " + expected, LaunchLine.Build(["tool", argument], dialect));

    [Fact]
    public void Cmd_refuses_arguments_it_cannot_quote_and_the_enter_key_follows_the_dialect()
    {
        Assert.Throws<EnvironmentException>(() => LaunchLine.Build(["tool", "a&b"], ShellDialect.Cmd));
        Assert.Throws<EnvironmentException>(() => LaunchLine.Build([], ShellDialect.Posix));
        Assert.Equal("\n", LaunchLine.Enter(ShellDialect.Posix));
        Assert.Equal("\r", LaunchLine.Enter(ShellDialect.Cmd));
        Assert.Equal("\r", LaunchLine.Enter(ShellDialect.PowerShell));
    }
}
