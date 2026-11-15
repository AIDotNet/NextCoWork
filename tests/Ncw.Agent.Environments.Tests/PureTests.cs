using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Environments.Tests;

public class ShellCommandTests
{
    [Theory]
    [InlineData("/bin/bash", ShellDialect.Posix)]
    [InlineData("/usr/bin/zsh", ShellDialect.Posix)]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", ShellDialect.Posix)]
    [InlineData("cmd.exe", ShellDialect.Cmd)]
    [InlineData(@"C:\Windows\System32\CMD.EXE", ShellDialect.Cmd)]
    [InlineData("pwsh.exe", ShellDialect.PowerShell)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ShellDialect.PowerShell)]
    public void Dialect_follows_the_shell_file_name(string shell, ShellDialect expected) =>
        Assert.Equal(expected, ShellCommands.DialectOf(shell));

    [Fact]
    public void Posix_quote_survives_embedded_single_quotes()
    {
        Assert.Equal("'a'\\''b'", ShellCommands.PosixQuote("a'b"));
        Assert.Equal("''", ShellCommands.PosixQuote(""));
        Assert.Equal("'$HOME `x` \"y\"'", ShellCommands.PosixQuote("$HOME `x` \"y\""));
        Assert.Equal("'a''b'", ShellCommands.PowerShellQuote("a'b"));
    }

    [Fact]
    public void Build_uses_the_arguments_each_dialect_needs()
    {
        var posix = ShellCommands.Build("/bin/sh", ShellDialect.Posix, "echo hi");
        Assert.Equal(["-c", "echo hi"], posix.Args);
        Assert.Null(posix.VerbatimArguments);

        // cmd 的引号规则不同于 CommandLineToArgv:必须原样传整条命令行
        var cmd = ShellCommands.Build("cmd.exe", ShellDialect.Cmd, "dir \"a b\"");
        Assert.Empty(cmd.Args);
        Assert.Equal("/d /s /c \"dir \"a b\"\"", cmd.VerbatimArguments);

        var ps = ShellCommands.Build("pwsh.exe", ShellDialect.PowerShell, "Get-Location");
        Assert.Equal("-EncodedCommand", ps.Args[3]);
        Assert.Equal("Get-Location", Encoding.Unicode.GetString(Convert.FromBase64String(ps.Args[4])));
    }
}

public class OutputPolicyTests
{
    private static OutputSnapshot Feed(IOutputPolicy policy, params string[] chunks)
    {
        var buffer = policy.CreateBuffer();
        foreach (var chunk in chunks)
        {
            buffer.Append(Encoding.UTF8.GetBytes(chunk));
        }

        return buffer.Snapshot();
    }

    [Fact]
    public void Nothing_is_dropped_below_the_limit()
    {
        var snapshot = Feed(OutputPolicies.Tail(100), "hello ", "world");
        Assert.False(snapshot.Truncated);
        Assert.Equal("hello world", snapshot.ToText());
        Assert.Equal(11, snapshot.TotalBytes);
    }

    [Fact]
    public void Tail_keeps_the_end_and_counts_what_it_dropped()
    {
        var snapshot = Feed(OutputPolicies.Tail(10), "0123456789", "abcde", "XYZ");
        Assert.True(snapshot.Truncated);
        Assert.Equal(18, snapshot.TotalBytes);
        Assert.Equal(8, snapshot.OmittedBytes);
        Assert.Equal("6789abcdeXYZ"[^10..], snapshot.ToText());
        Assert.Equal(10, snapshot.Tail.Length);
    }

    [Fact]
    public void Head_keeps_the_beginning()
    {
        var snapshot = Feed(OutputPolicies.Head(4), "abc", "defgh");
        Assert.Equal("abcd", snapshot.ToText());
        Assert.Equal(8, snapshot.TotalBytes);
        Assert.Equal(4, snapshot.OmittedBytes);
    }

    [Fact]
    public void Head_and_tail_leave_a_gap_in_the_middle()
    {
        var snapshot = Feed(OutputPolicies.HeadAndTail(3, 3), "0123456789");
        Assert.Equal("012...789", snapshot.ToText("..."));
        Assert.Equal(4, snapshot.OmittedBytes);
    }

    [Fact]
    public void A_single_chunk_larger_than_the_tail_keeps_only_its_end()
    {
        var snapshot = Feed(OutputPolicies.Tail(3), "abcdefgh");
        Assert.Equal("fgh", snapshot.ToText());
        Assert.Equal(5, snapshot.OmittedBytes);
    }

    [Fact]
    public void Tail_cut_in_the_middle_of_a_multibyte_character_does_not_produce_garbage()
    {
        // "你好世界" 每个字 3 字节;保留尾部 7 字节会从「界」的中间之前一个字的中间切开
        var buffer = OutputPolicies.Tail(7).CreateBuffer();
        buffer.Append(Encoding.UTF8.GetBytes("你好世界"));
        var text = buffer.Snapshot().ToText();
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal("世界", text);
    }

