using System.Text.RegularExpressions;
using Ncw.Build.Bundle;
using Ncw.Build.Core;
using Ncw.Build.Velopack;

namespace Ncw.Build.Tests;

public class VelopackPlanTests
{
    private static ProcessSpec Pack(Rid rid, Rid host, Dictionary<string, string>? env = null, bool preSigned = false, string? entitlements = null) =>
        VelopackPlans.Pack(new RepoLayout("/r"), new BundleLayout(rid, "/b"), "3.0.0-alpha.1", "stable", "/out", host,
            n => env is not null && env.TryGetValue(n, out var v) ? v : null, null, false, entitlements, preSigned);

    [Fact]
    public void Uses_the_pinned_tool_through_dotnet_tool_run_with_the_agreed_pack_arguments()
    {
        var spec = Pack(TestKit.MacArm, TestKit.MacArm);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal(["tool", "run", "vpk", "--", "pack"], spec.Args.Take(5));
        Assert.Equal("NextCoWork", spec.Args[spec.Args.ToList().IndexOf("-u") + 1]);
        Assert.Equal("3.0.0-alpha.1", spec.Args[spec.Args.ToList().IndexOf("-v") + 1]);
        Assert.Equal("stable", spec.Args[spec.Args.ToList().IndexOf("-c") + 1]);
        Assert.Equal("NextCoWork", spec.Args[spec.Args.ToList().IndexOf("-e") + 1]);
        Assert.EndsWith("NextCoWork.app", spec.Args[spec.Args.ToList().IndexOf("-p") + 1]);
        Assert.EndsWith("AppIcon.icns", spec.Args[spec.Args.ToList().IndexOf("--icon") + 1]);
        Assert.DoesNotContain("[osx]", spec.Args);
    }

    [Fact]
    public void Windows_main_exe_has_the_exe_suffix_and_packs_the_app_directory()
    {
        var spec = Pack(TestKit.WinX64, TestKit.WinX64);
        Assert.Equal("NextCoWork.exe", spec.Args[spec.Args.ToList().IndexOf("-e") + 1]);
        Assert.EndsWith("NextCoWork", spec.Args[spec.Args.ToList().IndexOf("-p") + 1]);
        Assert.EndsWith("AppIcon.ico", spec.Args[spec.Args.ToList().IndexOf("--icon") + 1]);
    }

    [Fact]
    public void Cross_os_packing_adds_the_vpk_os_prefix()
    {
        Assert.Equal("[win]", Pack(TestKit.WinX64, TestKit.MacArm).Args[4]);
        var linux = Pack(TestKit.LinuxX64, TestKit.MacArm);
        Assert.Equal("[linux]", linux.Args[4]);
        Assert.EndsWith("AppIcon.png", linux.Args[linux.Args.ToList().IndexOf("--icon") + 1]);
    }

