namespace Ncw.Agent.Kernel.Subagents;

/// <summary>排不进去时的理由。每一种在启动器里都会被翻成一句给模型看的人话(<see cref="SlotRefusals"/>)。</summary>
public enum SlotRefusal
{
    /// <summary>全局上限是 0:「完全禁用子代理」(合法配置)。</summary>
    Disabled,

    /// <summary>占着名额的 Run 自己全都在队列里阻塞着,没有人能腾出位子。</summary>
    Deadlock,

    /// <summary>父 Run 被中止或已经结束(两条路径不同,都要取消)。</summary>
    Cancelled,

    /// <summary>队列长度上限(防模型一口气派上百个)。</summary>
    Overflow,
}

/// <summary>并发上限。<see cref="PerSessionLimit"/> 是一个父 Run 名下的上限(≥ 1);<see cref="GlobalLimit"/> 是全应用的上限,<b>0 是合法值</b>,意为「完全禁用子代理」。</summary>
public readonly record struct SlotCapacity(int PerSessionLimit, int GlobalLimit)
{
    public const int DefaultLimit = 4;

    /// <summary>
    /// 设置文件是用户可编辑输入,所以对异常值做防御性归一化:每父上限 &lt; 1 → 默认 4;全局上限 &lt; 0 → 默认 4(<b>0 保留</b>,合法下界是 0 不是 1)。
    /// 每次调度都现读一次:用户在运行期间调高上限,下一次派发(以及队列的下一个 tick)就该生效。
    /// </summary>
    public static SlotCapacity Normalize(int perSession, int global) =>
        new(perSession >= 1 ? perSession : DefaultLimit, global >= 0 ? global : DefaultLimit);
}

/// <summary>
/// 「现在有谁在占着名额」。要的是 <b>id 列表</b>而不是计数:死锁判据要拿这些 id 去和「正被队列阻塞的 Run」求交集,只有计数是判不出来的。
/// 数的是<b>仍在运行</b>的(不是 <c>RunHandle.Children</c> 那个只增不减的累计集合)。
/// </summary>
public interface ISlotOccupancy
{
    IReadOnlyList<string> ChildRunIds(string parentRunId);

    IReadOnlyList<string> SubagentRunIds();
}

/// <summary>
/// 父 Run 的中断源。只用这几样,所以测试不必造一个完整的 <c>RunHandle</c>。
/// 等待者在父 Run <b>被中止</b>和<b>结束</b>两条路径上都要取消——它们在 <c>RunHandle</c> 上是完全不同的两条路(中止只触发取消令牌、不改状态;正常结束发 <c>run_end</c>、不碰令牌),
/// 只挂一个的话,漏掉的那一条上的等待者会永久留在队列里(2.x ★)。
/// </summary>
public interface IParentSignal
{
    string RunId { get; }

    /// <summary>仍在运行、且没被要求中止。</summary>
    bool IsActive { get; }

    CancellationToken Token { get; }

    /// <summary>父 Run 结束(<c>run_end</c>)时回调一次;已经结束的父 Run 不会调到这里(调用方在 <see cref="IsActive"/> 为假时根本不入队)。</summary>
    IDisposable OnEnded(Action callback);
}

public abstract record SlotResult<T>
{
    private SlotResult()
    {
    }

    public sealed record Granted(T Value) : SlotResult<T>;

    public sealed record Refused(SlotRefusal Reason) : SlotResult<T>;
}

