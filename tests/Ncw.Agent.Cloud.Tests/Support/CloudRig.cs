using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Ncw.Agent.Cloud;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Cloud;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;
using Ncw.Relay;

namespace Ncw.Agent.Cloud.Tests.Support;

/// <summary>
/// 假编排服务(进程内,直接实现 <see cref="IOrchestrationChannel"/>):按真实后端的契约工作——验证设备的「持有私钥证明」、签发身份绑定、
/// 保存未确认的指令(<c>busy</c> / 没回执的会重发)、长轮询、用量批次按 batchId 去重、记录生命周期通知。
/// 也提供攻击者视角的方法:重放已发出的指令、用别的密钥签名、发给别的设备。
/// </summary>
public sealed class FakeOrchestrator(Issuer issuer, string relayUrl, TimeProvider clock, string accountId = "acct-1") : IOrchestrationChannel
{
    private readonly object _gate = new();
    private readonly List<(long Seq, SignedEnvelope Envelope)> _pending = [];
    private readonly List<SignedEnvelope> _forced = [];
    private readonly List<UsageBatch> _usage = [];
    private readonly HashSet<string> _batchIds = [];
    private long _seq;
    private TaskCompletionSource _signal = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<AgentHello> Hellos { get; } = [];

    public List<CommandAck> Acks { get; } = [];

    public List<LifecycleNotice> Notices { get; } = [];

    /// <summary>通知到达的瞬间被调用(测试在这里抓「通知时」的状态:中继上还在线吗、WAL 截断了吗)。</summary>
    public Action<LifecycleNotice>? OnNotice { get; set; }

    /// <summary>true = 编排服务不可用(所有调用抛可重试的错误)。</summary>
    public bool Offline { get; set; }

    /// <summary>接下来这么多次用量上报失败(503)。</summary>
    public int FailUsage { get; set; }

    /// <summary>true = 身份绑定里的指纹是错的(模拟挂错了卷 / 编排服务出错)。</summary>
    public bool CorruptBinding { get; set; }

    public string? DeviceId { get; private set; }

    public string? Fingerprint { get; private set; }

    public IReadOnlyList<UsageBatch> UsageBatches
    {
        get
        {
            lock (_gate)
            {
                return [.. _usage];
            }
        }
    }

    public IReadOnlyList<SignedEnvelope> Issued
    {
        get
        {
            lock (_gate)
            {
                return [.. _issuedLog];
            }
        }
    }

