using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>变更与路径边界(移植自 2.x <c>workspace-files.test.ts</c> 的「file management and path boundaries」)。</summary>
public sealed class FileMutateTests
{
    private static ValueTask<FileMutation> Mutate(FilesRig rig, FileOperation op, string path, string? destination = null, bool overwrite = false) =>
        rig.Service.MutateAsync(rig.Fp, op, path, destination, overwrite);

    [Fact]
    public async Task Creates_renames_copies_and_moves_files_and_directories()
    {
        using var rig = new FilesRig();
        await Mutate(rig, FileOperation.CreateDirectory, "folder");
        await Mutate(rig, FileOperation.CreateFile, "folder/first.md");
        await Mutate(rig, FileOperation.Rename, "folder/first.md", "folder/renamed.md");
        rig.Write("folder/renamed.md", "# content");

        var copied = await Mutate(rig, FileOperation.Copy, "folder", "copied");
        Assert.Equal(new FileMutation("folder", "copied"), copied);
        await Mutate(rig, FileOperation.Move, "copied/renamed.md", "moved.md");

        Assert.Equal("# content", rig.Project.Read("moved.md"));
        Assert.Equal("# content", rig.Project.Read("folder/renamed.md"));
        Assert.Empty(Directory.GetFileSystemEntries(rig.Abs("copied")));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(rig.Root), p => Path.GetFileName(p).StartsWith(".ncw-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_moves_into_the_trash_directory_and_is_recoverable()
    {
        using var rig = new FilesRig();
        rig.Write("sub/file.txt", "recoverable");

        var result = await Mutate(rig, FileOperation.Delete, "sub/file.txt");

        Assert.False(rig.Project.Exists("sub/file.txt"));
        Assert.StartsWith(".next-cowork/trash/", result.RecoveryPath, StringComparison.Ordinal);
        Assert.EndsWith("/file.txt", result.RecoveryPath, StringComparison.Ordinal);
        Assert.Equal("recoverable", rig.Project.Read(result.RecoveryPath!));
        // 恢复 = 把它移回去
        await Mutate(rig, FileOperation.Move, result.RecoveryPath!, "sub/file.txt");
        Assert.Equal("recoverable", rig.Project.Read("sub/file.txt"));
    }

    [Fact]
    public async Task Deleting_a_directory_moves_the_whole_tree()
    {
        using var rig = new FilesRig();
        rig.Write("d/a.txt", "a");
        rig.Write("d/n/b.txt", "b");

        var result = await Mutate(rig, FileOperation.Delete, "d");

        Assert.False(rig.Project.Exists("d"));
        Assert.Equal("b", rig.Project.Read($"{result.RecoveryPath}/n/b.txt"));
    }

    [Fact]
    public async Task Deleting_a_missing_path_is_not_found_and_nothing_is_created()
    {
        using var rig = new FilesRig();

        await FilesRig.Fails(() => Mutate(rig, FileOperation.Delete, "nope.txt"), FilesErrorCode.NotFound);

        Assert.False(rig.Project.Exists(".next-cowork"));
    }

    [Theory]
    [InlineData(FileOperation.CreateFile)]
    [InlineData(FileOperation.CreateDirectory)]
    [InlineData(FileOperation.Rename)]
    [InlineData(FileOperation.Move)]
    [InlineData(FileOperation.Copy)]
    public async Task Refuses_to_replace_an_existing_destination(FileOperation op)
    {
        using var rig = new FilesRig();
        rig.Write("source.txt", "source");
        rig.Write("existing.txt", "keep");
        var path = op is FileOperation.CreateFile or FileOperation.CreateDirectory ? "existing.txt" : "source.txt";

        await FilesRig.Fails(() => Mutate(rig, op, path, "existing.txt"), FilesErrorCode.Exists);

        Assert.Equal("keep", rig.Project.Read("existing.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside/file.txt")]
    [InlineData("folder/../../outside/file.txt")]
    [InlineData("C:\\external.txt")]
    [InlineData("c:/external.txt")]
    [InlineData("a\0b")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("trailing/")]
    [InlineData("./a")]
    public async Task Refuses_root_absolute_or_malformed_paths_for_every_entry_point(string path)
    {
        using var rig = new FilesRig();

        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, path), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, path), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => rig.Service.WriteAsync(rig.Fp, path, "r", "x", null), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => rig.Service.ReadRangeAsync(rig.Fp, path, 0, 1, null), FilesErrorCode.InvalidPath);
        Assert.False(File.Exists(Path.Combine(rig.Outside, "file.txt")));
    }

