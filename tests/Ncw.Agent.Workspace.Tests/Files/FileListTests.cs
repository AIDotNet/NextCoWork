using Ncw.Agent.Workspace.Files;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>目录列表(移植自 2.x <c>workspace-listing.ts</c>:一层、软链要看指向什么、断链仍列出来;加上 3.0 的分页 / 忽略表)。</summary>
public sealed class FileListTests
{
    private static Task<DirListing> List(FilesRig rig, string path = "", FileSortBy sort = FileSortBy.Name, bool hide = false, int offset = 0, int limit = 0) =>
        rig.Service.ListAsync(rig.Fp, path, sort, hide, offset, limit).AsTask();

    [Fact]
    public async Task Lists_one_level_directories_first_with_project_relative_paths()
    {
        using var rig = new FilesRig();
        rig.Write("b.txt", "bb");
        rig.Write("a.txt", "a");
        rig.Write("src/inner/deep.txt", "x");
        Directory.CreateDirectory(rig.Abs("docs"));

        var root = await List(rig);

        Assert.Equal(["docs", "src", "a.txt", "b.txt"], root.Entries.Select(e => e.Name));
        Assert.Equal(["docs", "src"], root.Entries.Where(e => e.IsDirectory).Select(e => e.Path));
        Assert.False(root.Truncated);
        Assert.Equal(4, root.Total);
        Assert.Equal(2, root.Entries.Single(e => e.Name == "b.txt").Size);
        Assert.Null(root.Entries.Single(e => e.Name == "src").Size);

        var src = await List(rig, "src");
        Assert.Equal("src/inner", src.Entries.Single().Path);
        Assert.Equal("src", src.Path);
        Assert.DoesNotContain(rig.Root, string.Join('|', root.Entries.Select(e => e.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flags_hidden_and_ignored_entries_and_can_filter_the_ignored_ones()
    {
        using var rig = new FilesRig();
        rig.Write(".env", "x");
        rig.Write(".github/ci.yml", "x");
        rig.Write("node_modules/pkg/index.js", "x");
        rig.Write(".git/HEAD", "x");
        rig.Write(".DS_Store", "x");
        rig.Write("src/app.ts", "x");

        var all = await List(rig);
        Assert.True(all.Entries.Single(e => e.Name == ".env").Hidden);
        Assert.False(all.Entries.Single(e => e.Name == ".env").Ignored);
        Assert.True(all.Entries.Single(e => e.Name == "node_modules").Ignored);
        Assert.True(all.Entries.Single(e => e.Name == ".git").Ignored);
        Assert.True(all.Entries.Single(e => e.Name == ".DS_Store").Ignored);
        Assert.False(all.Entries.Single(e => e.Name == ".github").Ignored); // 点开头的目录不整体跳过

        var filtered = await List(rig, hide: true);
        Assert.Equal([".github", "src", ".env"], filtered.Entries.Select(e => e.Name));
        Assert.Equal(3, filtered.Total);
    }

    [Fact]
    public async Task Pages_are_cut_after_sorting_and_report_truncation()
    {
        using var rig = new FilesRig();
        foreach (var n in new[] { "e", "d", "c", "b", "a" })
        {
            rig.Write($"{n}.txt", n);
        }

        var first = await List(rig, limit: 2);
        var second = await List(rig, offset: 2, limit: 2);
        var third = await List(rig, offset: 4, limit: 2);
        var beyond = await List(rig, offset: 99, limit: 2);

        Assert.Equal(["a.txt", "b.txt"], first.Entries.Select(e => e.Name));
        Assert.True(first.Truncated);
        Assert.Equal(5, first.Total);
        Assert.Equal(["c.txt", "d.txt"], second.Entries.Select(e => e.Name));
        Assert.True(second.Truncated);
        Assert.Equal(["e.txt"], third.Entries.Select(e => e.Name));
        Assert.False(third.Truncated);
        Assert.Empty(beyond.Entries);
        Assert.False(beyond.Truncated);
    }

    [Fact]
    public async Task Sorts_by_name_mtime_or_size()
    {
        using var rig = new FilesRig();
        rig.Write("v10.ts", "1234567890");
        rig.Write("v2.ts", "1");
        rig.Write("v1.ts", "12345");
        File.SetLastWriteTimeUtc(rig.Abs("v10.ts"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(rig.Abs("v2.ts"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(rig.Abs("v1.ts"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["v1.ts", "v2.ts", "v10.ts"], (await List(rig)).Entries.Select(e => e.Name));
        Assert.Equal(["v2.ts", "v1.ts", "v10.ts"], (await List(rig, sort: FileSortBy.Mtime)).Entries.Select(e => e.Name));
        Assert.Equal(["v10.ts", "v1.ts", "v2.ts"], (await List(rig, sort: FileSortBy.Size)).Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task A_link_to_a_directory_lists_as_a_directory_and_a_broken_link_as_a_file_without_size()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        Directory.CreateDirectory(Path.Combine(rig.Outside, "target"));
        Directory.CreateSymbolicLink(rig.Abs("linked"), Path.Combine(rig.Outside, "target"));
        File.CreateSymbolicLink(rig.Abs("broken"), Path.Combine(rig.Outside, "missing"));

        var listing = await List(rig);

        var linked = listing.Entries.Single(e => e.Name == "linked");
        Assert.True(linked.IsDirectory);
        Assert.True(linked.IsLink);
        var broken = listing.Entries.Single(e => e.Name == "broken");
        Assert.False(broken.IsDirectory);
        Assert.True(broken.IsLink);
        Assert.Null(broken.Size);
    }

    [Fact]
    public async Task Listing_a_file_a_missing_directory_or_an_escape_fails()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "x");

        await FilesRig.Fails(() => rig.Service.ListAsync(rig.Fp, "a.txt", FileSortBy.Name, false, 0, 0), FilesErrorCode.NotFile);
        await FilesRig.Fails(() => rig.Service.ListAsync(rig.Fp, "nope", FileSortBy.Name, false, 0, 0), FilesErrorCode.NotFound);
        await FilesRig.Fails(() => rig.Service.ListAsync(rig.Fp, "..", FileSortBy.Name, false, 0, 0), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => rig.Service.ListAsync(rig.Fp, rig.Outside, FileSortBy.Name, false, 0, 0), FilesErrorCode.InvalidPath);
    }
}
