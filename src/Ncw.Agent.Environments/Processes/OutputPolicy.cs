using System.Text;

namespace Ncw.Agent.Environments;

/// <summary>
/// 输出截断策略的结果。<see cref="Head"/> 是开头保留的部分,<see cref="Tail"/> 是结尾保留的部分,
/// 中间被丢掉了 <see cref="OmittedBytes"/> 字节(没截断时 <see cref="Tail"/> 为空、<see cref="Head"/> 是全部)。
/// </summary>
public sealed class OutputSnapshot
{
    public static OutputSnapshot Empty { get; } = new(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, 0);

    public OutputSnapshot(ReadOnlyMemory<byte> head, ReadOnlyMemory<byte> tail, long totalBytes)
    {
        Head = head;
        Tail = tail;
        TotalBytes = totalBytes;
    }

    public ReadOnlyMemory<byte> Head { get; }

    public ReadOnlyMemory<byte> Tail { get; }

    /// <summary>进程实际写出的总字节数。</summary>
    public long TotalBytes { get; }

    public long OmittedBytes => TotalBytes - Head.Length - Tail.Length;

    public bool Truncated => OmittedBytes > 0;

    /// <summary>
    /// 解码成文本(UTF-8)。被截断时在两段之间插入 <paramref name="omittedMarker"/>(默认空串,让工具层自己决定怎么提示)。
    /// 只有尾部保留时,开头可能切在多字节字符中间,这里丢掉开头残缺的字节,避免出现乱码字符。
    /// </summary>
    public string ToText(string omittedMarker = "")
    {
        if (!Truncated)
        {
            // 没丢东西时头和尾是连续的,先拼起来再解码(多字节字符可能正好跨在两段的边界上)
            var all = new byte[Head.Length + Tail.Length];
            Head.CopyTo(all);
            Tail.CopyTo(all.AsMemory(Head.Length));
            return Decode(all, trimLeadingContinuation: false);
        }

        return Decode(Head.Span, trimLeadingContinuation: false) + omittedMarker + Decode(Tail.Span, trimLeadingContinuation: true);
    }

    public override string ToString() => ToText();

    private static string Decode(ReadOnlySpan<byte> bytes, bool trimLeadingContinuation)
    {
        if (trimLeadingContinuation)
        {
            var skip = 0;
            while (skip < bytes.Length && skip < 3 && (bytes[skip] & 0xC0) == 0x80)
            {
                skip++;
            }

            bytes = bytes[skip..];
        }

        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>一个流的输出缓冲。非线程安全:一个缓冲只由一个读取循环写入。</summary>
public interface IOutputBuffer
{
    void Append(ReadOnlySpan<byte> data);

    OutputSnapshot Snapshot();
}

/// <summary>输出截断策略。每次执行为 stdout、stderr 各创建一个缓冲。</summary>
public interface IOutputPolicy
{
    IOutputBuffer CreateBuffer();
}

/// <summary>内置策略。要把完整输出落到临时文件之类的,在 <see cref="RunOptions.OnOutput"/> 里自己接,策略只管内存里留多少。</summary>
public static class OutputPolicies
{
    /// <summary>默认:保留尾部 512 KiB(命令的结尾通常最有用:错误信息、最后的进度)。</summary>
    public static IOutputPolicy Default { get; } = Tail(512 * 1024);

    /// <summary>不截断。只给确定输出有界的命令用。</summary>
    public static IOutputPolicy Unbounded { get; } = new HeadTailPolicy(long.MaxValue, 0);

    /// <summary>只保留开头 <paramref name="maxBytes"/> 字节。超出后仍然继续消费管道(不会让子进程卡在写上)。</summary>
    public static IOutputPolicy Head(long maxBytes) => new HeadTailPolicy(maxBytes, 0);

    /// <summary>只保留结尾 <paramref name="maxBytes"/> 字节(Bash 工具的策略)。</summary>
    public static IOutputPolicy Tail(long maxBytes) => new HeadTailPolicy(0, maxBytes);

    /// <summary>开头和结尾各留一段,中间丢掉。</summary>
    public static IOutputPolicy HeadAndTail(long headBytes, long tailBytes) => new HeadTailPolicy(headBytes, tailBytes);

    private sealed class HeadTailPolicy(long head, long tail) : IOutputPolicy
    {
        public IOutputBuffer CreateBuffer() => new HeadTailBuffer(head, tail);
    }

    private sealed class HeadTailBuffer : IOutputBuffer
    {
        private readonly long _headLimit;
        private readonly long _tailLimit;
        private readonly MemoryStream _head = new();

        // 尾部用块队列:内存上限约为 tailLimit + 一个读块;整块丢弃,不做逐字节拷贝
        private readonly Queue<byte[]> _tailChunks = new();
        private long _tailBytes;
        private long _total;

        public HeadTailBuffer(long headLimit, long tailLimit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(headLimit);
            ArgumentOutOfRangeException.ThrowIfNegative(tailLimit);
            _headLimit = headLimit;
            _tailLimit = tailLimit;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            _total += data.Length;
            if (_head.Length < _headLimit)
            {
                var take = (int)Math.Min(data.Length, _headLimit - _head.Length);
                _head.Write(data[..take]);
                data = data[take..];
            }

            if (data.Length == 0 || _tailLimit == 0)
            {
                return;
            }

            if (data.Length > _tailLimit)
            {
                data = data[^(int)_tailLimit..];
            }

            _tailChunks.Enqueue(data.ToArray());
            _tailBytes += data.Length;
            while (_tailChunks.Count > 1 && _tailBytes - _tailChunks.Peek().Length >= _tailLimit)
            {
                _tailBytes -= _tailChunks.Dequeue().Length;
            }
        }

        public OutputSnapshot Snapshot()
        {
            var all = new byte[_tailBytes];
            var offset = 0;
            foreach (var chunk in _tailChunks)
            {
                chunk.CopyTo(all, offset);
                offset += chunk.Length;
            }

            var tail = _tailBytes > _tailLimit ? all.AsMemory((int)(_tailBytes - _tailLimit)) : all;
            return new OutputSnapshot(_head.ToArray(), tail, _total);
        }
    }
}
