using QueuedInput = Ncw.Agent.Kernel.Loop.QueuedInput;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>排队消息需要注册表提供的几样东西(包内接缝:队列不直接持有注册表)。</summary>
internal interface IQueueHost
{
    RunHandle? ActiveTopLevelRun(string sessionId);

    bool IsSessionBusy(string sessionId);

    LaunchResult Launch(RunLaunch launch);
}

/// <summary>
/// 排队消息(05 §3;2.x 的 <c>SessionRuntime</c> 移植,以及 <c>run-registry.ts</c> 里的插话信箱)。每个会话一个条目,条目里的 <see cref="RunInputQueues"/>
/// 同时是用户队列和正在跑的 Run 的引导信箱——<see cref="TakeSteering"/> 直接接 <c>RunRequest.TakeSteering</c>,所以「点插话」「编辑」「删除」不需要任何「同步到信箱」的步骤。
/// <list type="bullet">
/// <item><b>「下一轮什么时候开跑」的唯一决定者</b>:入口只有 <see cref="OnRunSettled"/>(<c>run_settled</c> 之后,不是 <c>run_end</c>:后者比收尾落盘早)、
/// 空闲时的入队 / 引入、以及手动 <see cref="Resume"/>。只有 Run 以 <c>Done</c> 结束才自动续跑;中止 / 出错的原样留着;冷启动不自动跑。</item>
/// <item><b>送达以同 id 的 <c>message_end</c> 为准</b>:被 <c>TakeSteering</c> 取走的用户条目先记在 inflight 里(仍然落盘),看到同 id 的 <c>message_end</c> 才真的移出;
/// Run 收尾时还没确认的,按持久消费回执(<c>session_input_receipts</c>)过滤后退回队列。</item>
/// <item><b>先落盘再确认</b>:每个变更先写 kv,成功后才广播 <c>queue_update</c> 并返回回执;写失败 → 回滚内存、抛 <see cref="QueuePersistenceException"/>,绝不回 accepted,
/// 也不去续跑(否则一条没落盘的消息会被发出去)。</item>
/// <item>内部输入(<see cref="DeliverInternal"/>:后台报告、目标检查)不进用户队列、不落盘,送达同样以同 id 的 <c>message_end</c> 为准。</item>
/// </list>
/// 线程安全:每个会话一把锁(可重入),全局 <c>Rev</c> 用原子计数。<c>Changed</c> 在会话锁内触发(同一个会话的广播严格按 <c>Rev</c> 有序),处理器必须快速返回。
/// </summary>
internal sealed class SessionQueues : IDisposable
{
    private sealed record QueueMeta(SendOptions Options, long EnqueuedAt, long? PromotedAt);

    private sealed class Entry
    {
        public Entry(string sessionId, TimeProvider clock)
        {
            SessionId = sessionId;
            Queue = new RunInputQueues(clock);
        }

        public string SessionId { get; }

        public Lock Gate { get; } = new();

        public RunInputQueues Queue { get; }

        public Dictionary<MessageId, QueueMeta> Meta { get; } = [];

        /// <summary>已被 Run 取走、还没看到同 id 的 <c>message_end</c> 的用户条目(仍然落盘)。</summary>
        public Dictionary<MessageId, QueueItem> Inflight { get; } = [];

        public List<InternalInput> Deferred { get; } = [];

        public Dictionary<RunId, Dictionary<MessageId, InternalInput>> Unconfirmed { get; } = [];

        public ITimer? Retry { get; set; }

        public int RetryAttempt { get; set; }

        /// <summary>内存里的这一份还没成功落盘(确认后清队列的写失败时留着,下一次成功的写会把它补上)。</summary>
        public bool Dirty { get; set; }

        public bool Evicted { get; set; }
    }

    private sealed record Snap(IReadOnlyList<QueueEntry> Entries, Dictionary<MessageId, QueueMeta> Meta, Dictionary<MessageId, QueueItem> Inflight);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, SendOptions> _lastOptions = new(StringComparer.Ordinal);
    private readonly SessionRepository _sessions;
    private readonly IQueueStore _store;
    private readonly IRunRequestFactory _requests;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly ILoopDiagnostics _log;
    private readonly RunRegistryOptions _options;
    private readonly IQueueHost _host;
    private long _rev;
    private bool _disposed;

    public SessionQueues(SessionRepository sessions, IQueueStore store, IRunRequestFactory requests, IIdGenerator ids, TimeProvider clock, ILoopDiagnostics log, RunRegistryOptions options, IQueueHost host)
    {
        _sessions = sessions;
        _store = store;
        _requests = requests;
        _ids = ids;
        _clock = clock;
        _log = log;
        _options = options;
        _host = host;
    }

