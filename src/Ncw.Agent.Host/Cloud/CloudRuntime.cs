using System.Security.Cryptography;
using Ncw.Agent.Cloud;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Cloud;

/// <summary>
/// 云端 Agent 的运行时(docs/design/02 A6):Host 里<b>唯一</b>把「编排服务」接到 Agent 内部状态的地方。
/// <list type="bullet">
/// <item><b>启动</b>:向编排服务打招呼(第一次 = 设备身份注册,编排服务回签名的身份绑定);处理积压的指令;之后才向中继注册(休眠期间到达的吊销在这之前就生效)。</item>
/// <item><b>指令</b>(<see cref="ICommandHandler"/>):信任记录下发 / 吊销 / 整体替换、配额、休眠、升级、令牌轮换。验签与防重放在 <see cref="CommandPump"/>。</item>
/// <item><b>配额</b>(<see cref="IRunGate"/>):Run 发起前检查 + 记账;收尾中拒绝新 Run。</item>
/// <item><b>空闲休眠</b>:周期检查(<see cref="IdlePolicy"/>),可休眠就走 <see cref="SleepAsync"/>:拒绝新活动 → 记下被打断的 Run → 停止对外服务并注销中继 →
/// 中止 Run / 杀后台 Shell / 关终端和浏览器 → 数据库检查点 → 最后一次用量上报 → 写休眠快照 → 通知编排服务 → 请求进程退出。</item>
/// <item><b>唤醒恢复</b>:读休眠快照,续跑被休眠打断的会话里排队的消息;信任记录、排队消息、目标(转录里的标记)、定时任务都在数据库里,不需要额外恢复。</item>
/// </list>
/// 编排服务不可用时 Agent 照常服务已有的控制端(带着数据库里的信任记录),后台退避重连。
/// </summary>
internal sealed class CloudRuntime : ICommandHandler, IRunGate, IDisposable
{
    private readonly CloudHostOptions _options;
    private readonly CloudConfig _config;
    private readonly DeviceIdentity _identity;
    private readonly RemoteService _remote;
    private readonly DelegatedAccount _account;
    private readonly TimeProvider _clock;
    private readonly Ncw.Agent.Kernel.Loop.ILoopDiagnostics _log;
    private readonly Func<Task> _quiesce;
    private readonly IdlePolicy _idle;
    private readonly string _bootId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private readonly TaskCompletionSource _dataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CloudExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _poll;
    private readonly List<ITimer> _timers = [];
    private readonly long _startedAt;

    private AgentDatabase? _db;
    private ConversationService? _conversation;
    private Services.DeviceServices? _services;
    private ICloudStore? _store;
    private QuotaService? _quota;
    private UsageReporter? _reporter;
    private CommandPump? _pump;
    private CommandVerifier? _verifier;
    private StorageMeter? _storage;
    private HealthEndpoint? _health;
    private Task _pollLoop = Task.CompletedTask;
    private SleepSnapshot? _snapshot;
    private long? _wokeAt;
    private string? _accountId;
    private long _lastActivity;
    private int _preparing;
    private int _ticking;
    private int _helloDone;
    private int _disposed;

