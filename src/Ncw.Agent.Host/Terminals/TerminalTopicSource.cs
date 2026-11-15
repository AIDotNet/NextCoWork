using Ncw.Agent.Host.Workspace;
using Ncw.Agent.Terminal;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Terminals;

/// <summary>
/// <c>terminal/&lt;id&gt;</c> 主题(06 §9.4 第 3 点,09 §8.2):主题的 <c>seq</c> 就是 <see cref="TerminalOutput.Seq"/>(每个终端内严格连续,退出事件也占一个),游标就是上次收到的最后一个 <c>seq</c>。
/// <list type="bullet">
/// <item>不带游标,或游标已经不在终端的输出日志里:发一个 <see cref="TerminalSnapshotDto"/>(元数据 + 整个环形缓冲,控制端用它重置显示)。</item>
/// <item>游标还在日志里:不发快照,只补发游标之后的事件帧,之后实时推送(「取快照 + 挂订阅」在 <see cref="TerminalManager.Attach"/> 里原子完成)。</item>
/// <item>终端输出<b>没有可丢的增量</b>(每一批都是结构性的):发送缓冲满(<see cref="ITopicWriter.TryWrite"/> 返回 false)时对该主题发 <c>resync</c> 并停止转发,控制端不带游标重订、拿整个缓冲。</item>
/// <item>终端不存在(从没有过或已回收),或是别的控制端的远程终端:订阅得到 <c>not_found</c>(不泄漏它是否存在)。</item>
/// </list>
/// </summary>
internal sealed class TerminalTopicSource(Func<WorkspaceToolsRuntime> tools) : ITopicSource
{
    public string Prefix => TerminalMethods.TopicPrefix;

    public Role MinRole => Role.Viewer;

    public Transports Allowed => Transports.Any;

    public IDisposable? Open(string topic, long? cursor, ITopicWriter writer)
    {
        var id = topic[Prefix.Length..];
        if (id.Length == 0)
        {
            return null;
        }

        var runtime = tools();
        var controller = runtime.TerminalControllers.Track(writer.Context);
        var listener = new Listener(topic, writer);
        TerminalAttachment attachment;
        try
        {
            attachment = runtime.Terminals.Attach(id, listener.OnEvent, cursor, controller);
        }
        catch (TerminalException e) when (e.Code is TerminalErrorCode.NotFound or TerminalErrorCode.ApprovalRequired)
        {
            return null;
        }

        listener.Attachment = attachment;
        if (attachment.Resumed)
        {
            foreach (var evt in attachment.Replay)
            {
                writer.Write(TerminalMapping.ToFrame(topic, evt));
            }
        }
        else
        {
            var snapshot = new TerminalSnapshotDto(TerminalMapping.ToDto(attachment.Info), attachment.Snapshot.ToArray(), attachment.Truncated, attachment.Seq);
            writer.Write(new SnapshotFrame(topic, attachment.Seq, FrameCodec.EncodePayload(snapshot, PayloadShapes.GetShape<TerminalSnapshotDto>())));
        }

        listener.Ready();
        return listener;
    }

    /// <summary>
    /// 订阅的接收方。初始帧(快照 / 补发)写完之前到的事件先攒着,<see cref="Ready"/> 时按序补上,所以连接上的帧顺序永远是「初始帧 → 实时事件」。
    /// <see cref="OnEvent"/> 在终端会话的锁内同步调用(顺序有保证),这里不阻塞、不回调 <see cref="TerminalManager"/>。
    /// </summary>
    private sealed class Listener(string topic, ITopicWriter writer) : IDisposable
    {
        private readonly object _gate = new();
        private List<TerminalEvent>? _held = [];
        private bool _stopped;

        public TerminalAttachment? Attachment { get; set; }

        public void OnEvent(TerminalEvent evt)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                if (_held is not null)
                {
                    _held.Add(evt);
                    return;
                }

                if (writer.TryWrite(TerminalMapping.ToFrame(topic, evt)))
                {
                    return;
                }

                // 发送缓冲满:终端输出的每一批都不能悄悄丢,让控制端不带游标重订(拿整个缓冲)
                _stopped = true;
                writer.Write(new ResyncFrame(topic, "backlog_overflow"));
            }

            Dispose();
        }

        public void Ready()
        {
            lock (_gate)
            {
                foreach (var evt in _held ?? [])
                {
                    // 控制端刚订阅,不会是慢消费者:初始帧之后的实时事件直接入队
                    writer.Write(TerminalMapping.ToFrame(topic, evt));
                }

                _held = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopped = true;
            }

            // 订阅表是写时复制的数组,在会话的锁内(OnEvent 里)取消订阅是安全的;Lock 可重入
            Attachment?.Dispose();
        }
    }
}
