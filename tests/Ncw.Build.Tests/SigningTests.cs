using Ncw.Build.Bundle;
using Ncw.Build.Core;
using Ncw.Build.Signing;

namespace Ncw.Build.Tests;

public class SigningPlanTests
{
    // 运行时自己的托管程序集:测试输出目录里一定有(自包含之外的测试宿主从 AppContext.BaseDirectory 找得到 Ncw.Build.dll)。
    private static readonly string CoreLib = Path.Combine(AppContext.BaseDirectory, "Ncw.Build.dll");

    private static readonly byte[] MachOMagic = [0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0];

    private static string FakeApp(TempDir dir)
    {
        dir.WriteBytes("A.app/Contents/MacOS/NextCoWork", MachOMagic);
        dir.WriteBytes("A.app/Contents/MacOS/Avalonia.dll", [0x4D, 0x5A, 0, 0]); // 托管 dll:在 MacOS 里也要签
        dir.WriteBytes("A.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/MacOS/NextCoWork Agent", MachOMagic);
        dir.WriteBytes("A.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/MacOS/libe_sqlite3.dylib", MachOMagic);
        dir.Write("A.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/Info.plist", "<plist/>");
        dir.Write("A.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/Resources/AppIcon.icns", "icns");
        dir.Write("A.app/Contents/Info.plist", "<plist/>");
        dir.Write("A.app/Contents/Resources/readme.txt", "text");
        return dir.Sub("A.app");
    }

    [Fact]
    public void Mach_o_detection_by_magic()
    {
        using var dir = new TempDir();
        Assert.True(SigningPlans.IsMachO(dir.WriteBytes("a", MachOMagic)));
        Assert.True(SigningPlans.IsMachO(dir.WriteBytes("fat", [0xCA, 0xFE, 0xBA, 0xBE])));
        Assert.False(SigningPlans.IsMachO(dir.WriteBytes("text", "hello"u8.ToArray())));
        Assert.False(SigningPlans.IsMachO(dir.WriteBytes("short", [1, 2])));
        Assert.False(SigningPlans.IsMachO(dir.Sub("missing")));
    }

    [Fact]
    public void Everything_under_Contents_MacOS_is_signed_inside_out_with_the_main_executable_last()
    {
        using var dir = new TempDir();
        var app = FakeApp(dir);
        var nested = SigningPlans.MacNestedCode(app);
        Assert.Contains(Path.Combine(app, "Contents", "MacOS", "Avalonia.dll"), nested);
        Assert.DoesNotContain(Path.Combine(app, "Contents", "Info.plist"), nested);
        Assert.DoesNotContain(Path.Combine(app, "Contents", "Resources", "readme.txt"), nested);
        Assert.Equal(Path.Combine(app, "Contents", "MacOS", "NextCoWork"), nested[^1]);
        // 嵌套的 Agent 包整组在前:它的文件(主程序最后)→ 嵌套包本身 → 外层包的文件
        var list = nested.ToList();
        var agentApp = Path.Combine(app, "Contents", "MacOS", "agent", "NextCoWork Agent.app");
        var agentExe = Path.Combine(agentApp, "Contents", "MacOS", "NextCoWork Agent");
        var agentLib = Path.Combine(agentApp, "Contents", "MacOS", "libe_sqlite3.dylib");
        Assert.True(list.IndexOf(agentLib) < list.IndexOf(agentExe));
        Assert.Equal(list.IndexOf(agentExe) + 1, list.IndexOf(agentApp));
        Assert.True(list.IndexOf(agentApp) < list.IndexOf(Path.Combine(app, "Contents", "MacOS", "Avalonia.dll")));
        // 嵌套包的 Info.plist 和图标由签嵌套包时封进去,不单独签
        Assert.DoesNotContain(Path.Combine(agentApp, "Contents", "Info.plist"), list);
        Assert.DoesNotContain(Path.Combine(agentApp, "Contents", "Resources", "AppIcon.icns"), list);
    }