    [Fact]
    public async Task An_absolute_path_to_a_file_outside_the_project_is_refused_not_followed()
    {
        using var rig = new FilesRig();
        var secret = Path.Combine(rig.Outside, "note.txt");
        File.WriteAllText(secret, "from outside");

        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, secret), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, Path.Combine(rig.Outside, "made.txt")), FilesErrorCode.InvalidPath);
        Assert.False(File.Exists(Path.Combine(rig.Outside, "made.txt")));
    }

    [Fact]
    public async Task Refuses_mutations_through_internal_and_external_symbolic_links()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        rig.Write("real/file.txt", "keep");
        Directory.CreateSymbolicLink(rig.Abs("internal"), rig.Abs("real"));
        Directory.CreateSymbolicLink(rig.Abs("external"), rig.Outside);

        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, "external/file.txt"), FilesErrorCode.Symlink);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Delete, "internal/file.txt"), FilesErrorCode.Symlink);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Delete, "internal"), FilesErrorCode.Symlink);

        Assert.Equal("keep", rig.Project.Read("real/file.txt"));
        Assert.Empty(Directory.GetFileSystemEntries(rig.Outside));
        Assert.False(rig.Project.Exists(".next-cowork"));
    }

    [Fact]
    public async Task Files_reached_through_a_linked_directory_inside_the_project_can_be_read_and_saved()
    {
        // 2.x #182:项目是一个装满 ln -s 的目录,每条链指向一个项目(可能在别的盘)
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        Directory.CreateDirectory(Path.Combine(rig.Outside, "project/src"));
        File.WriteAllText(Path.Combine(rig.Outside, "project/src/main.ts"), "linked");
        Directory.CreateSymbolicLink(rig.Abs("project"), Path.Combine(rig.Outside, "project"));

        var file = await rig.Service.ReadAsync(rig.Fp, "project/src/main.ts");
        Assert.Equal("linked", file.Text);
        await rig.Service.WriteAsync(rig.Fp, "project/src/main.ts", file.Revision, "edited", null);

        Assert.Equal("edited", File.ReadAllText(Path.Combine(rig.Outside, "project/src/main.ts")));
        // 链本身没被替换成普通文件
        Assert.Equal(["project"], Directory.GetFileSystemEntries(rig.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_file_that_is_itself_a_link_and_any_dotdot_escape_are_still_refused()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        Directory.CreateDirectory(Path.Combine(rig.Outside, "project/src"));
        File.WriteAllText(Path.Combine(rig.Outside, "project/src/main.ts"), "linked");
        Directory.CreateSymbolicLink(rig.Abs("project"), Path.Combine(rig.Outside, "project"));
        File.CreateSymbolicLink(rig.Abs("project-main.ts"), Path.Combine(rig.Outside, "project/src/main.ts"));

        await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, "project-main.ts"), FilesErrorCode.Symlink);
        foreach (var path in new[] { "project/../../outside/project/src/main.ts", "project/src/../../../outside/project/src/main.ts", "../outside/project/src/main.ts" })
        {
            await FilesRig.Fails(() => rig.Service.ReadAsync(rig.Fp, path), FilesErrorCode.InvalidPath);
        }
    }

    [Fact]
    public async Task Refuses_broken_links_including_an_existing_broken_link_destination()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        File.CreateSymbolicLink(rig.Abs("broken"), Path.Combine(rig.Outside, "missing"));
        rig.Write("file.txt", "keep");

        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, "broken"), FilesErrorCode.Symlink);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Copy, "file.txt", "broken"), FilesErrorCode.Symlink);
    }

    [Fact]
    public async Task Refuses_directory_copy_or_move_into_itself()
    {
        using var rig = new FilesRig();
        Directory.CreateDirectory(rig.Abs("folder"));

        await FilesRig.Fails(() => Mutate(rig, FileOperation.Copy, "folder", "folder/child"), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Move, "folder", "folder/child"), FilesErrorCode.InvalidPath);
    }

    [Fact]
    public async Task A_failed_copy_leaves_neither_the_destination_nor_a_staging_directory()
    {
        // 嵌套的符号链接:预检就拒(复制不给链接创建新的访问入口)
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        rig.Write("folder/a.txt", "a");
        Directory.CreateSymbolicLink(rig.Abs("folder/external"), rig.Outside);

        await FilesRig.Fails(() => Mutate(rig, FileOperation.Copy, "folder", "copy"), FilesErrorCode.Unsupported);

        Assert.False(rig.Project.Exists("copy"));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(rig.Root), p => Path.GetFileName(p).StartsWith(".ncw-copy-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copy_over_the_entry_limit_is_refused_by_the_pre_check_and_leaves_nothing()
    {
        using var rig = new FilesRig();
        rig.Write("folder/a.txt", "a");
        var tiny = new FileService(new FileServiceOptions { CopyMaxEntries = 1 });

        await FilesRig.Fails(() => tiny.MutateAsync(rig.Fp, FileOperation.Copy, "folder", "copy"), FilesErrorCode.TooLarge);

        Assert.False(rig.Project.Exists("copy"));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(rig.Root), p => Path.GetFileName(p).StartsWith(".ncw-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_overwrite_trashes_the_replaced_file_first()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "new");
        rig.Write("b.txt", "old");

        await Mutate(rig, FileOperation.Rename, "a.txt", "b.txt", overwrite: true);

        Assert.Equal("new", rig.Project.Read("b.txt"));
        Assert.False(rig.Project.Exists("a.txt"));
        var trashed = Directory.EnumerateFiles(rig.Abs(".next-cowork/trash"), "b.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("old", File.ReadAllText(trashed));
    }

    [Fact]
    public async Task Overwrite_only_applies_to_rename_and_only_to_files()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "a");
        rig.Write("b.txt", "b");
        Directory.CreateDirectory(rig.Abs("dir"));

        await FilesRig.Fails(() => Mutate(rig, FileOperation.Move, "a.txt", "b.txt", overwrite: true), FilesErrorCode.Exists);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Copy, "a.txt", "b.txt", overwrite: true), FilesErrorCode.Exists);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.Rename, "a.txt", "dir", overwrite: true), FilesErrorCode.Exists);
        Assert.Equal("b", rig.Project.Read("b.txt"));
        Assert.Equal("a", rig.Project.Read("a.txt"));
    }

    [Fact]
    public async Task Creating_under_a_missing_or_non_directory_parent_fails()
    {
        using var rig = new FilesRig();
        rig.Write("file.txt", "x");

        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, "nodir/new.txt"), FilesErrorCode.NotFound);
        await FilesRig.Fails(() => Mutate(rig, FileOperation.CreateFile, "file.txt/new.txt"), FilesErrorCode.InvalidPath);
    }

    [Fact]
    public async Task Rename_and_move_require_a_destination()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "x");

        await FilesRig.Fails(() => Mutate(rig, FileOperation.Rename, "a.txt"), FilesErrorCode.InvalidPath);
    }
}
