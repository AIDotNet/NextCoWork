using Ncw.Build.Core;
using Ncw.Build.Publish;

namespace Ncw.Build.Tests;

public class WarningFilterTests
{
    [Theory]
    [InlineData("/src/A.cs(12,3): warning IL2026: Using member 'X' which has 'RequiresUnreferencedCodeAttribute' [/src/A.csproj]", "IL2026")]
    [InlineData("/src/A.cs(1,1): warning IL3050: Using member requires dynamic code [/src/A.csproj]", "IL3050")]
    [InlineData("ILC : AOT analysis warning IL3053: Assembly 'X' produced AOT analysis warnings.", "IL3053")]
    [InlineData("ILC : Trim analysis warning IL2104: Assembly 'Y' produced trim warnings", "IL2104")]
    [InlineData("ILLink : warning IL2075: 'this' argument does not satisfy", "IL2075")]
    [InlineData("warning ILC1234: something", "ILC1234")]
    public void Trim_and_aot_warnings_are_failures(string line, string code)
    {
        var w = WarningFilter.Classify(line);
        Assert.NotNull(w);
        Assert.Equal(WarningKind.Trim, w.Kind);
        Assert.Equal(code, w.Code);
    }

    [Fact]
    public void Analysis_warning_without_a_code_is_still_a_trim_warning()
    {
        Assert.Equal(WarningKind.Trim, WarningFilter.Classify("ILC : Trim analysis warning: bad")!.Kind);
    }

    [Theory]
    [InlineData("clang: warning: could not build module 'Foundation' (module cache: /Users/x/.cache/clang/ModuleCache) [-Wmodule-cache]")]
    [InlineData("warning: Foundation-ABC123.pcm: module file not found")]
    [InlineData("clang: warning: unable to write /var/folders/ModuleCache/ABC/Foundation.pcm: permission denied")]
    public void Clang_module_cache_pcm_warnings_are_whitelisted(string line)
    {
        var w = WarningFilter.Classify(line);
        Assert.NotNull(w);
        Assert.Equal(WarningKind.AllowedEnvironment, w.Kind);
    }

    [Fact]
    public void Other_warnings_are_reported_but_not_trim()
    {
        Assert.Equal(WarningKind.Other, WarningFilter.Classify("/src/A.cs(1,1): warning CS0168: The variable 'x' is declared but never used")!.Kind);
        Assert.Equal(WarningKind.Other, WarningFilter.Classify("ld: warning: ignoring duplicate libraries: '-lm'")!.Kind);
    }

    [Theory]
    [InlineData("    0 Warning(s)")]
    [InlineData("Build succeeded.")]
    [InlineData("  Generating native code")]
    [InlineData("  Ncw.Agent -> /x/ncw-agent.dll")]
    [InlineData("")]
    public void Ordinary_lines_are_not_warnings(string line) => Assert.Null(WarningFilter.Classify(line));

    [Fact]
    public void A_trim_code_wins_over_the_environment_whitelist()
    {
        // 一行里同时出现 .pcm 和 IL 码,按裁剪警告算:白名单只放行「纯环境」警告。
        Assert.Equal(WarningKind.Trim, WarningFilter.Classify("warning IL2026: something about Foo.pcm")!.Kind);
    }

    [Fact]
    public void Duplicates_across_projects_collapse()
    {
        var report = WarningFilter.Analyze(
        [
            "/s/A.cs(1,1): warning IL2026: Using X [/s/One.csproj]",
            "/s/A.cs(1,1): warning IL2026: Using X [/s/Two.csproj]",
            "/s/A.cs(1,1): warning IL2026: Using X [/s/One.csproj]",
            "/s/B.cs(1,1): warning IL2026: Using Y [/s/One.csproj]",
        ]);
        Assert.Equal(2, report.Trim.Count());
        Assert.False(report.TrimClean);
    }

    [Fact]
    public void Clean_output_with_only_whitelisted_noise_passes()
    {
        var report = WarningFilter.Analyze(
        [
            "  Generating native code",
            "clang: warning: could not build module (module cache: /x/ModuleCache) [-Wmodule-cache]",
            "    0 Warning(s)",
        ]);
        Assert.True(report.TrimClean);
        Assert.Single(report.AllowedEnvironment);
        Assert.Empty(report.Other);
    }
}