    [Fact]
    public void Sign_sequence_has_hardened_runtime_timestamp_and_entitlements_only_for_executables_and_the_bundle()
    {
        using var dir = new TempDir();
        var app = FakeApp(dir);
        var config = new MacSignConfig("Developer ID Application: Example (TEAM123456)", "/k.keychain", "/e.entitlements");
        var seq = SigningPlans.MacSignSequence(app, SigningPlans.MacNestedCode(app), config);

        Assert.All(seq, s =>
        {
            Assert.Equal("codesign", s.FileName);
            Assert.Contains("runtime", s.Args);
            Assert.Contains("--timestamp", s.Args);
            Assert.Contains("--keychain", s.Args);
            Assert.Equal("Developer ID Application: Example (TEAM123456)", s.Args[s.Args.ToList().IndexOf("--sign") + 1]);
        });
        Assert.Equal(app, seq[^1].Args[^1]);
        Assert.Contains("--entitlements", seq[^1].Args);
        var dylib = seq.Single(s => s.Args[^1].EndsWith("libe_sqlite3.dylib", StringComparison.Ordinal));
        Assert.DoesNotContain("--entitlements", dylib.Args);
        var dll = seq.Single(s => s.Args[^1].EndsWith("Avalonia.dll", StringComparison.Ordinal));
        Assert.DoesNotContain("--entitlements", dll.Args);
        var agent = seq.Single(s => s.Args[^1].EndsWith("NextCoWork Agent", StringComparison.Ordinal));
        Assert.Contains("--entitlements", agent.Args);
        var agentApp = seq.Single(s => s.Args[^1].EndsWith("NextCoWork Agent.app", StringComparison.Ordinal));
        Assert.Contains("--entitlements", agentApp.Args);
    }

    [Fact]
    public void Ad_hoc_identity_cannot_use_a_secure_timestamp()
    {
        var spec = SigningPlans.MacSignFile("/x", new MacSignConfig("-", null, null), withEntitlements: false);
        Assert.Contains("--timestamp=none", spec.Args);
        Assert.DoesNotContain("--timestamp", spec.Args);
    }

    [Fact]
    public void Notary_credentials_come_from_a_profile_or_the_apple_id_triple_and_the_password_is_redacted()
    {
        Assert.Null(SigningPlans.NotaryConfigFrom(_ => null));
        Assert.Null(SigningPlans.NotaryConfigFrom(n => n == "NCW_NOTARY_APPLE_ID" ? "a@b.c" : null)); // 不完整
        var profile = SigningPlans.NotaryConfigFrom(n => n == "NCW_NOTARY_PROFILE" ? "ncw" : null)!;
        var spec = SigningPlans.MacNotarize("/a.zip", profile, null);
        Assert.Equal(["notarytool", "submit", "/a.zip", "--wait", "--keychain-profile", "ncw"], spec.Args);

        var triple = SigningPlans.NotaryConfigFrom(n => n switch { "NCW_NOTARY_APPLE_ID" => "a@b.c", "NCW_NOTARY_TEAM_ID" => "T", "NCW_NOTARY_PASSWORD" => "s3cret-pw", _ => null })!;
        var spec2 = SigningPlans.MacNotarize("/a.zip", triple, null);
        Assert.Contains("s3cret-pw", spec2.Args);
        Assert.DoesNotContain("s3cret-pw", spec2.Display());
        Assert.Contains("***", spec2.Display());
    }

    [Fact]
    public void Windows_config_from_pfx_or_thumbprint_with_defaults()
    {
        Assert.Null(SigningPlans.WindowsConfig(_ => null));
        var pfx = SigningPlans.WindowsConfig(n => n switch { "NCW_WIN_CERT_FILE" => "c.pfx", "NCW_WIN_CERT_PASSWORD" => "pw", _ => null })!;
        Assert.Equal("signtool", pfx.SigntoolPath);
        Assert.Equal("http://timestamp.digicert.com", pfx.TimestampUrl);
        var sign = SigningPlans.WindowsSignFile("a.exe", pfx);
        Assert.Equal(["sign", "/f", "c.pfx", "/p", "pw", "/fd", "sha256", "/tr", "http://timestamp.digicert.com", "/td", "sha256", "a.exe"], sign.Args);
        Assert.DoesNotContain("pw", sign.Display().Split(' '));

        var thumb = SigningPlans.WindowsConfig(n => n == "NCW_WIN_CERT_THUMBPRINT" ? "ABCD" : n == "NCW_SIGNTOOL" ? "C:\\kit\\signtool.exe" : null)!;
        Assert.Contains("/sha1", SigningPlans.WindowsSignFile("a.exe", thumb).Args);
        Assert.Equal("C:\\kit\\signtool.exe", SigningPlans.WindowsSignFile("a.exe", thumb).FileName);
    }

