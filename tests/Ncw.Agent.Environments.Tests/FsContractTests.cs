using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Environments.Tests;

/// <summary>文件系统契约:本机和 SSH(SFTP)必须表现一致,同一份测试跑两遍。</summary>
public abstract class FsContractTests : IAsyncLifetime
{
    protected IExecutionEnvironment Env { get; private set; } = null!;

    private IEnvironmentFs Fs => Env.Fs;

    protected abstract Task<IExecutionEnvironment> CreateAsync();

    public async ValueTask InitializeAsync()
    {
        Env = await CreateAsync();
        await Env.EnsureReadyAsync();
    }

    public async ValueTask DisposeAsync() => await Env.DisposeAsync();

    private static async Task<EnvironmentException> ExpectAsync(Func<ValueTask> action, EnvironmentErrorCode code)
    {
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await action());
        Assert.Equal(code, error.Code);
        return error;
    }

    [Fact]
    public async Task Write_read_stat_round_trip()
    {
        using var temp = new TempDir();
        var file = temp.EnvFile("a.txt");
        await Fs.WriteTextAsync(file, "héllo\r\nworld");

        Assert.Equal("héllo\r\nworld", await Fs.ReadTextAsync(file));
        var info = await Fs.StatAsync(file);
        Assert.True(info.IsFile);
        Assert.Equal(Encoding.UTF8.GetByteCount("héllo\r\nworld"), info.Size);
        Assert.True(await Fs.ExistsAsync(file));
        Assert.False(await Fs.ExistsAsync(temp.EnvFile("missing")));
        Assert.True((await Fs.StatAsync(temp.Env)).IsDirectory);
    }

    [Fact]
    public async Task Missing_paths_report_not_found()
    {
        using var temp = new TempDir();
        var missing = temp.EnvFile("nope");
        await ExpectAsync(() => Fs.StatAsync(missing).AsVoid(), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.LStatAsync(missing).AsVoid(), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.RealPathAsync(missing).AsVoid(), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.OpenReadAsync(missing).AsVoid(), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.ListAsync(missing).AsVoid(), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.DeleteFileAsync(missing), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.RenameAsync(missing, temp.EnvFile("x"), false), EnvironmentErrorCode.NotFound);
        await ExpectAsync(() => Fs.MkdirAsync(temp.Env.Join("a", "b"), recursive: false), EnvironmentErrorCode.NotFound);
    }

    [Fact]
    public async Task Exclusive_write_refuses_to_overwrite()
    {
        using var temp = new TempDir();
        var file = temp.EnvFile("a");
        await Fs.WriteTextAsync(file, "1", new FsWriteOptions(Exclusive: true));
        await ExpectAsync(() => Fs.WriteTextAsync(file, "2", new FsWriteOptions(Exclusive: true)), EnvironmentErrorCode.AlreadyExists);
        Assert.Equal("1", await Fs.ReadTextAsync(file));
        await Fs.WriteTextAsync(file, "3");
        Assert.Equal("3", await Fs.ReadTextAsync(file));
    }

    [Fact]
    public async Task Create_parents_and_mode()
    {
        using var temp = new TempDir();
        var file = temp.Env.Join("a", "b", "c.txt");
        await ExpectAsync(() => Fs.WriteTextAsync(file, "x"), EnvironmentErrorCode.NotFound);
        await Fs.WriteTextAsync(file, "x", new FsWriteOptions(UnixMode: 0x180 /* 0600 */, CreateParents: true));
        Assert.Equal("x", await Fs.ReadTextAsync(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(0x180, (await Fs.StatAsync(file)).UnixMode & 0x1FF);
        }
    }

    [Fact]
    public async Task Directories_cannot_be_read_or_written_as_files()
    {
        using var temp = new TempDir();
        await ExpectAsync(() => Fs.OpenReadAsync(temp.Env).AsVoid(), EnvironmentErrorCode.IsDirectory);
        await ExpectAsync(() => Fs.OpenWriteAsync(temp.Env, new FsWriteOptions()).AsVoid(), EnvironmentErrorCode.IsDirectory);
        await ExpectAsync(() => Fs.DeleteFileAsync(temp.Env), EnvironmentErrorCode.IsDirectory);
        var file = temp.EnvFile("f");
        await Fs.WriteTextAsync(file, "");
        await ExpectAsync(() => Fs.ListAsync(file).AsVoid(), EnvironmentErrorCode.NotDirectory);
        await ExpectAsync(() => Fs.DeleteDirectoryAsync(file, false), EnvironmentErrorCode.NotDirectory);
    }

    [Fact]
    public async Task Reading_beyond_the_limit_fails_without_loading_everything()
    {
        using var temp = new TempDir();
        var file = temp.EnvFile("big");
        await Fs.WriteBytesAsync(file, new byte[10_000]);
        await ExpectAsync(() => Fs.ReadBytesAsync(file, 9_999).AsVoid(), EnvironmentErrorCode.TooLarge);
        Assert.Equal(10_000, (await Fs.ReadBytesAsync(file, 10_000)).Length);
        Assert.Equal(16, (await Fs.ReadHeadAsync(file, 16)).Length);
        Assert.Equal(10_000, (await Fs.ReadHeadAsync(file, 20_000)).Length);
    }

    [Fact]
    public async Task List_is_sorted_and_reports_entry_kinds()
    {
        using var temp = new TempDir();
        await Fs.WriteTextAsync(temp.EnvFile("b.txt"), "bb");
        await Fs.WriteTextAsync(temp.EnvFile("a.txt"), "a");
        await Fs.MkdirAsync(temp.EnvFile("dir"), recursive: false);

        var entries = await Fs.ListAsync(temp.Env);
        Assert.Equal(["a.txt", "b.txt", "dir"], entries.Select(e => e.Name));
        Assert.True(entries[0].Info.IsFile);
        Assert.Equal(2, entries[1].Info.Size);
        Assert.True(entries[2].Info.IsDirectory);
    }

    [Fact]
    public async Task Symlinks_are_visible_to_lstat_and_followed_by_stat()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        var target = temp.EnvFile("target.txt");
        await Fs.WriteTextAsync(target, "12345");
        var link = temp.EnvFile("link");
        File.CreateSymbolicLink(link.Value, target.Value);
        var broken = temp.EnvFile("broken");
        File.CreateSymbolicLink(broken.Value, temp.File("nothing"));

        Assert.True((await Fs.LStatAsync(link)).IsSymlink);
        var followed = await Fs.StatAsync(link);
        Assert.True(followed.IsFile);
        Assert.Equal(5, followed.Size);
        Assert.True((await Fs.LStatAsync(broken)).IsSymlink);
        await ExpectAsync(() => Fs.StatAsync(broken).AsVoid(), EnvironmentErrorCode.NotFound);
        Assert.Equal((await Fs.RealPathAsync(target)).Value, (await Fs.RealPathAsync(link)).Value);
        Assert.Equal("12345", await Fs.ReadTextAsync(link));
        Assert.Contains((await Fs.ListAsync(temp.Env)).Where(e => e.Name == "link"), e => e.Info.IsSymlink);
    }

    [Fact]
    public async Task Symlink_loops_do_not_hang_realpath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        File.CreateSymbolicLink(temp.File("a"), temp.File("b"));
        File.CreateSymbolicLink(temp.File("b"), temp.File("a"));
        // 本机报 NotFound;SFTP 服务器可能原样返回路径。共同点:不挂住,出错就是结构化错误
        await Task.Run(async () =>
        {
            try
            {
                await Fs.RealPathAsync(temp.EnvFile("a"));
            }
            catch (EnvironmentException)
            {
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Deleting_or_renaming_a_symlink_acts_on_the_link_not_its_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        var target = temp.EnvFile("target.txt");
        await Fs.WriteTextAsync(target, "keep me");
        File.CreateSymbolicLink(temp.File("link"), target.Value);

        await Fs.RenameAsync(temp.EnvFile("link"), temp.EnvFile("moved"), replace: false);
        Assert.True((await Fs.LStatAsync(temp.EnvFile("moved"))).IsSymlink);
        Assert.False(await Fs.ExistsAsync(temp.EnvFile("link")));
        Assert.Equal("keep me", await Fs.ReadTextAsync(target));

        await Fs.DeleteFileAsync(temp.EnvFile("moved"));
        Assert.False(await Fs.ExistsAsync(temp.EnvFile("moved")));
        Assert.Equal("keep me", await Fs.ReadTextAsync(target));
    }

    [Fact]
    public async Task Recursive_delete_never_follows_symlinks_out_of_the_tree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        var outside = temp.EnvFile("outside");
        await Fs.MkdirAsync(outside, false);
        await Fs.WriteTextAsync(outside.Join("precious"), "x");
        var tree = temp.EnvFile("tree");
        await Fs.MkdirAsync(tree.Join("sub"), recursive: true);
        File.CreateSymbolicLink(temp.File("tree/sub/to-outside"), outside.Value);
        File.CreateSymbolicLink(temp.File("tree/file-link"), outside.Join("precious").Value);

        await Fs.DeleteDirectoryAsync(tree, recursive: true);
        Assert.False(await Fs.ExistsAsync(tree));
        Assert.Equal("x", await Fs.ReadTextAsync(outside.Join("precious")));
    }

    [Fact]
    public async Task Realpath_resolves_relative_links_and_dotdot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        await Fs.MkdirAsync(temp.Env.Join("real", "sub"), recursive: true);
        File.CreateSymbolicLink(temp.File("alias"), "real/sub");
        var resolved = await Fs.RealPathAsync(temp.Env.Join("alias", "..", "real"));
        Assert.Equal((await Fs.RealPathAsync(temp.Env.Join("real"))).Value, resolved.Value);
    }

    [Fact]
    public async Task Mkdir_semantics()
    {
        using var temp = new TempDir();
        var dir = temp.EnvFile("d");
        await Fs.MkdirAsync(dir, recursive: false);
        await ExpectAsync(() => Fs.MkdirAsync(dir, recursive: false), EnvironmentErrorCode.AlreadyExists);
        await Fs.MkdirAsync(dir, recursive: true);
        await Fs.MkdirAsync(dir.Join("x", "y"), recursive: true);
        Assert.True((await Fs.StatAsync(dir.Join("x", "y"))).IsDirectory);
        var file = temp.EnvFile("f");
        await Fs.WriteTextAsync(file, "");
        await ExpectAsync(() => Fs.MkdirAsync(file, recursive: true), EnvironmentErrorCode.AlreadyExists);
    }

    [Fact]
    public async Task Rename_respects_replace()
    {
        using var temp = new TempDir();
        var a = temp.EnvFile("a");
        var b = temp.EnvFile("b");
        await Fs.WriteTextAsync(a, "A");
        await Fs.WriteTextAsync(b, "B");

        await ExpectAsync(() => Fs.RenameAsync(a, b, replace: false), EnvironmentErrorCode.AlreadyExists);
        Assert.Equal("B", await Fs.ReadTextAsync(b));
        await Fs.RenameAsync(a, b, replace: true);
        Assert.Equal("A", await Fs.ReadTextAsync(b));
        Assert.False(await Fs.ExistsAsync(a));

        var dir1 = temp.EnvFile("d1");
        var dir2 = temp.EnvFile("d2");
        await Fs.MkdirAsync(dir1, false);
        await Fs.WriteTextAsync(dir1.Join("f"), "x");
        await Fs.RenameAsync(dir1, dir2, replace: false);
        Assert.Equal("x", await Fs.ReadTextAsync(dir2.Join("f")));
    }

    [Fact]
    public async Task Delete_semantics()
    {
        using var temp = new TempDir();
        var dir = temp.EnvFile("d");
        await Fs.MkdirAsync(dir.Join("sub"), recursive: true);
        await Fs.WriteTextAsync(dir.Join("sub", "f"), "x");

        await ExpectAsync(() => Fs.DeleteDirectoryAsync(dir, recursive: false), EnvironmentErrorCode.DirectoryNotEmpty);
        await Fs.DeleteFileAsync(dir.Join("sub", "f"));
        await Fs.DeleteDirectoryAsync(dir.Join("sub"), recursive: false);
        await Fs.DeleteDirectoryAsync(dir, recursive: false);
        Assert.False(await Fs.ExistsAsync(dir));

        var other = temp.EnvFile("o");
        await Fs.MkdirAsync(other.Join("a"), recursive: true);
        await Fs.WriteTextAsync(other.Join("a", "f"), "x");
        await Fs.DeleteDirectoryAsync(other, recursive: true);
        Assert.False(await Fs.ExistsAsync(other));
    }

    [Fact]
    public async Task Deleting_a_symlink_to_a_directory_does_not_touch_the_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var temp = new TempDir();
        var real = temp.EnvFile("real");
        await Fs.MkdirAsync(real, false);
        await Fs.WriteTextAsync(real.Join("keep"), "x");
        File.CreateSymbolicLink(temp.File("link"), real.Value);
        await Fs.DeleteDirectoryAsync(temp.EnvFile("link"), recursive: true);
        Assert.True(await Fs.ExistsAsync(real.Join("keep")));
        Assert.False(await Fs.ExistsAsync(temp.EnvFile("link")));
    }

    [Fact]
    public async Task Copy_enforces_limit_and_never_overwrites()
    {
        using var temp = new TempDir();
        var source = temp.EnvFile("s");
        await Fs.WriteBytesAsync(source, new byte[100]);
        var copy = temp.EnvFile("c");

        await ExpectAsync(() => Fs.CopyFileAsync(source, copy, 99).AsVoid(), EnvironmentErrorCode.TooLarge);
        Assert.False(await Fs.ExistsAsync(copy));
        Assert.Equal(100, await Fs.CopyFileAsync(source, copy, 100));
        await ExpectAsync(() => Fs.CopyFileAsync(source, copy, 100).AsVoid(), EnvironmentErrorCode.AlreadyExists);
        await ExpectAsync(() => Fs.CopyFileAsync(temp.Env, temp.EnvFile("x"), 100).AsVoid(), EnvironmentErrorCode.IsDirectory);
    }

    [Fact]
    public async Task Generic_copy_through_streams_matches_the_native_one()
    {
        using var temp = new TempDir();
        var source = temp.EnvFile("s");
        await Fs.WriteTextAsync(source, "payload");
        Assert.Equal(7, await Fs.CopyFileViaStreamsAsync(source, temp.EnvFile("c"), 100));
        Assert.Equal("payload", await Fs.ReadTextAsync(temp.EnvFile("c")));
        await ExpectAsync(() => Fs.CopyFileViaStreamsAsync(source, temp.EnvFile("c"), 100).AsVoid(), EnvironmentErrorCode.AlreadyExists);
        await ExpectAsync(() => Fs.CopyFileViaStreamsAsync(source, temp.EnvFile("d"), 3).AsVoid(), EnvironmentErrorCode.TooLarge);
        Assert.False(await Fs.ExistsAsync(temp.EnvFile("d")));
    }

    [Fact]
    public async Task Stream_write_is_atomic_and_cleans_up_when_over_the_limit()
    {
        using var temp = new TempDir();
        var file = temp.EnvFile("out");
        await Fs.WriteTextAsync(file, "old");

        using (var big = new MemoryStream(new byte[1000]))
        {
            await ExpectAsync(() => Fs.WriteStreamAsync(file, big, new FsWriteStreamOptions(MaxBytes: 500)).AsVoid(), EnvironmentErrorCode.TooLarge);
        }

        // 旧文件原样保留,目录里没有残留的临时文件
        Assert.Equal("old", await Fs.ReadTextAsync(file));
        Assert.Equal(["out"], (await Fs.ListAsync(temp.Env)).Select(e => e.Name));

        using var small = new MemoryStream("new content"u8.ToArray());
        Assert.Equal(11, await Fs.WriteStreamAsync(file, small, new FsWriteStreamOptions(MaxBytes: 500)));
        Assert.Equal("new content", await Fs.ReadTextAsync(file));

        using var third = new MemoryStream("x"u8.ToArray());
        await ExpectAsync(() => Fs.WriteStreamAsync(file, third, new FsWriteStreamOptions(MaxBytes: 500, Exclusive: true)).AsVoid(), EnvironmentErrorCode.AlreadyExists);
        Assert.Equal(["out"], (await Fs.ListAsync(temp.Env)).Select(e => e.Name));
    }

    [Fact]
    public async Task Paths_of_the_wrong_style_or_relative_paths_are_rejected()
    {
        var foreign = OperatingSystem.IsWindows() ? EnvPath.Posix("/tmp") : EnvPath.Windows(@"C:\tmp");
        await ExpectAsync(() => Fs.StatAsync(foreign).AsVoid(), EnvironmentErrorCode.InvalidPath);
        var relative = EnvPath.Parse("relative/file", OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix);
        await ExpectAsync(() => Fs.StatAsync(relative).AsVoid(), EnvironmentErrorCode.InvalidPath);
    }

    [Fact]
    public async Task Path_guard_rejects_a_symlink_that_points_out_of_the_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var project = new TempDir();
        using var outside = new TempDir();
        File.CreateSymbolicLink(project.File("escape"), outside.Path);

        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await Env.Paths.ResolveWithinAsync(project.Env, "escape/new.txt"));
        Assert.Equal(EnvironmentErrorCode.PathEscape, error.Code);
        // .. 绕出去也一样
        await Assert.ThrowsAsync<EnvironmentException>(async () => await Env.Paths.ResolveWithinAsync(project.Env, "../x"));
        var ok = await Env.Paths.ResolveWithinAsync(project.Env, "sub/new.txt");
        Assert.True(ok.IsWithin(await Fs.RealPathAsync(project.Env)));
        // 绝对路径指向根内同样放行,指向根外只标记 Outside
        Assert.True((await Env.Paths.ResolveAsync(project.Env, outside.Path)).Outside);
    }

    [Fact]
    public async Task Watcher_reports_created_files_in_batches()
    {
        using var temp = new TempDir();
        await using var watcher = Fs.Watch(temp.Env, new FsWatchOptions { Debounce = TimeSpan.FromMilliseconds(150), PollInterval = TimeSpan.FromMilliseconds(200) });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = new List<FsChange>();
        var reader = Task.Run(async () =>
        {
            await foreach (var batch in watcher.Batches.WithCancellation(cts.Token))
            {
                seen.AddRange(batch);
                if (seen.Any(c => c.Path.FileName == "new.txt"))
                {
                    return;
                }
            }
        });

        await Task.Delay(200);
        await Fs.WriteTextAsync(temp.EnvFile("new.txt"), "x");
        await reader;
        Assert.Contains(seen, c => c.Path.FileName == "new.txt" && c.Kind is FsChangeKind.Created or FsChangeKind.Modified);
    }

    [Fact]
    public async Task Disposing_the_watcher_ends_the_enumeration()
    {
        using var temp = new TempDir();
        var watcher = Fs.Watch(temp.Env, new FsWatchOptions());
        var drained = Task.Run(async () =>
        {
            await foreach (var _ in watcher.Batches)
            {
            }
        });
        await Task.Delay(100);
        await watcher.DisposeAsync();
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Change_batches_coalesce_per_path()
    {
        var batcher = new FsChangeBatcher();
        var p = EnvPath.Posix("/x/a");
        batcher.Add(new FsChange(FsChangeKind.Created, p));
        batcher.Add(new FsChange(FsChangeKind.Modified, p));
        var q = EnvPath.Posix("/x/b");
        batcher.Add(new FsChange(FsChangeKind.Created, q));
        batcher.Add(new FsChange(FsChangeKind.Deleted, q));
        var r = EnvPath.Posix("/x/c");
        batcher.Add(new FsChange(FsChangeKind.Modified, r));
        batcher.Add(new FsChange(FsChangeKind.Deleted, r));
        batcher.Complete();

        var batches = new List<IReadOnlyList<FsChange>>();
        await foreach (var batch in batcher.ReadBatchesAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None))
        {
            batches.Add(batch);
        }

        var all = Assert.Single(batches);
        Assert.Equal(2, all.Count);
        Assert.Equal(new FsChange(FsChangeKind.Created, p), all[0]);
        Assert.Equal(new FsChange(FsChangeKind.Deleted, r), all[1]);
    }
}

public class LocalFsTests : FsContractTests
{
    protected override Task<IExecutionEnvironment> CreateAsync() => Task.FromResult<IExecutionEnvironment>(new LocalEnvironment("dev"));
}

public class SshFsTests(SshdFixture fixture) : FsContractTests, IClassFixture<SshdFixture>
{
    protected override Task<IExecutionEnvironment> CreateAsync() => Task.FromResult<IExecutionEnvironment>(SshTestKit.NewEnv(fixture.Require()));
}

internal static class ValueTaskExtensions
{
    public static ValueTask AsVoid<T>(this ValueTask<T> task) => new(task.AsTask());
}
