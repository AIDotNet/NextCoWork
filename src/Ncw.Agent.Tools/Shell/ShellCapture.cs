using System.Text;
using System.Threading.Channels;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

/// <summary>完整输出落盘的目标:字节数超过 <see cref="Threshold"/> 才创建文件(绝大多数命令永远不会碰文件系统)。</summary>
internal sealed record SpillTarget(IEnvironmentFs Fs, EnvPath Path, long Threshold);

internal sealed record ShellTake(string Stdout, string Stderr, bool Dropped);

/// <summary>
/// 一个进程的输出收集器,前台命令和后台 shell 共用(前台转后台时整个收集器连同进程一起交给注册表,所以已经收集的输出一个字节都不丢):
/// <list type="bullet">
/// <item>每路一个「保留尾部」的内存缓冲(前台命令的最终结果);</item>
/// <item>每路一个「读走即清空」的文本缓冲(后台 shell 的 BashOutput;ANSI 在入口剥掉,因为 <c>filter</c> 是按行匹配的,行首挂着一段颜色序列会让 <c>^error</c> 一条都匹配不上);</item>
/// <item>界面用的输出尾巴(两路按到达顺序交错;不消费上一条的缓冲,所以界面看一眼不会吃掉模型还没读到的输出);</item>
/// <item>可选:超过阈值后把完整的原始输出(两路按到达顺序交错)写进环境里的临时文件。</item>
/// </list>
/// 超出预算后仍然持续读管道(丢最早的,不是不再接收):不读的话管道写满,子进程会卡在 write 上永不退出,看起来一点都不像背压。
/// </summary>
internal sealed class ShellCapture
{
    private const int TailBytes = 256 * 1024;

    private readonly IEnvProcess _process;
    private SpillTarget? _spill;
    private readonly object _gate = new();
    private readonly IOutputBuffer _stdoutTail = OutputPolicies.Tail(TailBytes).CreateBuffer();
    private readonly IOutputBuffer _stderrTail = OutputPolicies.Tail(TailBytes).CreateBuffer();
    private readonly Decoder _stdoutDecoder = Encoding.UTF8.GetDecoder();
    private readonly Decoder _stderrDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _stdoutUnread = new();
    private readonly StringBuilder _stderrUnread = new();
    private readonly StringBuilder _peek = new();
    private readonly MemoryStream? _early;
    private bool _stdoutDropped;
    private bool _stderrDropped;
    private long _total;
    private Channel<byte[]>? _spillChannel;
    private Task? _spillWriter;
    private volatile string? _spillPath;
    private Task _pumps = Task.CompletedTask;

    public ShellCapture(IEnvProcess process, SpillTarget? spill = null)
    {
        _process = process;
        _spill = spill;
        _early = spill is null ? null : new MemoryStream();
    }

    /// <summary>进程实际写出的总字节数。</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>完整输出所在的文件(只有超过阈值才有)。</summary>
    public string? SpillPath => _spillPath;

    public void Start() =>
        _pumps = Task.WhenAll(PumpAsync(_process.Stdout, OutputStreamKind.Stdout), PumpAsync(_process.Stderr, OutputStreamKind.Stderr));

    /// <summary>进程退出后继续等管道读完的最长时间。后台子进程还攥着管道时不能让调用一直挂着。</summary>
    public Task DrainAsync(TimeSpan grace) => Task.WhenAny(_pumps, Task.Delay(grace));

    public (OutputSnapshot Stdout, OutputSnapshot Stderr) SnapshotTails()
    {
        lock (_gate)
        {
            return (_stdoutTail.Snapshot(), _stderrTail.Snapshot());
        }
    }

    /// <summary>读走并清空(后台 shell 的回读)。<paramref name="filter"/> 按行筛,不匹配的丢掉,不留到下一次。</summary>
    public ShellTake Take(Func<string, bool>? filter)
    {
        lock (_gate)
        {
            var dropped = _stdoutDropped || _stderrDropped;
            _stdoutDropped = false;
            _stderrDropped = false;
            var stdout = Drain(_stdoutUnread, filter);
            var stderr = Drain(_stderrUnread, filter);
            return new ShellTake(stdout, stderr, dropped);
        }
    }

