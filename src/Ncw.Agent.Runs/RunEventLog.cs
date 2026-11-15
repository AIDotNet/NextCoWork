using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>带序号的事件。<c>seq</c> 每个 Run 一套、严格递增;日志裁剪后序号有空洞,客户端不做空洞检查,直接以快照的 <c>seq</c> 为准(2.x ★)。</summary>
public readonly record struct SequencedEvent(long Seq, AgentEvent Event);

/// <summary>续传游标的结局(09 §8.2):给了游标且还在日志里 = <see cref="Continued"/>;没给 = <see cref="Fresh"/>;游标早已被日志上限挤出去 = <see cref="Lost"/>(客户端以快照重建)。</summary>
public enum CursorStatus
{
    Fresh,
    Continued,
    Lost,
}

/// <summary>
/// 一个 Run 的只追加事件日志(05 §2、02 §3)。<b>不线程安全</b>:由 <see cref="RunHandle"/> 在它的锁里访问。
/// <list type="bullet">
/// <item>裁剪是<b>无损</b>的:<c>message_end</c> 之后清掉该消息的增量(完整消息已在里面)、<c>message_discard</c> 同理、<c>tool_end</c> 之后清掉该调用的进度(子代理排队的排位除外,见 <see cref="TrimProgressOf"/>)。
/// 结构性事件(消息起止、工具起止、交互、回合、run_*)永不裁。</item>
/// <item>硬上限(条数 / 字节)是<b>有损</b>的:从头砍,最新一条永远留着,并标记 <see cref="LogTrimmed"/> 和 <see cref="TrimmedThrough"/>(游标落在它之前就是 <see cref="CursorStatus.Lost"/>)。</item>
/// <item>重放用<b>过滤</b>(<c>seq &gt; since</c>),不用下标切片:序号有空洞时下标和序号不再一一对应。</item>
/// </list>
/// </summary>
internal sealed class RunEventLog(int maxEntries, long maxBytes)
{
    private sealed record Entry(long Seq, AgentEvent Event, long Bytes);

    private readonly List<Entry> _entries = [];
    private long _bytes;

    /// <summary>头部被条数 / 字节上限截掉过。</summary>
    public bool LogTrimmed { get; private set; }

    /// <summary>被上限截掉的最大序号(没截过为 0)。</summary>
    public long TrimmedThrough { get; private set; }

    public int Count => _entries.Count;

    public long RetainedBytes => _bytes;

    public void Append(long seq, AgentEvent ev)
    {
        var bytes = EventSizes.Estimate(ev);
        _entries.Add(new Entry(seq, ev, bytes));
        _bytes += bytes;

        switch (ev)
        {
            case MessageEndEvent end:
                TrimDeltasOf(end.MessageId);
                break;
            case MessageDiscardEvent discard:
                TrimDeltasOf(discard.MessageId);
                break;
            case ToolEndEvent toolEnd:
                TrimProgressOf(toolEnd.CallId);
                break;
        }

        if (_entries.Count > maxEntries)
        {
            DropHead(_entries.Count - maxEntries);
        }

        if (_bytes > maxBytes)
        {
            // 从头数到够为止;最新那一条永远留着(它正要推给订阅者,快照里也得有它)。
            var excess = _bytes - maxBytes;
            var n = 0;
            while (n < _entries.Count - 1 && excess > 0)
            {
                excess -= _entries[n++].Bytes;
            }

            DropHead(n);
        }
    }

    public IReadOnlyList<SequencedEvent> Since(long sinceSeq)
    {
        var result = new List<SequencedEvent>();
        foreach (var entry in _entries)
        {
            if (entry.Seq > sinceSeq)
            {
                result.Add(new SequencedEvent(entry.Seq, entry.Event));
            }
        }

        return result;
    }

    /// <summary>
    /// ★ <c>message_end</c> 一到,它之前这条消息的增量就是冗余的(完整消息已在 <c>message_end</c> 里,R7)。保留 <c>message_start</c> 和提交本身。
    /// 倒着扫到这条消息的 <c>message_start</c> 为止(之后更早的事件不可能有它的增量)。
    /// </summary>
    private void TrimDeltasOf(MessageId id)
    {
        for (var i = _entries.Count - 2; i >= 0; i--)
        {
            switch (_entries[i].Event)
            {
                case MessageDeltaEvent delta when delta.MessageId == id:
                    RemoveAt(i);
                    break;
                case MessageStartEvent start when start.MessageId == id:
                    return;
            }
        }
    }

