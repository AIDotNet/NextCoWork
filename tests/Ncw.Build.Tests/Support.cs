using Ncw.Build.Core;

namespace Ncw.Build.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncw-build-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string text = "x")
    {
        var full = Sub(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public string WriteBytes(string relative, byte[] bytes)
    {
        var full = Sub(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Func<ProcessSpec, ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, []);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken ct = default)
    {
        Calls.Add(spec);
        var result = Handler(spec);
        foreach (var line in result.Lines)
        {
            onLine?.Invoke(line);
        }

        return Task.FromResult(result);
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public static BuildContext Context(TempDir dir, FakeRunner runner, MemoryBuildLog log, Rid host, IReadOnlyDictionary<string, string>? env = null)
    {
        var repo = new RepoLayout(dir.Sub("repo"), dir.Sub("repo", "artifacts"));
        Directory.CreateDirectory(repo.Root);
        File.WriteAllText(repo.BuildProps, "<Project><PropertyGroup><Version>9.8.7-test.1</Version></PropertyGroup></Project>");
        return new BuildContext(repo, runner, log, name => env is not null && env.TryGetValue(name, out var v) ? v : null, new FixedClock(Now), host);
    }

    public static readonly Rid MacArm = Rid.Parse("osx-arm64");

    public static readonly Rid WinX64 = Rid.Parse("win-x64");

    public static readonly Rid LinuxX64 = Rid.Parse("linux-x64");

    /// <summary>找到仓库根(测试输出在 tests/*/bin/... 下)。</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "NextCoWork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }
}
