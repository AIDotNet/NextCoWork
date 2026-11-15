using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>手动推进的时钟:索引 TTL、遍历预算的测试不睡真的时间。</summary>
public sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));
}

/// <summary>临时项目 + 文件服务 + 项目句柄。</summary>
public sealed class FilesRig : IDisposable
{
    public FilesRig(FileServiceOptions? options = null)
    {
        Project = new TempProject();
        Time = new ManualTime();
        Service = new FileService(options ?? new FileServiceOptions { Time = Time });
        Fp = new FileProject("p1", Project.Env, Project.RootPath);
    }

    public TempProject Project { get; }

    public ManualTime Time { get; }

    public FileService Service { get; }

    public FileProject Fp { get; }

    public string Root => Project.Root;

    public string Outside => Project.Outside;

    public string Abs(string relative) => Project.Abs(relative);

    public string Write(string relative, string content) => Project.Write(relative, content);

    public void WriteBytes(string relative, byte[] bytes)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>建一个稀疏的大文件(只设长度)。</summary>
    public void WriteSized(string relative, long size)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create);
        stream.SetLength(size);
    }

    public void Dispose() => Project.Dispose();

    /// <summary>期望文件服务以某个错误码失败。</summary>
    public static async Task<FilesException> Fails<T>(Func<ValueTask<T>> work, FilesErrorCode code)
    {
        var e = await Assert.ThrowsAsync<FilesException>(async () => await work());
        Assert.Equal(code, e.Code);
        return e;
    }

    public static async Task<FilesException> Fails(Func<ValueTask> work, FilesErrorCode code)
    {
        var e = await Assert.ThrowsAsync<FilesException>(async () => await work());
        Assert.Equal(code, e.Code);
        return e;
    }

    public static bool SymlinksUnavailable => OperatingSystem.IsWindows();
}
