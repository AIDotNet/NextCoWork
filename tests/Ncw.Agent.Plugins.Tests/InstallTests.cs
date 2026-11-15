using System.IO.Compression;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>安装(2.x <c>installer.ts</c>):体积 / 路径 / 符号链接 / 重复 / 顶层目录 / 清单 / 包内文件 / 原子替换。zip 是不可信输入。</summary>
public sealed class InstallTests : IAsyncDisposable
{
    private readonly PluginFx _fx = new();

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private Task<InstalledPackage> InstallAsync(byte[] zip, string? sha = null) =>
        PluginPackageInstaller.InstallZipAsync(zip, _fx.Env, _fx.P(_fx.PluginRoot), sha);

    private static async Task<PluginException> RejectedAsync(Task<InstalledPackage> install, string key)
    {
        var e = await Assert.ThrowsAsync<PluginException>(() => install);
        Assert.Equal(key, e.Key);
        return e;
    }

    [Fact]
    public async Task A_real_fixture_package_installs_and_every_file_lands_in_the_plugin_directory()
    {
        var installed = await InstallAsync(PluginFx.FixtureZip("acme.hello"));

        Assert.Equal("acme.hello", installed.Manifest.Id);
        Assert.Equal(Path.Combine(_fx.PluginRoot, "acme.hello"), installed.Directory.Value);
        Assert.True(File.Exists(Path.Combine(_fx.PluginRoot, "acme.hello", "dist", "extension.js")));
        Assert.True(File.Exists(Path.Combine(_fx.PluginRoot, "acme.hello", "l10n", "zh-CN.json")));
        Assert.Equal(64, installed.Sha256!.Length);
    }