    [Fact]
    public void Windows_signs_exe_and_native_dlls_but_not_managed_assemblies()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.Copy(CoreLib, dir.Sub("Managed.dll")); // 托管
        dir.WriteBytes("agent/e_sqlite3.dll", "not a PE file"u8.ToArray());
        dir.WriteBytes("NextCoWork.exe", [1]);
        dir.WriteBytes("agent/ncw-agent.exe", [1]);
        var files = SigningPlans.WindowsFiles(dir.Path).Select(f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/')).ToArray();
        Assert.Contains("NextCoWork.exe", files);
        Assert.Contains("agent/ncw-agent.exe", files);
        Assert.Contains("agent/e_sqlite3.dll", files);
        Assert.DoesNotContain("Managed.dll", files);
        Assert.True(SigningPlans.IsManagedAssembly(CoreLib));
    }

    [Fact]
    public void Entitlements_are_well_formed_and_cover_the_dotnet_runtime_needs()
    {
        var text = System.Xml.Linq.XDocument.Parse(SigningPlans.EntitlementsPlist).ToString();
        Assert.Contains("com.apple.security.cs.allow-jit", text);
        Assert.Contains("com.apple.security.cs.disable-library-validation", text);
        Assert.EndsWith(".entitlements", SigningPlans.EntitlementsFileName); // vpk 的要求
    }
}

public class SignCommandTests
{
    private static (BuildContext Ctx, FakeRunner Runner, MemoryBuildLog Log, BundleLayout Layout) Setup(TempDir dir, Rid rid, Dictionary<string, string>? env)
    {
        var runner = new FakeRunner();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, runner, log, rid, env);
        var layout = new BundleLayout(rid, ctx.Repo.BundleDir(rid));
        dir.WriteBytes("p/desktop/" + rid.DesktopExecutable, [0xCF, 0xFA, 0xED, 0xFE]);
        dir.WriteBytes("p/agent/" + rid.AgentExecutable, [0xCF, 0xFA, 0xED, 0xFE]);
        BundleBuilder.Build(layout, dir.Sub("p", "desktop"), dir.Sub("p", "agent"), "1.0.0", ctx.Clock);
        return (ctx, runner, log, layout);
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task Without_credentials_signing_is_skipped_and_says_so_and_changes_nothing(string rid)
    {
        using var dir = new TempDir();
        var (ctx, runner, log, layout) = Setup(dir, Rid.Parse(rid), null);
        Assert.Equal(0, await Program.RunAsync(["sign", "--rid", rid], ctx, default));
        Assert.Contains(log.Lines, l => l.Contains("SKIPPED signing"));
        Assert.Empty(runner.Calls);
        Assert.False(BundleBuilder.Read(layout.ManifestPath).Signed);
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task Require_turns_a_skip_into_a_failure_so_release_jobs_cannot_ship_unsigned_builds(string rid)
    {
        using var dir = new TempDir();
        var (ctx, _, log, _) = Setup(dir, Rid.Parse(rid), null);
        Assert.Equal(1, await Program.RunAsync(["sign", "--rid", rid, "--require"], ctx, default));
        Assert.Contains(log.Lines, l => l.Contains("--require"));
    }

    [Fact]
    public async Task Macos_signs_inside_out_verifies_and_refreshes_the_bundle_manifest()
    {
        using var dir = new TempDir();
        var (ctx, runner, _, layout) = Setup(dir, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "-" });
        Assert.Equal(0, await Program.RunAsync(["sign", "--rid", "osx-arm64"], ctx, default));
        var names = runner.Calls.Select(c => c.FileName).ToArray();
        Assert.Equal("xattr", names[0]);
        Assert.Equal("codesign", names[1]);
        Assert.Contains("--verify", runner.Calls[^1].Args);
        Assert.Equal(layout.AppRoot, runner.Calls[^2].Args[^1]); // 最后签 .app
        Assert.True(BundleBuilder.Read(layout.ManifestPath).Signed);
        Assert.True(File.Exists(Path.Combine(layout.OutputDir, SigningPlans.EntitlementsFileName)));
    }