/// <param name="Parent">父 Run。</param>
/// <param name="Blocking">
/// 等待期间<b>这个父 Run 自己是不是停住了</b>。前台派发 true,后台派发 false。死锁判据只认 blocking 的等待者:后台派发立刻返回、父 Run 继续往下做、随时会结束并腾出名额——
/// 把它也算成「卡住了」的话,一个连发 5 个后台任务的父代理会被误判成死锁,当场拒掉一次本来排一会儿就能跑的派发。
/// </param>
/// <param name="Start">
/// ★ <b>拿到名额的瞬间在派发循环里内联执行</b>(队列的锁内)。这是整个队列避开 TOCTOU 的唯一手段:如果改成「完成等待者的任务、让等待者自己去建 Run」,
/// 那次建 Run 会发生在<b>另一个线程</b>上——而派发循环在同一轮内继续往下走,于是同一个空位会被唤醒的 N 个等待者同时认领。让 <c>Start</c> 在循环内同步跑完(Run 一登记,占用量就是新的了),
/// 下一轮判断读到的占用量才是准的。所以它必须是同步的、必须快。
/// </param>
/// <param name="OnQueued">真的排上队时回调一次,之后每次排位变化再回调(前面还有几个)。<b>没排队(直接拿到名额)就不会调</b>——启动器靠这个同步信号区分「已启动」和「已排队」。</param>
public sealed record SlotRequest<T>(IParentSignal Parent, bool Blocking, Func<T> Start, Action<int>? OnQueued = null);

/// <summary>
/// 子代理并发队列(2.x <c>kernel/subagent-queue.ts</c>)——撞上上限时<b>排队</b>,而不是把一次调度问题报成失败。
/// <para>
/// 为什么不是「返回拒绝让模型自己重试」:模型拿到失败之后的行为是不可控的——可能放弃这个子任务、可能换一个 <c>subagent_type</c> 再试(以为是那个代理坏了)、也可能自己硬做,<b>这次派发的意图直接丢了</b>。
/// 而用户配「同时最多 4 个」的本意是「同时最多<b>跑</b> 4 个」,不是「最多<b>派</b> 4 个」。
/// </para>
/// <para>
/// 线程安全:一把(可重入的)锁。占用量与上限都是注入进来的,所以本类零 Run 注册表依赖、能脱离整套编排单测。锁序固定为「队列锁 → 注册表锁」;
/// 所有回调(排位通知)都在锁外调用,等待者的完成走 <c>RunContinuationsAsynchronously</c>,不会在锁内跑调用方的延续。
/// </para>
/// 排队没有墙钟上限:子代理一跑半小时是常事,排在它后面的就得等。不会永久挂住靠的是别的出口:父 Run 中止 / 结束即取消、死锁当场拒绝、队列溢出直接拒绝。
/// </summary>
public sealed class SubagentQueue : IDisposable
{
    /// <summary>队列非空时的兜底重扫周期。有了它,正确性就<b>不依赖穷举每一条名额释放路径</b>:Run 被中止但从未结束、启动抛了异常、某个 Run 被整个遗弃,全都由下一次重扫收敛。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>每个父 Run 排队上限。</summary>
    public const int MaxQueuedPerParent = 16;

    /// <summary>全局排队上限。</summary>
    public const int MaxQueuedTotal = 64;

    private sealed class Waiter
    {
        public required string ParentRunId { get; init; }

        public required IParentSignal Parent { get; init; }

        public required bool Blocking { get; init; }

        public required Func<object?> Start { get; init; }

        public Action<int>? OnQueued { get; init; }

        /// <summary>完成:<c>true</c> = 拿到名额(<see cref="Value"/> 是 Start 的返回);<c>false</c> = 被拒(<see cref="Refusal"/>);异常走 <see cref="Fail"/>。</summary>
        public required Action<object?, SlotRefusal?, Exception?> Complete { get; init; }

        /// <summary>上一次报给调用方的排位,用来抑制重复的进度通知。</summary>
        public int Reported { get; set; } = -1;

        public CancellationTokenRegistration AbortRegistration { get; set; }

        public IDisposable? EndRegistration { get; set; }

        public void Cleanup()
        {
            AbortRegistration.Dispose();
            EndRegistration?.Dispose();
        }
    }

    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiters = [];

    // parentRunId → 该 Run 名下「卡住它自己」的等待者数(只数前台)。死锁判据要用。
    private readonly Dictionary<string, int> _blocked = new(StringComparer.Ordinal);

