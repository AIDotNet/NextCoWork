using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>保存(移植自 2.x <c>workspace-files.test.ts</c> 的「workspace file saves」和「base64 image saves」)。</summary>
public sealed class FileWriteTests
{
    private static byte[] Png() => Convert.FromHexString("89504e470d0a1a0a0000000d49484452");

    [Fact]
    public async Task Saves_exact_text_atomically_and_keeps_the_executable_bit()
    {
        using var rig = new FilesRig();
        var target = rig.Write("script.sh", "#!/bin/sh\r\necho old\r\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, (UnixFileMode)Convert.ToInt32("755", 8));
        }

        var before = await rig.Service.ReadAsync(rig.Fp, "script.sh");
        var content = "﻿#!/bin/sh\r\necho 新内容\r\n";
        var saved = await rig.Service.WriteAsync(rig.Fp, "script.sh", before.Revision, content, null);

        Assert.NotEqual(before.Revision, saved.Revision);
        Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(content), File.ReadAllBytes(target)); // 含 BOM 的字节(ReadAllText 会吞掉 BOM)
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), File.GetUnixFileMode(target));
        }

        // 临时文件不留
        Assert.Equal(["script.sh"], Directory.GetFileSystemEntries(rig.Root).Select(Path.GetFileName));
        // 新修订号就是新内容的修订号
        Assert.Equal((await rig.Service.ReadAsync(rig.Fp, "script.sh")).Revision, saved.Revision);
    }

    [Fact]
    public async Task A_read_save_round_trip_preserves_the_original_bytes_with_bom_and_mixed_newlines()
    {
        using var rig = new FilesRig();
        var original = System.Text.Encoding.UTF8.GetBytes("﻿a\r\nb\rc\n");
        rig.WriteBytes("roundtrip.txt", original);

        var file = await rig.Service.ReadAsync(rig.Fp, "roundtrip.txt");
        await rig.Service.WriteAsync(rig.Fp, "roundtrip.txt", file.Revision, file.Text, null);

        Assert.Equal(original, File.ReadAllBytes(rig.Abs("roundtrip.txt")));
    }

    [Fact]
    public async Task Saving_unchanged_content_does_not_touch_the_file()
    {
        using var rig = new FilesRig();
        rig.Write("same.txt", "x");
        var file = await rig.Service.ReadAsync(rig.Fp, "same.txt");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(rig.Abs("same.txt"), stamp);

        var saved = await rig.Service.WriteAsync(rig.Fp, "same.txt", file.Revision, "x", null);

        Assert.Equal(file.Revision, saved.Revision);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(rig.Abs("same.txt")));
    }

    [Fact]
    public async Task An_external_change_is_detected_even_when_the_length_is_unchanged()
    {
        using var rig = new FilesRig();
        rig.Write("source.ts", "const a = 1\n");
        var before = await rig.Service.ReadAsync(rig.Fp, "source.ts");
        rig.Write("source.ts", "const a = 2\n");

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "source.ts", before.Revision, "const a = 3\n", null), FilesErrorCode.Conflict);

        Assert.Equal("const a = 2\n", rig.Project.Read("source.ts"));
        Assert.Single(Directory.GetFileSystemEntries(rig.Root));
    }

    [Fact]
    public async Task A_file_replaced_by_a_symbolic_link_after_reading_is_not_written_through()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        rig.Write("file.txt", "original");
        var before = await rig.Service.ReadAsync(rig.Fp, "file.txt");
        File.Delete(rig.Abs("file.txt"));
        File.WriteAllText(Path.Combine(rig.Outside, "external.txt"), "external");
        File.CreateSymbolicLink(rig.Abs("file.txt"), Path.Combine(rig.Outside, "external.txt"));

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "file.txt", before.Revision, "changed", null), FilesErrorCode.Symlink);

        Assert.Equal("external", File.ReadAllText(Path.Combine(rig.Outside, "external.txt")));
    }

    [Fact]
    public async Task A_hard_link_is_replaced_locally_without_changing_its_external_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ln is a POSIX tool");
        using var rig = new FilesRig();
        var external = Path.Combine(rig.Outside, "external.txt");
        File.WriteAllText(external, "external");
        using (var ln = System.Diagnostics.Process.Start("ln", [external, rig.Abs("local.txt")]))
        {
            await ln!.WaitForExitAsync();
            Assert.Equal(0, ln.ExitCode);
        }

        var before = await rig.Service.ReadAsync(rig.Fp, "local.txt");
        await rig.Service.WriteAsync(rig.Fp, "local.txt", before.Revision, "local edit", null);

        Assert.Equal("external", File.ReadAllText(external));
        Assert.Equal("local edit", rig.Project.Read("local.txt"));
    }

    [Fact]
    public async Task Oversized_and_lossy_text_is_refused_before_the_file_changes()
    {
        using var rig = new FilesRig();
        rig.Write("file.txt", "original");
        var file = await rig.Service.ReadAsync(rig.Fp, "file.txt");

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "file.txt", file.Revision, new string('a', FileLimits.TextMax + 1), null), FilesErrorCode.TooLarge);
        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "file.txt", file.Revision, "\ud800", null), FilesErrorCode.InvalidEncoding);
        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "file.txt", file.Revision, "a\0b", null), FilesErrorCode.InvalidEncoding);

        Assert.Equal("original", rig.Project.Read("file.txt"));
    }

    [Fact]
    public async Task Exactly_one_of_text_or_data_is_required_and_text_only_overwrites_text_files()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "x");
        rig.WriteBytes("p.png", Png());
        var a = await rig.Service.ReadAsync(rig.Fp, "a.txt");
        var p = await rig.Service.ReadAsync(rig.Fp, "p.png");

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "a.txt", a.Revision, null, null), FilesErrorCode.InvalidEncoding);
        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "a.txt", a.Revision, "x", [1]), FilesErrorCode.InvalidEncoding);
        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "p.png", p.Revision, "text over an image", null), FilesErrorCode.Unsupported);
    }

    [Fact]
    public async Task Writing_a_missing_file_is_not_found()
    {
        using var rig = new FilesRig();

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "nope.txt", "r", "x", null), FilesErrorCode.NotFound);
    }

    // ── 图片字节 ──

    [Fact]
    public async Task Overwrites_an_existing_image_atomically_and_keeps_its_mode()
    {
        using var rig = new FilesRig();
        var target = rig.Abs("photo.png");
        rig.WriteBytes("photo.png", Png());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, (UnixFileMode)Convert.ToInt32("644", 8));
        }

        var before = await rig.Service.ReadAsync(rig.Fp, "photo.png");
        var edited = Convert.FromHexString("89504e470d0a1a0affd8ffe0");
        var saved = await rig.Service.WriteAsync(rig.Fp, "photo.png", before.Revision, null, edited);

        Assert.Equal(edited.Length, saved.Size);
        Assert.Equal(edited, File.ReadAllBytes(target));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((UnixFileMode)Convert.ToInt32("644", 8), File.GetUnixFileMode(target));
        }

        Assert.Equal(["photo.png"], Directory.GetFileSystemEntries(rig.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Skips_the_write_when_the_bytes_match_the_file()
    {
        using var rig = new FilesRig();
        rig.WriteBytes("same.png", Png());
        var before = await rig.Service.ReadAsync(rig.Fp, "same.png");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(rig.Abs("same.png"), stamp);

        var saved = await rig.Service.WriteAsync(rig.Fp, "same.png", before.Revision, null, Png());

        Assert.Equal(before.Revision, saved.Revision);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(rig.Abs("same.png")));
    }

    [Fact]
    public async Task Keeps_external_changes_when_the_image_revision_is_stale()
    {
        using var rig = new FilesRig();
        rig.WriteBytes("stale.png", Png());
        var before = await rig.Service.ReadAsync(rig.Fp, "stale.png");
        var external = Convert.FromHexString("89504e470d0a1a0a1111");
        rig.WriteBytes("stale.png", external);

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "stale.png", before.Revision, null, Png()), FilesErrorCode.Conflict);

        Assert.Equal(external, File.ReadAllBytes(rig.Abs("stale.png")));
    }

    [Fact]
    public async Task Refuses_byte_writes_onto_non_image_files()
    {
        using var rig = new FilesRig();
        rig.Write("notes.txt", "plain text");
        var before = await rig.Service.ReadAsync(rig.Fp, "notes.txt");

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "notes.txt", before.Revision, null, Png()), FilesErrorCode.Unsupported);

        Assert.Equal("plain text", rig.Project.Read("notes.txt"));
    }

    [Fact]
    public async Task Rejects_oversized_byte_payloads_before_touching_the_file()
    {
        using var rig = new FilesRig();
        rig.WriteBytes("huge.png", Png());
        var before = await rig.Service.ReadAsync(rig.Fp, "huge.png");

        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, "huge.png", before.Revision, null, new byte[FileLimits.ImageMax + 1]), FilesErrorCode.TooLarge);

        Assert.Equal(Png(), File.ReadAllBytes(rig.Abs("huge.png")));
    }
}
