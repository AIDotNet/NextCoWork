namespace Ncw.Agent.Terminal;

/// <summary>
/// 一个终端的输出日志:按「批」存放的有界环形缓冲。同时服务两件事:
/// <list type="bullet">
/// <item><c>getBuffer</c> / attach 快照:最近 <c>capacity</c> 字节(超出时丢最早的,精确到字节,并对齐到 UTF-8 字符边界);</item>
/// <item>游标续传:游标之后的批如果都还完整地在日志里,就按原来的序号重放,控制端不必整屏重置。</item>
/// </list>
/// 不线程安全:调用方持有会话的锁。
/// </summary>
internal sealed class OutputLog(int capacity)
{
    private readonly List<Chunk> _chunks = [];
    private long _total;
    private TerminalExit? _exit;

    public long Seq { get; private set; }

    public bool Truncated { get; private set; }

    public int Length => (int)_total;

    public TerminalExit? Exit => _exit;

    public long Append(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            return Seq;
        }

        var seq = ++Seq;
        _chunks.Add(new Chunk(seq, data.ToArray(), Partial: false));
        _total += data.Length;
        Trim();
        return seq;
    }

    /// <summary>同一个 id 上重新开了新的一段会话:上一段的退出事件不再属于当前日志(序号接着走)。</summary>
    public void ClearExit() => _exit = null;

    public TerminalExit AppendExit(int? exitCode, int? signal, Environments.ProcessExitReason reason)
    {
        _exit = new TerminalExit(++Seq, exitCode, signal, reason);
        return _exit;
    }

    public byte[] Snapshot()
    {
        var result = new byte[_total];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            chunk.Data.CopyTo(result, offset);
            offset += chunk.Data.Length;
        }

        return result;
    }

    /// <summary>
    /// 游标 <paramref name="cursor"/> 之后的事件。游标之后的批没有被丢掉、也没有被裁成半截时返回 true(可能是空列表:游标已是最新);
    /// 否则返回 false,调用方改发整个快照。
    /// </summary>
    public bool TryReplayAfter(long cursor, out List<TerminalEvent> events)
    {
        events = [];
        if (cursor < 0 || cursor > Seq)
        {
            return false;
        }

        var expected = cursor + 1;
        foreach (var chunk in _chunks)
        {
            if (chunk.Seq <= cursor)
            {
                continue;
            }

            if (chunk.Seq != expected || chunk.Partial)
            {
                // 要补发的批里有一条已经被丢掉了或被裁成半截
                return false;
            }

            events.Add(new TerminalOutput(chunk.Seq, chunk.Data));
            expected++;
        }

        if (_exit is { } exit && exit.Seq > cursor)
        {
            if (exit.Seq != expected)
            {
                return false;
            }

            events.Add(exit);
            expected++;
        }

        return expected == Seq + 1;
    }

    private void Trim()
    {
        while (_total > capacity)
        {
            var excess = (int)(_total - capacity);
            var first = _chunks[0];
            Truncated = true;
            if (first.Data.Length <= excess)
            {
                _chunks.RemoveAt(0);
                _total -= first.Data.Length;
                continue;
            }

            // 只裁掉第一批的前半截,并对齐到字符边界:不从一个多字节字符的中间开始
            var start = excess;
            while (start < first.Data.Length && start - excess < 3 && (first.Data[start] & 0xC0) == 0x80)
            {
                start++;
            }

            _chunks[0] = new Chunk(first.Seq, first.Data[start..], Partial: true);
            _total -= start;
            break;
        }
    }

    private readonly record struct Chunk(long Seq, byte[] Data, bool Partial);
}