    [Fact]
    public void Macos_signing_is_delegated_to_vpk_because_it_modifies_the_app_after_we_signed_it()
    {
        var spec = Pack(TestKit.MacArm, TestKit.MacArm, new()
        {
            ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)",
            ["NCW_MAC_INSTALLER_IDENTITY"] = "Developer ID Installer: X (T)",
            ["NCW_NOTARY_PROFILE"] = "ncw",
        }, preSigned: true, entitlements: "/e/NextCoWork.entitlements");
        var args = spec.Args.ToList();
        Assert.Equal("Developer ID Application: X (T)", args[args.IndexOf("--signAppIdentity") + 1]);
        Assert.Equal("Developer ID Installer: X (T)", args[args.IndexOf("--signInstallIdentity") + 1]);
        Assert.Equal("ncw", args[args.IndexOf("--notaryProfile") + 1]);
        Assert.Equal("/e/NextCoWork.entitlements", args[args.IndexOf("--signEntitlements") + 1]);
        Assert.Contains("--signDisableDeep", args);
        Assert.DoesNotContain("--signDisableDeep", Pack(TestKit.MacArm, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "-" }).Args);
    }

    [Fact]
    public void A_distribution_build_without_an_installer_certificate_ships_no_pkg_rather_than_an_unsigned_one()
    {
        var withoutInstaller = Pack(TestKit.MacArm, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)", ["NCW_NOTARY_PROFILE"] = "ncw" });
        Assert.Contains("--noInst", withoutInstaller.Args);
        Assert.DoesNotContain("--signInstallIdentity", withoutInstaller.Args);

        // 有安装器证书:出 .pkg 并签名;本机的 ad-hoc / 不签名构建:照旧出 .pkg
        Assert.DoesNotContain("--noInst", Pack(TestKit.MacArm, TestKit.MacArm, new()
        {
            ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)",
            ["NCW_MAC_INSTALLER_IDENTITY"] = "Developer ID Installer: X (T)",
        }).Args);
        Assert.DoesNotContain("--noInst", Pack(TestKit.MacArm, TestKit.MacArm, new() { ["NCW_MAC_SIGN_IDENTITY"] = "-" }).Args);
        Assert.DoesNotContain("--noInst", Pack(TestKit.MacArm, TestKit.MacArm).Args);
        Assert.DoesNotContain("--noInst", Pack(TestKit.WinX64, TestKit.WinX64, new() { ["NCW_MAC_SIGN_IDENTITY"] = "Developer ID Application: X (T)" }).Args);
    }

    [Fact]
    public void Without_identities_no_signing_arguments_are_passed()
    {
        var args = Pack(TestKit.MacArm, TestKit.MacArm).Args;
        Assert.DoesNotContain(args, a => a.StartsWith("--sign", StringComparison.Ordinal) || a == "--notaryProfile");
    }

    [Fact]
    public void Windows_sign_params_carry_the_certificate_and_the_password_is_never_logged()
    {
        var spec = Pack(TestKit.WinX64, TestKit.WinX64, new() { ["NCW_WIN_CERT_FILE"] = "c.pfx", ["NCW_WIN_CERT_PASSWORD"] = "hunter2" });
        var param = spec.Args[spec.Args.ToList().IndexOf("--signParams") + 1];
        Assert.Contains("/f \"c.pfx\"", param);
        Assert.Contains("hunter2", param);
        Assert.DoesNotContain("hunter2", spec.Display());
    }

    [Fact]
    public void Tool_version_is_read_from_the_dotnet_tool_manifest()
    {
        using var dir = new TempDir();
        var path = dir.Write("m.json", """{"version":1,"isRoot":true,"tools":{"vpk":{"version":"9.9.9","commands":["vpk"]}}}""");
        Assert.Equal("9.9.9", VelopackPlans.ToolVersion(path));
        Assert.Null(VelopackPlans.ToolVersion(dir.Sub("none.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Keep_output_lets_a_feed_accumulate_versions_and_the_default_still_starts_clean(bool keep, bool oldFileSurvives)
    {
        using var dir = new TempDir();
        var runner = new FakeRunner { Handler = spec => { File.WriteAllText(Path.Combine(spec.Args[spec.Args.ToList().IndexOf("-o") + 1], "new.nupkg"), "n"); return new ProcessResult(0, []); } };
        var ctx = TestKit.Context(dir, runner, new MemoryBuildLog(), TestKit.MacArm);
        Directory.CreateDirectory(Path.Combine(ctx.Repo.BundleDir(TestKit.MacArm), "NextCoWork.app"));
        var feed = dir.Sub("feed");
        dir.Write("feed/NextCoWork-3.0.0-stable-full.nupkg", "the previous release");

        var args = new List<string> { "velopack", "--rid", "osx-arm64", "--version", "3.0.1", "--out", feed, "--no-restore" };
        if (keep)
        {
            args.Add("--keep-output");
        }

        Assert.Equal(0, await Program.RunAsync([.. args], ctx, default));

        Assert.Equal(oldFileSurvives, File.Exists(Path.Combine(feed, "NextCoWork-3.0.0-stable-full.nupkg")));
        Assert.True(File.Exists(Path.Combine(feed, "new.nupkg")));
        Assert.Equal("3.0.1", runner.Calls.Single().Args[runner.Calls.Single().Args.ToList().IndexOf("-v") + 1]);
    }

    [Fact]
    public void The_pinned_vpk_matches_the_Velopack_version_in_Directory_Packages_props()
    {
        // 版本集中管理:vpk 只能写在 .config/dotnet-tools.json,运行时库写在 Directory.Packages.props,两者必须同一个版本。
        var repo = new RepoLayout(TestKit.RepoRoot());
        var tool = VelopackPlans.ToolVersion(repo.ToolManifest);
        Assert.False(string.IsNullOrEmpty(tool));
        var props = File.ReadAllText(Path.Combine(repo.Root, "Directory.Packages.props"));
        var m = Regex.Match(props, "<PackageVersion\\s+Include=\"Velopack\"\\s+Version=\"([^\"]+)\"");
        Assert.True(m.Success, "Directory.Packages.props must pin Velopack (same version as vpk)");
        Assert.Equal(tool, m.Groups[1].Value);
    }
}
