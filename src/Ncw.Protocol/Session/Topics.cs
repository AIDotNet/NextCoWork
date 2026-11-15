using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;
using PolyType;

namespace Ncw.Protocol.Session;

/// <summary>服务端往一个订阅里写帧的出口(每个订阅一个)。帧按写入顺序送出。</summary>
public interface ITopicWriter
{
    /// <summary>订阅者是谁(角色、承载)。</summary>
    CallContext Context { get; }

    /// <summary>写一帧并且一定入队:初始帧(快照 / 补发)和不能悄悄丢的帧用它。</summary>
    void Write(Frame frame);

    /// <summary>
    /// 实时推送一帧。这条连接的发送缓冲已满(<see cref="ProtocolInfo.MaxPendingBytes"/>)时不入队并返回 false:
    /// 调用方据此整批丢掉增量,或对结构性事件改发 <see cref="ResyncFrame"/>(docs/design/09 §8.2)。
    /// </summary>
    bool TryWrite(Frame frame);
}

/// <summary>
/// 一类自己管理序号和游标的动态主题(例如 <c>run/&lt;id&gt;</c>:序号就是 Run 事件日志的序号,游标就是 <c>sinceSeq</c>)。
/// 订阅时由来源写初始帧(快照,或游标之后的补发),之后实时往 <see cref="ITopicWriter"/> 写;返回的对象释放即退订。
/// </summary>
public interface ITopicSource
{
    /// <summary>主题名前缀,例如 <c>run/</c>。</summary>
    string Prefix { get; }

    Role MinRole { get; }

    Transports Allowed { get; }

    /// <summary>订阅 <paramref name="topic"/>。主题不存在返回 null(控制端得到 <c>not_found</c>);其它失败抛 <see cref="ProtocolException"/>。</summary>
    IDisposable? Open(string topic, long? cursor, ITopicWriter writer);
}

/// <summary>
/// 主题(docs/design/09 §8.2):注册快照生成函数,发布增量。保留最近一段事件,游标还在范围内就续传,否则给快照。
/// 快照和增量都由这里统一编号,同一主题内 seq 严格递增。三种主题:
/// <list type="bullet">
/// <item><b>固定主题</b>(<see cref="Register{TState}"/>):<c>device</c>、<c>data</c>、<c>interactions</c>……</item>
/// <item><b>前缀主题</b>(<see cref="RegisterPrefix{TState}"/>):<c>session/&lt;id&gt;</c> 这类按名字产生、有人订阅才存在的主题;
/// 没人订阅时发布是空操作,最后一个订阅者离开就丢弃(再订阅拿快照)。序号从当前时间派生的基数起算,所以旧游标永远不会被误认为有效。</item>
/// <item><b>来源主题</b>(<see cref="RegisterSource"/>):序号和游标由来源自己管理(<c>run/&lt;id&gt;</c>)。</item>
/// </list>
/// </summary>
public sealed class TopicHub
{
    private const int LogCapacity = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Topic> _topics = new(StringComparer.Ordinal);
    private readonly List<PrefixTopic> _prefixes = [];
    private readonly List<ITopicSource> _sources = [];

    private sealed record PrefixTopic(string Prefix, Func<string, byte[]?> Snapshot, Role MinRole, Transports Allowed);

    public void Register<TState>(TopicDef<TState> def, Func<TState> snapshot) =>
        _topics[def.Name] = new Topic(() => FrameCodec.EncodePayload(snapshot(), def.StateShape), def.MinRole, def.Allowed, dynamic: false);

    /// <summary>注册一类前缀主题。<paramref name="snapshot"/> 返回 null 表示这个名字的主题不存在(<c>not_found</c>)。</summary>
    public void RegisterPrefix<TState>(string prefix, ITypeShape<TState> shape, Func<string, TState?> snapshot, Role minRole = Role.Viewer, Transports allowed = Transports.Any)
        where TState : class =>
        _prefixes.Add(new PrefixTopic(prefix, name => snapshot(name) is { } state ? FrameCodec.EncodePayload(state, shape) : null, minRole, allowed));

    /// <summary>注册一类来源主题(自己管理序号和游标)。</summary>
    public void RegisterSource(ITopicSource source) => _sources.Add(source);

    /// <summary>这个主题名有没有人认(固定、前缀、来源三类之一)。</summary>
    public bool Knows(string topic)
    {
        lock (_gate)
        {
            return _topics.ContainsKey(topic) || _prefixes.Exists(p => topic.StartsWith(p.Prefix, StringComparison.Ordinal)) || FindSource(topic) is not null;
        }
    }

