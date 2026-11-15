using System.Buffers;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

/// <summary>
/// 设备上的终端会话表(2.x <c>agent/server/terminals.ts</c> 的 <c>TerminalManager</c> 与 <c>main/terminal-host.ts</c> 的 <c>TerminalHost</c> 合并后的逻辑层,06 §9)。
/// 本机和 SSH 环境走同一个接口:只依赖 <see cref="IExecutionEnvironment.OpenTerminalAsync"/> 返回的 <see cref="ITerminal"/>。
/// <list type="bullet">
/// <item><b>终端属于设备,不属于某条连接</b>:控制端断开、切走都不会杀终端;回来用 <see cref="Attach"/> 取回缓冲并续订输出(游标续传)。</item>
/// <item><b>输出合批</b>(16ms / 64 KiB),每批一个递增的 <c>Seq</c>;环形缓冲只留最近 256 KiB(超出标 <c>Truncated</c>)。
/// 退出事件也占一个序号,所以订阅者看到的序号是连续的,有缺口就是丢了。</item>
/// <item><b>状态单调</b>:<see cref="TerminalStatus.Exited"/> 是终态,之后的输出和第二个退出事件一律丢弃。</item>
/// <item><b>限额</b>:每作用域 8、总共 32(正在创建的也算);已退出的终端保留 10 分钟供回看,之后回收,没有退出的永不回收。</item>
/// <item><b>远程环境逐个批准</b>(2.x 的 <c>prepare / approve</c>):SSH 环境里开终端等于在别人的机器上拿到一个 shell,
/// 必须先 <see cref="PrepareAsync"/> 拿到带环境标识的意图,由发起它的控制端 <see cref="Approve"/>,凭得到的凭据 <see cref="CreateAsync"/>;
/// 凭据一次性、绑定控制端 / 终端 id / 作用域 / 环境实例 / 目录,60 秒过期;远程终端只有创建它的控制端能看和写,控制端离开(<see cref="ReleaseController"/>)就杀掉它。
/// 本机环境不需要批准。这个握手是「用户在界面上点开一个终端」的意图,不是模型的一次工具调用,所以不走 Run 作用域的 <c>InteractionGate</c>。</item>
/// <item><b>权限</b>:本层不认识身份和租约。<c>Write / Resize / Kill</c> 由调用方(Host)在租约闸之后调用;控制端标识只用于远程终端的归属。</item>
/// </list>
/// 线程安全。订阅回调(<see cref="Attach"/> 的 <c>sink</c>)在会话的锁内同步调用,以保证顺序和「快照 + 订阅」的原子性:回调里不要阻塞,不要调用本类的其它方法。
/// </summary>
public sealed class TerminalManager : IAsyncDisposable
{
    private readonly TerminalManagerOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCreation> _creating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Preparation> _preparing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaunchSpecEntry> _launchSpecs = new(StringComparer.Ordinal);

    public TerminalManager(TerminalManagerOptions? options = null)
    {
        _options = options ?? new TerminalManagerOptions();
        _time = _options.Time;
        _newId = _options.NewId ?? (() => "term-" + Guid.NewGuid().ToString("N"));
    }

    // ───────────────────────── 创建 ─────────────────────────

