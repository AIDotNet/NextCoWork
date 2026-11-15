using System.IO.Compression;
using System.Text;
using Ncw.Agent.Skills.Install;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// zip 安装,移植自 2.x <c>kernel/skill/__tests__/install.test.ts</c>,外加不可信包的防线(路径穿越、zip bomb、重复 / 符号链接 / 保留名条目、缺 SKILL.md、超大文件、原子替换)。
/// 全部用真 zip(<see cref="ZipArchive"/> 现造)+ 本机执行环境 + 真临时目录。
/// </summary>
public sealed class SkillZipInstallerTests : IDisposable
{
    private readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    private string Installed => Path.Combine(_fx.Root, "installed");

    private Task<InstalledSkill> InstallAsync(byte[] zip, string? sha = null, string? hint = null) =>
        SkillZipInstaller.InstallAsync(zip, _fx.Env, _fx.P(Installed), sha, hint);

    private static byte[] Demo(string content, string name = "demo") => Fx.Zip(($"{name}/SKILL.md", content));

    private async Task<string> KeyOfAsync(byte[] zip, string? sha = null)
    {
        var e = await Assert.ThrowsAsync<SkillException>(() => InstallAsync(zip, sha));
        return e.Key;
    }

    // ───────────────────────── 2.x 的用例 ─────────────────────────

