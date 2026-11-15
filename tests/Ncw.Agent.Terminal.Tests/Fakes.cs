using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>手动拨动的时钟:<see cref="Advance"/> 之前定时器和 <c>Task.Delay</c> 都不会触发(合批、过期、回收都是确定性的)。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        for (;;)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value > _now ? next.Due.Value : _now;
                next.Fire();
            }

            next.Callback(next.State);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        /// <summary>调用方持有 owner 的锁。</summary>
        public void Fire() => Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>假终端:测试推送输出、结束、读取收到的输入。<see cref="PushAsync"/> 返回时,管理器的输出泵已经读完并处理了这批数据。</summary>
internal sealed class FakeTerminal : ITerminal
{
    private readonly FakeStream _stream = new();
    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _kills;
    private int _disposals;

    public Stream Stream => _stream;

    public Task<ProcessExit> Exited => _exited.Task;

    public List<byte[]> Written => _stream.Written;

    public string WrittenText => Encoding.UTF8.GetString(_stream.Written.SelectMany(static w => w).ToArray());

    public (int Columns, int Rows) Size { get; private set; }

    public int Kills => Volatile.Read(ref _kills);

    public int Disposals => Volatile.Read(ref _disposals);

    public Task PushAsync(string text) => _stream.PushAsync(Encoding.UTF8.GetBytes(text));

    public Task PushAsync(byte[] data) => _stream.PushAsync(data);

    /// <summary>进程退出:输出流到 EOF,<see cref="Exited"/> 完成。重复调用只有第一次有效(第二个退出事件必须被丢弃,不由假终端保证)。</summary>
    public void Finish(int code, ProcessExitReason reason = ProcessExitReason.Exited)
    {
        _stream.Complete();
        _exited.TrySetResult(new ProcessExit(code, null, reason));
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        Size = (columns, rows);
        return ValueTask.CompletedTask;
    }

    public ValueTask KillAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _kills);
        Finish(137, ProcessExitReason.Killed);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        Finish(137, ProcessExitReason.Killed);
        return ValueTask.CompletedTask;
    }

    private sealed class FakeStream : Stream
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _queue = new();
        private readonly List<TaskCompletionSource> _idle = [];
        private TaskCompletionSource? _readWaiter;
        private bool _complete;
        private byte[] _rest = [];

        public List<byte[]> Written { get; } = [];

        public Task PushAsync(byte[] data)
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _queue.Enqueue(data);
                _idle.Add(idle);
                _readWaiter?.TrySetResult();
            }

            return idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Complete()
        {
            lock (_gate)
            {
                _complete = true;
                _readWaiter?.TrySetResult();
                foreach (var idle in _idle)
                {
                    idle.TrySetResult();
                }

                _idle.Clear();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (;;)
            {
                TaskCompletionSource waiter;
                lock (_gate)
                {
                    if (_rest.Length == 0 && _queue.Count > 0)
                    {
                        _rest = _queue.Dequeue();
                    }

                    if (_rest.Length > 0)
                    {
                        var count = Math.Min(buffer.Length, _rest.Length);
                        _rest.AsMemory(0, count).CopyTo(buffer);
                        _rest = _rest[count..];
                        return count;
                    }

                    if (_complete)
                    {
                        return 0;
                    }

                    // 队列空了,泵在等下一批:此刻之前推送的数据都已处理完
                    foreach (var idle in _idle)
                    {
                        idle.TrySetResult();
                    }

                    _idle.Clear();
                    waiter = _readWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Written.Add(buffer.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// 包着真 <see cref="LocalEnvironment"/>(文件系统、路径守卫是真的,所以目录校验是真的)但终端由测试控制的环境。
/// 可以假装成 SSH 环境(<see cref="Kind"/>),也可以让它「断线」(<see cref="State"/>)、让打开终端阻塞(<see cref="OpenGate"/>)。
/// </summary>
internal sealed class TestEnvironment : IExecutionEnvironment
{
    private readonly LocalEnvironment _inner = new("dev", new LocalEnvironmentOptions());

    public TestEnvironment(string name = "local", EnvironmentKind kind = EnvironmentKind.Local, string shell = "/bin/sh")
    {
        Id = new EnvironmentId("dev", name);
        Kind = kind;
        DisplayName = name;
        Facts = _inner.Facts with { Shell = shell };
    }

    public List<FakeTerminal> Terminals { get; } = [];

    public List<TerminalSpec> Specs { get; } = [];

    /// <summary>非空时,<see cref="OpenTerminalAsync"/> 等它完成才返回(模拟慢连接);测试在它完成后才拿到终端。</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    public Exception? OpenFailure { get; set; }

    public int OpenCount => Specs.Count;

    public EnvironmentId Id { get; }

    public EnvironmentKind Kind { get; }

    public string DisplayName { get; }

    public int Generation { get; set; }

    public EnvironmentState State { get; set; } = EnvironmentState.Ready;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public EnvironmentFacts Facts { get; }

    public PathGuard Paths => _inner.Paths;

    public IEnvironmentFs Fs => _inner.Fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => Task.FromResult(Facts);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => _inner.OpenProcessAsync(spec, ct);

    public async Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default)
    {
        lock (Specs)
        {
            Specs.Add(spec);
        }

        if (OpenGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        var terminal = new FakeTerminal();
        lock (Terminals)
        {
            Terminals.Add(terminal);
        }

        return terminal;
    }

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => _inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => _inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