    /// <summary>界面看一眼输出尾巴。不消费。</summary>
    public string Peek()
    {
        lock (_gate)
        {
            return _peek.ToString();
        }
    }

    /// <summary>
    /// 停止落盘并等已经排队的写完(命令结束了,或前台命令被转去后台:之后的输出由后台缓冲接手,不再写文件)。幂等。
    /// 返回已经写下的文件路径(没有就是 null)。
    /// </summary>
    public async Task<string?> StopSpillAsync()
    {
        Channel<byte[]>? channel;
        Task? writer;
        lock (_gate)
        {
            _spill = null;
            channel = _spillChannel;
            writer = _spillWriter;
        }

        channel?.Writer.TryComplete();
        if (writer is not null)
        {
            await writer.ConfigureAwait(false);
        }

        return _spillPath;
    }

    private static string Drain(StringBuilder buffer, Func<string, bool>? filter)
    {
        var text = buffer.ToString();
        buffer.Clear();
        if (filter is null || text.Length == 0)
        {
            return text;
        }

        return string.Join('\n', text.Split('\n').Where(filter));
    }

    private async Task PumpAsync(Stream stream, OutputStreamKind kind)
    {
        var chunk = new byte[16 * 1024];
        try
        {
            for (;;)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await AppendAsync(kind, chunk.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or EnvironmentException)
        {
            // 进程被杀或连接断开时流会以异常结束,不是调用方要处理的错误
        }
    }

    internal async ValueTask AppendAsync(OutputStreamKind kind, ReadOnlyMemory<byte> data)
    {
        byte[]? toSpill = null;
        Channel<byte[]>? channel = null;
        lock (_gate)
        {
            var span = data.Span;
            _total += span.Length;
            var stdout = kind == OutputStreamKind.Stdout;
            (stdout ? _stdoutTail : _stderrTail).Append(span);

            var chars = new char[Encoding.UTF8.GetMaxCharCount(span.Length)];
            var count = (stdout ? _stdoutDecoder : _stderrDecoder).GetChars(span, chars, flush: false);
            var text = Text.StripAnsi(new string(chars, 0, count));
            var unread = stdout ? _stdoutUnread : _stderrUnread;
            unread.Append(text);
            if (unread.Length > ShellLimits.MaxBufferChars)
            {
                unread.Remove(0, unread.Length - ShellLimits.MaxBufferChars);
                if (stdout)
                {
                    _stdoutDropped = true;
                }
                else
                {
                    _stderrDropped = true;
                }
            }

            _peek.Append(text);
            if (_peek.Length > ShellLimits.PeekChars)
            {
                _peek.Remove(0, _peek.Length - ShellLimits.PeekChars);
            }

            if (_spill is { } spill)
            {
                if (_spillChannel is null)
                {
                    _early!.Write(span);
                    if (_total > spill.Threshold)
                    {
                        _spillChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
                        _spillChannel.Writer.TryWrite(_early.ToArray());
                        _spillWriter = Task.Run(() => RunSpillWriterAsync(spill, _spillChannel.Reader));
                    }
                }
                else
                {
                    toSpill = span.ToArray();
                    channel = _spillChannel;
                }
            }
        }

        if (toSpill is not null)
        {
            try
            {
                await channel!.Writer.WriteAsync(toSpill).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // 已经停止落盘
            }
        }
    }

    private async Task RunSpillWriterAsync(SpillTarget spill, ChannelReader<byte[]> reader)
    {
        Stream? file = null;
        try
        {
            file = await spill.Fs.OpenWriteAsync(spill.Path, new FsWriteOptions(Exclusive: true, UnixMode: 0x180, CreateParents: true)).ConfigureAwait(false);
            _spillPath = spill.Path.Value;
            await foreach (var bytes in reader.ReadAllAsync().ConfigureAwait(false))
            {
                await file.WriteAsync(bytes).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is EnvironmentException or IOException or ObjectDisposedException)
        {
            // 落盘失败不能连累命令本身:没有文件就没有「完整输出」的路径,结果里只带尾部
            _spillPath = null;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out _))
                {
                }
            }
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
