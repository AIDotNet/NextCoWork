using Ncw.Client.Devices;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 终端显示面(UI 层的终端控件实现)。<see cref="Reset"/> / <see cref="Write"/> 可以从任意线程调用(实现自己切到 UI 线程并保持顺序);
/// <see cref="Input"/> 是用户敲的键(已经编码成 PTY 要的字节),<see cref="GridChanged"/> 是显示区能容纳的行列数变了。两个事件在 UI 线程上引发。
/// </summary>
public interface ITerminalSurface
{
    /// <summary>清空屏幕和回滚(下面要写入一份完整的缓冲)。</summary>
    void Reset();

    void Write(byte[] data);

    (int Columns, int Rows) Grid { get; }

    event Action<byte[]>? Input;

    event Action<int, int>? GridChanged;
}

/// <summary>
/// 把设备推来的终端输出交给显示面(<see cref="ITerminalSink"/> 的 UI 侧适配)。回调来自网络线程,按序、不并发。
/// 显示面还没挂上来(页签没打开 / 控件还没建好)时先攒着,挂上来时依次放出;攒得太多(超过 <see cref="PendingLimit"/>)就丢掉并要求重新订阅——
/// 订阅会从设备拿一份完整缓冲(<see cref="Resync"/>),屏幕不会因为丢了中间一段而乱掉。
/// </summary>
public sealed class TerminalOutputRelay : ITerminalSink
{
    public const int PendingLimit = 1024 * 1024;

    private readonly object _gate = new();
    private readonly List<(bool Reset, byte[] Data)> _pending = [];
    private long _pendingBytes;
    private ITerminalSurface? _surface;

    /// <summary>攒的输出超限被丢掉了:接收方应该重新订阅拿完整缓冲。</summary>
    public event Action? Resync;

    /// <summary>终端在设备上已经不存在。</summary>
    public event Action? Gone;

    /// <summary>终端退出(只记录;状态在 Store 里,界面从 Store 读)。</summary>
    public event Action<int?, int?, string>? Exited;

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _surface is not null;
            }
        }
    }

    public void Attach(ITerminalSurface surface)
    {
        var overflow = false;
        lock (_gate)
        {
            _surface = surface;
            foreach (var (reset, data) in _pending)
            {
                if (reset)
                {
                    surface.Reset();
                }

                surface.Write(data);
            }

            overflow = _pendingBytes < 0;
            _pending.Clear();
            _pendingBytes = 0;
        }

        if (overflow)
        {
            Resync?.Invoke();
        }
    }

    public void Detach(ITerminalSurface surface)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_surface, surface))
            {
                _surface = null;
            }
        }
    }

    public void OnSnapshot(byte[] data, bool truncated)
    {
        lock (_gate)
        {
            if (_surface is { } surface)
            {
                surface.Reset();
                surface.Write(data);
                return;
            }

            // 整份缓冲覆盖之前攒的一切
            _pending.Clear();
            _pending.Add((true, data));
            _pendingBytes = data.Length;
        }
    }

    public void OnData(byte[] data)
    {
        lock (_gate)
        {
            if (_surface is { } surface)
            {
                surface.Write(data);
                return;
            }

            if (_pendingBytes < 0)
            {
                return; // 已经溢出,等重新订阅
            }

            _pendingBytes += data.Length;
            if (_pendingBytes > PendingLimit)
            {
                _pending.Clear();
                _pendingBytes = -1;
                return;
            }

            _pending.Add((false, data));
        }
    }

    public void OnExit(int? exitCode, int? signal, string reason) => Exited?.Invoke(exitCode, signal, reason);

    public void OnGone() => Gone?.Invoke();
}

/// <summary>
/// 键盘输入的合并(简单版)。一次按键一个命令太碎:<b>没有写在途时立刻发出去</b>(第一个字符零延迟),写在途期间到达的输入攒起来,
/// 上一次写完成后一次发出(合成一个命令,单次不超过 <see cref="MaxChunk"/>,和设备的 64 KiB 上限对齐)。
/// 顺序严格保持:同一时刻最多一个写在途。写失败(断线 / 终端已退出)就丢掉攒着的输入并通知 <see cref="Failed"/>——不替用户重放几秒钟前敲的键。
/// </summary>
public sealed class TerminalInputPump(Func<byte[], Task<bool>> send, int maxChunk = 64 * 1024)
{
    private readonly object _gate = new();
    private readonly List<byte> _pending = [];
    private TaskCompletionSource _idle = Completed();
    private bool _running;

    public int MaxChunk { get; } = maxChunk;

    /// <summary>已经发出的写命令次数(测试 / 诊断用)。</summary>
    public int Sends { get; private set; }

    public event Action? Failed;

    /// <summary>没有写在途、也没有攒着的输入时完成。</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var b in data)
            {
                _pending.Add(b);
            }

            if (_running)
            {
                return;
            }

            _running = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = RunAsync();
    }

    /// <summary>丢掉攒着的输入(终端不再接受输入时)。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            byte[] chunk;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    _idle.TrySetResult();
                    return;
                }

                var take = Math.Min(MaxChunk, _pending.Count);
                chunk = [.. _pending.Take(take)];
                _pending.RemoveRange(0, take);
                Sends++;
            }

            bool ok;
            try
            {
                ok = await send(chunk).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ok = false;
            }

            if (!ok)
            {
                lock (_gate)
                {
                    _pending.Clear();
                    _running = false;
                    _idle.TrySetResult();
                }

                Failed?.Invoke();
                return;
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}

/// <summary>
/// 窗口大小同步:只关心「最新的行列数」。同一时刻最多一个 resize 在途,在途期间的新请求只留最后一个;和上次已发出的相同就不发。
/// </summary>
public sealed class TerminalResizePump(Func<int, int, Task<bool>> send)
{
    private readonly object _gate = new();
    private (int Columns, int Rows)? _latest;
    private (int Columns, int Rows)? _sent;
    private bool _running;
    private TaskCompletionSource _idle = Completed();

    public int Sends { get; private set; }

    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    /// <summary>已经确认发给设备的行列数(新建终端时用的大小也算)。</summary>
    public void Known(int columns, int rows)
    {
        lock (_gate)
        {
            _sent = (columns, rows);
        }
    }

    public void Request(int columns, int rows)
    {
        if (columns < 1 || rows < 1)
        {
            return;
        }

        lock (_gate)
        {
            _latest = (columns, rows);
            if (_running || _sent == _latest)
            {
                return;
            }

            _running = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            (int Columns, int Rows) next;
            lock (_gate)
            {
                if (_latest is not { } latest || _sent == latest)
                {
                    _running = false;
                    _idle.TrySetResult();
                    return;
                }

                next = latest;
                Sends++;
            }

            bool ok;
            try
            {
                ok = await send(next.Columns, next.Rows).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ok = false;
            }

            lock (_gate)
            {
                if (ok)
                {
                    _sent = next;
                }
                else
                {
                    // 失败(断线):不重试,下一次显示区大小变化或重连后再请求
                    _latest = _sent;
                }
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}
