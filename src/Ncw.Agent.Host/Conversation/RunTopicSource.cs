using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Runs;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// <c>run/&lt;id&gt;</c> 主题(attach,docs/design/09 §8.2、08.05 §2):主题的 <c>seq</c> 就是这个 Run 事件日志的序号,游标就是 <c>sinceSeq</c>。
/// <list type="bullet">
/// <item>不带游标(首次订阅)或游标已被日志上限挤出去:发一个 <see cref="RunSnapshotDto"/> 快照(状态、日志里的事件、挂起的交互),控制端据此重建。</item>
/// <item>游标还在日志里:不发快照,只补发游标之后的事件帧(重连续传),之后实时推送。</item>
/// <item>「取快照 + 挂订阅」由 <see cref="RunRegistry.Attach"/> 在 Run 的锁里原子完成:快照里的事件不会再推一遍,快照之后的事件一条不漏。</item>
/// <item>每个订阅有自己的事件泵(按控制端 hello 里的 <c>coalesceMs</c> 合并文本增量);发送缓冲满(8 MiB)时只有增量的批整批丢弃,含结构性事件的批改发 <c>resync</c>,绝不悄悄丢。</item>
/// <item>Run 不存在(从没有过,或已结束且被回收)返回 <c>not_found</c>:控制端退回库里的终态(会话主题 / <c>sessions.get</c>)。</item>
/// </list>
/// <para>
/// <b>子代理</b>:父 Run 的主题只转发<b>根 Run 自己的事件</b>(主题的 <c>seq</c> 就是它的序号),其中包括子代理层 <c>Post</c> 进来的 <c>subagent_start / update / end</c>
/// ——子 Run 的状态镜像(阶段、当前工具、工具数、上下文用量、终态)就这样并进父 Run 的日志,和其它事件一起享有游标续传与「终态之后丢弃」。
/// 子 Run 自己的完整事件流(逐字转录)是它自己的 <c>run/&lt;childRunId&gt;</c> 主题:控制端点开子代理面板时才订阅(<c>subagent_start</c> 带来了子 Run id 与子会话 id),
/// 没点开的子代理一个字的转录都不占。所以不需要「设备主动推送的子主题」——服务端只回应订阅,不替控制端订阅。
/// </para>
/// </summary>
internal sealed class RunTopicSource(Func<ConversationService> service) : ITopicSource
{
    public string Prefix => ConversationMethods.RunTopicPrefix;

    public Role MinRole => Role.Viewer;

    public Transports Allowed => Transports.Any;

    public IDisposable? Open(string topic, long? cursor, ITopicWriter writer)
    {
        var svc = service();
        var id = topic[Prefix.Length..];
        if (id.Length == 0)
        {
            return null;
        }

        var runId = new RunId(id);
        var listener = new Listener(topic, runId, writer);
        var subscription = svc.Registry.Subscribe(listener, new SubscriptionOptions { Coalesce = TimeSpan.FromMilliseconds(writer.Context.CoalesceMs) });
        var attach = svc.Registry.Attach(runId, cursor, subscription);
        if (attach is null)
        {
            subscription.Dispose();
            return null;
        }

        var root = attach.Root;
        if (root.Cursor == CursorStatus.Continued)
        {
            // 游标续得上:只补发之后的事件(日志有空洞也不要紧,控制端不做 seq == last + 1 的检查)。
            foreach (var e in root.Events)
            {
                if (Frame(topic, e) is { } frame)
                {
                    writer.Write(frame);
                }
            }
        }
        else
        {
            var events = new List<SeqEventDto>(root.Events.Count);
            foreach (var e in root.Events)
            {
                if (Mapping.ToDto(e.Event) is { } dto)
                {
                    events.Add(new SeqEventDto(e.Seq, dto));
                }
            }

            var pending = svc.Gate.Snapshot(runId).Pending.Select(static p => Mapping.ToDto(p)).ToArray();
            var snapshot = new RunSnapshotDto(
                root.RunId.Value, root.SessionId, root.WorkspaceId, root.ParentRunId?.Value, root.Depth, Mapping.StateName(root.State),
                root.Error is { } error ? Mapping.ToDto(error) : null, root.StartedAt.ToUnixTimeMilliseconds(), root.EndedAt?.ToUnixTimeMilliseconds(),
                root.Seq, root.Cursor == CursorStatus.Lost ? "lost" : "fresh", [.. events], pending, [.. root.Children.Select(static c => c.Value)], root.LogTrimmed, root.Settled);
            writer.Write(new SnapshotFrame(topic, root.Seq, FrameCodec.EncodePayload(snapshot, PayloadShapes.GetShape<RunSnapshotDto>())));
        }

        listener.Ready();
        return subscription;
    }

    private static EventFrame? Frame(string topic, SequencedEvent e)
    {
        if (Mapping.ToDto(e.Event) is not { } dto)
        {
            return null;
        }

        return new EventFrame(topic, e.Seq, Mapping.TypeName(dto), FrameCodec.EncodePayload(dto, PayloadShapes.GetShape<RunEventDto>()));
    }

    /// <summary>
    /// 订阅的接收方。初始帧(快照 / 补发)写完之前到的批先攒着,<see cref="Ready"/> 时按序补上,所以连接上的帧顺序永远是「初始帧 → 实时事件」。
    /// 只转发根 Run 自己的事件(主题的 <c>seq</c> 是它的序号)。
    /// </summary>
    private sealed class Listener(string topic, RunId root, ITopicWriter writer) : IRunListener
    {
        private readonly object _gate = new();
        private List<IReadOnlyList<RunEventBatch>>? _held = [];

        public bool TryDeliver(IReadOnlyList<RunEventBatch> batches)
        {
            lock (_gate)
            {
                if (_held is not null)
                {
                    _held.Add(batches);
                    return true;
                }
            }

            return Send(batches);
        }

        public void Ready()
        {
            lock (_gate)
            {
                foreach (var batches in _held ?? [])
                {
                    // 初始帧之后的实时批:缓冲满也不能丢结构性事件,所以这里直接写(控制端刚订阅,不会是慢消费者)。
                    foreach (var batch in batches.Where(b => b.RunId == root))
                    {
                        foreach (var e in batch.Events)
                        {
                            if (Frame(topic, e) is { } frame)
                            {
                                writer.Write(frame);
                            }
                        }
                    }
                }

                _held = null;
            }
        }

        private bool Send(IReadOnlyList<RunEventBatch> batches)
        {
            foreach (var batch in batches)
            {
                if (batch.RunId != root)
                {
                    continue;
                }

                foreach (var e in batch.Events)
                {
                    if (Frame(topic, e) is { } frame && !writer.TryWrite(frame))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public void Resync(ResyncReason reason) => writer.Write(new ResyncFrame(topic, reason == ResyncReason.BacklogOverflow ? "backlog_overflow" : "unknown"));
    }
}