    /// <summary>这个调用者能不能订阅这个主题(角色和承载,和方法表同样的规则)。</summary>
    public bool CanSubscribe(string topic, CallContext context)
    {
        lock (_gate)
        {
            var (minRole, allowed) =
                _topics.TryGetValue(topic, out var t) ? (t.MinRole, t.Allowed)
                : _prefixes.Find(p => topic.StartsWith(p.Prefix, StringComparison.Ordinal)) is { } prefix ? (prefix.MinRole, prefix.Allowed)
                : FindSource(topic) is { } source ? (source.MinRole, source.Allowed)
                : (Role.Owner, Transports.Local);
            return context.Role >= minRole && (allowed & context.Transport) != 0;
        }
    }

    public ITopicSource? FindSource(string topic) => _sources.Find(s => topic.StartsWith(s.Prefix, StringComparison.Ordinal));

    /// <summary>发布一条增量,推给所有订阅者。前缀主题没有订阅者(主题不存在)时什么都不做;固定主题没注册是编程错误。</summary>
    public void Publish<TData>(string topic, string type, TData data, ITypeShape<TData> shape)
    {
        var bytes = FrameCodec.EncodePayload(data, shape);
        List<Func<Frame, ValueTask>> sinks;
        EventFrame frame;
        lock (_gate)
        {
            if (!_topics.TryGetValue(topic, out var t))
            {
                if (_prefixes.Exists(p => topic.StartsWith(p.Prefix, StringComparison.Ordinal)))
                {
                    return;
                }

                throw new KeyNotFoundException($"Topic '{topic}' is not registered");
            }

            t.Seq++;
            frame = new EventFrame(topic, t.Seq, type, bytes);
            t.Log.Enqueue(frame);
            while (t.Log.Count > LogCapacity)
            {
                t.Log.Dequeue();
            }

            sinks = [.. t.Sinks];
        }

        foreach (var sink in sinks)
        {
            _ = sink(frame).AsTask();
        }
    }

    /// <summary>某个前缀主题当前有没有订阅者(没有就不必为它读库、编码)。</summary>
    public bool HasSubscribers(string topic)
    {
        lock (_gate)
        {
            return _topics.TryGetValue(topic, out var t) && t.Sinks.Count > 0;
        }
    }

    /// <summary>订阅:返回需要立刻发给控制端的帧(快照,或从游标之后的补发),并登记后续推送。主题不存在返回 null。</summary>
    public IReadOnlyList<Frame>? Subscribe(string topic, long? cursor, Func<Frame, ValueTask> sink)
    {
        lock (_gate)
        {
            byte[]? state = null;
            if (!_topics.TryGetValue(topic, out var t))
            {
                if (_prefixes.Find(p => topic.StartsWith(p.Prefix, StringComparison.Ordinal)) is not { } prefix || (state = prefix.Snapshot(topic)) is null)
                {
                    return null;
                }

                t = new Topic(() => prefix.Snapshot(topic), prefix.MinRole, prefix.Allowed, dynamic: true)
                {
                    // 时间派生的基数:重建后的主题序号一定大于之前任何一次的,旧游标不会被当成有效。
                    Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20,
                };
                _topics[topic] = t;
            }

            if (cursor is { } c && c <= t.Seq && (t.Log.Count == 0 ? c == t.Seq : t.Log.Peek().Seq <= c + 1))
            {
                t.Sinks.Add(sink);
                return [.. t.Log.Where(e => e.Seq > c)];
            }

            state ??= t.Snapshot();
            if (state is null)
            {
                if (t.Dynamic && t.Sinks.Count == 0)
                {
                    _topics.Remove(topic);
                }

                return null;
            }

            t.Sinks.Add(sink);
            return [new SnapshotFrame(topic, t.Seq, state)];
        }
    }

    public void Unsubscribe(string topic, Func<Frame, ValueTask> sink)
    {
        lock (_gate)
        {
            if (_topics.TryGetValue(topic, out var t))
            {
                t.Sinks.Remove(sink);
                if (t.Dynamic && t.Sinks.Count == 0)
                {
                    _topics.Remove(topic);
                }
            }
        }
    }

    private sealed class Topic(Func<byte[]?> snapshot, Role minRole, Transports allowed, bool dynamic)
    {
        public Func<byte[]?> Snapshot { get; } = snapshot;

        public Role MinRole { get; } = minRole;

        public Transports Allowed { get; } = allowed;

        public bool Dynamic { get; } = dynamic;

        public long Seq { get; set; }

        public Queue<EventFrame> Log { get; } = new();

        public List<Func<Frame, ValueTask>> Sinks { get; } = [];
    }
}
