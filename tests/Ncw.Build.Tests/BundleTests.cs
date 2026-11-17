using System.Xml.Linq;
using Ncw.Build.Bundle;
using Ncw.Build.Core;

namespace Ncw.Build.Tests;

public class BundleLayoutTests
{
    [Fact]
    public void Macos_puts_the_sidecar_next_to_the_main_executable_inside_the_app()
    {
        var l = new BundleLayout(TestKit.MacArm, "/o");
        Assert.Equal(Path.GetFullPath("/o") == "/o" ? "/o/NextCoWork.app" : Path.Combine("/o", "NextCoWork.app"), l.AppRoot);
        Assert.Equal(Path.Combine(l.AppRoot, "Contents", "MacOS"), l.ExecutableDir);
        Assert.Equal(Path.Combine(l.AppRoot, "Contents", "MacOS", "agent", "ncw-agent"), l.AgentExe);
        Assert.Equal(Path.Combine(l.AppRoot, "Contents", "MacOS", "NextCoWork"), l.DesktopExe);
        Assert.Equal(Path.Combine("/o", "bundle.json"), l.ManifestPath); // 清单在包外,签名之后不动包内容
    }

    [Fact]
    public void Windows_and_linux_use_a_plain_directory()
    {
        var win = new BundleLayout(TestKit.WinX64, "/o");
        Assert.Equal(Path.Combine("/o", "NextCoWork", "agent", "ncw-agent.exe"), win.AgentExe);
        Assert.Equal(Path.Combine("/o", "NextCoWork", "NextCoWork.exe"), win.DesktopExe);
        var linux = new BundleLayout(TestKit.LinuxX64, "/o");
        Assert.Equal(Path.Combine("/o", "NextCoWork", "agent", "ncw-agent"), linux.AgentExe);
        Assert.False(linux.IsMacBundle);
    }
}

public class InfoPlistTests
{
    [Theory]
    [InlineData("3.0.0", "3.0.0")]
    [InlineData("3.0.0-alpha.1", "3.0.0")]
    [InlineData("3.1.2-beta.4+abc123", "3.1.2")]
    public void Numeric_version_drops_prerelease_and_metadata(string full, string numeric) =>
        Assert.Equal(numeric, InfoPlist.NumericVersion(full));

    [Fact]
    public void Plist_is_well_formed_and_keeps_the_2x_bundle_id_and_login_scheme()
    {
        var doc = XDocument.Parse(InfoPlist.Render("3.0.0-alpha.1"));
        var text = doc.ToString();
        Assert.Contains("com.nextcowork.app", text);
        Assert.Contains("<string>nextcowork</string>", text);
        Assert.Contains("<string>3.0.0</string>", text);
        Assert.Contains("<string>3.0.0-alpha.1</string>", text); // NCWFullVersion
        Assert.Contains("CFBundleExecutable", text);
        Assert.DoesNotContain("CFBundleIconFile", text); // 没有图标就不写这个键(指向不存在的文件会让 Dock 退回默认图标)
    }

    [Fact]
    public void Plist_names_the_icon_file_when_the_bundle_has_one()
    {
        var text = XDocument.Parse(InfoPlist.Render("3.0.0", BundleLayout.MacIconFileName)).ToString();

        Assert.Contains("<key>CFBundleIconFile</key>", text);
        Assert.Contains($"<string>{BundleLayout.MacIconFileName}</string>", text);
    }
}

public class BundleBuilderTests
{
    private static (string Desktop, string Agent) FakePublishes(TempDir dir, Rid rid)
    {
        dir.Write("desktop/" + rid.DesktopExecutable, "desktop-exe");
        dir.Write("desktop/Avalonia.dll", "lib");
        dir.Write("desktop/cs/Avalonia.resources.dll", "res");
        dir.Write("desktop/Ncw.UI.pdb", "pdb");
        dir.Write("desktop/agent/ncw-agent", "STRAY DEV AGENT");
        dir.Write("agent/" + rid.AgentExecutable, "agent-exe");
        dir.Write("agent/libe_sqlite3.dylib", "sqlite");
        dir.Write("agent/ncw-agent.dSYM/Contents/Info.plist", "dsym");
        dir.Write("agent/Ncw.Core.pdb", "pdb");
        return (dir.Sub("desktop"), dir.Sub("agent"));
    }