    public CloudRuntime(
        CloudHostOptions options, DeviceIdentity identity, RemoteService remote, DelegatedAccount account, TimeProvider hostClock,
        Ncw.Agent.Kernel.Loop.ILoopDiagnostics log, Func<Task> quiesce)
    {
        _options = options;
        _config = options.Config;
        _identity = identity;
        _remote = remote;
        _account = account;
        _clock = options.Clock ?? hostClock;
        _log = log;
        _quiesce = quiesce;
        _idle = new IdlePolicy(_config.IdleAfter, _config.WakeLead, _config.MinSleep);
        _startedAt = Now;
        _lastActivity = _startedAt;
        _poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>进程要退出了(休眠 / 升级):<c>AgentHost.RunAsync</c> 等它。</summary>
    public Task<CloudExit> Exited => _exit.Task;

    public bool Preparing => Volatile.Read(ref _preparing) != 0;

    public bool Ready => _helloDone != 0 && !Preparing;

    // ───────────────────────── 装配 ─────────────────────────

    /// <summary>数据库就绪(Host 的 <c>Ready</c> 回调里调用):建配额、用量、指令泵,挂上 Run 闸门,读休眠快照。</summary>
    public void OnDataReady(AgentDatabase database, ConversationService conversation, Services.DeviceServices services)
    {
        _db = database;
        _conversation = conversation;
        _services = services;
        _store = new DbCloudStore(new KvStore(database));
        _storage = new StorageMeter([_config.DataRoot, _config.WorkspacesRoot], _clock);
        var usage = new CloudUsageSource(services.Usage, conversation.Registry, _storage);
        _quota = new QuotaService(_store, usage, _clock);
        _reporter = new UsageReporter(_identity.DeviceId, _store, usage, _quota, _options.Channel, _clock);
        _verifier = new CommandVerifier(_config.Keys, _identity.DeviceId, _clock);
        _pump = new CommandPump(_verifier, _store, this, _options.Channel, (what, detail) => Log(what, detail));
        _snapshot = _store.Read(CloudKeys.Sleep, CloudStateJson.Default.SleepSnapshot);
        _wokeAt = _snapshot is null ? null : _startedAt;
        if (_store.Read(CloudKeys.Binding, CloudStateJson.Default.DeviceBinding) is { } binding && binding.DeviceId == _identity.DeviceId)
        {
            _accountId = binding.AccountId;
        }

        conversation.RunGate = this;
        conversation.Registry.ActiveRunsChanged += _ => Touch();
        _remote.SessionsChanged += Touch;
        _storage.RefreshIfStale();
        _dataReady.TrySetResult();
    }

    private void Log(string what, string? detail) => _log.Warn(detail is null ? $"cloud: {what}" : $"cloud: {what} ({detail})");

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Now);

    // ───────────────────────── 启动 ─────────────────────────

    /// <summary>
    /// 启动云端服务:账户令牌 → 第一次打招呼并处理积压的指令 → 向中继注册 → 后台指令轮询 / 空闲检测 / 用量上报 → 唤醒恢复。
    /// 第一次打招呼最多等 <see cref="CloudHostOptions.FirstHelloDeadline"/>:编排服务不可用时设备带着已有的信任记录先上线。
    /// 身份绑定和设备自己不一致(挂错了卷)抛 <see cref="CloudStartupException"/>。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _dataReady.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        var expiresAt = _config.AccountTokenExpiresAt ?? Now + 3_600_000;
        _account.Attach(_config.AccountToken, expiresAt, _accountId ?? _config.AccountId ?? "", null);

