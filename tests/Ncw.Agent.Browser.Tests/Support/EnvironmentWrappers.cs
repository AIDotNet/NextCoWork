using Ncw.Agent.Environments;

namespace Ncw.Agent.Browser.Tests;

/// <summary>「声称自己是 SSH」的包装环境:其余行为转给本机环境(只测「浏览器不在远程环境里起」的拒绝,不碰真 SSH)。</summary>
public sealed class SshLikeEnvironment(IExecutionEnvironment inner) : IExecutionEnvironment
{
    public int OpenProcessCalls { get; private set; }

    public EnvironmentId Id => new(inner.Id.DeviceId, "dev-box");

    public EnvironmentKind Kind => EnvironmentKind.Ssh;

    public string DisplayName => "dev-box";

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => inner.Paths;

    public IEnvironmentFs Fs => inner.Fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default)
    {
        OpenProcessCalls++;
        return inner.OpenProcessAsync(spec, ct);
    }

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>文件系统:只有 <paramref name="exists"/> 里的路径存在(都是文件)。用来测「找不到浏览器」。</summary>
public sealed class ScriptedFsEnvironment(IExecutionEnvironment inner, EnvironmentFacts facts, Func<string, bool> exists) : IExecutionEnvironment
{
    private readonly ScriptedFs _fs = new(exists);

    public EnvironmentId Id => inner.Id;

    public EnvironmentKind Kind => EnvironmentKind.Local;

    public string DisplayName => inner.DisplayName;

    public int Generation => 0;

    public EnvironmentState State => EnvironmentState.Ready;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public EnvironmentFacts Facts => facts;

    public PathGuard Paths => inner.Paths;

    public IEnvironmentFs Fs => _fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => Task.FromResult(facts);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public List<string> Created => _fs.Created;

    private sealed class ScriptedFs(Func<string, bool> exists) : IEnvironmentFs
    {
        public List<string> Created { get; } = [];

        public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) =>
            exists(path.Value)
                ? ValueTask.FromResult(new FsEntryInfo(FsEntryKind.File, 1, DateTimeOffset.UnixEpoch, 0x1ED))
                : throw new EnvironmentException(EnvironmentErrorCode.NotFound, path.Value);

        public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default)
        {
            Created.Add(path.Value);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) => StatAsync(path, ct);

        public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => ValueTask.FromResult(path);

        public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => throw new NotSupportedException();

        public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => throw new NotSupportedException();
    }
}
