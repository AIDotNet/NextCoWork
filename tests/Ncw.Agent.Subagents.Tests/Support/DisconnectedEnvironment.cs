using Ncw.Agent.Environments;

namespace Ncw.Agent.Subagents.Tests.Support;

/// <summary>文件系统一律抛「连接断了」的执行环境(测「环境连不上 ≠ 没有子代理」)。</summary>
public sealed class DisconnectedEnvironment(IExecutionEnvironment inner) : IExecutionEnvironment
{
    private sealed class DeadFs(IEnvironmentFs inner) : IEnvironmentFs
    {
        private static EnvironmentException Down() => new(EnvironmentErrorCode.Disconnected);

        public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => throw Down();

        public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw Down();

        public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => throw Down();

        public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => throw Down();

        public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw Down();

        public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => throw Down();

        public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => inner.Watch(directory, options);
    }

    public EnvironmentId Id => inner.Id;

    public EnvironmentKind Kind => inner.Kind;

    public string DisplayName => inner.DisplayName;

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add => inner.StatusChanged += value;
        remove => inner.StatusChanged -= value;
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => new(Fs, inner.Facts.PathStyle);

    public IEnvironmentFs Fs { get; } = new DeadFs(inner.Fs);

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