    private readonly List<SignedEnvelope> _issuedLog = [];

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new OrchestrationException("orchestrator offline", retryable: true, 503);
        }
    }

    public Task<HelloResponse> HelloAsync(AgentHello hello, CancellationToken ct)
    {
        ThrowIfOffline();
        if (!DeviceProof.Verify(hello))
        {
            throw new OrchestrationException("bad proof", retryable: false, 403);
        }

        var spki = Convert.FromBase64String(hello.PublicKey);
        lock (_gate)
        {
            Hellos.Add(hello);
            DeviceId = hello.DeviceId;
            Fingerprint = Convert.ToHexStringLower(SHA256.HashData(spki));
            var fingerprint = CorruptBinding ? new string('0', 64) : Fingerprint;
            var binding = issuer.Signer.Sign(new DeviceBinding(hello.DeviceId, fingerprint, accountId, relayUrl, clock.GetUtcNow().ToUnixTimeMilliseconds()));
            var commands = _pending.Where(p => p.Seq > hello.LastSeq).Select(p => p.Envelope).ToArray();
            return Task.FromResult(new HelloResponse(binding, commands, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        }
    }

    public async Task<SignedEnvelope[]> PollCommandsAsync(long afterSeq, TimeSpan wait, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            ThrowIfOffline();
            Task signal;
            lock (_gate)
            {
                var ready = _pending.Where(p => p.Seq > afterSeq).Select(p => p.Envelope).Concat(_forced).ToArray();
                if (ready.Length > 0)
                {
                    _forced.Clear(); // 伪造 / 重放的信封只被「中间人」递送一次
                    return ready;
                }

                signal = _signal.Task;
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                return [];
            }

            try
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(left), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }
    }

    public Task AckAsync(CommandAck ack, CancellationToken ct)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            Acks.Add(ack);
            if (ack.Status != AckStatuses.Busy)
            {
                _pending.RemoveAll(p => p.Seq == ack.Seq);
            }
        }

        return Task.CompletedTask;
    }

    public int UsageAttempts;

    public Task<AckResponse> ReportUsageAsync(UsageBatch batch, CancellationToken ct)
    {
        Interlocked.Increment(ref UsageAttempts);
        ThrowIfOffline();
        lock (_gate)
        {
            if (FailUsage > 0)
            {
                FailUsage--;
                throw new OrchestrationException("busy", retryable: true, 503);
            }

            var fresh = _batchIds.Add(batch.BatchId);
            if (fresh)
            {
                _usage.Add(batch);
            }

            return Task.FromResult(new AckResponse(true, !fresh));
        }
    }

    public Task NotifyLifecycleAsync(LifecycleNotice notice, CancellationToken ct)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            Notices.Add(notice);
        }

        OnNotice?.Invoke(notice);
        return Task.CompletedTask;
    }

    // ---- 编排服务一侧的动作 --------------------------------------------------------------------------------------

    /// <summary>签发一条指令(序号自动递增)并排进待发队列。返回指令体和信封。</summary>
    public (CommandBody Body, SignedEnvelope Envelope) Issue(string type, Func<CommandBody, CommandBody> fill, TimeSpan? lifetime = null)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var body = fill(new CommandBody(type)
            {
                DeviceId = DeviceId ?? throw new InvalidOperationException("no device has said hello yet"),
                Seq = ++_seq,
                IssuedAt = now.ToUnixTimeMilliseconds(),
                ExpiresAt = (now + (lifetime ?? TimeSpan.FromMinutes(30))).ToUnixTimeMilliseconds(),
            });
            var envelope = issuer.Signer.Sign(body);
            _pending.Add((body.Seq, envelope));
            _issuedLog.Add(envelope);
            Wake();
            return (body, envelope);
        }
    }

    /// <summary>控制端把证书公钥注册到账户 API:生成路由令牌(只给控制端),把信任记录(指纹 + 令牌哈希)下发给云端设备。</summary>
    public (string RouteToken, long Seq) Grant(PeerIdentity controller, string role = "owner", string name = "Test Phone")
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var entry = new TrustEntry(controller.FingerprintHex, name, role, RelayProtocol.HashToken(token));
        var (body, _) = Issue(CommandTypes.TrustPut, b => b with { Trust = entry });
        return (token, body.Seq);
    }

    public long Revoke(PeerIdentity controller) => Issue(CommandTypes.TrustRevoke, b => b with { Fingerprint = controller.FingerprintHex }).Body.Seq;

    public long SetQuota(QuotaLimits limits) => Issue(CommandTypes.QuotaSet, b => b with { Quota = limits }).Body.Seq;

    public long RequestSleep(string mode, string reason = "test") => Issue(CommandTypes.SleepRequest, b => b with { Sleep = new SleepRequest(mode, reason) }).Body.Seq;

    /// <summary>攻击者(中间人)重放一条已经发过的信封(原样):不管 Agent 说「我已经到序号 N」,它都被递送一次。</summary>
    public void Replay(SignedEnvelope envelope) => Inject(envelope);

    /// <summary>攻击者递送任意信封(伪造的、过期的、发给别人的):一次性递送,不进编排服务自己的待发队列。</summary>
    public void Inject(SignedEnvelope envelope)
    {
        lock (_gate)
        {
            _forced.Add(envelope);
            Wake();
        }
    }

    public long NextSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq + 1;
            }
        }
    }

    private void Wake()
    {
        var old = _signal;
        _signal = NewSignal();
        old.TrySetResult();
    }

    public CommandAck? AckFor(long seq)
    {
        lock (_gate)
        {
            return Acks.LastOrDefault(a => a.Seq == seq);
        }
    }
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>假上游:按脚本一次一个请求回复。脚本可以被一道门挡住(模拟一个跑得很久的 Run)。</summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<Func<CancellationToken, IAsyncEnumerable<LlmEvent>>> _scripts = new();

    public int Requests;

    public FakeLlm Then(IReadOnlyList<LlmEvent> events) => Then(ct => Replay(events, ct));

    public FakeLlm Then(Func<CancellationToken, IAsyncEnumerable<LlmEvent>> script)
    {
        lock (_scripts)
        {
            _scripts.Enqueue(script);
        }

        return this;
    }

    /// <summary>一个先发开头、再等 <paramref name="gate"/>、最后说完话的 Run。</summary>
    public FakeLlm ThenGated(Task gate, string text) => Then(ct => Gated(gate, text, ct));

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Requests);
        Func<CancellationToken, IAsyncEnumerable<LlmEvent>>? script;
        lock (_scripts)
        {
            _scripts.TryDequeue(out script);
        }

        return script?.Invoke(ct) ?? Replay([new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))], ct);
    }

    private static async IAsyncEnumerable<LlmEvent> Replay(IReadOnlyList<LlmEvent> events, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ev;
        }
    }

    private static async IAsyncEnumerable<LlmEvent> Gated(Task gate, string text, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("m", "p", "anthropic", "up");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        foreach (var ev in Say(text).Skip(1))
        {
            yield return ev;
        }
    }

    public static IReadOnlyList<LlmEvent> Say(string text) =>
        [new MessageStart("m", "p", "anthropic", "up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered)];
}

public sealed class FakeModels(ILlmClient client) : IModelResolver
{
    public ILlmClient Client { get; } = client;

    public ModelBinding? Resolve(ModelRef model) => model.Alias == "sonnet" ? new ModelBinding("sonnet", "p1", "up", 200_000, 8192) : null;
}

/// <summary>
/// 云端 Agent 的端到端夹具:假编排服务 + 假 KMS + 进程内中继(<c>Ncw.Relay</c>)+ 真实的 <see cref="AgentHost"/>(<c>--mode cloud</c> 装配)+ 控制端 <see cref="DeviceClient"/> 经中继。
/// 云端运行时用手动时钟(空闲检测 / 用量上报 / 配额窗口确定性);Agent 其余部分用真实时钟。「唤醒」= 对同一个数据根、同一个 KMS 再起一个 AgentHost。
/// </summary>
public sealed class CloudRig : IAsyncLifetime
{
    public static readonly RelayClientOptions FastRetry = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200));

    public const string AccountToken = "acct-token-SECRET";

    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<(AgentHost Host, CancellationTokenSource Cts, Task Run)> _agents = [];
    private readonly Issuer _issuerForRig = new();

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ncwc-{Guid.NewGuid():N}"[..14]);

    public string Workspaces => Root + "-ws";

    public string DataRoot => Path.Combine(Root, "data");

    public Issuer Issuer => _issuerForRig;

    public FakeKms Kms { get; } = new();

    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeLlm Llm { get; } = new();

    public RelayServer Relay { get; private set; } = null!;

    public FakeOrchestrator Orchestrator { get; private set; } = null!;

    public AgentHost Agent { get; private set; } = null!;

    public Task Run { get; private set; } = Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Workspaces);
        Relay = await RelayServer.StartAsync(new RelayOptions
        {
            Urls = ["http://127.0.0.1:0"],
            IdleTimeout = TimeSpan.FromMinutes(2),
            MaxBytesPerSecondPerSession = 0,
            ConfigureLogging = logging => logging.ClearProviders(),
        });
        Orchestrator = new FakeOrchestrator(Issuer, RelayUrl, Clock);
    }

    public string RelayUrl => RelayAddress.ToText(Relay.WebSocketUrl);

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try { await disposable.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or ProtocolException) { }
        }

        foreach (var (host, cts, run) in _agents)
        {
            await cts.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception e) when (e is OperationCanceledException or TimeoutException or CloudStartupException) { }
            host.Dispose();
            cts.Dispose();
        }

        await Relay.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { Root, Workspaces })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    public CloudConfig Config(CloudEgressFile? egress = null, string? expectedDeviceId = null, int idleMinutes = 15) =>
        CloudConfigLoader.Build(
            new CloudConfigFile(
                DataRoot, Workspaces, "cloud-test", "http://127.0.0.1:9/", RelayUrl, [new OrchestratorKeyFile(Issuer.Kid, Issuer.PublicKey)], expectedDeviceId, "acct-1", null,
                new CloudKmsFile("dev-env", "acct-1/sandbox-1"), new CloudIdleFile(idleMinutes), 60, null, egress),
            AccountToken, null);

    /// <summary>起(或「唤醒」)云端 Agent,等到它向中继注册完成。</summary>
    public async Task<AgentHost> StartAgentAsync(
        CloudConfig? config = null, bool waitForRelay = true, TimeSpan? firstHelloDeadline = null, FakeLlm? llm = null)
    {
        config ??= Config();
        var keySource = await CloudBoot.OpenKeySourceAsync(config, Kms, default);
        var options = new CloudHostOptions
        {
            Config = config,
            Channel = Orchestrator,
            Token = new AccountTokenHolder(config.AccountToken),
            KeySource = keySource,
            Egress = HostAllowlistEgress.FromConfig(config.Egress),
            Clock = Clock,
            RetryInitial = TimeSpan.FromMilliseconds(20),
            RetryMax = TimeSpan.FromMilliseconds(100),
            PollWait = TimeSpan.FromMilliseconds(300),
            FirstHelloDeadline = firstHelloDeadline ?? TimeSpan.FromSeconds(10),
            NoticeAttempts = 2,
            NoticeTimeout = TimeSpan.FromSeconds(5),
            EnableHealth = false,
        };
        AgentHost host;
        try
        {
            host = new AgentHost(
                new AgentOptions(AgentMode.Cloud, config.DataRoot, "", "", null),
                pairingFailureDelay: TimeSpan.Zero,
                services: new AgentServices { Models = new FakeModels(llm ?? Llm), Delayer = new NoDelay(), Loop = new AgentLoopOptions { ResumeDelays = [] } },
                relayOptions: FastRetry, cloud: options);
        }
        catch
        {
            keySource.Dispose();
            throw;
        }

        var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = host.RunAsync(cts.Token, () => ready.TrySetResult());
        _ = run.ContinueWith(t => ready.TrySetException(t.Exception!.InnerException ?? t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        _agents.Add((host, cts, run));
        Agent = host;
        Run = run;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (waitForRelay)
        {
            await WaitUntil(() => host.Remote.RelayStatus?.State == RelayConnectionState.Registered && Relay.IsOnline(host.DeviceId), "device registered at the relay");
        }

        return host;
    }

    /// <summary>休眠之后:等 Agent 进程(RunAsync)退出。</summary>
    public async Task WaitForExitAsync() => await Run.WaitAsync(TimeSpan.FromSeconds(30));

    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(15);
        }
    }

    public static async Task WaitUntil(Func<Task<bool>> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(30);
        }
    }

    /// <summary>
    /// 一条信任下发(<c>Orch.Grant</c>)的回执,并且中继已经登记了路由令牌。「已回执」只说明设备本地的信任记录生效、令牌哈希已经发给中继;
    /// 中继的 <c>tokens</c> 控制消息没有回执,它处理完之前控制端拿着新令牌来连会得到 <c>unauthorized</c>(整套并行 / 机器忙时见过:
    /// <c>code=unauthorized, tokensAtRelay=0, deviceOnline=True</c>)。等中继那一侧可观察的结果,而不是赌两条连接上的消息谁先到。
    /// </summary>
    public async Task<CommandAck> GrantAppliedAsync(long seq, int routeTokens = 1)
    {
        var ack = await AckedAsync(seq);
        await WaitUntil(() => Relay.TokenCount(Agent.DeviceId) >= routeTokens, "the relay to know the granted route token");
        return ack;
    }

    /// <summary>等编排服务收到这条指令的回执,并返回它。</summary>
    public async Task<CommandAck> AckedAsync(long seq)
    {
        CommandAck? ack = null;
        await WaitUntil(() => (ack = Orchestrator.AckFor(seq)) is not null, $"ack for command {seq}");
        return ack!;
    }

    // ---- 控制端 -------------------------------------------------------------------------------------------------

    /// <summary>控制端经中继连接云端设备(没有局域网地址:云端设备只在中继上)。</summary>
    public async Task<DeviceClient> ConnectAsync(PeerIdentity controller, string routeToken, string clientId = "phone")
    {
        var connection = await RemoteDevices.ConnectAsync(
            Agent.DeviceId, new DeviceAddresses([], new RelayRoute(Relay.WebSocketUrl, routeToken)), controller, Agent.Fingerprint);
        var client = await DeviceClient.ConnectAsync(connection.Stream, new ClientHello(clientId, "3.0.0", FormFactor: "phone"));
        _disposables.Add(client);
        return client;
    }

    public static async Task<string> NewProjectAsync(DeviceClient client, string name, string root)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return (await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(dir, name))).WorkspaceId;
    }

    private int _ids;

    private string NextId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _ids)}";

    public async Task<string> NewSessionAsync(DeviceClient client, string workspaceId) =>
        (await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(workspaceId, Id: NextId("sess"), Model: "sonnet"), idem: NextId("idem"))).Id;

    public Task<RunsStartResult> StartRunAsync(DeviceClient client, string sessionId, string text) =>
        client.CallAsync(
            ConversationMethods.RunsStart,
            new RunsStartParams(NextId("run"), sessionId, NextId("msg"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto("sonnet", PermissionMode: "auto")),
            idem: NextId("idem"));

    public async Task<string[]> AssistantTextsAsync(DeviceClient client, string sessionId)
    {
        var page = await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(sessionId));
        return [.. page.Messages.Where(m => m.Role == "assistant").Select(m => m.PartsJson)];
    }

    public static async Task<ProtocolException> ErrorOf(Func<Task> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    public static string? ErrorKey(ProtocolException e) =>
        e.Details is { } d ? FrameCodec.DecodePayload(d, PayloadShapes.GetShape<ProviderErrorDto>()).Key : null;

    public static IReadOnlyDictionary<string, string> ErrorParams(ProtocolException e) =>
        e.Details is { } d ? FrameCodec.DecodePayload(d, PayloadShapes.GetShape<ProviderErrorDto>()).Params : new Dictionary<string, string>();
}