    [Fact]
    public void Unbounded_keeps_everything()
    {
        var snapshot = Feed(OutputPolicies.Unbounded, new string('x', 100_000));
        Assert.False(snapshot.Truncated);
        Assert.Equal(100_000, snapshot.ToText().Length);
    }

    [Fact]
    public void Many_small_chunks_stay_bounded()
    {
        var buffer = OutputPolicies.Tail(1000).CreateBuffer();
        var chunk = new byte[7];
        Array.Fill(chunk, (byte)'z');
        for (var i = 0; i < 100_000; i++)
        {
            buffer.Append(chunk);
        }

        var snapshot = buffer.Snapshot();
        Assert.Equal(700_000, snapshot.TotalBytes);
        Assert.Equal(1000, snapshot.Tail.Length);
    }
}

public class TextNewlineTests
{
    [Fact]
    public void Detect_picks_the_dominant_style()
    {
        Assert.Null(TextNewlines.Detect("no newline"));
        Assert.Equal(NewlineStyle.Lf, TextNewlines.Detect("a\nb\n"));
        Assert.Equal(NewlineStyle.CrLf, TextNewlines.Detect("a\r\nb\r\n"));
        Assert.Equal(NewlineStyle.CrLf, TextNewlines.Detect("a\r\nb\r\nc\n"));
        Assert.Equal(NewlineStyle.Lf, TextNewlines.Detect("a\r\nb\nc\n"));
    }

    [Fact]
    public void Round_trip_preserves_crlf()
    {
        var original = "line1\r\nline2\r\n";
        var lf = TextNewlines.ToLf(original);
        Assert.Equal("line1\nline2\n", lf);
        Assert.Equal(original, TextNewlines.Apply(lf, NewlineStyle.CrLf));
        Assert.Equal(lf, TextNewlines.Apply(lf, NewlineStyle.Lf));
    }

    [Fact]
    public void Bom_helpers()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'a'];
        Assert.True(TextNewlines.HasUtf8Bom(bytes));
        Assert.Equal([(byte)'a'], TextNewlines.StripUtf8Bom(bytes).ToArray());
        Assert.False(TextNewlines.HasUtf8Bom("a"u8));
    }
}

public class FactsAndProbeTests
{
    private static EnvironmentFacts Facts(EnvironmentOs os = EnvironmentOs.Linux, string shell = "/bin/bash") => new()
    {
        Os = os, OsVersion = "1", Shell = shell, Home = "/h", Hostname = "h", Username = "u",
    };

    [Fact]
    public void Derived_facts_follow_the_os()
    {
        Assert.Equal(PathStyle.Posix, Facts().PathStyle);
        Assert.Equal("\n", Facts().NewLine);
        var windows = Facts(EnvironmentOs.Windows, "pwsh.exe");
        Assert.Equal(PathStyle.Windows, windows.PathStyle);
        Assert.Equal(ShellDialect.PowerShell, windows.ShellDialect);
        Assert.Equal("\r\n", windows.NewLine);
    }

    [Fact]
    public void Probe_output_is_parsed_into_tools_and_git_version()
    {
        var facts = ToolProbe.Parse(Facts(), "tool:git\ntool:rg\ngitversion:git version 2.43.0\n");
        Assert.True(facts.HasTool("rg"));
        Assert.True(facts.HasTool("git"));
        Assert.False(facts.HasTool("grep"));
        Assert.Equal("2.43.0", facts.GitVersion);
        Assert.Null(ToolProbe.Parse(Facts(), "").GitVersion);
    }

    [Fact]
    public void EnsureGeneration_rejects_a_stale_generation()
    {
        using var temp = new TempDir();
        var environment = new LocalEnvironment("dev");
        environment.EnsureGeneration(0);
        var error = Assert.Throws<EnvironmentException>(() => environment.EnsureGeneration(3));
        Assert.Equal(EnvironmentErrorCode.StaleGeneration, error.Code);
    }

    [Fact]
    public void Environment_ids_carry_device_and_environment()
    {
        var id = EnvironmentId.Local("dev-1");
        Assert.True(id.IsLocal);
        Assert.Equal("dev-1/local", id.ToString());
        Assert.NotEqual(id, new EnvironmentId("dev-2", "local"));
    }

    [Fact]
    public void Command_spec_validation()
    {
        Assert.Throws<EnvironmentException>(() => new CommandSpec().Validate());
        Assert.Throws<EnvironmentException>(() => new CommandSpec { Program = "a", ShellCommand = "b" }.Validate());
        Assert.Throws<EnvironmentException>(() => CommandSpec.Exec("a\0b").Validate());
        Assert.Throws<EnvironmentException>(() => (CommandSpec.Shell("x") with { Environment = new Dictionary<string, string> { ["BAD;NAME"] = "v" } }).Validate());
        CommandSpec.Exec("ls", "-la").Validate();
        CommandSpec.Shell("echo hi").Validate();
    }
}