    [Fact]
    public void Builds_the_macos_app_with_sidecar_manifest_and_no_symbols()
    {
        using var dir = new TempDir();
        var (desktop, agent) = FakePublishes(dir, TestKit.MacArm);
        var layout = new BundleLayout(TestKit.MacArm, dir.Sub("out"));
        var manifest = BundleBuilder.Build(layout, desktop, agent, "3.0.0-alpha.1", new FixedClock(TestKit.Now));

        Assert.True(File.Exists(layout.DesktopExe));
        Assert.True(File.Exists(layout.AgentExe));
        Assert.True(File.Exists(Path.Combine(layout.AgentDir, "libe_sqlite3.dylib")));
        Assert.True(File.Exists(layout.InfoPlist));
        Assert.Equal("agent-exe", File.ReadAllText(layout.AgentExe)); // 不是桌面发布目录里那个误带的开发版 agent
        Assert.DoesNotContain(manifest.Files, f => f.Path.EndsWith(".pdb", StringComparison.Ordinal) || f.Path.Contains(".dSYM", StringComparison.Ordinal));
        Assert.Contains(manifest.Files, f => f.Path == "Contents/MacOS/agent/ncw-agent");
        Assert.Equal("Contents/MacOS/agent/ncw-agent", manifest.AgentExe);
        Assert.Equal("Contents/MacOS/NextCoWork", manifest.MainExe);
        Assert.Equal("osx-arm64", manifest.Rid);
        Assert.Equal("3.0.0-alpha.1", manifest.Version);
        Assert.False(manifest.Signed);
        Assert.True(File.Exists(layout.ManifestPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(layout.AgentExe).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void The_macos_app_carries_the_icon_in_resources_and_the_plist_points_at_it()
    {
        using var dir = new TempDir();
        var (desktop, agent) = FakePublishes(dir, TestKit.MacArm);
        var layout = new BundleLayout(TestKit.MacArm, dir.Sub("out"));
        var icon = dir.Write("AppIcon.icns", "icns-bytes");

        BundleBuilder.Build(layout, desktop, agent, "3.0.0", new FixedClock(TestKit.Now), icon);

        Assert.Equal("icns-bytes", File.ReadAllText(layout.MacIconPath));
        Assert.Contains("<key>CFBundleIconFile</key>", File.ReadAllText(layout.InfoPlist));
    }

    [Fact]
    public void A_missing_icon_file_fails_the_bundle_instead_of_shipping_a_blank_dock_icon()
    {
        using var dir = new TempDir();
        var (desktop, agent) = FakePublishes(dir, TestKit.MacArm);

        var e = Assert.Throws<BuildException>(() => BundleBuilder.Build(new BundleLayout(TestKit.MacArm, dir.Sub("out")), desktop, agent, "3.0.0", new FixedClock(TestKit.Now), dir.Sub("nope", "AppIcon.icns")));

        Assert.Contains("icon", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_committed_icns_exists_and_is_a_real_icns_file()
    {
        var path = RepoLayout.Find().MacIcon;

        Assert.True(File.Exists(path), path);
        var head = File.ReadAllBytes(path).AsSpan(0, 4);
        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(head));
    }

    [Fact]
    public void Builds_a_plain_directory_on_other_platforms()
    {
        using var dir = new TempDir();
        var (desktop, agent) = FakePublishes(dir, TestKit.LinuxX64);
        var layout = new BundleLayout(TestKit.LinuxX64, dir.Sub("out"));
        var manifest = BundleBuilder.Build(layout, desktop, agent, "1.0.0", new FixedClock(TestKit.Now));
        Assert.Equal("agent/ncw-agent", manifest.AgentExe);
        Assert.False(File.Exists(layout.InfoPlist));
        Assert.True(File.Exists(Path.Combine(layout.AppRoot, "cs", "Avalonia.resources.dll")));
    }

    [Fact]
    public void Missing_publish_output_is_a_clear_error()
    {
        using var dir = new TempDir();
        var layout = new BundleLayout(TestKit.MacArm, dir.Sub("out"));
        var e = Assert.Throws<BuildException>(() => BundleBuilder.Build(layout, dir.Sub("none"), dir.Sub("none2"), "1.0.0", TimeProvider.System));
        Assert.Contains("publish-desktop", e.Message);
    }

    [Fact]
    public void Manifest_verification_detects_changed_missing_and_unexpected_files_and_refresh_resigns_the_list()
    {
        using var dir = new TempDir();
        var (desktop, agent) = FakePublishes(dir, TestKit.LinuxX64);
        var layout = new BundleLayout(TestKit.LinuxX64, dir.Sub("out"));
        var manifest = BundleBuilder.Build(layout, desktop, agent, "1.0.0", new FixedClock(TestKit.Now));
        Assert.Empty(manifest.Verify(layout.AppRoot));

        File.WriteAllText(layout.AgentExe, "tampered");
        File.Delete(Path.Combine(layout.AppRoot, "Avalonia.dll"));
        File.WriteAllText(Path.Combine(layout.AppRoot, "extra.txt"), "x");
        var problems = manifest.Verify(layout.AppRoot);
        Assert.Contains("changed: agent/ncw-agent", problems);
        Assert.Contains("missing: Avalonia.dll", problems);
        Assert.Contains("unexpected: extra.txt", problems);

        var refreshed = BundleBuilder.Refresh(layout, manifest, signed: true);
        Assert.Empty(refreshed.Verify(layout.AppRoot));
        Assert.True(refreshed.Signed);
        Assert.Equal(manifest.CreatedUtc, refreshed.CreatedUtc);
        Assert.True(BundleBuilder.Read(layout.ManifestPath).Signed);
    }

    [Fact]
    public async Task Bundle_command_uses_the_default_publish_locations()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, runner, log, TestKit.MacArm);
        var rid = TestKit.MacArm;
        Directory.CreateDirectory(ctx.Repo.PublishDir(rid, "desktop"));
        Directory.CreateDirectory(ctx.Repo.PublishDir(rid, "agent"));
        File.WriteAllText(Path.Combine(ctx.Repo.PublishDir(rid, "desktop"), "NextCoWork"), "d");
        File.WriteAllText(Path.Combine(ctx.Repo.PublishDir(rid, "agent"), "ncw-agent"), "a");
        Directory.CreateDirectory(Path.GetDirectoryName(ctx.Repo.MacIcon)!);
        File.WriteAllText(ctx.Repo.MacIcon, "icns"); // bundle 在 macOS 上要求仓库里有应用图标

        Assert.Equal(0, await Program.RunAsync(["bundle", "--rid", "osx-arm64"], ctx, default));
        Assert.True(File.Exists(new BundleLayout(rid, ctx.Repo.BundleDir(rid)).MacIconPath));
        var manifest = BundleBuilder.Read(Path.Combine(ctx.Repo.BundleDir(rid), "bundle.json"));
        Assert.Equal("9.8.7-test.1", manifest.Version); // 来自 Directory.Build.props 的唯一版本号
    }
}