    // parentRunId → 该 Run 名下排队中的等待者总数(前台 + 后台)。公平性与溢出上限要用。
    private readonly Dictionary<string, int> _queuedPerParent = new(StringComparer.Ordinal);
    private readonly Func<SlotCapacity> _capacity;
    private readonly ISlotOccupancy _occupancy;
    private readonly TimeProvider _clock;
    private ITimer? _tick;
    private bool _disposed;

    public SubagentQueue(Func<SlotCapacity> capacity, ISlotOccupancy occupancy, TimeProvider? clock = null)
    {
        _capacity = capacity;
        _occupancy = occupancy;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>当前排队人数(日志与测试用)。</summary>
    public int Size
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>这个 Run 是不是正卡在队列里等名额(死锁判据要用)。</summary>
    public bool IsBlocked(string runId)
    {
        lock (_gate)
        {
            return IsBlockedLocked(runId);
        }
    }

    /// <summary>
    /// 要一个名额。★ <b>同步不变式</b>:本方法在返回之前就把「直接启动 / 入队 / 立刻拒绝」这个决定做完了。所以调用方可以在 <c>Acquire(...)</c> 返回之后<b>立即</b>读取 <c>OnQueued</c> 有没有被调过,
    /// 据此判断这次派发是「已启动」还是「已排队」——后台派发要立刻回话给模型,靠的就是这条。别在决定之前插入任何 await。
    /// </summary>
    public Task<SlotResult<T>> Acquire<T>(SlotRequest<T> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<Action> after = [];
        Task<SlotResult<T>> task;
        lock (_gate)
        {
            task = AcquireLocked(request, after);
        }

        RunAll(after);
        return task;
    }

    private Task<SlotResult<T>> AcquireLocked<T>(SlotRequest<T> request, List<Action> after)
    {
        var parent = request.Parent;
        var parentRunId = parent.RunId;

        // 父 Run 已经没了就别排了——排上也没人会去消费结果。
        if (_disposed || !parent.IsActive || parent.Token.IsCancellationRequested)
        {
            return Task.FromResult<SlotResult<T>>(new SlotResult<T>.Refused(SlotRefusal.Cancelled));
        }

        var capacity = _capacity();

        // GlobalLimit: 0 是合法配置,意为「完全禁用子代理」。它永远不会有空位,入队就是永久挂起——必须在这里短路掉。
        if (capacity.GlobalLimit <= 0)
        {
            return Task.FromResult<SlotResult<T>>(new SlotResult<T>.Refused(SlotRefusal.Disabled));
        }

        // 快路径:有位置就地启动。只在「本 Run 没有别的等待者」时才走——否则同一个父 Run 的后来者会插队,把先到的那个饿死。
        // 别的父 Run 的等待者不必让:Pump 刚刚跑过,它们要是能放下,早就已经被启动了。
        if (HasRoom(parentRunId, capacity) && !_queuedPerParent.ContainsKey(parentRunId))
        {
            try
            {
                return Task.FromResult<SlotResult<T>>(new SlotResult<T>.Granted(request.Start()));
            }
            catch (Exception e)
            {
                return Task.FromException<SlotResult<T>>(e);
            }
        }

        // 判死锁之前先把自己算进去(self):globalLimit = 2,子代理 A、B 都在跑,B 已经卡在队列里等孙代理,现在 A 也要派孙代理。
        // 先判后入队的话,救兵集合里还剩一个 A——判成「再等等」,可 A 正是提问的那个人,它一挂就真的没人能动了。只有前台派发算数。
        if (DeadlockReason(parentRunId, capacity, request.Blocking) is { } deadlock)
        {
            return Task.FromResult<SlotResult<T>>(new SlotResult<T>.Refused(deadlock));
        }

        if (_queuedPerParent.GetValueOrDefault(parentRunId) >= MaxQueuedPerParent || _waiters.Count >= MaxQueuedTotal)
        {
            return Task.FromResult<SlotResult<T>>(new SlotResult<T>.Refused(SlotRefusal.Overflow));
        }

        return Enqueue(request, after);
    }

    /// <summary>有子 Run 结束了——立刻重扫一遍,把延迟从「最多一个 tick」降到 0。</summary>
    public void Notify()
    {
        List<Action> after = [];
        lock (_gate)
        {
            PumpLocked(after);
        }

        RunAll(after);
    }

    /// <summary>清空:所有等待者以取消收口,停掉定时器。</summary>
    public void Reset()
    {
        List<Waiter> pending;
        lock (_gate)
        {
            pending = [.. _waiters];
            _waiters.Clear();
            _blocked.Clear();
            _queuedPerParent.Clear();
            StopTick();
        }

        foreach (var waiter in pending)
        {
            waiter.Cleanup();
            waiter.Complete(null, SlotRefusal.Cancelled, null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Reset();
    }

    // ───────────────────────── 内部 ─────────────────────────

    private bool IsBlockedLocked(string runId) => _blocked.GetValueOrDefault(runId) > 0;

    // 两个名额一起判、一起拿,中间没有任何 await:不存在「先攥住一半再去等另一半」的部分持有状态,也就不存在环。
    private bool HasRoom(string parentRunId, SlotCapacity capacity) =>
        _occupancy.ChildRunIds(parentRunId).Count < capacity.PerSessionLimit
        && _occupancy.SubagentRunIds().Count < capacity.GlobalLimit;

    /// <summary>
    /// 死锁判据:<b>关着的那道闸门背后,是不是每一个占位者自己也卡在这个队列里</b>。<c>MaxDepth = 2</c>,子代理还能再派孙代理。全局池满、而池子里那 N 个子代理恰好全都在等自己的孙代理时,
    /// 没有任何人能跑完——谁也腾不出空位。这时候入队就是永久挂起,必须当场拒绝。
    /// </summary>
    private SlotRefusal? DeadlockReason(string parentRunId, SlotCapacity capacity, bool self)
    {
        bool Stuck(string id) => (self && id == parentRunId) || IsBlockedLocked(id);

        var global = _occupancy.SubagentRunIds();
        if (global.Count >= capacity.GlobalLimit && global.Count > 0 && global.All(Stuck))
        {
            return SlotRefusal.Deadlock;
        }

        var mine = _occupancy.ChildRunIds(parentRunId);
        if (mine.Count >= capacity.PerSessionLimit && mine.Count > 0 && mine.All(Stuck))
        {
            return SlotRefusal.Deadlock;
        }

        return null;
    }

    private Task<SlotResult<T>> Enqueue<T>(SlotRequest<T> request, List<Action> after)
    {
        var parentRunId = request.Parent.RunId;
        var completion = new TaskCompletionSource<SlotResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Waiter? waiter = null;
        void Cancel()
        {
            lock (_gate)
            {
                if (waiter is null || !RemoveLocked(waiter))
                {
                    return;
                }
            }

            waiter.Cleanup();
            waiter.Complete(null, SlotRefusal.Cancelled, null);
        }

        waiter = new Waiter
        {
            ParentRunId = parentRunId,
            Parent = request.Parent,
            Blocking = request.Blocking,
            Start = () => request.Start(),
            OnQueued = request.OnQueued,
            Complete = (value, refusal, error) =>
            {
                if (error is not null)
                {
                    completion.TrySetException(error);
                }
                else if (refusal is { } reason)
                {
                    completion.TrySetResult(new SlotResult<T>.Refused(reason));
                }
                else
                {
                    completion.TrySetResult(new SlotResult<T>.Granted((T)value!));
                }
            },
        };

        _waiters.Add(waiter);
        _queuedPerParent[parentRunId] = _queuedPerParent.GetValueOrDefault(parentRunId) + 1;
        if (waiter.Blocking)
        {
            _blocked[parentRunId] = _blocked.GetValueOrDefault(parentRunId) + 1;
        }

        // 中止和结束两个都要挂,缺一不可(见 IParentSignal)。
        waiter.AbortRegistration = request.Parent.Token.Register(Cancel);
        waiter.EndRegistration = request.Parent.OnEnded(Cancel);
        StartTick();
        ReportPositions(after);
        return completion.Task;
    }

    /// <summary>从队列里摘掉,并把计数减回去。返回它是否还在队列里。</summary>
    private bool RemoveLocked(Waiter waiter)
    {
        if (!_waiters.Remove(waiter))
        {
            return false;
        }

        Decrement(_queuedPerParent, waiter.ParentRunId);
        if (waiter.Blocking)
        {
            Decrement(_blocked, waiter.ParentRunId);
        }

        if (_waiters.Count == 0)
        {
            StopTick();
        }

        return true;
    }

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        var left = counts.GetValueOrDefault(key, 1) - 1;
        if (left <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = left;
        }
    }

    /// <summary>
    /// 派发一轮。扫的是整条队列而不是只看队头——队头因为<b>它自己那个会话</b>满了而排着的时候,不该顺带挡住另一个会话里明明放得下的请求。
    /// 每一轮现读一次上限:用户在运行期间调高并发,下一个 tick 就生效。
    /// </summary>
    private void PumpLocked(List<Action> after)
    {
        if (_waiters.Count == 0)
        {
            StopTick();
            return;
        }

        var capacity = _capacity();
        var started = false;
        for (var i = 0; i < _waiters.Count;)
        {
            var waiter = _waiters[i];

            // 父 Run 已经被要求中止(取消回调还没轮到我们):不再为它起新的子 Run——否则级联中止早已遍历完子 Run 集合,这个新子代理会成为孤儿。
            if (!waiter.Parent.IsActive || waiter.Parent.Token.IsCancellationRequested)
            {
                RemoveLocked(waiter);
                after.Add(waiter.Cleanup);
                waiter.Complete(null, SlotRefusal.Cancelled, null);
                continue;
            }

            if (!HasRoom(waiter.ParentRunId, capacity))
            {
                // 死锁每一轮都要重判,不能只在入队时判一次:它会后来才形成——入队的那一刻还有一个空闲的救兵,三秒后那个救兵自己也去排队了。
                // (self: false:前台等待者自己早已在 _blocked 里。)
                if (DeadlockReason(waiter.ParentRunId, capacity, self: false) is not { } deadlock)
                {
                    i++;
                    continue;
                }

                RemoveLocked(waiter);
                after.Add(waiter.Cleanup);
                waiter.Complete(null, deadlock, null);
                continue;
            }

            RemoveLocked(waiter);
            // ★ 摘掉取消登记要在锁外做:CancellationTokenRegistration.Dispose 会等正在执行的回调跑完,而那个回调(Cancel)正等着拿这把锁——锁内 Dispose 就是死锁。
            after.Add(waiter.Cleanup);
            started = true;
            try
            {
                // 同步执行,见 SlotRequest.Start 的说明。
                waiter.Complete(waiter.Start(), null, null);
            }
            catch (Exception e)
            {
                waiter.Complete(null, null, e);
            }

            // 不推进 i:刚摘掉一个,当前下标已经是下一个等待者了。
        }

        if (started)
        {
            ReportPositions(after);
        }
    }

    /// <summary>把「前面还有几个」报给每个还在排的等待者,排位没变的不重复报。回调攒起来在锁外调。</summary>
    private void ReportPositions(List<Action> after)
    {
        for (var index = 0; index < _waiters.Count; index++)
        {
            var waiter = _waiters[index];
            if (waiter.Reported == index)
            {
                continue;
            }

            waiter.Reported = index;
            if (waiter.OnQueued is { } callback)
            {
                var ahead = index;
                after.Add(() => callback(ahead));
            }
        }
    }

    private void StartTick() => _tick ??= _clock.CreateTimer(static state => ((SubagentQueue)state!).Notify(), this, TickInterval, TickInterval);

    private void StopTick()
    {
        _tick?.Dispose();
        _tick = null;
    }

    private static void RunAll(List<Action> actions)
    {
        foreach (var action in actions)
        {
            action();
        }
    }
}
