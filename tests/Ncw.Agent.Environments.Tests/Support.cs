using Ncw.Agent.Environments;

namespace Ncw.Agent.Environments.Tests;

/// <summary>测试用的临时目录,释放时删除。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncw-env-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public EnvPath Env => EnvPath.Parse(Path, OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public EnvPath EnvFile(string name) => Env.Join(name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 30_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(25);
        }
    }

    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>只实现 realpath 的假文件系统,给纯路径逻辑的测试用。</summary>
internal sealed class FakeFs(Func<EnvPath, EnvPath> realPath) : IEnvironmentFs
{
    public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => ValueTask.FromResult(realPath(path));

    public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => throw new NotSupportedException();

    public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => throw new NotSupportedException();
}