public class PublishCommandTests
{
    private static (BuildContext Ctx, FakeRunner Runner, MemoryBuildLog Log, TempDir Dir) Setup(Rid host, Func<ProcessSpec, ProcessResult>? handler = null)
    {
        var dir = new TempDir();
        var runner = new FakeRunner();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, runner, log, host);
        runner.Handler = handler ?? (spec =>
        {
            // 假装 dotnet publish 产出了可执行文件、一个 pdb 和一个 dSYM
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, ctx.Host.Os == OsFamily.Windows ? "ncw-agent.exe" : "ncw-agent"), "bin");
            File.WriteAllText(Path.Combine(outDir, "Ncw.Core.pdb"), "pdb");
            Directory.CreateDirectory(Path.Combine(outDir, "ncw-agent.dSYM"));
            return new ProcessResult(0, ["  Generating native code", "clang: warning: x (module cache: /m/ModuleCache) [-Wmodule-cache]"]);
        });
        return (ctx, runner, log, dir);
    }

    [Fact]
    public void Desktop_publish_plan_embeds_the_release_public_keys_only_when_given_and_escapes_msbuild_separators()
    {
        var repo = new RepoLayout("/r");
        Assert.DoesNotContain(PublishPlans.Desktop(repo, TestKit.MacArm, "Release", "/out", null, false).Args, a => a.Contains("NcwReleasePublicKeys", StringComparison.Ordinal));
        Assert.DoesNotContain(PublishPlans.Desktop(repo, TestKit.MacArm, "Release", "/out", null, false, "  ").Args, a => a.Contains("NcwReleasePublicKeys", StringComparison.Ordinal));

        var args = PublishPlans.Desktop(repo, TestKit.MacArm, "Release", "/out", "3.0.1", false, "k1:AAAA=,k2:BBBB=").Args;
        // 逗号 / 分号在 MSBuild 命令行属性里是分隔符:必须转义,否则第二把公钥会被当成另一个属性
        Assert.Contains("-p:NcwReleasePublicKeys=k1:AAAA=%2Ck2:BBBB=", args);
        Assert.Contains("-p:Version=3.0.1", args);
    }

    [Fact]
    public async Task Publish_desktop_embeds_the_keys_from_the_environment_and_warns_when_there_are_none()
    {
        foreach (var withKeys in new[] { true, false })
        {
            using var dir = new TempDir();
            var runner = new FakeRunner
            {
                Handler = spec =>
                {
                    var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "NextCoWork"), "bin");
                    return new ProcessResult(0, []);
                },
            };
            var log = new MemoryBuildLog();
            var ctx = TestKit.Context(dir, runner, log, TestKit.MacArm, withKeys ? new Dictionary<string, string> { ["NCW_RELEASE_PUBLIC_KEYS"] = "dev-1:AAAA=" } : null);

            Assert.Equal(0, await Program.RunAsync(["publish-desktop", "--rid", "osx-arm64"], ctx, default));

            var plan = Assert.Single(runner.Calls);
            Assert.Equal(withKeys, plan.Args.Contains("-p:NcwReleasePublicKeys=dev-1:AAAA="));
            Assert.Equal(!withKeys, log.Lines.Any(l => l.Contains("no release public key", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Agent_publish_plan_only_compiles_the_dev_kms_provider_in_when_asked()
    {
        var repo = new RepoLayout("/r");
        Assert.Contains("-p:NcwDevKms=true", PublishPlans.Agent(repo, TestKit.MacArm, "Release", "/out", null, devKms: true).Args);
        Assert.Contains("-p:NcwDevKms=false", PublishPlans.Agent(repo, TestKit.MacArm, "Release", "/out", null).Args);
    }

    [Theory]
    [InlineData("ascii", false)]
    [InlineData("utf16", false)]
    [InlineData("packed-utf16-without-the-last-high-byte", false)]
    [InlineData("none", true)]
    [InlineData("split-across-buffer", false)]
    public void The_binary_scan_finds_the_dev_kms_marker_in_ascii_or_utf16_even_across_a_buffer_boundary(string kind, bool expectedAbsent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncw-scan-{Guid.NewGuid():N}.bin");
        try
        {
            var marker = PublishPlans.DevKmsMarker;
            var filler = new byte[(1 << 20) - 5]; // 让标记正好跨过 1 MiB 的读缓冲边界
            byte[] payload = kind switch
            {
                "ascii" => [.. System.Text.Encoding.ASCII.GetBytes("xx" + marker + "yy")],
                "utf16" => [.. System.Text.Encoding.Unicode.GetBytes("x" + marker + "y")],
                // NativeAOT 实测排布:最后一个字符的 0x00 高位字节被下一条记录的头占掉。
                "packed-utf16-without-the-last-high-byte" => [.. System.Text.Encoding.Unicode.GetBytes(marker)[..^1], 0x79, 0x2b, 0xe8, 0x03],
                "split-across-buffer" => [.. filler, .. System.Text.Encoding.ASCII.GetBytes(marker)],
                _ => [.. System.Text.Encoding.ASCII.GetBytes("nothing to see here")],
            };
            File.WriteAllBytes(path, payload);

            Assert.Equal(!expectedAbsent, PublishAgentCommand.BinaryContains(path, marker));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Agent_publish_plan_is_nativeaot_for_the_rid()
    {
        var repo = new RepoLayout("/r");
        var spec = PublishPlans.Agent(repo, TestKit.MacArm, "Release", "/out", "1.2.3");
        Assert.Contains("-p:NcwDevKms=false", spec.Args);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal("publish", spec.Args[0]);
        Assert.Contains("-r", spec.Args);
        Assert.Contains("osx-arm64", spec.Args);
        Assert.Contains("-p:PublishAot=true", spec.Args);
        Assert.Contains("--self-contained", spec.Args);
        Assert.Contains("-p:Version=1.2.3", spec.Args);
        Assert.EndsWith("Ncw.Agent.csproj", spec.Args[1]);
    }

    [Fact]
    public void Desktop_publish_plan_is_self_contained_and_untrimmed()
    {
        var spec = PublishPlans.Desktop(new RepoLayout("/r"), TestKit.WinX64, "Release", "/out", null, readyToRun: false);
        Assert.Contains("win-x64", spec.Args);
        Assert.Contains("-p:PublishTrimmed=false", spec.Args);
        Assert.Contains("-p:PublishReadyToRun=false", spec.Args);
        Assert.DoesNotContain(spec.Args, a => a.StartsWith("-p:Version", StringComparison.Ordinal));
        Assert.EndsWith("Ncw.Desktop.csproj", spec.Args[1]);
    }

    [Fact]
    public async Task Publish_agent_succeeds_moves_symbols_and_tolerates_whitelisted_noise()
    {
        var (ctx, runner, log, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            var code = await Program.RunAsync(["publish-agent", "--rid", "osx-arm64"], ctx, default);
            Assert.Equal(0, code);
            var outDir = ctx.Repo.PublishDir(TestKit.MacArm, "agent");
            Assert.True(File.Exists(Path.Combine(outDir, "ncw-agent")));
            Assert.False(File.Exists(Path.Combine(outDir, "Ncw.Core.pdb")));
            Assert.True(File.Exists(Path.Combine(ctx.Repo.SymbolsDir(TestKit.MacArm), "Ncw.Core.pdb")));
            Assert.True(Directory.Exists(Path.Combine(ctx.Repo.SymbolsDir(TestKit.MacArm), "ncw-agent.dSYM")));
            Assert.Single(runner.Calls);
            Assert.Contains(log.Lines, l => l.Contains("whitelisted environment warning"));
        }
    }

    [Fact]
    public async Task Publish_agent_fails_on_a_trim_warning_even_when_dotnet_exits_zero()
    {
        var (ctx, _, log, dir) = Setup(TestKit.MacArm, spec =>
        {
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "ncw-agent"), "bin");
            return new ProcessResult(0, ["/s/A.cs(1,1): warning IL3050: Using member requires dynamic code [/s/A.csproj]"]);
        });
        using (dir)
        {
            var code = await Program.RunAsync(["publish-agent", "--rid", "osx-arm64"], ctx, default);
            Assert.Equal(1, code);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("IL3050"));
            Assert.Contains(log.Lines, l => l.Contains("zero", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Publish_agent_fails_when_dotnet_fails()
    {
        var (ctx, _, _, dir) = Setup(TestKit.MacArm, _ => new ProcessResult(1, ["error NETSDK1100: nope"]));
        using (dir)
        {
            Assert.Equal(1, await Program.RunAsync(["publish-agent", "--rid", "osx-arm64"], ctx, default));
        }
    }

    [Fact]
    public async Task Publish_agent_refuses_to_cross_compile_across_operating_systems_and_non_release()
    {
        var (ctx, runner, log, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            Assert.Equal(1, await Program.RunAsync(["publish-agent", "--rid", "win-x64"], ctx, default));
            Assert.Equal(1, await Program.RunAsync(["publish-agent", "--rid", "osx-arm64", "--configuration", "Debug"], ctx, default));
            Assert.Empty(runner.Calls);
            Assert.Contains(log.Lines, l => l.Contains("cross-compile"));
        }
    }

    [Fact]
    public async Task Strict_warnings_turns_other_warnings_into_failures()
    {
        var (ctx, _, _, dir) = Setup(TestKit.MacArm, spec =>
        {
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "ncw-agent"), "bin");
            return new ProcessResult(0, ["ld: warning: something odd"]);
        });
        using (dir)
        {
            Assert.Equal(0, await Program.RunAsync(["publish-agent", "--rid", "osx-arm64"], ctx, default));
            Assert.Equal(1, await Program.RunAsync(["publish-agent", "--rid", "osx-arm64", "--strict-warnings"], ctx, default));
        }
    }

    [Fact]
    public async Task Publish_desktop_removes_a_stray_agent_directory()
    {
        var (ctx, _, _, dir) = Setup(TestKit.LinuxX64, spec =>
        {
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(Path.Combine(outDir, "agent"));
            File.WriteAllText(Path.Combine(outDir, "agent", "ncw-agent"), "dev build");
            File.WriteAllText(Path.Combine(outDir, "NextCoWork"), "bin");
            return new ProcessResult(0, []);
        });
        using (dir)
        {
            Assert.Equal(0, await Program.RunAsync(["publish-desktop", "--rid", "linux-x64"], ctx, default));
            Assert.False(Directory.Exists(Path.Combine(ctx.Repo.PublishDir(TestKit.LinuxX64, "desktop"), "agent")));
        }
    }
}
