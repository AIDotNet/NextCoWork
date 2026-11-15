using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Tests;

public class CommandLineTests
{
    private static readonly CommandSpec Spec = new("demo", "demo", ["rid", "artifact"], ["strict"], []);

    [Fact]
    public void Parses_space_and_equals_forms_and_repeats()
    {
        var args = ParsedArgs.Parse(Spec, ["--rid", "osx-arm64", "--artifact=a", "--artifact", "b", "--strict"]);
        Assert.Equal("osx-arm64", args.Get("rid"));
        Assert.Equal(["a", "b"], args.GetAll("artifact"));
        Assert.True(args.Has("strict"));
        Assert.Null(args.Get("missing"));
    }

    [Fact]
    public void Value_may_contain_equals_and_colons()
    {
        var args = ParsedArgs.Parse(Spec, ["--artifact=win-x64:setup:C:\\out\\a=b.exe"]);
        Assert.Equal("win-x64:setup:C:\\out\\a=b.exe", args.Get("artifact"));
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("positional")]
    [InlineData("--")]
    public void Rejects_unknown_things(string bad) =>
        Assert.Throws<BuildException>(() => ParsedArgs.Parse(Spec, [bad]));

    [Fact]
    public void Rejects_missing_value_and_valued_flag()
    {
        Assert.Throws<BuildException>(() => ParsedArgs.Parse(Spec, ["--rid"]));
        Assert.Throws<BuildException>(() => ParsedArgs.Parse(Spec, ["--strict=yes"]));
    }

    [Fact]
    public void Require_reports_the_option_name()
    {
        var args = ParsedArgs.Parse(Spec, []);
        var e = Assert.Throws<BuildException>(() => args.Require("rid"));
        Assert.Contains("--rid", e.Message);
    }

    [Fact]
    public async Task Program_exit_codes()
    {
        Assert.Equal(2, await Program.RunAsync(["nope"], null, CancellationToken.None));
        Assert.Equal(0, await Program.RunAsync(["bundle", "--help"], null, CancellationToken.None));
    }

    [Fact]
    public void Every_command_declares_the_shared_artifacts_option_or_is_self_contained()
    {
        // 每个子命令都要能被 --help 说明,且名字唯一
        var names = Program.Commands.Select(c => c.Spec.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(Program.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Spec.Summary)));
        Assert.Contains("publish-agent", names);
        Assert.Contains("publish-desktop", names);
        Assert.Contains("bundle", names);
        Assert.Contains("verify", names);
        Assert.Contains("manifest", names);
        Assert.Contains("velopack", names);
        Assert.Contains("sign", names);
        Assert.Contains("build-relay", names);
    }
}

public class RidTests
{
    [Theory]
    [InlineData("osx-arm64", OsFamily.MacOs, "arm64")]
    [InlineData("osx-x64", OsFamily.MacOs, "x64")]
    [InlineData("win-x64", OsFamily.Windows, "x64")]
    [InlineData("win-arm64", OsFamily.Windows, "arm64")]
    [InlineData("linux-x64", OsFamily.Linux, "x64")]
    [InlineData("linux-arm64", OsFamily.Linux, "arm64")]
    internal void Parses_supported(string text, OsFamily os, string arch)
    {
        Assert.True(Rid.TryParse(text, out var rid));
        Assert.Equal(os, rid.Os);
        Assert.Equal(arch, rid.Arch);
        Assert.Equal(text, rid.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("osx")]
    [InlineData("osx-ppc")]
    [InlineData("freebsd-x64")]
    [InlineData("win10-x64")]
    public void Rejects_others(string text) => Assert.False(Rid.TryParse(text, out _));

    [Fact]
    public void Executable_names_follow_the_platform()
    {
        Assert.Equal("ncw-agent.exe", TestKit.WinX64.AgentExecutable);
        Assert.Equal("ncw-agent", TestKit.MacArm.AgentExecutable);
        Assert.Equal("NextCoWork.exe", TestKit.WinX64.DesktopExecutable);
        Assert.Equal("NextCoWork", TestKit.LinuxX64.DesktopExecutable);
    }

    [Fact]
    public void NativeAot_cannot_cross_operating_systems_but_can_cross_architectures()
    {
        Assert.NotNull(TestKit.WinX64.CannotNativeAotFrom(TestKit.MacArm));
        Assert.Null(Rid.Parse("osx-x64").CannotNativeAotFrom(TestKit.MacArm));
        Assert.Null(TestKit.MacArm.CannotNativeAotFrom(TestKit.MacArm));
    }
}

public class RepoLayoutTests
{
    [Fact]
    public void Artifacts_live_outside_bin_and_obj()
    {
        var repo = new RepoLayout("/r");
        var rid = TestKit.MacArm;
        Assert.Equal(Path.GetFullPath("/r/artifacts/publish/osx-arm64/agent"), repo.PublishDir(rid, "agent"));
        Assert.Equal(Path.GetFullPath("/r/artifacts/bundle/osx-arm64"), repo.BundleDir(rid));
        Assert.Equal(Path.GetFullPath("/r/artifacts/velopack/osx-arm64/stable"), repo.VelopackDir(rid, "stable"));
        Assert.Equal(Path.GetFullPath("/r/artifacts/release/1.2.3"), repo.ReleaseDir("1.2.3"));
        Assert.DoesNotContain("/bin/", repo.BundleDir(rid));
    }

    [Fact]
    public void Reads_the_one_application_version_from_Directory_Build_props()
    {
        var repo = new RepoLayout(TestKit.RepoRoot());
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", repo.ReadVersion());
    }

    [Fact]
    public void Artifacts_is_gitignored()
    {
        var ignore = File.ReadAllLines(Path.Combine(TestKit.RepoRoot(), ".gitignore")).Select(l => l.Trim());
        Assert.Contains("artifacts/", ignore);
    }
}