        using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token))
        {
            first.CancelAfter(_options.FirstHelloDeadline);
            try
            {
                await HelloUntilSuccessAsync(first.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log("hello.deferred", null); // 编排服务暂时不可用:先上线,后台继续重试
            }
        }

        _remote.StartCloudRelay();
        _pollLoop = Task.Run(() => PollLoopAsync(_lifetime.Token), CancellationToken.None);
        _timers.Add(_clock.CreateTimer(_ => _ = IdleTickAsync(), null, _config.IdleCheckInterval, _config.IdleCheckInterval));
        _timers.Add(_clock.CreateTimer(_ => _ = ReportTickAsync(), null, _config.UsageReportInterval, _config.UsageReportInterval));
        if (_options.EnableHealth && _config.Health is { } health)
        {
            _health = HealthEndpoint.Start(health.Bind, health.Port, () => (Ready, _identity.DeviceId));
        }

        RecoverAfterWake();
        _ = Task.Run(() => NotifyAwakeAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task HelloUntilSuccessAsync(CancellationToken ct)
    {
        var delay = _options.RetryInitial;
        while (true)
        {
            try
            {
                await HelloOnceAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OrchestrationException e)
            {
                Log("hello.failed", e.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(_options.RetryMax.Ticks, delay.Ticks * 2));
            }
        }
    }

    private async Task HelloOnceAsync(CancellationToken ct)
    {
        var issuedAt = Now;
        var hello = new AgentHello(
            OrchestrationProtocol.Version, _identity.DeviceId, Convert.ToBase64String(RelayProtocol.PublicKeyInfo(_identity.Keys)),
            Convert.ToBase64String(DeviceProof.Sign(_identity.Keys, issuedAt, _bootId)), issuedAt, AgentHost.Version, _bootId, _snapshot is not null, _pump!.LastSeq);
        var response = await _options.Channel.HelloAsync(hello, ct).ConfigureAwait(false);

        if (response.Binding is not null)
        {
            // 身份绑定必须由编排密钥签名;签名不对的忽略(不是我们认识的编排服务说的话)
            if (_verifier!.OpenBinding(response.Binding) is { } binding)
            {
                if (!string.Equals(binding.Fingerprint, _identity.Keys.FingerprintHex, StringComparison.Ordinal))
                {
                    throw new CloudStartupException(CloudStartupCodes.BindingMismatch, "binding", CloudExitCodes.Identity, "The identity binding does not match this device.");
                }

                _store!.Write(CloudKeys.Binding, binding, CloudStateJson.Default.DeviceBinding);
                _accountId = binding.AccountId;
                _account.Attach(_options.Token.Current ?? _config.AccountToken, _config.AccountTokenExpiresAt ?? Now + 3_600_000, binding.AccountId, binding.TeamId);
            }
            else
            {
                Log("binding.rejected", null);
            }
        }

        await _pump!.ProcessAsync(response.Commands ?? [], _lifetime.Token).ConfigureAwait(false);
        Volatile.Write(ref _helloDone, 1);
    }

    private async Task PollLoopAsync(CancellationToken lifetime)
    {
        var delay = _options.RetryInitial;
        while (!lifetime.IsCancellationRequested && !_poll.IsCancellationRequested)
        {
            try
            {
                if (_helloDone == 0)
                {
                    await HelloOnceAsync(_poll.Token).ConfigureAwait(false);
                }

                var envelopes = await _options.Channel.PollCommandsAsync(_pump!.LastSeq, _options.PollWait, _poll.Token).ConfigureAwait(false);
                if (envelopes.Length > 0)
                {
                    // 指令用进程生命周期的令牌处理:休眠把轮询取消掉时,已经在处理(含回执)的这批不被打断
                    var outcomes = await _pump.ProcessAsync(envelopes, lifetime).ConfigureAwait(false);
                    if (outcomes.Any(static o => o.Status == AckStatuses.Busy))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), _poll.Token).ConfigureAwait(false);
                    }
                }

                delay = _options.RetryInitial;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (OrchestrationException e)
            {
                Log("poll.failed", e.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    await Task.Delay(delay, _poll.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = TimeSpan.FromTicks(Math.Min(_options.RetryMax.Ticks, delay.Ticks * 2));
            }
            catch (CloudStartupException e)
            {
                // 运行中发现身份不符(编排服务换了绑定):无法继续,收尾退出
                Log("identity.mismatch", e.Code);
                _exit.TrySetResult(new CloudExit("identity", null));
                break;
            }
        }
    }

    private async Task NotifyAwakeAsync(CancellationToken ct)
    {
        try
        {
            await _options.Channel.NotifyLifecycleAsync(new LifecycleNotice(OrchestrationProtocol.Version, _identity.DeviceId, LifecycleStates.Awake, _snapshot is null ? "start" : "wake", Now, null, _snapshot?.Summary), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OrchestrationException or OperationCanceledException)
        {
            // 尽力而为
        }
    }

    // ───────────────────────── 唤醒恢复 ─────────────────────────

    /// <summary>
    /// 从休眠里醒来之后的恢复(02 A6、M6 验收「会话和未完成的状态都不丢」)。
    /// 其余状态本来就在数据库里,不需要恢复动作:会话和消息、排队的消息、控制端信任记录(+ 路由令牌哈希,中继注册时整体推送)、目标(转录里的 <c>goal_status</c> 标记,按需恢复)、
    /// 定时任务(调度器启动时对账:错过的记 skipped,不补跑)。要做的只有一件事:被休眠<b>打断</b>的会话里排队的消息接着发(用户的意图还在队列里,没有「停止」这个动作)。
    /// 被打断的那个 Run 本身<b>不自动重跑</b>(重跑可能重复有副作用的工具调用),在 <c>cloud.getStatus</c> 的 <c>InterruptedRuns</c> 里让控制端提示用户。
    /// </summary>
    private void RecoverAfterWake()
    {
        if (_snapshot is null)
        {
            return;
        }

        var interrupted = ReadInterrupted();
        foreach (var session in interrupted.Select(static r => r.SessionId).Distinct(StringComparer.Ordinal))
        {
            if (QueuedCount(session) == 0 || !_quota!.Check().Allowed)
            {
                continue; // 配额用尽:消息留在队列里,配额恢复后用户手动继续
            }

            try
            {
                _conversation!.Registry.ResumeQueue(session);
            }
            catch (Exception e) when (e is InvalidOperationException or ProtocolException)
            {
                Log("resume.failed", null);
            }
        }

        _store!.Remove(CloudKeys.Sleep); // 数据库里的快照清掉;内存里的保留(ResumedFromSleep / 摘要)
    }

    private IReadOnlyList<InterruptedRun> ReadInterrupted() =>
        _store!.Read(CloudKeys.Interrupted, CloudStateJson.Default.InterruptedRuns)?.Runs ?? [];

    private int QueuedCount(string sessionId) =>
        new SessionInputStore(_db!, _clock).Read(sessionId)?.Queued?.Count ?? 0;

    private int TotalQueued()
    {
        var total = 0;
        List<string> keys = [];
        lock (_db!.Gate)
        {
            using var command = _db.Connection.CreateCommand();
            command.CommandText = "SELECT key FROM kv WHERE key LIKE 'session.input.%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }
        }

        var inputs = new SessionInputStore(_db, _clock);
        foreach (var key in keys)
        {
            total += inputs.Read(key["session.input.".Length..])?.Queued?.Count ?? 0;
        }

        return total;
    }

    // ───────────────────────── Run 闸门 ─────────────────────────

    public RunTicket? AdmitRun()
    {
        if (Preparing)
        {
            throw PreparingError();
        }

        RunTicket? ticket = null;
        if (_quota is { } quota)
        {
            ticket = quota.Admit(out var verdict);
            if (ticket is null)
            {
                throw QuotaError(verdict);
            }
        }

        Touch();
        return ticket;
    }

    public void CheckInput()
    {
        if (Preparing)
        {
            throw PreparingError();
        }

        if (_quota?.Check() is { Allowed: false } verdict)
        {
            throw QuotaError(verdict);
        }
    }

    private static ProtocolException PreparingError() =>
        ServiceMapping.Error(ErrorCodes.Unavailable, CloudErrorKeys.Preparing, "The cloud agent is getting ready to sleep; try again in a moment", retryable: true);

    internal static ProtocolException QuotaError(QuotaVerdict verdict) =>
        ServiceMapping.Error(
            ErrorCodes.RateLimited, CloudErrorKeys.QuotaExceeded, "The quota for this account has been used up", retryable: false,
            ("window", verdict.Window ?? ""),
            ("limit", verdict.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            ("used", verdict.Used?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            ("resetsAt", verdict.ResetsAt?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""));

    // ───────────────────────── 状态 ─────────────────────────

    public CloudStatus Status()
    {
        var quota = _quota ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        var (limits, usage, verdict) = quota.Snapshot();
        return new CloudStatus(
            Preparing ? CloudStates.Preparing : CloudStates.Awake, _startedAt, _snapshot is not null, _wokeAt, limits, usage,
            verdict.Allowed ? null : CloudErrorKeys.QuotaExceeded, verdict.Window,
            [.. ReadInterrupted().Select(static r => new CloudInterruptedRunDto(r.RunId, r.SessionId, r.At))], _accountId);
    }

    public void ClearInterrupted(IReadOnlyCollection<string> runIds)
    {
        var keep = ReadInterrupted().Where(r => runIds.Count > 0 && !runIds.Contains(r.RunId)).ToArray();
        _store!.Write(CloudKeys.Interrupted, new InterruptedRuns(keep), CloudStateJson.Default.InterruptedRuns);
    }

    // ───────────────────────── 指令 ─────────────────────────

    private static async Task<CommandOutcome> AppliedAfterAsync(Task done)
    {
        await done.ConfigureAwait(false);
        return CommandOutcome.Applied;
    }

    public Task<CommandOutcome> ApplyAsync(CommandBody command, CancellationToken ct)
    {
        switch (command.Type)
        {
            case CommandTypes.TrustPut:
                if (!RemoteService.IsValidTrust(command.Trust))
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                return AppliedAfterAsync(_remote.PutTrustAsync(command.Trust!)); // 路由令牌哈希已发给中继才回执(见 RemoteService.SyncRelayTokensAsync)

            case CommandTypes.TrustRevoke:
                if (command.Fingerprint is not { Length: 64 })
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                return AppliedAfterAsync(_remote.RevokeTrustAsync(command.Fingerprint)); // 立即断开连接,令牌撤销已发给中继才回执

            case CommandTypes.TrustReplace:
                if (command.Trusts is not { } trusts || !trusts.All(RemoteService.IsValidTrust))
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                return AppliedAfterAsync(_remote.ReplaceTrustAsync(trusts));

            case CommandTypes.QuotaSet:
                if (command.Quota is not { } limits || !ValidQuota(limits))
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                _quota!.Set(limits);
                return Task.FromResult(CommandOutcome.Applied);

            case CommandTypes.AccountToken:
                if (command.Account is not { Token.Length: > 0 } grant || grant.AccountId.Length == 0 || grant.Token.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                _options.Token.Current = grant.Token;
                _accountId = grant.AccountId;
                _account.Attach(grant.Token, grant.ExpiresAt, grant.AccountId, grant.TeamId);
                return Task.FromResult(CommandOutcome.Applied);

            case CommandTypes.SleepRequest:
                if (command.Sleep is not { } sleep || sleep.Mode is not (SleepModes.WhenIdle or SleepModes.Now))
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                return Task.FromResult(StartSleep(sleep.Mode, sleep.Reason ?? "orchestrator", LifecycleStates.Sleeping, requireNoControllers: true));

            case CommandTypes.UpgradeRequest:
                if (command.Upgrade is not { } upgrade || upgrade.Mode is not (UpgradeModes.WhenIdle or UpgradeModes.Now) || upgrade.Version.Length == 0)
                {
                    return Task.FromResult(CommandOutcome.Invalid(RejectCodes.Invalid));
                }

                return Task.FromResult(StartSleep(upgrade.Mode, $"upgrade:{upgrade.Version}", LifecycleStates.Upgrading, requireNoControllers: false));

            default:
                return Task.FromResult(CommandOutcome.Invalid(RejectCodes.UnknownType));
        }
    }

    private static bool ValidQuota(QuotaLimits l) =>
        l.DailyRuns is null or >= 0 && l.MonthlyRuns is null or >= 0 && l.DailyTokens is null or >= 0 && l.MonthlyTokens is null or >= 0
        && l.MaxConcurrentRuns is null or >= 0 && l.MonthlyComputeSeconds is null or >= 0 && l.StorageBytes is null or >= 0;

    // ───────────────────────── 空闲 / 休眠 ─────────────────────────

    private IdleInputs Inputs()
    {
        long? next = null;
        foreach (var task in _services!.Schedules.List())
        {
            if (task is { Enabled: true, NextRunAt: { } at } && (next is null || at < next))
            {
                next = at;
            }
        }

        return new IdleInputs(Now, _conversation!.Registry.ActiveRunIds().Count, _remote.ConnectedSessions, Interlocked.Read(ref _lastActivity), next);
    }

    /// <summary>一次空闲检查(定时器触发;测试直接调用)。可以休眠就开始休眠。</summary>
    public Task IdleTickAsync()
    {
        if (_conversation is null || Preparing || Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            _quota?.Meter();
            var verdict = _idle.Evaluate(Inputs());
            if (verdict.CanSleep)
            {
                StartSleep(SleepModes.WhenIdle, "idle", LifecycleStates.Sleeping, requireNoControllers: true, verdict.WakeAt);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log("idle.failed", e.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }

        return Task.CompletedTask;
    }

    private async Task ReportTickAsync()
    {
        if (_reporter is null || Preparing)
        {
            return;
        }

        try
        {
            _storage?.RefreshIfStale();
            await _reporter.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OrchestrationException or OperationCanceledException)
        {
            // 留在发件箱,下次再来
        }
        catch (Exception e)
        {
            Log("report.failed", e.GetType().Name); // 不让定时器线程上的意外异常无声消失
        }
    }

    /// <summary>
    /// 开始休眠 / 升级(后台进行)。<paramref name="mode"/> = whenIdle 时先复查:有运行中的 Run(休眠还要没有控制端)就返回 <c>busy</c>,什么都没动;
    /// now 时直接进入收尾(中止 Run、断开控制端)。收尾一旦开始就不可逆:成功后进程退出。
    /// </summary>
    private CommandOutcome StartSleep(string mode, string reason, string state, bool requireNoControllers, long? wakeAt = null)
    {
        if (_conversation is null)
        {
            return CommandOutcome.Busy("not-ready");
        }

        if (mode == SleepModes.WhenIdle)
        {
            var inputs = Inputs();
            if (inputs.ActiveRuns > 0 || (requireNoControllers && inputs.Controllers > 0))
            {
                return CommandOutcome.Busy("busy");
            }

            wakeAt ??= NextWake(inputs);
        }
        else
        {
            wakeAt ??= NextWake(Inputs());
        }

        if (Interlocked.CompareExchange(ref _preparing, 1, 0) != 0)
        {
            return CommandOutcome.Applied; // 已经在收尾了
        }

        _ = Task.Run(() => SleepAsync(reason, state, wakeAt), CancellationToken.None);
        return CommandOutcome.Applied;
    }

    private long? NextWake(IdleInputs inputs) =>
        inputs.NextScheduledAt is { } next ? Math.Max(Now, next - (long)_config.WakeLead.TotalMilliseconds) : null;

    /// <summary>休眠 / 升级前的收尾(02 A6 + M6 验收)。<see cref="_preparing"/> 已经置位:新的 Run 被拒绝,<c>cloud.getStatus</c> 报告 <c>preparing</c>。</summary>
    private async Task SleepAsync(string reason, string state, long? wakeAt)
    {
        try
        {
            // 1. 不再收指令、不再长轮询(正在处理的这批指令含回执会处理完)
            await _poll.CancelAsync().ConfigureAwait(false);
            await _pollLoop.ConfigureAwait(false);

            // 2. 记下将被打断的 Run(状态在中止之前读),后台 Shell 数、排队消息数
            var runs = _conversation!.Registry.ActiveRuns().Runs;
            var interrupted = runs.Select(r => new InterruptedRun(r.RunId, r.SessionId, Now)).ToArray();
            var killedShells = _conversation.Shells.RunningCount;
            if (interrupted.Length > 0)
            {
                var merged = ReadInterrupted().Where(old => interrupted.All(n => n.RunId != old.RunId)).Concat(interrupted).ToArray();
                _store!.Write(CloudKeys.Interrupted, new InterruptedRuns(merged), CloudStateJson.Default.InterruptedRuns);
            }

            // 3. 停止对外服务并注销中继、中止 Run、杀后台 Shell、关终端和浏览器、停调度器、数据库检查点(顺序见 AgentHost.QuiesceAsync)
            await _quiesce().ConfigureAwait(false);

            // 4. 最后一次用量上报(尽力而为,有上限),休眠快照,再检查点一次(快照和账本是检查点之后写的)
            await FlushUsageBestEffortAsync().ConfigureAwait(false);
            var summary = new SleepSummary(interrupted.Length, killedShells, TotalQueued());
            _store!.Write(CloudKeys.Sleep, new SleepSnapshot(Now, reason, summary, wakeAt), CloudStateJson.Default.SleepSnapshot);
            _db!.Checkpoint();

            // 5. 通知编排服务(失败不阻止退出:退出码 0 + 休眠快照本身就是真相)
            await NotifyAsync(new LifecycleNotice(OrchestrationProtocol.Version, _identity.DeviceId, state, reason, Now, wakeAt, summary)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log("sleep.failed", e.GetType().Name);
        }
        finally
        {
            _exit.TrySetResult(new CloudExit(state == LifecycleStates.Upgrading ? "upgrade" : "sleep", wakeAt));
        }
    }

    private async Task FlushUsageBestEffortAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(_options.NoticeTimeout);
            await _reporter!.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OrchestrationException or OperationCanceledException)
        {
        }
    }

    private async Task NotifyAsync(LifecycleNotice notice)
    {
        for (var attempt = 0; attempt < _options.NoticeAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(_options.NoticeTimeout);
                await _options.Channel.NotifyLifecycleAsync(notice, cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is OrchestrationException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1))).ConfigureAwait(false);
            }
        }

        Log("lifecycle.notify.failed", notice.State);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        foreach (var timer in _timers)
        {
            timer.Dispose();
        }

        _health?.Dispose();
        try
        {
            _pollLoop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _poll.Dispose();
        _lifetime.Dispose();
    }
}
