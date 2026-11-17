using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>
/// ConPTY 的真实测试。只在 Windows 上执行(其它平台跳过);<b>作者没有 Windows 环境,这些用例还没有在真机上跑过</b>(docs/design/06 §9),
/// CI 上有 Windows 机器时第一次运行很可能需要微调。纯逻辑部分(命令行引号、环境块)在所有平台上跑。
/// </summary>
public class WindowsConPtyTests
{
    [Theory]
    [InlineData("""C:\Program Files\PowerShell\7\pwsh.exe""", "-NoLogo", "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    [InlineData("cmd.exe", "a b", "cmd.exe \"a b\"")]
    [InlineData("cmd.exe", "say \"hi\"", "cmd.exe \"say \\\"hi\\\"\"")]
    [InlineData("cmd.exe", "trailing\\", "cmd.exe trailing\\")]
    [InlineData("cmd.exe", "with space\\", "cmd.exe \"with space\\\\\"")]
    [InlineData("cmd.exe", "", "cmd.exe \"\"")]
    public void Command_lines_follow_the_CommandLineToArgv_rules(string program, string arg, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Build(program, [arg]));
    }

    [Fact]
    public void The_environment_block_is_sorted_and_double_nul_terminated()
    {
        var block = WindowsCommandLine.EnvironmentBlock([new("b", "2"), new("A", "1"), new("c", "")]);
        Assert.Equal("A=1\0b=2\0c=\0\0", new string(block));
    }

    [Fact]
    public async Task A_shell_runs_commands_and_reports_its_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY needs Windows");
        var env = new LocalEnvironment("dev", new LocalEnvironmentOptions { Shell = Environment.GetEnvironmentVariable("ComSpec"), TerminalFactory = LocalTerminals.CreateFactory() });
        var terminal = await env.OpenTerminalAsync(new TerminalSpec());
        await using var output = new Collector(terminal);
        await output.SendAsync("echo ncw-%OS%\r"); // 交互式 cmd:%OS% 直接展开(%% 只在批处理文件里才是转义,这里会原样输出 ncw-%Windows_NT%)
        await output.WaitForAsync("ncw-Windows_NT");
        await output.SendAsync("exit 7\r");
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(7, exit.ExitCode);
    }

    [Fact]
    public async Task Resize_and_kill_work()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY needs Windows");
        var env = new LocalEnvironment("dev", new LocalEnvironmentOptions { Shell = Environment.GetEnvironmentVariable("ComSpec"), TerminalFactory = LocalTerminals.CreateFactory() });
        var terminal = await env.OpenTerminalAsync(new TerminalSpec(Columns: 100, Rows: 30));
        await using var output = new Collector(terminal);
        await output.SendAsync("ping -n 100 127.0.0.1\r");
        await Task.Delay(500);
        await terminal.ResizeAsync(120, 40);
        await terminal.KillAsync();
        var exit = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ProcessExitReason.Killed, exit.Reason);
    }
}
