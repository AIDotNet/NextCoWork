using System.Text;
using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>读与分类(移植自 2.x <c>workspace-files.test.ts</c> 的「workspace file previews」,以及 <c>workspace-range</c> 的范围读取)。</summary>
public sealed class FileReadTests
{
    [Fact]
    public async Task Reads_utf8_exactly_including_bom_mixed_newlines_and_unicode()
    {
        using var rig = new FilesRig();
        var content = "﻿# 你好\r\nline one\nlast line\r";
        rig.Write("readme.md", content);

        var result = await rig.Service.ReadAsync(rig.Fp, "readme.md");

        Assert.Equal(FileKind.Text, result.Kind);
        Assert.Equal("readme.md", result.Path);
        Assert.Equal(content, result.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), result.Size);
        Assert.Matches("^[a-f0-9]{64}$", result.Revision);
        Assert.DoesNotContain(rig.Root, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supports_empty_files_dotfiles_and_extensionless_source_files()
    {
        using var rig = new FilesRig();
        rig.Write(".env", "TOKEN=value\n");
        rig.Write("Makefile", "");

        Assert.Equal("TOKEN=value\n", (await rig.Service.ReadAsync(rig.Fp, ".env")).Text);
        var makefile = await rig.Service.ReadAsync(rig.Fp, "Makefile");
        Assert.Equal(FileKind.Text, makefile.Kind);
        Assert.Equal("", makefile.Text);
    }

    [Fact]
    public async Task Images_come_back_as_bytes_with_their_mime_type()
    {
        using var rig = new FilesRig();
        var bytes = Convert.FromHexString("89504e470d0a1a0a");
        rig.WriteBytes("image.PNG", bytes);

        var result = await rig.Service.ReadAsync(rig.Fp, "image.PNG");

        Assert.Equal(FileKind.Image, result.Kind);
        Assert.Equal("image/png", result.Mime);
        Assert.Equal(bytes, result.Data);
        Assert.False(result.Chunked);
    }

    [Theory]
    [InlineData("document.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2d }, BinaryReason.Unsupported)]
    [InlineData("binary.txt", new byte[] { 0, 1, 2 }, BinaryReason.Unsupported)]
    [InlineData("invalid.txt", new byte[] { 0xc3, 0x28 }, BinaryReason.Encoding)]
    [InlineData("utf16.txt", new byte[] { 0xff, 0xfe, 0x61, 0 }, BinaryReason.Encoding)]
    public async Task Falls_back_to_binary_without_corrupting_anything(string name, byte[] bytes, BinaryReason reason)
    {
        using var rig = new FilesRig();
        rig.WriteBytes(name, bytes);

        var result = await rig.Service.ReadAsync(rig.Fp, name);

        Assert.Equal(FileKind.Binary, result.Kind);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Text);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("large.txt", FileLimits.TextMax)]
    [InlineData("large.png", FileLimits.ImageMax)]
    public async Task Oversized_files_are_not_read_at_all(string name, int limit)
    {
        using var rig = new FilesRig();
        rig.WriteSized(name, limit + 1L);

        var result = await rig.Service.ReadAsync(rig.Fp, name);

        Assert.Equal(FileKind.Binary, result.Kind);
        Assert.Equal(BinaryReason.TooLarge, result.Reason);
        Assert.Equal(limit + 1L, result.Size);
        Assert.StartsWith("large:", result.Revision, StringComparison.Ordinal);
        Assert.Null(result.Text);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task A_big_image_is_chunked_instead_of_inlined()
    {
        using var rig = new FilesRig();
        rig.WriteSized("big.png", FileLimits.InlineImageMax + 1L);

        var result = await rig.Service.ReadAsync(rig.Fp, "big.png");

        Assert.Equal(FileKind.Image, result.Kind);
        Assert.True(result.Chunked);
        Assert.Null(result.Data);
        Assert.StartsWith("large:", result.Revision, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_directory_or_a_missing_file_fails_with_the_right_code()
    {
        using var rig = new FilesRig();
        Directory.CreateDirectory(rig.Abs("dir"));

        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, "dir"), FilesErrorCode.NotFile);
        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, "nope.txt"), FilesErrorCode.NotFound);
    }

    [Fact]
    public async Task A_missing_project_root_is_project_unavailable()
    {
        using var rig = new FilesRig();
        Directory.Delete(rig.Root, true);

        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, "a.txt"), FilesErrorCode.ProjectUnavailable);
    }

    // ── readRange ──

    [Fact]
    public async Task Range_reads_walk_a_large_file_in_chunks_that_join_back_to_the_original()
    {
        using var rig = new FilesRig();
        var bytes = new byte[FileLimits.RangeMax * 2 + 12345];
        new Random(7).NextBytes(bytes);
        rig.WriteBytes("movie.bin", bytes);

        var joined = new List<byte>();
        long offset = 0;
        string? revision = null;
        for (var i = 0; i < 10; i++)
        {
            var chunk = await rig.Service.ReadRangeAsync(rig.Fp, "movie.bin", offset, FileLimits.RangeMax * 4, revision);
            Assert.True(chunk.Length <= FileLimits.RangeMax);
            joined.AddRange(chunk.Data);
            offset += chunk.Length;
            revision = chunk.Revision;
            Assert.Equal(bytes.Length, chunk.Size);
            if (chunk.Eof)
            {
                break;
            }
        }

        Assert.Equal(bytes, joined);
    }

    [Fact]
    public async Task A_range_past_the_end_is_empty_and_eof()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "hello");

        var chunk = await rig.Service.ReadRangeAsync(rig.Fp, "a.txt", 100, 10, null);

        Assert.Empty(chunk.Data);
        Assert.Equal(0, chunk.Length);
        Assert.True(chunk.Eof);
    }

    [Fact]
    public async Task A_stale_revision_between_chunks_is_a_conflict()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "hello world");
        var first = await rig.Service.ReadRangeAsync(rig.Fp, "a.txt", 0, 5, null);
        rig.Write("a.txt", "hello world, changed");

        await FilesRig.Fails(() => rig.Service.ReadRangeAsync(rig.Fp, "a.txt", 5, 5, first.Revision), FilesErrorCode.Conflict);
    }

    [Fact]
    public async Task A_negative_range_is_refused()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "hello");

        await FilesRig.Fails(() => rig.Service.ReadRangeAsync(rig.Fp, "a.txt", -1, 5, null), FilesErrorCode.InvalidPath);
    }
}