    /// <summary>
    /// 创建前的握手。本机环境:直接创建,返回 <see cref="TerminalPreparation.Ready"/>。
    /// 远程环境:同一个 id 的活终端(同一个控制端)直接 Ready,否则返回带环境标识的 <see cref="TerminalPreparation.ApprovalRequired"/> 意图——此时什么都没有启动。
    /// </summary>
    public async Task<TerminalPreparation> PrepareAsync(TerminalCreateRequest request, string controllerId, CancellationToken ct = default)
    {
        Validate(request, controllerId);
        var environment = request.Environment;
        AssertReady(environment);
        if (environment.Kind == EnvironmentKind.Local)
        {
            return new TerminalPreparation.Ready(await CreateAsync(request, controllerId, ct).ConfigureAwait(false));
        }

        var id = request.Id ?? _newId();
        Session? existing;
        lock (_gate)
        {
            existing = _sessions.GetValueOrDefault(id);
        }

        if (existing is { Info.Alive: true })
        {
            CheckSession(existing, request, controllerId);
            return new TerminalPreparation.Ready(existing.Info);
        }

        if (existing is not null)
        {
            CheckOwner(existing, controllerId);
        }

        var preparation = new Preparation(controllerId, new CancellationTokenSource());
        lock (_gate)
        {
            if (_preparing.TryGetValue(id, out var previous))
            {
                if (previous.Owner != controllerId)
                {
                    throw new TerminalException(TerminalErrorCode.ApprovalRequired);
                }

                previous.Cts.Cancel();
            }

            _preparing[id] = preparation;
        }

        string cwd;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preparation.Cts.Token);
            cwd = (await ResolveCwdAsync(request, linked.Token).ConfigureAwait(false)).Value;
            if (preparation.Cts.IsCancellationRequested)
            {
                throw new TerminalException(TerminalErrorCode.Cancelled);
            }
        }
        catch (OperationCanceledException) when (preparation.Cts.IsCancellationRequested)
        {
            throw new TerminalException(TerminalErrorCode.Cancelled);
        }
        finally
        {
            lock (_gate)
            {
                if (_preparing.TryGetValue(id, out var current) && ReferenceEquals(current, preparation))
                {
                    _preparing.Remove(id);
                }
            }
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var (intentId, pending) in _intents.ToList())
            {
                if (pending.Info.ExpiresAt <= now)
                {
                    _intents.Remove(intentId);
                }
                else if (pending.Info.TerminalId == id)
                {
                    if (pending.Owner != controllerId)
                    {
                        throw new TerminalException(TerminalErrorCode.ApprovalRequired);
                    }

                    _intents.Remove(intentId);
                }
            }

            if (_intents.Count >= _options.MaxIntents)
            {
                throw new TerminalException(TerminalErrorCode.Conflict, "too many pending approvals");
            }

            var info = new TerminalIntent(
                Guid.NewGuid().ToString("N"),
                id,
                request.ScopeId,
                TerminalEnvironmentRef.From(environment),
                cwd,
                environment.Facts.Shell,
                now + _options.IntentTtl);
            _intents[info.Id] = new Intent(info, controllerId, environment, environment.Generation);
            return new TerminalPreparation.ApprovalRequired(info);
        }
    }

    /// <summary>
    /// 回答一次待批准的意图。只有发起它的控制端能回答(别人得到 <see cref="TerminalErrorCode.ApprovalExpired"/>,不泄漏意图是否存在)。
    /// 拒绝 = 丢弃意图,返回 null;批准 = 返回一次性凭据,放进 <see cref="TerminalCreateRequest.Approval"/>。
    /// </summary>
    public string? Approve(string intentId, bool approved, string controllerId)
    {
        lock (_gate)
        {
            if (!_intents.TryGetValue(intentId, out var intent) || intent.Owner != controllerId)
            {
                throw new TerminalException(TerminalErrorCode.ApprovalExpired);
            }

            if (!approved)
            {
                _intents.Remove(intentId);
                return null;
            }

            if (intent.Info.ExpiresAt <= _time.GetUtcNow())
            {
                _intents.Remove(intentId);
                throw new TerminalException(TerminalErrorCode.ApprovalExpired);
            }

            AssertReady(intent.Environment);
            return intent.Grant ??= Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// 创建终端。同一个 id 并发创建、参数完全一致的合并成一次;已有活终端(同作用域、同环境)直接返回,控制端重新挂载后借此重连。
    /// 远程环境必须带 <see cref="TerminalCreateRequest.Approval"/>。创建中途被取消(<see cref="KillAsync"/>、<see cref="ReleaseController"/>、<see cref="ShutdownAsync"/>)时,
    /// 迟到才开出来的 PTY 会被立刻杀掉,不留孤儿。
    /// </summary>
    public Task<TerminalInfo> CreateAsync(TerminalCreateRequest request, string controllerId, CancellationToken ct = default)
    {
        Validate(request, controllerId);
        var id = request.Id ?? _newId();
        request = request with { Id = id };
        PendingCreation pending;
        lock (_gate)
        {
            if (_creating.TryGetValue(id, out var running))
            {
                if (running.Owner != controllerId || running.Request.ScopeId != request.ScopeId || running.Request.Approval != request.Approval
                    || running.Request.Cwd != request.Cwd || running.Request.Environment.Id != request.Environment.Id)
                {
                    return Task.FromException<TerminalInfo>(new TerminalException(TerminalErrorCode.ApprovalRequired));
                }

                return running.Completion.Task;
            }

            pending = new PendingCreation(request, controllerId, new CancellationTokenSource(), new TaskCompletionSource<TerminalInfo>(TaskCreationOptions.RunContinuationsAsynchronously));
            _creating[id] = pending;
        }

        _ = RunCreationAsync(pending, ct);
        return pending.Completion.Task;
    }

    private async Task RunCreationAsync(PendingCreation pending, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pending.Cts.Token);
            var info = await CreateCoreAsync(pending, linked.Token).ConfigureAwait(false);
            pending.Completion.TrySetResult(info);
        }
        catch (OperationCanceledException) when (pending.Cts.IsCancellationRequested)
        {
            pending.Completion.TrySetException(new TerminalException(TerminalErrorCode.Cancelled));
        }
        catch (Exception e)
        {
            pending.Completion.TrySetException(e);
        }
        finally
        {
            lock (_gate)
            {
                if (_creating.TryGetValue(pending.Request.Id!, out var current) && ReferenceEquals(current, pending))
                {
                    _creating.Remove(pending.Request.Id!);
                }
            }

            pending.Cts.Dispose();
        }
    }

    private async Task<TerminalInfo> CreateCoreAsync(PendingCreation pending, CancellationToken ct)
    {
        var request = pending.Request;
        var id = request.Id!;
        var environment = request.Environment;
        var controllerId = pending.Owner;
        Reap();

        Session? existing;
        lock (_gate)
        {
            existing = _sessions.GetValueOrDefault(id);
        }

        AssertReady(environment);
        if (existing is not null)
        {
            CheckOwner(existing, controllerId);
            if (existing.Info.Alive)
            {
                CheckSession(existing, request, controllerId);
                return existing.Info;
            }
        }

        lock (_gate)
        {
            var running = _sessions.Values.Count(static s => s.Info.Alive) + _creating.Count - 1;
            var inScope = _sessions.Values.Count(s => s.Info.Alive && s.Info.ScopeId == request.ScopeId)
                + _creating.Values.Count(c => c.Request.ScopeId == request.ScopeId && !ReferenceEquals(c, pending));
            if (running >= _options.MaxTotal || inScope >= _options.MaxPerScope)
            {
                throw new TerminalException(TerminalErrorCode.ResourceLimit, "too many open terminals");
            }
        }

        EnvPath cwd;
        if (environment.Kind != EnvironmentKind.Local)
        {
            cwd = ConsumeGrant(request, controllerId, environment);
        }
        else
        {
            cwd = await ResolveCwdAsync(request, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        // 启动规格只在本机消费:远程路径上环境变量注入不进 PTY,消费它只会造成「规格没了、启动也没发生」的双重丢失,
        // 而且 env 里可能带密钥——远程环境绝不写启动行(2.x ★)
        var spec = environment.Kind == EnvironmentKind.Local ? PeekLaunchSpec(id, request.ScopeId) : null;
        var columns = SafeSize(request.Columns, 100);
        var rows = SafeSize(request.Rows, 28);
        var terminal = await environment.OpenTerminalAsync(new TerminalSpec(cwd, columns, rows, spec?.Environment), ct).ConfigureAwait(false);
        Session session;
        try
        {
            if (ct.IsCancellationRequested)
            {
                throw new TerminalException(TerminalErrorCode.Cancelled);
            }

            AssertReady(environment);
            var shell = environment.Facts.Shell;
            var info = new TerminalInfo(
                id,
                request.ScopeId,
                TerminalEnvironmentRef.From(environment),
                cwd.FileName is { Length: > 0 } name ? name : ShellName(shell),
                cwd.Value,
                shell,
                columns,
                rows,
                TerminalStatus.Running,
                _time.GetUtcNow());
            session = new Session(info, terminal, environment, environment.Generation, controllerId, existing?.Log ?? new OutputLog(_options.BufferBytes));
            lock (_gate)
            {
                if (_creating.TryGetValue(id, out var current) && !ReferenceEquals(current, pending))
                {
                    throw new TerminalException(TerminalErrorCode.Cancelled);
                }

                if (existing is not null)
                {
                    // 同一个 id 重新开:订阅者(已经收到了上一段的退出事件)继续收新的一段
                    lock (existing.Gate)
                    {
                        session.Sinks = existing.Sinks;
                        existing.Sinks = [];
                        existing.Closed = true;
                    }

                    session.Log.ClearExit();
                }

                _sessions[id] = session;
            }
        }
        catch
        {
            // 迟到的 PTY:不留孤儿
            await DisposeQuietlyAsync(terminal).ConfigureAwait(false);
            throw;
        }

        StartSession(session);
        if (spec is not null)
        {
            DropLaunchSpec(id);
            var dialect = environment.Facts.ShellDialect;
            var line = LaunchLine.Build(spec.Argv, dialect) + LaunchLine.Enter(dialect);
            await terminal.Stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), CancellationToken.None).ConfigureAwait(false);
        }

        return session.Info;
    }

    private EnvPath ConsumeGrant(TerminalCreateRequest request, string controllerId, IExecutionEnvironment environment)
    {
        Intent intent;
        lock (_gate)
        {
            var found = request.Approval is null ? null : _intents.Values.FirstOrDefault(i => i.Grant == request.Approval);
            if (found is null)
            {
                throw new TerminalException(TerminalErrorCode.ApprovalRequired);
            }

            if (found.Owner != controllerId || found.Info.TerminalId != request.Id || found.Info.ScopeId != request.ScopeId
                || !ReferenceEquals(found.Environment, environment) || found.Generation != environment.Generation
                || (request.Cwd is not null && request.Cwd != found.Info.Cwd))
            {
                throw new TerminalException(TerminalErrorCode.ApprovalRequired);
            }

            if (found.Info.ExpiresAt <= _time.GetUtcNow())
            {
                _intents.Remove(found.Info.Id);
                throw new TerminalException(TerminalErrorCode.ApprovalExpired);
            }

            AssertReady(environment);
            _intents.Remove(found.Info.Id);
            intent = found;
        }

        return EnvPath.Parse(intent.Info.Cwd, environment.Facts.PathStyle);
    }

    // ───────────────────────── 查询与订阅 ─────────────────────────

    /// <summary>作用域下的终端。远程终端只列给创建它的控制端(<paramref name="controllerId"/> 为 null 时不过滤,给设备内部用)。</summary>
    public IReadOnlyList<TerminalInfo> List(string scopeId, string? controllerId = null)
    {
        Reap();
        lock (_gate)
        {
            return _sessions.Values
                .Where(s => s.Info.ScopeId == scopeId && (controllerId is null || s.Environment.Kind == EnvironmentKind.Local || s.Owner == controllerId))
                .Select(static s => s.Info)
                .OrderBy(static i => i.CreatedAt)
                .ToList();
        }
    }

    public TerminalInfo Get(string id, string? controllerId = null)
    {
        var session = Require(id);
        if (controllerId is not null)
        {
            CheckOwner(session, controllerId);
        }

        return session.Info;
    }

    /// <summary>环形缓冲里现存的输出(<c>getBuffer</c>)。会先把还在合批里的输出冲进缓冲,所以快照是完整的。</summary>
    public TerminalBuffer GetBuffer(string id, string? controllerId = null)
    {
        var session = Require(id);
        if (controllerId is not null)
        {
            CheckOwner(session, controllerId);
        }

        lock (session.Gate)
        {
            FlushLocked(session);
            return new TerminalBuffer(id, session.Log.Snapshot(), session.Log.Truncated, session.Log.Seq);
        }
    }

    /// <summary>
    /// 取回缓冲并订阅之后的事件。取快照和挂订阅在同一把锁里,中间插不进事件。
    /// <paramref name="sinceSeq"/>(控制端手里的最后一个序号)还在日志里 → 只补发错过的事件(<see cref="TerminalAttachment.Resumed"/>),否则给整个快照。
    /// <paramref name="sink"/> 在会话的锁内同步调用:不要阻塞,不要回调本类。释放返回值即取消订阅。
    /// </summary>
    public TerminalAttachment Attach(string id, Action<TerminalEvent> sink, long? sinceSeq = null, string? controllerId = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var session = Require(id);
        if (controllerId is not null)
        {
            CheckOwner(session, controllerId);
        }

        lock (session.Gate)
        {
            FlushLocked(session);
            session.Sinks = [.. session.Sinks, sink];
            var log = session.Log;
            var info = session.Info;
            if (sinceSeq is { } cursor && log.TryReplayAfter(cursor, out var replay))
            {
                return new TerminalAttachment(info, log.Seq, resumed: true, ReadOnlyMemory<byte>.Empty, log.Truncated, replay, () => Unsubscribe(session, sink));
            }

            return new TerminalAttachment(info, log.Seq, resumed: false, log.Snapshot(), log.Truncated, [], () => Unsubscribe(session, sink));
        }
    }

    private static void Unsubscribe(Session session, Action<TerminalEvent> sink)
    {
        lock (session.Gate)
        {
            session.Sinks = [.. session.Sinks.Where(s => s != sink)];
        }
    }

    // ───────────────────────── 输入、调整大小、终止 ─────────────────────────

    /// <summary>写键盘输入(原始字节)。已退出:<see cref="TerminalErrorCode.Conflict"/>;空或超过 <see cref="TerminalManagerOptions.InputMaxBytes"/>:拒绝。</summary>
    public async Task WriteAsync(string id, ReadOnlyMemory<byte> data, string? controllerId = null, CancellationToken ct = default)
    {
        var session = RequireAlive(id, controllerId);
        if (data.Length == 0)
        {
            throw new TerminalException(TerminalErrorCode.InvalidArgument, "empty input");
        }

        if (data.Length > _options.InputMaxBytes)
        {
            throw new TerminalException(TerminalErrorCode.TooLarge, "terminal input is too large");
        }

        session.Environment.EnsureGeneration(session.Generation);
        AssertReady(session.Environment);
        await session.Terminal.Stream.WriteAsync(data, ct).ConfigureAwait(false);
        await session.Terminal.Stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task WriteAsync(string id, string text, string? controllerId = null, CancellationToken ct = default) =>
        WriteAsync(id, System.Text.Encoding.UTF8.GetBytes(text), controllerId, ct);

    /// <summary>调整大小。越界的值保持原样(不报错,同 2.x 的 <c>safeSize</c>)。</summary>
    public async Task ResizeAsync(string id, int columns, int rows, string? controllerId = null, CancellationToken ct = default)
    {
        var session = RequireAlive(id, controllerId);
        session.Environment.EnsureGeneration(session.Generation);
        AssertReady(session.Environment);
        var info = session.Info;
        var nextColumns = SafeSize(columns, info.Columns);
        var nextRows = SafeSize(rows, info.Rows);
        await session.Terminal.ResizeAsync(nextColumns, nextRows, ct).ConfigureAwait(false);
        lock (session.Gate)
        {
            if (session.Info.Alive)
            {
                session.Info = session.Info with { Columns = nextColumns, Rows = nextRows };
            }
        }
    }

    /// <summary>
    /// 终止终端(整棵进程树)。会话留在表里,标为已退出、发退出事件,供回看。
    /// 终端还在创建或等待批准时:取消创建 / 丢弃意图(迟到的 PTY 被杀掉)。已退出:<see cref="TerminalErrorCode.Conflict"/>。
    /// </summary>
    public async Task KillAsync(string id, string? controllerId = null, CancellationToken ct = default)
    {
        if (CancelPending(id, controllerId))
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(id))
                {
                    return;
                }
            }
        }

        var session = RequireAlive(id, controllerId);
        await session.Terminal.KillAsync(ct).ConfigureAwait(false);
        await Task.WhenAny(session.ExitHandled.Task, Task.Delay(TimeSpan.FromSeconds(15), _time, ct)).ConfigureAwait(false);
    }

    /// <summary>关闭终端页签:活的先终止,然后从表里移除(不再保留回看)。不存在则什么都不做。</summary>
    public async Task CloseAsync(string id, string? controllerId = null, CancellationToken ct = default)
    {
        CancelPending(id, controllerId);
        Session? session;
        lock (_gate)
        {
            _sessions.TryGetValue(id, out session);
        }

        if (session is null)
        {
            return;
        }

        if (controllerId is not null)
        {
            CheckOwner(session, controllerId);
        }

        if (session.Info.Alive)
        {
            await session.Terminal.KillAsync(ct).ConfigureAwait(false);
            await Task.WhenAny(session.ExitHandled.Task, Task.Delay(TimeSpan.FromSeconds(15), _time, ct)).ConfigureAwait(false);
        }

        Remove(session);
    }

    /// <summary>项目被删除时,收掉它名下所有终端(含还在创建 / 等待批准的)。</summary>
    public async Task CloseScopeAsync(string scopeId)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _creating.Where(c => c.Value.Request.ScopeId == scopeId).Select(static c => c.Key), .. _sessions.Where(s => s.Value.Info.ScopeId == scopeId).Select(static s => s.Key)];
            foreach (var (intentId, intent) in _intents.Where(i => i.Value.Info.ScopeId == scopeId).ToList())
            {
                _intents.Remove(intentId);
            }
        }

        foreach (var id in ids.Distinct())
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 控制端离开(断开、被吊销):丢弃它名下待批准的意图、取消它正在进行的创建、杀掉它创建的远程终端(没有别人能操作它们)。
    /// 本机终端不受影响——终端属于设备。
    /// </summary>
    public async Task ReleaseController(string controllerId)
    {
        List<Session> owned;
        lock (_gate)
        {
            foreach (var pending in _creating.Values.Where(c => c.Owner == controllerId))
            {
                pending.Cts.Cancel();
            }

            foreach (var preparation in _preparing.Values.Where(p => p.Owner == controllerId))
            {
                preparation.Cts.Cancel();
            }

            foreach (var intentId in _intents.Where(i => i.Value.Owner == controllerId).Select(static i => i.Key).ToList())
            {
                _intents.Remove(intentId);
            }

            owned = [.. _sessions.Values.Where(s => s.Owner == controllerId && s.Environment.Kind != EnvironmentKind.Local)];
        }

        foreach (var session in owned)
        {
            await CloseAsync(session.Info.Id).ConfigureAwait(false);
        }
    }

    // ───────────────────────── 一次性启动规格 ─────────────────────────

    /// <summary>
    /// 备好插件终端(<c>tabs.openTerminal</c>)的一次性启动规格:之后用同一个 <paramref name="terminalId"/> 创建本机终端时,
    /// 环境变量注入进 PTY,启动行写进 shell。环境变量只活在这张表和 PTY 子进程的环境里——不进输出缓冲、不落盘。
    /// 一次性:写入启动行后立即删除;没被认领的由 TTL 兜底清理。
    /// </summary>
    public void SetLaunchSpec(string terminalId, TerminalLaunchSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(terminalId);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Argv.Count == 0)
        {
            throw new TerminalException(TerminalErrorCode.InvalidArgument, "launch argv");
        }

        lock (_gate)
        {
            _launchSpecs[terminalId] = new LaunchSpecEntry(spec, _time.GetUtcNow() + _options.LaunchSpecTtl);
        }
    }

    private TerminalLaunchSpec? PeekLaunchSpec(string id, string scopeId)
    {
        lock (_gate)
        {
            if (!_launchSpecs.TryGetValue(id, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt <= _time.GetUtcNow())
            {
                _launchSpecs.Remove(id);
                return null;
            }

            return entry.Spec.ScopeId == scopeId ? entry.Spec : null;
        }
    }

    private void DropLaunchSpec(string id)
    {
        lock (_gate)
        {
            _launchSpecs.Remove(id);
        }
    }

    // ───────────────────────── 停止 ─────────────────────────

    /// <summary>停止服务:取消所有创建、丢弃意图和启动规格、杀掉全部仍在跑的终端、清空会话表。之后本对象仍可继续使用(重新创建)。</summary>
    public async Task ShutdownAsync()
    {
        List<Session> sessions;
        lock (_gate)
        {
            foreach (var pending in _creating.Values)
            {
                pending.Cts.Cancel();
            }

            foreach (var preparation in _preparing.Values)
            {
                preparation.Cts.Cancel();
            }

            _intents.Clear();
            _launchSpecs.Clear();
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            lock (session.Gate)
            {
                session.Closed = true;
                session.Timer?.Dispose();
                session.Timer = null;
                session.Sinks = [];
            }
        }

        await Task.WhenAll(sessions.Select(static s => DisposeQuietlyAsync(s.Terminal))).ConfigureAwait(false);
        // 等创建中的任务收尾(它们会把迟到的 PTY 杀掉)
        Task[] creations;
        lock (_gate)
        {
            creations = [.. _creating.Values.Select(static c => c.Completion.Task)];
        }

        await Task.WhenAll(creations.Select(static t => t.ContinueWith(static _ => { }, TaskScheduler.Default))).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    // ───────────────────────── 会话:输出泵、合批、退出 ─────────────────────────

    private void StartSession(Session session)
    {
        session.Pump = Task.Run(() => PumpAsync(session));
        _ = Task.Run(() => WatchExitAsync(session));
    }

    private async Task PumpAsync(Session session)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            for (;;)
            {
                var read = await session.Terminal.Stream.ReadAsync(buffer, session.Stop.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                Append(session, buffer.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // 流结束:退出由 WatchExitAsync 处理
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Append(Session session, ReadOnlySpan<byte> data)
    {
        lock (session.Gate)
        {
            if (session.Closed || !session.Info.Alive)
            {
                return;
            }

            session.Pending.Write(data);
            if (session.Pending.WrittenCount >= _options.MaxChunkBytes)
            {
                FlushLocked(session);
            }
            else if (session.Timer is null)
            {
                session.Timer = _time.CreateTimer(static state => ((Callback)state!).Run(), new Callback(this, session), _options.FlushInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private sealed class Callback(TerminalManager manager, Session session)
    {
        public void Run()
        {
            lock (session.Gate)
            {
                manager.FlushLocked(session);
            }
        }
    }

    /// <summary>把合批里的输出写进日志并发给订阅者(单批不超过 <see cref="TerminalManagerOptions.MaxChunkBytes"/>)。调用方持有会话的锁。</summary>
    private void FlushLocked(Session session)
    {
        session.Timer?.Dispose();
        session.Timer = null;
        if (session.Pending.WrittenCount == 0)
        {
            return;
        }

        var pending = session.Pending.WrittenMemory;
        for (var offset = 0; offset < pending.Length; offset += _options.MaxChunkBytes)
        {
            var chunk = pending.Slice(offset, Math.Min(_options.MaxChunkBytes, pending.Length - offset)).ToArray();
            var seq = session.Log.Append(chunk);
            Emit(session, new TerminalOutput(seq, chunk));
        }

        session.Pending.ResetWrittenCount();
    }

    private static void Emit(Session session, TerminalEvent evt)
    {
        foreach (var sink in session.Sinks)
        {
            try
            {
                sink(evt);
            }
            catch (Exception)
            {
                // 单个订阅者失败不影响其他订阅者,也不影响终端
            }
        }
    }

    private async Task WatchExitAsync(Session session)
    {
        ProcessExit exit;
        try
        {
            exit = await session.Terminal.Exited.ConfigureAwait(false);
        }
        catch (Exception)
        {
            exit = new ProcessExit(null, null, ProcessExitReason.ConnectionLost);
        }

        // 让还在路上的输出先进缓冲:输出读到 EOF 之后才发退出事件(读不到 EOF 的话最多等 ExitDrainWait)
        if (session.Pump is { } pump)
        {
            await Task.WhenAny(pump, Task.Delay(_options.ExitDrainWait, _time)).ConfigureAwait(false);
        }

        lock (session.Gate)
        {
            if (!session.Closed && session.Info.Alive)
            {
                FlushLocked(session);
                var evt = session.Log.AppendExit(exit.ExitCode, exit.Signal, exit.Reason);
                session.Info = session.Info with
                {
                    Status = TerminalStatus.Exited,
                    ExitCode = exit.ExitCode,
                    Signal = exit.Signal,
                    ExitReason = exit.Reason,
                    ExitedAt = _time.GetUtcNow(),
                };
                Emit(session, evt);
            }
        }

        session.Stop.Cancel();
        session.ExitHandled.TrySetResult();
        await DisposeQuietlyAsync(session.Terminal).ConfigureAwait(false);
    }

    private static async Task DisposeQuietlyAsync(ITerminal terminal)
    {
        try
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放失败不影响调用方
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static void Validate(TerminalCreateRequest request, string controllerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(controllerId);
        ArgumentException.ThrowIfNullOrEmpty(request.ScopeId);
        ArgumentNullException.ThrowIfNull(request.Environment);
    }

    private static int SafeSize(int value, int fallback) => value > 0 ? Math.Clamp(value, 2, 10_000) : fallback;

    private static string ShellName(string shell)
    {
        var name = shell[(shell.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>环境已断开(失败或关闭)就拒绝;<see cref="EnvironmentState.Idle"/>(SSH 惰性连接)和重连中放行——打开终端时会等待连接。</summary>
    private static void AssertReady(IExecutionEnvironment environment)
    {
        if (environment.State is EnvironmentState.Failed or EnvironmentState.Closed)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Disconnected, environment.Id.ToString());
        }
    }

    private static async ValueTask<EnvPath> ResolveCwdAsync(TerminalCreateRequest request, CancellationToken ct)
    {
        var environment = request.Environment;
        await environment.EnsureReadyAsync(ct).ConfigureAwait(false);
        var cwd = await environment.Paths.ResolveWithinAsync(request.Root, request.Cwd ?? request.Root.Value, ct).ConfigureAwait(false);
        var stat = await environment.Fs.StatAsync(cwd, ct).ConfigureAwait(false);
        if (!stat.IsDirectory)
        {
            throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, cwd.Value);
        }

        AssertReady(environment);
        return cwd;
    }

    private Session Require(string id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var session) ? session : throw new TerminalException(TerminalErrorCode.NotFound);
        }
    }

    private Session RequireAlive(string id, string? controllerId)
    {
        var session = Require(id);
        if (controllerId is not null)
        {
            CheckOwner(session, controllerId);
        }

        return session.Info.Alive ? session : throw new TerminalException(TerminalErrorCode.Conflict, "terminal has exited");
    }

    /// <summary>远程终端只有创建它的控制端能碰。</summary>
    private static void CheckOwner(Session session, string controllerId)
    {
        if (session.Environment.Kind != EnvironmentKind.Local && session.Owner != controllerId)
        {
            throw new TerminalException(TerminalErrorCode.ApprovalRequired);
        }
    }

    private static void CheckSession(Session session, TerminalCreateRequest request, string controllerId)
    {
        CheckOwner(session, controllerId);
        if (session.Info.ScopeId != request.ScopeId || session.Info.Environment.Id != request.Environment.Id)
        {
            throw session.Environment.Kind == EnvironmentKind.Local
                ? new TerminalException(TerminalErrorCode.Conflict, "terminal id belongs to another scope")
                : new TerminalException(TerminalErrorCode.ApprovalRequired);
        }

        AssertReady(session.Environment);
    }

    /// <summary>取消正在进行的创建 / 准备、丢弃这个终端 id 的待批准意图。返回是否有什么被取消了。</summary>
    private bool CancelPending(string id, string? controllerId)
    {
        lock (_gate)
        {
            _creating.TryGetValue(id, out var pending);
            _preparing.TryGetValue(id, out var preparation);
            if (controllerId is not null && ((pending is not null && pending.Owner != controllerId) || (preparation is not null && preparation.Owner != controllerId)))
            {
                throw new TerminalException(TerminalErrorCode.ApprovalRequired);
            }

            var any = pending is not null || preparation is not null;
            pending?.Cts.Cancel();
            preparation?.Cts.Cancel();
            foreach (var intentId in _intents.Where(i => i.Value.Info.TerminalId == id && (controllerId is null || i.Value.Owner == controllerId)).Select(static i => i.Key).ToList())
            {
                _intents.Remove(intentId);
                any = true;
            }

            return any;
        }
    }

    private void Remove(Session session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Info.Id, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.Info.Id);
            }
        }

        lock (session.Gate)
        {
            session.Closed = true;
            session.Timer?.Dispose();
            session.Timer = null;
            session.Sinks = [];
        }
    }

    /// <summary>回收过期的已退出终端。没退出的永不回收。</summary>
    private void Reap()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (id, session) in _sessions.ToList())
            {
                var info = session.Info;
                if (!info.Alive && info.ExitedAt is { } at && now - at >= _options.ExitedRetention)
                {
                    _sessions.Remove(id);
                }
            }

            foreach (var (id, spec) in _launchSpecs.ToList())
            {
                if (spec.ExpiresAt <= now)
                {
                    _launchSpecs.Remove(id);
                }
            }
        }
    }

    private sealed class Session(TerminalInfo info, ITerminal terminal, IExecutionEnvironment environment, int generation, string owner, OutputLog log)
    {
        public readonly Lock Gate = new();
        private TerminalInfo _info = info;

        public TerminalInfo Info
        {
            get => Volatile.Read(ref _info);
            set => Volatile.Write(ref _info, value);
        }

        public ITerminal Terminal { get; } = terminal;

        public IExecutionEnvironment Environment { get; } = environment;

        public int Generation { get; } = generation;

        public string Owner { get; } = owner;

        public OutputLog Log { get; } = log;

        public ArrayBufferWriter<byte> Pending { get; } = new();

        public ITimer? Timer { get; set; }

        public Action<TerminalEvent>[] Sinks { get; set; } = [];

        public bool Closed { get; set; }

        public Task? Pump { get; set; }

        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource ExitHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingCreation(TerminalCreateRequest Request, string Owner, CancellationTokenSource Cts, TaskCompletionSource<TerminalInfo> Completion);

    private sealed record Preparation(string Owner, CancellationTokenSource Cts);

    private sealed class Intent(TerminalIntent info, string owner, IExecutionEnvironment environment, int generation)
    {
        public TerminalIntent Info { get; } = info;

        public string Owner { get; } = owner;

        public IExecutionEnvironment Environment { get; } = environment;

        public int Generation { get; } = generation;

        public string? Grant { get; set; }
    }

    private sealed record LaunchSpecEntry(TerminalLaunchSpec Spec, DateTimeOffset ExpiresAt);
}
