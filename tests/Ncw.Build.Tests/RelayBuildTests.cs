using Ncw.Build.Cli;
using Ncw.Build.Core;
using Ncw.Build.Publish;

namespace Ncw.Build.Tests;

/// <summary>build-relay:参数解析、发布计划、产物布局、警告处理(沿用 publish-agent 的规则)、自检的调用条件,以及一次真实的产物自检。</summary>
public class RelayBuildTests
{
    private static readonly BuildRelayCommand Real = new();

    private static ParsedArgs Parse(params string[] args) => ParsedArgs.Parse(Real.Spec, args);

    private static RelayCheckResult Passed() => new(TimeSpan.FromMilliseconds(5), 1234, true, 426, 0, TimeSpan.FromMilliseconds(1));

    private static (BuildContext Ctx, FakeRunner Runner, MemoryBuildLog Log, TempDir Dir) Setup(Rid host, Func<ProcessSpec, ProcessResult>? handler = null)
    {
        var dir = new TempDir();
        var runner = new FakeRunner();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, runner, log, host);
        runner.Handler = handler ?? (spec =>
        {
            // 假装 dotnet publish 产出了 ncw-relay、一个 pdb 和一个 dSYM
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            var rid = Rid.Parse(spec.Args[spec.Args.ToList().IndexOf("-r") + 1]);
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, rid.RelayExecutable), "bin");
            File.WriteAllText(Path.Combine(outDir, "Ncw.Relay.pdb"), "pdb");
            Directory.CreateDirectory(Path.Combine(outDir, "ncw-relay.dSYM"));
            return new ProcessResult(0, ["  Determining projects to restore...", "    0 Warning(s)"]);
        });
        return (ctx, runner, log, dir);
    }

    // ── 参数解析 ──

    [Fact]
    public void Arguments_parse_values_flags_and_reject_unknown_options()
    {
        var args = Parse("--rid", "linux-x64", "--aot", "--out=/x", "--timeout", "5");
        Assert.Equal("linux-x64", args.Get("rid"));
        Assert.True(args.Has("aot"));
        Assert.False(args.Has("framework-dependent"));
        Assert.Equal("/x", args.Get("out"));
        Assert.Throws<BuildException>(() => Parse("--bogus"));
        Assert.Throws<BuildException>(() => Parse("--aot=true")); // 开关不带值
        Assert.Throws<BuildException>(() => Parse("--rid")); // 缺值
        Assert.Throws<BuildException>(() => Parse("linux-x64")); // 不是选项
    }

    [Fact]
    public async Task The_command_is_registered_documented_and_listed_in_help()
    {
        Assert.Contains(Program.Commands, c => c.Spec.Name == "build-relay");
        Assert.Contains("--aot", Real.Spec.Usage);
        Assert.Contains("--framework-dependent", Real.Spec.Usage);
        Assert.Contains("--skip-check", Real.Spec.Usage);
        Assert.Equal(0, await Program.RunAsync(["build-relay", "--help"], null, CancellationToken.None));
        Assert.Equal(1, await Program.RunAsync(["build-relay", "--bogus"], null, CancellationToken.None));
    }

    [Fact]
    public async Task Aot_and_framework_dependent_are_mutually_exclusive_and_nothing_is_run()
    {
        var (ctx, runner, _, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            await Assert.ThrowsAsync<BuildException>(() => Real.RunAsync(ctx, Parse("--aot", "--framework-dependent"), default));
            Assert.Empty(runner.Calls);
            Assert.Throws<BuildException>(() => PublishPlans.Relay(ctx.Repo, TestKit.MacArm, "Release", "/o", null, aot: true, frameworkDependent: true));
        }
    }

    // ── 发布计划与产物布局 ──

    [Fact]
    public void Default_plan_is_a_self_contained_untrimmed_non_aot_publish()
    {
        var spec = PublishPlans.Relay(new RepoLayout("/r"), TestKit.LinuxX64, "Release", "/out", "1.2.3", aot: false, frameworkDependent: false);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal("publish", spec.Args[0]);
        Assert.EndsWith("Ncw.Relay.csproj", spec.Args[1]);
        Assert.Contains("linux-x64", spec.Args);
        Assert.Contains("--self-contained", spec.Args);
        Assert.Contains("-p:PublishAot=false", spec.Args);
        Assert.Contains("-p:PublishTrimmed=false", spec.Args);
        Assert.Contains("-p:Version=1.2.3", spec.Args);
        Assert.DoesNotContain("--no-self-contained", spec.Args);
    }

    [Fact]
    public void Framework_dependent_plan_does_not_bundle_the_runtime()
    {
        var spec = PublishPlans.Relay(new RepoLayout("/r"), TestKit.WinX64, "Release", "/out", null, aot: false, frameworkDependent: true);
        Assert.Contains("--no-self-contained", spec.Args);
        Assert.DoesNotContain("--self-contained", spec.Args);
        Assert.Contains("win-x64", spec.Args);
        Assert.DoesNotContain(spec.Args, a => a.StartsWith("-p:Version", StringComparison.Ordinal));
    }

    [Fact]
    public void Aot_plan_turns_nativeaot_on_and_never_passes_publishtrimmed_which_the_sdk_rejects()
    {
        var spec = PublishPlans.Relay(new RepoLayout("/r"), TestKit.MacArm, "Release", "/out", null, aot: true, frameworkDependent: false);
        Assert.Contains("-p:PublishAot=true", spec.Args);
        Assert.Contains("--self-contained", spec.Args);
        Assert.DoesNotContain(spec.Args, a => a.StartsWith("-p:PublishTrimmed", StringComparison.Ordinal));
    }

    [Fact]
    public void Artifact_layout_follows_the_publish_convention()
    {
        var repo = new RepoLayout("/r", "/r/artifacts");
        Assert.Equal(Path.Combine("/r", "src", "Ncw.Relay", "Ncw.Relay.csproj"), repo.RelayProject);
        Assert.Equal(Path.Combine("/r/artifacts", "publish", "linux-x64", "relay"), repo.PublishDir(TestKit.LinuxX64, "relay"));
        Assert.Equal("ncw-relay", TestKit.LinuxX64.RelayExecutable);
        Assert.Equal("ncw-relay", TestKit.MacArm.RelayExecutable);
        Assert.Equal("ncw-relay.exe", TestKit.WinX64.RelayExecutable);
    }

    // ── 命令:发布、警告、符号、自检 ──

    [Fact]
    public async Task Publish_moves_symbols_out_of_the_publish_directory_and_runs_the_self_check_on_the_host_platform()
    {
        var (ctx, runner, log, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            string? checked_ = null;
            var command = new BuildRelayCommand((exe, _, _, _) =>
            {
                checked_ = exe;
                return Task.FromResult(Passed());
            });
            Assert.Equal(0, await command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64"]), default));

            var outDir = ctx.Repo.PublishDir(TestKit.MacArm, "relay");
            Assert.True(File.Exists(Path.Combine(outDir, "ncw-relay")));
            Assert.False(File.Exists(Path.Combine(outDir, "Ncw.Relay.pdb")));
            var symbols = ctx.Repo.SymbolsDir(TestKit.MacArm) + "-relay";
            Assert.True(File.Exists(Path.Combine(symbols, "Ncw.Relay.pdb")));
            Assert.True(Directory.Exists(Path.Combine(symbols, "ncw-relay.dSYM")));
            Assert.Single(runner.Calls);
            Assert.Equal(Path.Combine(outDir, "ncw-relay"), checked_);
            Assert.Contains(log.Lines, l => l.StartsWith("INFO PASS", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_failing_self_check_fails_the_command()
    {
        var (ctx, _, _, dir) = Setup(TestKit.LinuxX64);
        using (dir)
        {
            var command = new BuildRelayCommand((_, _, _, _) => throw new BuildException("ncw-relay exited with 134 after SIGTERM; expected 0."));
            await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "linux-x64"]), default));
        }
    }

    [Fact]
    public async Task Skip_check_and_a_foreign_rid_publish_without_starting_the_artifact()
    {
        var (ctx, _, log, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            var started = 0;
            var command = new BuildRelayCommand((_, _, _, _) =>
            {
                started++;
                return Task.FromResult(Passed());
            });
            Assert.Equal(0, await command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64", "--skip-check"]), default));
            // 非 AOT 的中继可以为别的平台发布(不需要原生链接器),但没法在本机启动它
            Assert.Equal(0, await command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "linux-x64"]), default));
            Assert.Equal(0, started);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("--skip-check"));
            Assert.Contains(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("skipping the run check"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_il_or_trim_warning_fails_the_publish_even_when_dotnet_exits_zero(bool aot)
    {
        var (ctx, _, log, dir) = Setup(TestKit.MacArm, spec =>
        {
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "ncw-relay"), "bin");
            return new ProcessResult(0, ["/s/Relay.cs(1,1): warning IL2026: Using member requires unreferenced code [/s/Ncw.Relay.csproj]"]);
        });
        using (dir)
        {
            var command = new BuildRelayCommand((_, _, _, _) => Task.FromResult(Passed()));
            var args = aot ? new[] { "--rid", "osx-arm64", "--aot" } : ["--rid", "osx-arm64"];
            var ex = await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, args), default));
            Assert.Contains("IL/trim warning", ex.Message);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("IL2026"));
        }
    }

    [Fact]
    public async Task Whitelisted_clang_noise_is_reported_but_other_warnings_only_fail_under_strict_warnings()
    {
        var (ctx, _, log, dir) = Setup(TestKit.MacArm, spec =>
        {
            var outDir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "ncw-relay"), "bin");
            return new ProcessResult(0, ["clang: warning: could not build module (module cache: /m/ModuleCache) [-Wmodule-cache]", "ld: warning: something odd"]);
        });
        using (dir)
        {
            var command = new BuildRelayCommand((_, _, _, _) => Task.FromResult(Passed()));
            Assert.Equal(0, await command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64"]), default));
            Assert.Contains(log.Lines, l => l.Contains("whitelisted environment warning"));
            await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64", "--strict-warnings"]), default));
        }
    }

    [Fact]
    public async Task A_failed_dotnet_publish_or_a_missing_executable_fails()
    {
        var (ctx, runner, _, dir) = Setup(TestKit.MacArm, _ => new ProcessResult(1, ["error NETSDK1100: nope"]));
        using (dir)
        {
            var command = new BuildRelayCommand((_, _, _, _) => Task.FromResult(Passed()));
            await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64"]), default));
            runner.Handler = _ => new ProcessResult(0, []); // dotnet 成功但没有产出可执行文件
            var ex = await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64"]), default));
            Assert.Contains("does not exist", ex.Message);
        }
    }

    [Fact]
    public async Task Aot_refuses_to_cross_compile_across_operating_systems_and_non_release()
    {
        var (ctx, runner, _, dir) = Setup(TestKit.MacArm);
        using (dir)
        {
            var command = new BuildRelayCommand((_, _, _, _) => Task.FromResult(Passed()));
            var cross = await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "win-x64", "--aot"]), default));
            Assert.Contains("cross-compile", cross.Message);
            await Assert.ThrowsAsync<BuildException>(() => command.RunAsync(ctx, ParsedArgs.Parse(command.Spec, ["--rid", "osx-arm64", "--aot", "--configuration", "Debug"]), default));
            Assert.Empty(runner.Calls);
        }
    }

    // ── 真实自检 ──

    private static string? FindRelay()
    {
        if (Environment.GetEnvironmentVariable("NCW_RELAY_PATH") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        var name = OperatingSystem.IsWindows() ? "ncw-relay.exe" : "ncw-relay";
        var bin = Path.Combine(TestKit.RepoRoot(), "src", "Ncw.Relay", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    [Fact]
    public async Task A_real_relay_starts_answers_healthz_and_metrics_refuses_plain_http_and_exits_cleanly()
    {
        if (Environment.GetEnvironmentVariable("NCW_SKIP_RELAY_CHECK") == "1")
        {
            Assert.Skip("NCW_SKIP_RELAY_CHECK=1");
        }

        var relay = FindRelay();
        if (relay is null)
        {
            Assert.Skip("no ncw-relay binary: build src/Ncw.Relay first (dotnet build NextCoWork.slnx) or set NCW_RELAY_PATH");
        }

        var log = new MemoryBuildLog();
        var result = await RelayCheck.RunAsync(relay, log, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(result.MetricsOk, string.Join("\n", log.Lines));
        Assert.Equal(426, result.NonWebSocketStatus);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log.Lines, l => l.Contains("after stdin closed", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(log.Lines, l => l.Contains("after SIGTERM", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_self_check_reports_a_relay_that_dies_on_start_with_its_output()
    {
        using var dir = new TempDir();
        // 一个立刻退出、退出码非 0 的「中继」
        string exe;
        if (OperatingSystem.IsWindows())
        {
            exe = dir.Write("fake-relay.cmd", "@echo off\r\necho boom\r\nexit /b 3\r\n");
        }
        else
        {
            exe = dir.Write("fake-relay.sh", "#!/bin/sh\necho boom\nexit 3\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var ex = await Assert.ThrowsAsync<BuildException>(() => RelayCheck.RunAsync(exe, new MemoryBuildLog(), TimeSpan.FromSeconds(20), CancellationToken.None));
        Assert.Contains("exited before /healthz answered", ex.Message);
        Assert.Contains("boom", ex.Message);
    }
}