    [Fact]
    public async Task The_marketplaces_authoritative_digest_must_match_the_package()
    {
        var zip = PluginFx.FixtureZip("acme.think");

        await RejectedAsync(InstallAsync(zip, new string('0', 64)), PluginErrorKeys.DigestMismatch);
        Assert.False(Directory.Exists(Path.Combine(_fx.PluginRoot, "acme.think")));

        var ok = await InstallAsync(zip, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)));
        Assert.Equal("acme.think", ok.Manifest.Id);
    }

    [Theory]
    [InlineData("acme.evil/../../escape.txt")]
    [InlineData("acme.evil/dist/../../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/x")]
    [InlineData("acme.evil\\dist\\x.js")]
    [InlineData("acme.evil//double.txt")]
    public async Task Hostile_entry_names_are_rejected_before_anything_is_written(string name)
    {
        var zip = PluginFx.Zip(("acme.evil/package.json", "{}"), (name, "x"));

        await RejectedAsync(InstallAsync(zip), PluginErrorKeys.ZipInvalid);

        Assert.False(File.Exists(Path.Combine(_fx.Root, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_fx.PluginRoot, "acme.evil")));
    }

    [Fact]
    public async Task Two_top_level_directories_are_rejected()
    {
        var zip = PluginFx.Zip(("acme.one/package.json", "{}"), ("acme.two/package.json", "{}"));

        await RejectedAsync(InstallAsync(zip), PluginErrorKeys.ZipInvalid);
    }

    [Fact]
    public async Task The_top_level_directory_must_equal_the_manifest_id()
    {
        var zip = PluginFx.Zip(
            ("acme.other/package.json", """{ "name": "hello", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./m.js" }"""),
            ("acme.other/m.js", "export const a = 1"));

        var e = await RejectedAsync(InstallAsync(zip), PluginErrorKeys.ZipInvalid);
        Assert.Contains("does not match", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_insensitive_duplicates_are_rejected_because_the_later_one_would_overwrite_the_earlier()
    {
        var zip = PluginFx.Zip(("acme.dup/package.json", "{}"), ("acme.dup/A.js", "1"), ("acme.dup/a.js", "2"));

        await RejectedAsync(InstallAsync(zip), PluginErrorKeys.ZipInvalid);
    }

    [Fact]
    public async Task Symbolic_link_entries_are_rejected_never_restored_from_an_untrusted_archive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("acme.link/package.json");
            using (var w = manifest.Open())
            {
                w.Write("{}"u8);
            }

            var link = archive.CreateEntry("acme.link/dist");
            link.ExternalAttributes = unchecked((int)(0xA1FF << 16)); // Unix 符号链接
            using var lw = link.Open();
            lw.Write("/etc"u8);
        }

        await RejectedAsync(InstallAsync(stream.ToArray()), PluginErrorKeys.ZipInvalid);
    }

    [Fact]
    public async Task Too_many_entries_an_oversized_package_and_a_non_zip_are_rejected()
    {
        var many = PluginFx.Zip([.. Enumerable.Range(0, PluginLimits.MaxEntries + 1).Select(i => ($"acme.many/f{i}.txt", "x"))]);
        await RejectedAsync(InstallAsync(many), PluginErrorKeys.ZipInvalid);

        await RejectedAsync(InstallAsync(new byte[PluginLimits.MaxZipBytes + 1]), PluginErrorKeys.PackageTooLarge);
        await RejectedAsync(InstallAsync(Encoding.UTF8.GetBytes("this is not a zip")), PluginErrorKeys.ZipInvalid);
        await RejectedAsync(InstallAsync([]), PluginErrorKeys.PackageTooLarge);
    }

    [Fact]
    public async Task A_zip_bomb_is_stopped_by_the_expanded_size_budget()
    {
        // 51 个 1MB 的全零条目:压缩后只有几十 KB,展开 51MB 超过 50MB 的上限
        var chunk = new byte[1024 * 1024];
        var entries = new List<(string, byte[])> { ("acme.bomb/package.json", "{}"u8.ToArray()) };
        entries.AddRange(Enumerable.Range(0, 51).Select(i => ($"acme.bomb/blob{i}.bin", chunk)));
        var zip = PluginFx.Zip([.. entries]);
        Assert.True(zip.Length < 1024 * 1024);

        await RejectedAsync(InstallAsync(zip), PluginErrorKeys.PackageTooLarge);
        Assert.False(Directory.Exists(Path.Combine(_fx.PluginRoot, "acme.bomb")));
    }

    [Fact]
    public async Task A_deeply_nested_path_is_rejected()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", PluginLimits.MaxDepth + 1));

        await RejectedAsync(InstallAsync(PluginFx.Zip(("acme.deep/package.json", "{}"), ($"acme.deep/{deep}/f.txt", "x"))), PluginErrorKeys.ZipInvalid);
    }

    [Fact]
    public async Task A_bad_manifest_is_rejected_with_the_field_in_the_error()
    {
        var zip = PluginFx.Zip(("acme.bad/package.json", """{ "name": "bad", "publisher": "acme", "version": "nope", "engines": { "nextcowork": "^0.3.0" }, "main": "./m.js" }"""), ("acme.bad/m.js", ""));

        var e = await RejectedAsync(InstallAsync(zip), PluginErrorKeys.ManifestInvalid);

        Assert.Equal("version", e.Parameters!["field"]);
    }

    [Fact]
    public async Task A_missing_package_json_is_rejected()
    {
        await RejectedAsync(InstallAsync(PluginFx.Zip(("acme.none/readme.md", "hi"))), PluginErrorKeys.ManifestInvalid);
    }

    [Theory]
    [InlineData("dist/extension.js", "dist/extension.js")]
    [InlineData("l10n/zh-CN.json", "l10n/zh-CN.json")]
    [InlineData("skills/guard-style/SKILL.md", "skills/guard-style")] // 目录里一个文件都不剩 = 目录不存在
    public async Task Files_the_manifest_points_at_must_really_be_in_the_package(string removed, string expectedPath)
    {
        var id = removed.StartsWith("skills", StringComparison.Ordinal) ? "acme.guard" : "acme.hello";
        var zip = PluginFx.FixtureZip(id, (path, bytes) => path == removed ? null : bytes);

        var e = await RejectedAsync(InstallAsync(zip), PluginErrorKeys.PackageFileMissing);

        Assert.Equal(expectedPath, e.Parameters!["path"]);
    }

    [Fact]
    public async Task Upgrading_replaces_the_package_atomically_and_a_failed_upgrade_leaves_the_old_version_in_place()
    {
        await InstallAsync(PluginFx.FixtureZip("acme.hello"));
        var before = File.ReadAllText(Path.Combine(_fx.PluginRoot, "acme.hello", "dist", "extension.js"));

        // 升级包的清单对、但 main 缺失:在写盘之前就被拒,旧版本原封不动
        await RejectedAsync(InstallAsync(PluginFx.FixtureZip("acme.hello", (path, bytes) => path == "dist/extension.js" ? null : bytes)), PluginErrorKeys.PackageFileMissing);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_fx.PluginRoot, "acme.hello", "dist", "extension.js")));

        // 成功的升级:换成新内容,没有残留的 staging / backup 目录
        await InstallAsync(PluginFx.FixtureZip("acme.hello", (path, bytes) => path == "dist/extension.js" ? Encoding.UTF8.GetBytes("export function activate() {}") : bytes));
        Assert.Equal("export function activate() {}", File.ReadAllText(Path.Combine(_fx.PluginRoot, "acme.hello", "dist", "extension.js")));
        Assert.Equal(["acme.hello"], Directory.GetDirectories(_fx.PluginRoot).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Installing_from_a_directory_copies_it_skips_node_modules_and_rejects_symlinks()
    {
        var source = Path.Combine(_fx.Root, "dev", "acme.think");
        CopyDirectory(PluginFx.FixtureDirectory("acme.think"), source);
        Directory.CreateDirectory(Path.Combine(source, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(source, "node_modules", "pkg", "index.js"), "junk");

        var installed = await PluginPackageInstaller.InstallDirectoryAsync(_fx.Env, _fx.P(source), _fx.Env, _fx.P(_fx.PluginRoot));

        Assert.Equal("acme.think", installed.Manifest.Id);
        Assert.True(File.Exists(Path.Combine(_fx.PluginRoot, "acme.think", "dist", "extension.js")));
        Assert.False(Directory.Exists(Path.Combine(_fx.PluginRoot, "acme.think", "node_modules")));

        // 目录里混进一个软链:拒绝(不把指向开发目录之外的链接落到插件目录)
        var outside = Path.Combine(_fx.Root, "secret.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(source, "dist", "link.txt"), outside);
        var e = await Assert.ThrowsAsync<PluginException>(() => PluginPackageInstaller.InstallDirectoryAsync(_fx.Env, _fx.P(source), _fx.Env, _fx.P(_fx.PluginRoot)));
        Assert.Equal(PluginErrorKeys.ZipInvalid, e.Key);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