    /// <summary>
    /// <c>tool_end</c> 之后,同一次调用的 <c>tool_progress</c> 作废(客户端在 <c>tool_end</c> 处把它们清掉,以 output 为准)——<b>排位除外</b>:
    /// 后台 Task 调用立刻返回(<c>tool_end</c>),而它的派发可能还在排队,客户端(<c>ConversationReducer</c>)特意把排位留到 <c>subagent_start</c>(或父 Run 结束)。
    /// 以前这里连排位一起裁掉:订阅者的合批窗口还没把 <c>tool_progress</c> 推出去 <c>tool_end</c> 就到了(CPU 吃紧时稳定出现),
    /// 控制端永远看不到「排队中(前面还有 N 个)」——重连按游标续传、中途打开的快照同样丢。排位是按变化才发的、每次几十字节,留着没有代价。
    /// </summary>
    private void TrimProgressOf(string callId)
    {
        for (var i = _entries.Count - 2; i >= 0; i--)
        {
            switch (_entries[i].Event)
            {
                case ToolProgressEvent progress when progress.CallId == callId && !IsQueuePosition(progress.Progress):
                    RemoveAt(i);
                    break;
                case ToolStartEvent start when start.CallId == callId:
                    return;
            }
        }
    }

    /// <summary>子代理派发排队的结构化排位(<c>Details = {"kind":"queued","ahead":N}</c>,和客户端 <c>SubagentReceipts.QueuedAhead</c> 同一个形状)。</summary>
    private static bool IsQueuePosition(Ncw.Agent.Kernel.Tools.ToolProgress progress) =>
        progress.Details is { ValueKind: System.Text.Json.JsonValueKind.Object } details
        && details.TryGetProperty("kind", out var kind)
        && kind.ValueKind == System.Text.Json.JsonValueKind.String
        && kind.ValueEquals("queued");

    private void RemoveAt(int index)
    {
        _bytes -= _entries[index].Bytes;
        _entries.RemoveAt(index);
    }

    private void DropHead(int n)
    {
        if (n <= 0)
        {
            return;
        }

        TrimmedThrough = Math.Max(TrimmedThrough, _entries[n - 1].Seq);
        for (var i = 0; i < n; i++)
        {
            _bytes -= _entries[i].Bytes;
        }

        _entries.RemoveRange(0, n);
        LogTrimmed = true;
    }
}

/// <summary>一条事件大约占多少字节(字符数 + 少量结构开销)。只用于预算,不求精确——精确的做法是序列化,那要为每条事件多分配一份;也不走反射。</summary>
internal static class EventSizes
{
    private const long Overhead = 48;

    public static long Estimate(AgentEvent ev) => Overhead + ev switch
    {
        MessageDeltaEvent delta => delta.Delta.Length + (delta.CallId?.Length ?? 0) + (delta.ToolName?.Length ?? 0),
        MessageEndEvent end => Message(end.Message),
        ToolStartEvent start => start.ToolName.Length + start.Input.GetRawText().Length,
        ToolEndEvent end => Output(end.Output),
        ToolProgressEvent progress => (progress.Progress.Message?.Length ?? 0) + (progress.Progress.Details?.GetRawText().Length ?? 0),
        NotificationEvent note => note.Key.Length + (note.Detail?.Length ?? 0),
        RunEndEvent { Error: { } error } => error.Message.Length,
        _ => 0,
    };

    private static long Message(Message message)
    {
        long n = 0;
        foreach (var part in message.Parts)
        {
            n += part switch
            {
                TextPart text => text.Text.Length,
                ThinkingPart thinking => thinking.Text.Length,
                ToolCallPart call => call.Name.Length + call.Input.GetRawText().Length,
                ToolResultPart result => Output(result.Output),
                ImagePart image => image.DataRef.Length,
                FileRefPart file => file.Path.Length,
                _ => 32,
            };
        }

        return n;
    }

    private static long Output(ToolOutput output) =>
        output.Content.Length + (output.Details?.GetRawText().Length ?? 0) + (output.Images?.Sum(i => i.DataRef.Length) ?? 0);
}