    /// <summary>队列变了(全量快照,按 <c>Rev</c> 有序)。</summary>
    public event Action<QueueUpdate>? Changed;

    public long Rev => Interlocked.Read(ref _rev);

    // ───────────────────────── 条目 ─────────────────────────

    private Entry GetOrCreate(string sessionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }
        }

        var entry = new Entry(sessionId, _clock);
        Load(entry);
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var raced))
            {
                return raced;
            }

            _entries[sessionId] = entry;
            return entry;
        }
    }

    private Entry? TryGet(string sessionId)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(sessionId);
        }
    }

    private T With<T>(string sessionId, Func<Entry, T> body)
    {
        while (true)
        {
            var entry = GetOrCreate(sessionId);
            lock (entry.Gate)
            {
                if (entry.Evicted)
                {
                    continue;
                }

                try
                {
                    return body(entry);
                }
                finally
                {
                    EvictIfIdle(entry);
                }
            }
        }
    }

    /// <summary>空条目不常驻:开过几百个会话,这张表也只留着真有未发输入的那几个。dirty 的不能丢:内存里是唯一一份还没保存的输入。</summary>
    private void EvictIfIdle(Entry e)
    {
        if (e.Evicted || e.Dirty || e.Retry is not null || e.Deferred.Count > 0 || e.Unconfirmed.Count > 0 || e.Inflight.Count > 0)
        {
            return;
        }

        var snapshot = e.Queue.Snapshot();
        if (snapshot.Entries.Count > 0 || snapshot.InternalSteering.Count > 0)
        {
            return;
        }

        e.Evicted = true;
        lock (_gate)
        {
            if (_entries.TryGetValue(e.SessionId, out var current) && current == e)
            {
                _entries.Remove(e.SessionId);
            }
        }
    }

    /// <summary>冷启动:从存档恢复(不自动执行)。存档结构对不上 / 过期整份丢弃;读失败按「没有」处理并记诊断。</summary>
    private void Load(Entry e)
    {
        IReadOnlyList<QueueItem>? saved;
        try
        {
            saved = _store.Load(e.SessionId);
        }
        catch (Exception ex)
        {
            _log.Warn($"The saved queue of session {e.SessionId} could not be read.", ex);
            return;
        }

        if (saved is null || saved.Count == 0)
        {
            return;
        }

        var restored = new List<QueueEntry>();
        long order = 0;
        foreach (var item in saved.Take(RunInputQueues.MaxItems))
        {
            e.Meta[item.Id] = new QueueMeta(item.Options, item.EnqueuedAt, item.PromotedAt);
        }

        // 引入顺序按 promotedAt(缺失时退化成入队序,而不是让它抢到所有人前面)。
        var rank = saved.Where(i => i.State == QueueState.Promoted).OrderBy(i => i.PromotedAt ?? i.EnqueuedAt).Select((i, n) => (i.Id, Order: (long)(n + 1))).ToDictionary(t => t.Id, t => t.Order);
        foreach (var item in saved.Take(RunInputQueues.MaxItems))
        {
            var promoted = rank.TryGetValue(item.Id, out var o);
            order = promoted ? o : 0;
            restored.Add(new QueueEntry(new QueuedInput(item.Id, item.Parts), promoted ? QueueState.Promoted : QueueState.Pending, order));
        }

        e.Queue.Restore(restored);
        RemoveCommitted(e);
    }

    // ───────────────────────── 快照 / 落盘 / 广播 ─────────────────────────

    private List<QueueItem> ItemsOf(Entry e) =>
    [
        .. e.Queue.Snapshot().Entries.Select(q =>
        {
            e.Meta.TryGetValue(q.Input.Id, out var meta);
            return new QueueItem(q.Input.Id, q.Input.Parts, q.State, q.PromotedOrder, meta?.EnqueuedAt ?? 0, q.State == QueueState.Promoted ? meta?.PromotedAt : null, meta?.Options ?? new SendOptions(ModelRef.Unpinned("")));
        }),
    ];

    private Snap Capture(Entry e) => new(e.Queue.Snapshot().Entries, new Dictionary<MessageId, QueueMeta>(e.Meta), new Dictionary<MessageId, QueueItem>(e.Inflight));

    private static void Rollback(Entry e, Snap before)
    {
        e.Queue.Restore(before.Entries);
        e.Meta.Clear();
        foreach (var (k, v) in before.Meta)
        {
            e.Meta[k] = v;
        }

        e.Inflight.Clear();
        foreach (var (k, v) in before.Inflight)
        {
            e.Inflight[k] = v;
        }
    }

    /// <summary>落盘 = 队列里的条目 + 已取走未确认的(它们还没有被消费的证据)。失败标脏并抛。</summary>
    private void PersistLocked(Entry e)
    {
        var items = ItemsOf(e);
        items.AddRange(e.Inflight.Values.Select(i => i with { State = QueueState.Promoted }));
        try
        {
            _store.Save(e.SessionId, items);
            e.Dirty = false;
        }
        catch (Exception ex)
        {
            e.Dirty = true;
            _log.Warn($"The queue of session {e.SessionId} could not be saved.", ex);
            throw new QueuePersistenceException(e.SessionId, ex);
        }
    }

    /// <summary>落盘成功之后才递增 Rev 并广播(顺序不能反:先递增的话,一条没保存的消息会拿到一个「比谁都新」的回执)。</summary>
    private QueueUpdate CommitLocked(Entry e)
    {
        var update = new QueueUpdate(e.SessionId, Interlocked.Increment(ref _rev), ItemsOf(e));
        Raise(update);
        return update;
    }

    private QueueUpdate CurrentLocked(Entry e) => new(e.SessionId, Rev, ItemsOf(e));

    private void Raise(QueueUpdate update)
    {
        // 已经落盘成功:广播失败只是这一次没送到界面,下一份快照会补上,不能报成失败(也不能让排在它后面的订阅者收不到)。
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<QueueUpdate>)handler)(update);
            }
            catch (Exception ex)
            {
                _log.Warn($"The queue update of session {update.SessionId} could not be broadcast.", ex);
            }
        }
    }

    /// <summary>按持久消费回执过滤(冷启动、每一次续跑之前):「已执行的输入绝不会再发一次」的硬保证——清队列的写可能失败,转录也可能被压缩 / 改写,而回执独立留痕。</summary>
    private bool RemoveCommitted(Entry e)
    {
        var entries = e.Queue.Snapshot().Entries;
        if (entries.Count == 0)
        {
            return false;
        }

        IReadOnlyList<string> committed;
        try
        {
            committed = _sessions.CommittedInputIds(e.SessionId, [.. entries.Select(x => x.Input.Id.Value)]);
        }
        catch (Exception ex)
        {
            // 读不到回执时不据它删条目:宁可多留一条,也不能凭失败的查询丢掉输入。
            _log.Warn($"The consumed-input receipts of session {e.SessionId} could not be read.", ex);
            return false;
        }

        if (committed.Count == 0)
        {
            return false;
        }

        var done = new HashSet<string>(committed, StringComparer.Ordinal);
        e.Queue.Restore([.. entries.Where(x => !done.Contains(x.Input.Id.Value))]);
        foreach (var id in done)
        {
            e.Meta.Remove(new MessageId(id));
        }

        try
        {
            PersistLocked(e);
        }
        catch (QueuePersistenceException)
        {
            // 内存里已经不含它们,不会再被本进程发出去;盘上那份旧队列靠持久消费回执挡住。
        }

        return true;
    }

    // ───────────────────────── 用户操作 ─────────────────────────

    public QueueUpdate Current(string sessionId) => With(sessionId, CurrentLocked);

    public QueueResult Enqueue(string sessionId, MessageId? id, IReadOnlyList<Part> parts, SendOptions options)
    {
        var result = With(sessionId, e =>
        {
            var messageId = id ?? new MessageId(_ids.NewId());
            var count = e.Queue.Snapshot().Entries.Count + e.Inflight.Count;
            // 软上限:超过就不是队列了,是便签本。拒绝入队,由调用方保留草稿——静默丢弃会让用户以为消息进了队列。
            if (count >= RunInputQueues.MaxItems || e.Meta.ContainsKey(messageId) || e.Inflight.ContainsKey(messageId))
            {
                return new QueueResult(false, CurrentLocked(e));
            }

            var before = Capture(e);
            e.Queue.Enqueue(new QueuedInput(messageId, parts));
            e.Meta[messageId] = new QueueMeta(options, _clock.GetUtcNow().ToUnixTimeMilliseconds(), null);
            return Persisted(e, before);
        });

        // 渲染层以为还在跑,其实那一轮已经收尾了 —— 这时入队就等于「接着发」。
        if (result.Accepted && _host.ActiveTopLevelRun(sessionId) is null)
        {
            Drain(sessionId);
        }

        return result;
    }

    /// <summary>「插话」是 toggle:Pending ⇄ Promoted。取消引入后再引入排到已引入者的队尾。空闲态被点插话 = 立即发送;运行中才是插话的正题(下一个回合边界送进去)。</summary>
    public QueueResult TogglePromote(string sessionId, MessageId id)
    {
        var result = With(sessionId, e =>
        {
            var before = Capture(e);
            if (!e.Queue.TogglePromote(id))
            {
                return new QueueResult(false, CurrentLocked(e));
            }

            var meta = e.Meta[id];
            var promoted = e.Queue.Snapshot().Entries.First(x => x.Input.Id == id).State == QueueState.Promoted;
            // 不能直接用 now:毫秒分辨率下两次点击可能同值。取 max(now, 已有最大值 + 1) 保证严格单调;取消引入时清掉,再引入排到队尾。
            var maxAt = e.Meta.Values.Max(m => m.PromotedAt ?? 0);
            e.Meta[id] = meta with { PromotedAt = promoted ? Math.Max(_clock.GetUtcNow().ToUnixTimeMilliseconds(), maxAt + 1) : null };
            return Persisted(e, before);
        });

        if (result.Accepted && _host.ActiveTopLevelRun(sessionId) is null)
        {
            Drain(sessionId);
        }

        return result;
    }

    /// <summary>编辑内容:不动档位快照,也不动引入顺序。</summary>
    public QueueResult Edit(string sessionId, MessageId id, IReadOnlyList<Part> parts) => With(sessionId, e =>
    {
        var before = Capture(e);
        return e.Queue.Edit(id, parts) ? Persisted(e, before) : new QueueResult(false, CurrentLocked(e));
    });

    public QueueResult Drop(string sessionId, MessageId id) => Remove(sessionId, id, take: false);

    /// <summary>撤回:出队并把内容交回调用方(「撤回到输入框」)。</summary>
    public QueueResult Take(string sessionId, MessageId id) => Remove(sessionId, id, take: true);

    private QueueResult Remove(string sessionId, MessageId id, bool take) => With(sessionId, e =>
    {
        var before = Capture(e);
        if (e.Queue.Remove(id) is not { } removed)
        {
            return new QueueResult(false, CurrentLocked(e));
        }

        e.Meta.Remove(id);
        return Persisted(e, before, take ? removed.Parts : null);
    });

    /// <summary>切换权限档位时,队列里还没被消费的条目原地改成新档位(其余档位不动)——用户切到「完全访问」图的就是接下来都不用再被打断。</summary>
    public QueueResult RetagPermission(string sessionId, string mode) => Retag(sessionId, o => o.PermissionMode == mode ? o : o with { PermissionMode = mode });

    public QueueResult RetagMode(string sessionId, string mode) => Retag(sessionId, o => o.Mode == mode ? o : o with { Mode = mode });

    private QueueResult Retag(string sessionId, Func<SendOptions, SendOptions> change) => With(sessionId, e =>
    {
        var before = Capture(e);
        foreach (var q in e.Queue.Snapshot().Entries)
        {
            if (e.Meta.TryGetValue(q.Input.Id, out var meta))
            {
                e.Meta[q.Input.Id] = meta with { Options = change(meta.Options) };
            }
        }

        return Persisted(e, before);
    });

    /// <summary>用户在停止 / 报错 / 重启之后手动继续,与自动续跑走同一条路径。</summary>
    public QueueResult Resume(string sessionId)
    {
        Drain(sessionId);
        return new QueueResult(true, Current(sessionId));
    }

    private QueueResult Persisted(Entry e, Snap before, IReadOnlyList<Part>? taken = null)
    {
        try
        {
            PersistLocked(e);
        }
        catch (QueuePersistenceException)
        {
            Rollback(e, before);
            throw;
        }

        return new QueueResult(true, CommitLocked(e), taken);
    }

    // ───────────────────────── 续跑 ─────────────────────────

    /// <summary>
    /// 发出排队的下一批(2.x 的 <c>drain</c>)。返回是否真的起了一个新 Run。
    /// ★ 先确认档位在,再动队列(反过来的话,没有档位时条目已经被移出队列,那几句话就凭空消失了);去掉这批之前先落盘,写失败就回滚并抛;启动失败把条目放回队首。
    /// </summary>
    public bool Drain(string sessionId) => With(sessionId, e =>
    {
        if (_host.IsSessionBusy(sessionId))
        {
            return false;
        }

        RemoveCommitted(e);
        if (e.Dirty)
        {
            PersistLocked(e);
        }

        var items = ItemsOf(e);
        var batch = QueueBatches.PickNext(items);
        if (batch.Count == 0)
        {
            return false;
        }

        // 档位取最早被引入那条的快照:用户按他当时看到的设置写下这句话。
        var options = batch[0].Options;
        if (options.Model.Alias.Length == 0 && LastOptions(sessionId) is { } last)
        {
            options = last;
        }

        if (_sessions.Get(sessionId) is not { } session)
        {
            return false;
        }

        var merged = QueueBatches.Merge(batch);
        // 超限被排除的条目退回 pending —— 留在 promoted 会让下一轮又把它们排到最前。
        var deferredIds = merged.DeferredIds.ToHashSet();
        var sent = batch.Where(q => !deferredIds.Contains(q.Id)).ToList();
        var sentIds = sent.Select(q => q.Id).ToHashSet();

        var before = Capture(e);
        var remaining = e.Queue.Snapshot().Entries
            .Where(x => !sentIds.Contains(x.Input.Id))
            .Select(x => deferredIds.Contains(x.Input.Id) ? x with { State = QueueState.Pending, PromotedOrder = 0 } : x)
            .ToList();
        e.Queue.Restore(remaining);
        foreach (var id in sentIds)
        {
            e.Meta.Remove(id);
        }

        try
        {
            PersistLocked(e);
        }
        catch (QueuePersistenceException)
        {
            Rollback(e, before);
            throw;
        }

        CommitLocked(e);

        if (merged.Parts.Count == 0)
        {
            // 全是空白:没有什么可发的,它们已经离开队列。
            return false;
        }

        // 单条的批沿用条目 id(这样「已消费回执」同样能挡住它);多条合并成一条新消息。
        var messageId = sent.Count == 1 ? sent[0].Id : new MessageId(_ids.NewId());
        var runId = new RunId(_ids.NewId());
        LaunchResult result;
        try
        {
            var message = new Message(messageId, Role.User, merged.Parts, _clock.GetUtcNow());
            var request = _requests.Create(new RunTemplate(runId, sessionId, session.WorkspaceId, [message], options, RunOrigin.Internal));
            result = _host.Launch(new RunLaunch(sessionId, session.WorkspaceId, request) { Origin = RunOrigin.Internal, Options = options });
        }
        catch (Exception ex)
        {
            _log.Warn($"The queued input of session {sessionId} could not start.", ex);
            result = new LaunchResult.Refused(RunRefusal.InvalidRequest, ex.Message);
        }

        if (result is LaunchResult.Started)
        {
            return true;
        }

        // 没发出去就原样放回队首——宁可多等一轮,也不能让用户排的话消失。放回本身也要落盘;这一步再失败就抛出去(不能静默算作成功,否则重启后这几句话就没了)。
        var putBack = sent.Select(q => new QueueEntry(new QueuedInput(q.Id, q.Parts), q.State, q.PromotedOrder)).ToList();
        foreach (var q in sent)
        {
            e.Meta[q.Id] = new QueueMeta(q.Options, q.EnqueuedAt, q.PromotedAt);
        }

        e.Queue.Restore([.. putBack, .. e.Queue.Snapshot().Entries]);
        PersistLocked(e);
        CommitLocked(e);
        return false;
    });

    // ───────────────────────── Run 生命周期 ─────────────────────────

    /// <summary>每个顶层 Run 启动时调用:记住档位(内部输入续用),并把这个会话的队列装载进来(冷启动恢复的条目在用户随后发起的 Run 里才会动)。</summary>
    public void OnRunStarted(RunHandle run, RunLaunch launch)
    {
        if (!run.IsTopLevel)
        {
            return;
        }

        if (launch.Options is { } options)
        {
            RememberOptions(run.SessionId, options);
        }

        GetOrCreate(run.SessionId);
    }

    /// <summary>
    /// 回合边界取引导消息(接 <c>RunRequest.TakeSteering</c>):取走即消费。用户条目记进 inflight(仍然落盘),看到同 id 的 <c>message_end</c> 才真的移出。
    /// 只看已经装载的条目(不为每个回合边界去读一次库)。
    /// </summary>
    public IReadOnlyList<Message> TakeSteering(RunHandle run)
    {
        if (TryGet(run.SessionId) is not { } entry)
        {
            return [];
        }

        lock (entry.Gate)
        {
            if (entry.Evicted)
            {
                return [];
            }

            var before = ItemsOf(entry).ToDictionary(i => i.Id);
            var taken = entry.Queue.TakeSteering();
            var userTaken = false;
            foreach (var message in taken)
            {
                if (before.TryGetValue(message.Id, out var item))
                {
                    entry.Inflight[item.Id] = item;
                    entry.Meta.Remove(item.Id);
                    userTaken = true;
                }
            }

            if (userTaken)
            {
                CommitLocked(entry);
            }

            return taken;
        }
    }

    /// <summary>
    /// 一条用户消息提交了(<c>message_end</c>):同 id 的排队条目 / 内部输入这时才算「已送达」。
    /// 消费已经是事实,清队列的写失败也不能把条目放回可执行队列:内存保持「已移除」并标脏,盘上那份旧队列靠持久消费回执挡住。
    /// </summary>
    public void OnMessageEnd(RunHandle run, MessageId messageId)
    {
        if (TryGet(run.SessionId) is not { } entry)
        {
            return;
        }

        InternalInput? confirmed = null;
        lock (entry.Gate)
        {
            if (entry.Evicted)
            {
                return;
            }

            if (entry.Unconfirmed.TryGetValue(run.RunId, out var pending) && pending.Remove(messageId, out confirmed) && pending.Count == 0)
            {
                entry.Unconfirmed.Remove(run.RunId);
            }

            if (entry.Inflight.Remove(messageId))
            {
                try
                {
                    PersistLocked(entry);
                }
                catch (QueuePersistenceException)
                {
                    // 已标脏(PersistLocked),不回滚。
                }
            }
            else if (entry.Queue.Snapshot().Entries.Any(x => x.Input.Id == messageId))
            {
                // 条目没经过 TakeSteering 就被提交了(例如批量续跑沿用了它的 id):同样移出。
                entry.Queue.Remove(messageId);
                entry.Meta.Remove(messageId);
                try
                {
                    PersistLocked(entry);
                    CommitLocked(entry);
                }
                catch (QueuePersistenceException)
                {
                }
            }

            EvictIfIdle(entry);
        }

        Done(confirmed, true);
    }

    /// <summary>
    /// 驱动收尾完成、会话互斥已经释放、<c>run_settled</c> 已发之后调用(R8)。顺序(2.x):收回没确认的内部输入 → 等着投递的内部输入先走 → 只有 <c>Done</c> 才续跑排队消息。
    /// </summary>
    public void OnRunSettled(RunHandle run)
    {
        if (!run.IsTopLevel || TryGet(run.SessionId) is not { } entry)
        {
            return;
        }

        var status = run.State.ToStatus();
        var cleanExit = run.State == RunState.Done && !run.AbortRequested;
        var failed = new List<InternalInput>();
        var launchedInternal = false;
        var hasQueued = false;
        lock (entry.Gate)
        {
            if (entry.Evicted)
            {
                return;
            }

            // 还留在引导队列里没被取走的内部消息:从这里拿走,交给下面的「没确认就收回」,否则会被下一个 Run 顺手取走(重复投递)。
            entry.Queue.TakeInternal();
            RestoreInflight(entry);

            if (entry.Unconfirmed.Remove(run.RunId, out var pending))
            {
                if (!cleanExit)
                {
                    failed.AddRange(pending.Values);
                }
                else if (pending.Count > 0)
                {
                    // 正常结束 → 按原投递顺序放回 deferred 队首,优先于排队的用户消息继续投递。
                    entry.Deferred.InsertRange(0, pending.Values);
                }
            }

            launchedInternal = FlushDeferred(entry);
            // 队列里没有东西就不必去续跑(也省得为一个空条目再读一次库)。
            hasQueued = entry.Queue.Snapshot().Entries.Count > 0 || entry.Dirty;
            EvictIfIdle(entry);
        }

        foreach (var input in failed)
        {
            Done(input, false);
        }

        if (!launchedInternal && hasQueued && status == RunStatus.Done)
        {
            try
            {
                Drain(run.SessionId);
            }
            catch (Exception ex)
            {
                _log.Warn($"The queued input of session {run.SessionId} could not be continued.", ex);
            }
        }
    }

    /// <summary>取走了却没被消费的用户条目(Run 中止 / 出错 / 事件被丢)退回队列:先按持久回执过滤——提交过的就是已送达,不能再发。</summary>
    private void RestoreInflight(Entry e)
    {
        if (e.Inflight.Count == 0)
        {
            return;
        }

        var back = e.Inflight.Values.ToList();
        e.Inflight.Clear();
        HashSet<string> committed;
        try
        {
            committed = [.. _sessions.CommittedInputIds(e.SessionId, [.. back.Select(i => i.Id.Value)])];
        }
        catch (Exception ex)
        {
            _log.Warn($"The consumed-input receipts of session {e.SessionId} could not be read.", ex);
            committed = [];
        }

        var entries = e.Queue.Snapshot().Entries.ToList();
        foreach (var item in back.Where(i => !committed.Contains(i.Id.Value)))
        {
            entries.Add(new QueueEntry(new QueuedInput(item.Id, item.Parts), QueueState.Promoted, item.PromotedOrder));
            e.Meta[item.Id] = new QueueMeta(item.Options, item.EnqueuedAt, item.PromotedAt);
        }

        e.Queue.Restore(entries);
        try
        {
            PersistLocked(e);
            CommitLocked(e);
        }
        catch (QueuePersistenceException)
        {
            // 已标脏:内存里是对的,下一次成功的写补上。
        }
    }

    // ───────────────────────── 内部输入 ─────────────────────────

    /// <summary>
    /// 投递一条内部输入:Run 在跑就进它的引导队列,会话正在收尾就排到收尾之后,空闲就开一个新 Run。三条路都只是「发出去了」——
    /// <see cref="InternalInput.Done"/>(true) 要等同 id 的 <c>message_end</c> 回执(<see cref="OnMessageEnd"/>)。
    /// </summary>
    public InternalDelivery DeliverInternal(string sessionId, InternalInput input) => With(sessionId, e => DeliverLocked(e, input));

    private InternalDelivery DeliverLocked(Entry e, InternalInput input)
    {
        var active = _host.ActiveTopLevelRun(e.SessionId);
        if (active is { State: RunState.Running, AbortRequested: false })
        {
            // 先登记再入队:入队成功不算送达。
            var id = new MessageId(_ids.NewId());
            Track(e, active.RunId, id, input);
            if (e.Queue.EnqueueInternalSteering(new QueuedInput(id, input.Parts, true)))
            {
                return InternalDelivery.Interjected;
            }

            Untrack(e, active.RunId, id);
        }

        if (_host.IsSessionBusy(e.SessionId))
        {
            e.Deferred.Add(input);
            ScheduleRetry(e);
            return InternalDelivery.Deferred;
        }

        return LaunchInternal(e, input) ? InternalDelivery.Launched : InternalDelivery.Failed;
    }

    private bool LaunchInternal(Entry e, InternalInput input)
    {
        var options = input.Options ?? LastOptions(e.SessionId);
        // 连档位都没有:不能静默 return——界面上那颗「处理」按钮还在,一个按下去什么都不发生的按钮不是诚实的界面。
        if (options is null || _sessions.Get(e.SessionId) is not { } session)
        {
            _log.Warn($"An internal input for session {e.SessionId} has no send options to start a run with.");
            Done(input, false);
            return false;
        }

        // 在 launch 之前 mint 并登记:Run 可能同步提交输入,回执早于本函数返回。
        var runId = new RunId(_ids.NewId());
        var messageId = new MessageId(_ids.NewId());
        Track(e, runId, messageId, input);
        try
        {
            var message = new Message(messageId, Role.User, input.Parts, _clock.GetUtcNow(), Internal: true);
            var request = _requests.Create(new RunTemplate(runId, e.SessionId, session.WorkspaceId, [message], options, RunOrigin.Internal));
            if (_host.Launch(new RunLaunch(e.SessionId, session.WorkspaceId, request) { Origin = RunOrigin.Internal, Options = options }) is LaunchResult.Started)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"An internal input for session {e.SessionId} could not start.", ex);
        }

        // 启动被拒:清掉自己的未确认项再 done(false),免得之后收尾被重复接回。
        Untrack(e, runId, messageId);
        Done(input, false);
        return false;
    }

    private static void Track(Entry e, RunId run, MessageId id, InternalInput input)
    {
        // 没有送达回调的(目标检查)不需要等回执。
        if (input.Done is null)
        {
            return;
        }

        if (!e.Unconfirmed.TryGetValue(run, out var pending))
        {
            e.Unconfirmed[run] = pending = [];
        }

        pending[id] = input;
    }

    private static void Untrack(Entry e, RunId run, MessageId id)
    {
        if (e.Unconfirmed.TryGetValue(run, out var pending))
        {
            pending.Remove(id);
            if (pending.Count == 0)
            {
                e.Unconfirmed.Remove(run);
            }
        }
    }

    private void Done(InternalInput? input, bool delivered)
    {
        if (input?.Done is not { } done)
        {
            return;
        }

        try
        {
            done(delivered);
        }
        catch (Exception ex)
        {
            _log.Warn("An internal input delivery callback threw.", ex);
        }
    }

    /// <summary>会话空出来了:投递第一条等着的内部输入。起了新 Run 返回 true(剩下的留给那个 Run:它在跑时投递会进它的引导队列,收尾后再从这里继续)。</summary>
    private bool FlushDeferred(Entry e)
    {
        if (e.Deferred.Count == 0)
        {
            return false;
        }

        if (_host.IsSessionBusy(e.SessionId))
        {
            ScheduleRetry(e);
            return false;
        }

        ClearRetry(e);
        while (e.Deferred.Count > 0)
        {
            var next = e.Deferred[0];
            e.Deferred.RemoveAt(0);
            var result = DeliverLocked(e, next);
            if (result == InternalDelivery.Launched)
            {
                if (e.Deferred.Count > 0)
                {
                    ScheduleRetry(e);
                }

                return true;
            }

            if (result == InternalDelivery.Deferred)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>历史操作(手动压缩)占着会话时不会有 <c>run_settled</c> 来叫醒等着的输入,所以另挂一个有上限的重试。</summary>
    private void ScheduleRetry(Entry e)
    {
        if (e.Retry is not null || _disposed)
        {
            return;
        }

        e.Retry = _clock.CreateTimer(static state => ((Action)state!)(), (Action)(() => OnRetryTick(e)), _options.DeferredRetry, Timeout.InfiniteTimeSpan);
    }

    private void OnRetryTick(Entry e)
    {
        List<InternalInput>? giveUp = null;
        lock (e.Gate)
        {
            if (e.Evicted)
            {
                return;
            }

            ClearRetry(e);
            if (e.Deferred.Count == 0)
            {
                e.RetryAttempt = 0;
            }
            else if (!_host.IsSessionBusy(e.SessionId))
            {
                e.RetryAttempt = 0;
                FlushDeferred(e);
            }
            else if (_host.ActiveTopLevelRun(e.SessionId) is not null)
            {
                // Run 在跑:把等着的输入直接塞进它的引导队列。
                e.RetryAttempt = 0;
                var list = e.Deferred.ToList();
                e.Deferred.Clear();
                foreach (var item in list)
                {
                    DeliverLocked(e, item);
                }
            }
            else if (++e.RetryAttempt >= _options.DeferredMaxRetries)
            {
                // 一个永远不释放的互斥不该让定时器永远转下去。
                _log.Warn($"Gave up delivering internal input to session {e.SessionId}.");
                giveUp = e.Deferred.ToList();
                e.Deferred.Clear();
                e.RetryAttempt = 0;
            }
            else
            {
                ScheduleRetry(e);
            }

            EvictIfIdle(e);
        }

        if (giveUp is not null)
        {
            foreach (var item in giveUp)
            {
                Done(item, false);
            }
        }
    }

    private void ClearRetry(Entry e)
    {
        e.Retry?.Dispose();
        e.Retry = null;
    }

    // ───────────────────────── 档位记忆 ─────────────────────────

    internal void RememberOptions(string sessionId, SendOptions options)
    {
        lock (_gate)
        {
            _lastOptions.Remove(sessionId);
            _lastOptions.Add(sessionId, options);
            while (_lastOptions.Count > _options.LastOptionsMax)
            {
                _lastOptions.RemoveAt(0);
            }
        }
    }

    private SendOptions? LastOptions(string sessionId)
    {
        lock (_gate)
        {
            return _lastOptions.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>会话删除:丢掉内存里的排队状态(存档由会话删除一并清掉),没送达的内部输入退回(done(false))。</summary>
    public void Forget(string sessionId)
    {
        Entry? e;
        lock (_gate)
        {
            _entries.Remove(sessionId, out e);
            _lastOptions.Remove(sessionId);
        }

        if (e is null)
        {
            return;
        }

        List<InternalInput> pending;
        lock (e.Gate)
        {
            e.Evicted = true;
            ClearRetry(e);
            pending = [.. e.Deferred, .. e.Unconfirmed.Values.SelectMany(m => m.Values)];
            e.Deferred.Clear();
            e.Unconfirmed.Clear();
        }

        foreach (var input in pending)
        {
            Done(input, false);
        }
    }

    // ───────────────────────── 收尾 ─────────────────────────

    /// <summary>退出前:把还 dirty 的条目写掉,而不是丢掉。</summary>
    public void Flush()
    {
        Entry[] all;
        lock (_gate)
        {
            all = [.. _entries.Values];
        }

        foreach (var e in all)
        {
            lock (e.Gate)
            {
                if (!e.Dirty || e.Evicted)
                {
                    continue;
                }

                try
                {
                    PersistLocked(e);
                }
                catch (QueuePersistenceException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                e.Retry?.Dispose();
                e.Retry = null;
            }
        }
    }
}