    [Fact]
    public async Task Strips_the_archive_top_level_directory_and_atomically_replaces_an_install()
    {
        var first = await InstallAsync(Demo("---\nname: demo\ndescription: old\n---\nold body\n"));

        Assert.Equal("demo", first.Name);
        Assert.Contains("old body", File.ReadAllText(Path.Combine(Installed, "demo", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(Installed, "demo", "demo", "SKILL.md")));

        var second = await InstallAsync(Demo("---\nname: demo\ndescription: new\n---\nnew body\n"));

        Assert.Contains("new body", File.ReadAllText(Path.Combine(second.Target.Value, "SKILL.md")));
        // 替换之后没有留下 .installing-* / .backup-* 的残渣
        Assert.Equal(["demo"], Directory.GetDirectories(Installed).Select(Path.GetFileName));
        // 扫描读回来:来源是 zip,摘要对得上
        var scanned = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Installed), null, null));
        var skill = Assert.Single(scanned.Skills);
        Assert.Equal((SkillSourceKind.Zip, second.Sha256), (skill.Source.Kind, skill.Source.Sha256));
    }

    [Fact]
    public async Task Installs_archives_whose_entries_start_with_dot_slash()
    {
        var zip = Fx.Zip(("./a/SKILL.md", "---\nname: a\ndescription: dot prefix\n---\ndot-prefixed body\n"));

        var result = await InstallAsync(zip);

        Assert.Equal("a", result.Name);
        Assert.Contains("dot-prefixed body", File.ReadAllText(Path.Combine(Installed, "a", "SKILL.md")));
    }

    [Fact]
    public async Task Installs_a_skill_md_whose_description_is_a_block_scalar()
    {
        // 市场包把多行说明写成 `description: |`。安装器拒绝任何读不完整的 frontmatter,所以一个跳过块标量的解析器会让安装失败。
        var result = await InstallAsync(Demo("---\nname: demo\ndescription: |\n  第一行\n  第二行\n---\nbody\n"));

        var scanned = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Installed), null, null));
        Assert.Equal("demo", result.Name);
        Assert.Equal("第一行\n第二行", scanned.Skills[0].Description);
        Assert.Empty(scanned.Diagnostics);
    }

    [Fact]
    public async Task Installs_a_market_skill_md_with_a_metadata_block_and_takes_the_version_from_it()
    {
        var content = string.Join('\n',
            "---", "name: programming-tutor", "description: \"全能 AI 编程导师\"", "license: MIT", "metadata:",
            "  author: \"Samuel Kahessay\"", "  version: \"1.0.1\"", "  category: \"education\"", "---", "body", "");

        var result = await InstallAsync(Demo(content, "programming-tutor"));

        Assert.Equal(("programming-tutor", "1.0.1"), (result.Name, result.Version));
        var scanned = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Installed), null, null));
        Assert.Equal("programming-tutor", scanned.Skills[0].Name);
        Assert.Empty(scanned.Diagnostics);
    }

    [Fact]
    public async Task Uses_the_frontmatter_name_when_skill_md_is_at_the_archive_root_never_the_download_file_name()
    {
        var zip = Fx.Zip(
            ("SKILL.md", "---\nname: flat-demo\ndescription: flat package\n---\nflat body\n"),
            ("knowledge/guide.md", "guide"),
            ("RELEASE.md", "release"));

        var result = await InstallAsync(zip, hint: "renamed-download.zip");

        Assert.Equal("flat-demo", result.Name);
        Assert.Equal("guide", File.ReadAllText(Path.Combine(Installed, "flat-demo", "knowledge", "guide.md")));
        Assert.Equal("release", File.ReadAllText(Path.Combine(Installed, "flat-demo", "RELEASE.md")));
        var scanned = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Installed), null, null));
        Assert.Equal(("flat-demo", SkillSourceKind.Zip), (scanned.Skills[0].Name, scanned.Skills[0].Source.Kind));
    }

    [Fact]
    public async Task Still_rejects_wrapped_archives_with_multiple_top_level_directories()
    {
        var zip = Fx.Zip(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n"), ("extra/file.md", "extra"));

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(zip));
    }

    [Fact]
    public async Task Rejects_symbolic_links_in_archives()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = archive.CreateEntry("demo/SKILL.md").Open())
            {
                w.Write(Encoding.UTF8.GetBytes("---\nname: demo\ndescription: d\n---\nbody\n"));
            }

            var link = archive.CreateEntry("demo/escape");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // 0120777:符号链接
            using (var w = link.Open())
            {
                w.Write(Encoding.UTF8.GetBytes("/tmp"));
            }
        }

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(stream.ToArray()));
        Assert.False(Directory.Exists(Path.Combine(Installed, "demo")));
    }

    [Fact]
    public async Task Rejects_unsupported_frontmatter_syntax_even_when_name_and_description_are_present()
    {
        // 两层嵌套仍然读不了:安装必须失败在被跳过的那一行上。
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo("---\nname: demo\ndescription: d\na:\n  b:\n    c: 1\n---\nbody\n")));
    }

    // ───────────────────────── 不可信包的防线 ─────────────────────────

    [Theory]
    [InlineData("../evil/SKILL.md")]
    [InlineData("demo/../../evil.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.md")]
    [InlineData("demo\\SKILL.md")]
    [InlineData("demo//SKILL.md")]
    public async Task Invariant_hostile_paths_are_rejected_before_anything_is_written(string path)
    {
        var zip = Fx.Zip(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n"), (path, "x"));

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(zip));
        Assert.False(Directory.Exists(Installed) && Directory.GetFileSystemEntries(Installed).Length > 0);
        Assert.False(File.Exists(Path.Combine(_fx.Root, "evil.md")));
    }

    [Fact]
    public async Task Too_deep_paths_are_rejected()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", SkillLimits.MaxDepth + 1)) + "/f.md";
        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(Fx.Zip(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n"), ("demo/" + deep, "x"))));
    }

    [Fact]
    public async Task Duplicate_entries_are_rejected_even_when_they_only_differ_by_case()
    {
        var zip = Fx.Zip(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n"), ("demo/Notes.md", "a"), ("demo/notes.md", "b"));

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(zip));
    }

    [Fact]
    public async Task Invariant_a_zip_bomb_is_refused_by_the_declared_size_and_the_install_root_stays_untouched()
    {
        // 60MB 的零压缩后只有几十 KB:体积检查不能只看包本身。
        var bomb = new byte[60 * 1024 * 1024];
        var zip = Fx.Zip(("demo/SKILL.md", Encoding.UTF8.GetBytes("---\nname: demo\ndescription: d\n---\nbody\n")), ("demo/blob.bin", bomb));
        Assert.True(zip.Length < SkillLimits.MaxZipBytes);

        Assert.Equal(SkillErrorKeys.PackageTooLarge, await KeyOfAsync(zip));
        Assert.False(Directory.Exists(Installed) && Directory.GetFileSystemEntries(Installed).Length > 0);
    }

    [Fact]
    public async Task A_package_over_the_byte_limit_is_refused_outright()
    {
        Assert.Equal(SkillErrorKeys.PackageTooLarge, await KeyOfAsync(new byte[SkillLimits.MaxZipBytes + 1]));
    }

    [Fact]
    public async Task Too_many_entries_are_refused()
    {
        var entries = Enumerable.Range(0, SkillLimits.MaxEntries + 1).Select(i => ($"demo/f{i}.txt", "x")).Append(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n")).ToArray();

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(Fx.Zip(entries)));
    }

    [Fact]
    public async Task Garbage_that_is_not_a_zip_is_a_zip_invalid_error_not_a_crash()
    {
        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(Encoding.UTF8.GetBytes("this is not a zip file at all")));
    }

    [Fact]
    public async Task A_package_without_skill_md_is_refused()
    {
        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(Fx.Zip(("demo/readme.md", "x"))));
    }

    [Fact]
    public async Task A_reserved_metadata_file_name_in_the_archive_is_refused()
    {
        var zip = Fx.Zip(("demo/SKILL.md", "---\nname: demo\ndescription: d\n---\nbody\n"), ("demo/.nextcowork-package.json", "{}"));

        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(zip));
    }

    [Fact]
    public async Task The_wrapper_directory_must_equal_the_frontmatter_name()
    {
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo("---\nname: other\ndescription: d\n---\nbody\n")));
    }

    [Fact]
    public async Task A_wrapper_directory_that_is_not_a_valid_skill_name_is_refused()
    {
        Assert.Equal(SkillErrorKeys.ZipInvalid, await KeyOfAsync(Fx.Zip(("Not Valid/SKILL.md", "---\nname: x\ndescription: d\n---\nbody\n"))));
    }

    [Fact]
    public async Task An_oversized_skill_md_is_refused()
    {
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo($"---\nname: demo\ndescription: d\n---\n{new string('A', SkillLimits.FileMaxBytes + 10)}\n")));
    }

    [Fact]
    public async Task A_missing_description_an_empty_body_or_an_overlong_description_are_refused()
    {
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo("---\nname: demo\n---\nbody\n")));
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo("---\nname: demo\ndescription: d\n---\n   \n")));
        Assert.Equal(SkillErrorKeys.SkillMdInvalid, await KeyOfAsync(Demo($"---\nname: demo\ndescription: {new string('x', SkillLimits.DescriptionMax + 1)}\n---\nbody\n")));
    }

    [Fact]
    public async Task The_digest_must_match_when_one_is_given_and_a_mismatch_installs_nothing()
    {
        var zip = Demo("---\nname: demo\ndescription: d\n---\nbody\n");

        Assert.Equal(SkillErrorKeys.DigestMismatch, await KeyOfAsync(zip, new string('0', 64)));
        Assert.False(Directory.Exists(Path.Combine(Installed, "demo")));

        var ok = await InstallAsync(zip, sha: (await InstallAsync(zip)).Sha256.ToUpperInvariant());
        Assert.Equal("demo", ok.Name);
    }

    [Fact]
    public async Task Invariant_a_failed_install_leaves_the_previous_version_untouched()
    {
        await InstallAsync(Demo("---\nname: demo\ndescription: d\n---\nv1 body\n"));

        await Assert.ThrowsAsync<SkillException>(() => InstallAsync(Demo("---\nname: demo\ndescription: d\n---\n   \n")));

        Assert.Contains("v1 body", File.ReadAllText(Path.Combine(Installed, "demo", "SKILL.md")));
        Assert.Equal(["demo"], Directory.GetDirectories(Installed).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_version_falls_back_to_the_file_name_hint_only_when_the_frontmatter_has_none()
    {
        var withVersion = await InstallAsync(Demo("---\nname: demo\nversion: 2.0.0\ndescription: d\n---\nb\n"), hint: "demo-v9.9.9.zip");
        var withoutVersion = await InstallAsync(Demo("---\nname: demo\ndescription: d\n---\nb\n"), hint: "demo-v1.2.3.zip");

        Assert.Equal(("2.0.0", "1.2.3"), (withVersion.Version, withoutVersion.Version));
    }

    [Fact]
    public async Task Invariant_package_metadata_is_written_for_the_scan_and_an_executable_bit_survives()
    {
        var content = Encoding.UTF8.GetBytes("#!/bin/sh\necho hi\n");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = archive.CreateEntry("demo/SKILL.md").Open())
            {
                w.Write(Encoding.UTF8.GetBytes("---\nname: demo\ndescription: d\n---\nbody\n"));
            }

            var script = archive.CreateEntry("demo/scripts/run.sh");
            script.ExternalAttributes = unchecked((int)(0x81EDu << 16)); // 0100755
            using (var w = script.Open())
            {
                w.Write(content);
            }
        }

        var result = await InstallAsync(stream.ToArray());

        Assert.True(File.Exists(Path.Combine(result.Target.Value, SkillLoader.PackageMetadataFile)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(result.Target.Value, "scripts", "run.sh")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void Entry_path_normalisation_accepts_dot_slash_and_trailing_slash_but_nothing_hostile()
    {
        Assert.Equal("a/b", SkillZipInstaller.SafeEntryPath("./a/b/"));
        Assert.Null(SkillZipInstaller.SafeEntryPath("a/../b"));
        Assert.Null(SkillZipInstaller.SafeEntryPath("a\\b"));
        Assert.Null(SkillZipInstaller.SafeEntryPath(""));
        Assert.Null(SkillZipInstaller.SafeEntryPath("./"));
    }
}