    [Fact]
    public async Task Macos_notarize_needs_a_real_identity_and_credentials()
    {
        using var dir = new TempDir();
        var (ctx, _, _, _) = Setup(dir, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "-" });
        Assert.Equal(1, await Program.RunAsync(["sign", "--rid", "osx-arm64", "--notarize"], ctx, default)); // ad-hoc 不能公证

        using var dir2 = new TempDir();
        var (ctx2, runner2, log2, _) = Setup(dir2, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)" });
        Assert.Equal(0, await Program.RunAsync(["sign", "--rid", "osx-arm64", "--notarize"], ctx2, default));
        Assert.Contains(log2.Lines, l => l.Contains("notarization credentials missing"));
        Assert.DoesNotContain(runner2.Calls, c => c.FileName == "xcrun");

        using var dir3 = new TempDir();
        var (ctx3, runner3, _, _) = Setup(dir3, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)", ["NCW_NOTARY_PROFILE"] = "ncw" });
        Assert.Equal(0, await Program.RunAsync(["sign", "--rid", "osx-arm64", "--notarize"], ctx3, default));
        var tail = runner3.Calls.TakeLast(3).Select(c => c.FileName + " " + c.Args[0]).ToArray();
        Assert.Equal(["ditto -c", "xcrun notarytool", "xcrun stapler"], tail);
    }

    [Fact]
    public async Task A_failing_codesign_fails_the_step_and_leaves_the_manifest_unsigned()
    {
        using var dir = new TempDir();
        var (ctx, runner, _, layout) = Setup(dir, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "-" });
        runner.Handler = spec => spec.FileName == "codesign" ? new ProcessResult(1, ["boom"]) : new ProcessResult(0, []);
        Assert.Equal(1, await Program.RunAsync(["sign", "--rid", "osx-arm64"], ctx, default));
        Assert.False(BundleBuilder.Read(layout.ManifestPath).Signed);
    }

    [Fact]
    public async Task Windows_signs_with_signtool_when_a_certificate_is_configured()
    {
        using var dir = new TempDir();
        var (ctx, runner, _, layout) = Setup(dir, TestKit.WinX64, new() { ["NCW_WIN_CERT_FILE"] = "c.pfx", ["NCW_WIN_CERT_PASSWORD"] = "pw" });
        Assert.Equal(0, await Program.RunAsync(["sign", "--rid", "win-x64"], ctx, default));
        Assert.Contains(runner.Calls, c => c.FileName == "signtool" && c.Args[0] == "sign" && c.Args[^1].EndsWith("ncw-agent.exe", StringComparison.Ordinal));
        Assert.Contains(runner.Calls, c => c.Args[0] == "verify");
        Assert.True(BundleBuilder.Read(layout.ManifestPath).Signed);
    }

    [Fact]
    public async Task Signing_a_foreign_platform_is_skipped_not_attempted()
    {
        using var dir = new TempDir();
        var (ctx, runner, log, _) = Setup(dir, TestKit.WinX64, new() { ["NCW_WIN_CERT_FILE"] = "c.pfx" });
        // 在 Linux 宿主上签 Windows 包:signtool 不存在
        var linuxCtx = ctx with { Host = TestKit.LinuxX64 };
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(0, await Program.RunAsync(["sign", "--rid", "win-x64"], linuxCtx, default));
            Assert.Empty(runner.Calls);
            Assert.Contains(log.Lines, l => l.Contains("only runs on Windows"));
        }
    }
}
