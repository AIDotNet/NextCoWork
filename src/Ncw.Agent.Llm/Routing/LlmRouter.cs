using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.History;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Sse;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Routing;

/// <summary>模型层对上的唯一入口:上层永远看不到线协议。</summary>
public interface ILlmClient
{
    /// <summary>
    /// 流式调用。<b>流返回后永不抛异常</b>:错误(包括取消)都变成 <see cref="StreamError"/> 事件。
    /// 重试和换供应商只发生在第一个内容字节之前,所以消费者看到的内容永远是单个供应商的一次连续输出。
    /// </summary>
    IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default);
}

/// <summary>
/// 路由与重试(01-llm §5,2.x <c>upstream/router.ts</c>)。归一化与治理是一个纯模块,内核在进程内直接调它。
/// 重试放在这里,绝不放在会话里:放在会话里会重放已经执行过的工具调用。
/// </summary>
public sealed class LlmRouter : ILlmClient
{
    /// <summary>每个供应商最多试几次(含首次)。第 3 次还不行,换下一个供应商比继续磕更有用。</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// 纯连接层错误(DNS 失败、握手中断)不是「这家供应商不行」而是「这台机器这一刻连不上网」:换家大概率同一个病因,
    /// 管用的是多等一会儿再碰,所以给它比 <see cref="MaxAttempts"/> 更多的机会。
    /// </summary>
    public const int MaxNetworkAttempts = 6;

    /// <summary>
    /// 一次逻辑请求里最多换几个账号。这是一道保险,不是业务规则:「换号不消耗重试次数」意味着只要池子一直说「还能换」循环就不前进,
    /// 池子任何一个判断写歪都会变成死循环(Run 卡住、CPU 打满、一条错误都不输出)。
    /// </summary>
    public const int MaxAccountSwitches = 8;

    private const int AnthropicUserIdMax = 512;

    private readonly IProviderConfigSource _config;
    private readonly IUpstreamHttp _http;
    private readonly CredentialResolver _credentials;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly IDelayer _delayer;
    private readonly LlmRouterOptions _options;
    private readonly IPriceBook? _prices;
    private readonly IUsageSink? _usage;
    private readonly AccountPool? _accounts;
    private readonly IImageResolver? _images;
    private readonly ITransportDecorator _transport;

    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderHealth> _health = [];

    /// <summary>
    /// 「这一家在这个时刻之前别再发请求了」:被限流之后立起来的进程级闸门。价值整个在于这个类是进程内单例——
    /// 主代理和它派出的每个子代理共用一个实例,任意一条流吃到 429,其余几条在发请求之前就被挡住(否则是各自独立退避:同时撞、同时退、再同时撞)。
    /// 只记 rate_limit(5xx / 网络错误是这一条请求的事)。键是「账号 ref ?? providerId」:额度按账号算。
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Until, string Reason)> _rateGate = [];

    public LlmRouter(
        IProviderConfigSource config,
        IUpstreamHttp http,
        CredentialResolver credentials,
        TimeProvider? clock = null,
        IIdGenerator? ids = null,
        IDelayer? delayer = null,
        LlmRouterOptions? options = null,
        IPriceBook? prices = null,
        IUsageSink? usage = null,
        AccountPool? accounts = null,
        IImageResolver? images = null,
        ITransportDecorator? transport = null)
    {
        _config = config;
        _http = http;
        _credentials = credentials;
        _clock = clock ?? TimeProvider.System;
        _ids = ids ?? new UlidIdGenerator(_clock);
        _delayer = delayer ?? new TimeProviderDelayer(_clock);
        _options = options ?? new LlmRouterOptions();
        _prices = prices;
        _usage = usage;
        _accounts = accounts;
        _images = images;
        _transport = transport ?? NoTransportDecoration.Instance;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    // ── 状态查询 ──

    public IReadOnlyList<ProviderHealth> Health()
    {
        lock (_gate)
        {
            return _health.Values.ToList();
        }
    }

    public void ResetHealth(string? providerId = null)
    {
        lock (_gate)
        {
            if (providerId is null)
            {
                _health.Clear();
                _rateGate.Clear();
                return;
            }

            _health.Remove(providerId);
            _rateGate.Remove(providerId);
            foreach (var key in _rateGate.Keys.Where(k => k.StartsWith($"provider:{providerId}#", StringComparison.Ordinal)).ToList())
            {
                _rateGate.Remove(key);
            }
        }
    }

    /// <summary>
    /// 「这个别名 + 这个供应商」最终落在哪一条绑定上。路由之外的调用方(校验上下文、挑思考档)都必须走这里,而不是自己 find:
    /// 取数组第一条和路由取 priority 最小的那条只是碰巧一致,分家的症状是「界面按 A 的窗口校验,请求却发给了 B」,且不报错。
    /// 不过健康冷却:它回答的是「这个选择指向哪一条绑定」,不是「现在该试谁」。
    /// </summary>
    public ModelBinding? ResolveBinding(ModelRef model) => BindingsFor(model.Alias, model.ProviderId).FirstOrDefault()?.Binding;

    // ── 候选 ──

    private sealed record Candidate(ProviderConfig Provider, ModelBinding Binding)
    {
        public WireProtocol Protocol => Binding.ProtocolOverride ?? Provider.Protocol;
    }

    private List<Candidate> BindingsFor(string alias, string? pinnedProviderId)
    {
        var providers = _config.Providers.ToDictionary(p => p.Id);
        var list = new List<Candidate>();
        foreach (var binding in _config.Bindings)
        {
            if (binding.Alias != alias)
            {
                continue;
            }

            // 钉住时不做任何回退:那家没了就是空,而不是「换一家给你」(静默换家正是用户报的那个 bug)。
            if (pinnedProviderId is not null && binding.ProviderId != pinnedProviderId)
            {
                continue;
            }

            if (!providers.TryGetValue(binding.ProviderId, out var provider) || !provider.Enabled || !binding.Enabled)
            {
                continue;
            }

            list.Add(new Candidate(provider, binding));
        }

        // 只按 priority 升序,同分时靠稳定排序保留输入顺序。
        return list.OrderBy(c => c.Provider.Priority).ToList();
    }

    /// <summary>
    /// 候选集。pinned 有值是硬约束:候选集被压到最多一条,于是重试循环天然只在这一家里打转,ProviderSwitch 也永远不会发出来。
    /// 锁死语义整个由候选集表达,不要去 Stream 里加 <c>if (pinned)</c> 分支——那会造出第二个真相来源。
    /// </summary>
    private List<Candidate> Candidates(string alias, string? pinned)
    {
        var list = BindingsFor(alias, pinned);
        if (!_config.FailoverEnabled)
        {
            return list.Take(1).ToList();
        }

        // 钉住时跳过冷却过滤:冷却的意义是「绕开一家坏的」,锁死之后无处可绕,过滤掉唯一那条候选只会让用户收到「都在冷却中」。
        if (pinned is not null)
        {
            return list;
        }

        var now = Now;
        lock (_gate)
        {
            return list
                .Where(c => !_health.TryGetValue(c.Provider.Id, out var h) || h.CooldownUntil is not { } until || until <= now)
                .OrderBy(c => c.Provider.Priority)
                .ThenByDescending(c => _health.TryGetValue(c.Provider.Id, out var h) ? h.Score : 1.0)
                .ToList();
        }
    }

    private void RecordSuccess(string providerId, int latencyMs, string gateKey)
    {
        lock (_gate)
        {
            var h = _health.GetValueOrDefault(providerId);
            var score = h is null ? 1.0 : h.Score;
            // 恢复比衰减快,但要多次成功才回到 1:一次侥幸成功不该抹掉刚才连挂三次的事实。
            _health[providerId] = new ProviderHealth(providerId, true, Math.Min(1.0, score * 1.5 + 0.1), 0, null, latencyMs, null, Now);

            // 一次成功 = 配额确实回来了。清的是这一次真的用了的那个键(账号 ref 或 providerId)。
            _rateGate.Remove(gateKey);
        }
    }

    private void RecordFailure(string providerId, AgentError error)
    {
        lock (_gate)
        {
            var h = _health.GetValueOrDefault(providerId);
            var failures = (h?.ConsecutiveFailures ?? 0) + 1;
            var unhealthy = failures >= _options.UnhealthyAfter;
            _health[providerId] = new ProviderHealth(
                providerId,
                !unhealthy,
                (h?.Score ?? 1.0) * 0.5,
                failures,
                unhealthy ? Now + _options.Cooldown : h?.CooldownUntil,
                h?.LastLatencyMs,
                error.Message,
                Now);
        }
    }

    /// <summary>
    /// 这次失败该退避多久。三张表:限流、可重试的 provider 5xx、其余(网络抖动)。
    /// 上游给了 Retry-After 就只听它的,不套下限(它知道配额窗口什么时候重置;<c>retry-after: 0</c> 是合法的「立刻再来」)。
    /// </summary>
    private TimeSpan RetryDelayFor(AgentError error, int attempt)
    {
        if (error.RetryAfterMs is { } after)
        {
            return Min(TimeSpan.FromMilliseconds(after), _options.MaxDelay);
        }

        var floor = error.Code == AgentErrorCode.RateLimit ? _options.RateLimitFloor
            : error is { Code: AgentErrorCode.Provider, Retryable: true } ? _options.ProviderErrorFloor
            : _options.BaseDelay;
        return Min(TimeSpan.FromTicks(floor.Ticks * (1L << Math.Min(attempt, 20))), _options.MaxDelay);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private (TimeSpan Wait, string Reason) GateWait(string gateKey)
    {
        lock (_gate)
        {
            if (!_rateGate.TryGetValue(gateKey, out var gate))
            {
                return (TimeSpan.Zero, "");
            }

            var wait = gate.Until - Now;
            if (wait <= TimeSpan.Zero)
            {
                _rateGate.Remove(gateKey);
                return (TimeSpan.Zero, "");
            }

            return (wait, gate.Reason);
        }
    }

    /// <summary>`Retry-After` 可以是秒数或 HTTP 日期;尊重它不是礼貌问题(很多上游把限流期间的请求也计入配额)。上限一分钟。</summary>
    public static int? ParseRetryAfter(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0)
        {
            return (int)Math.Min(seconds * 1000, 60_000);
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? (int)Math.Min(Math.Max((at - now).TotalMilliseconds, 0), 60_000)
            : null;
    }

    /// <summary>
    /// 「第一个内容字节」的定义:<see cref="MessageStart"/> 不算(它不携带任何内容,此时重试不会产生重复输出)。
    /// 从文字 / 思考 / 工具调用 / 透传中任何一个开始,消费者手里就有了半条消息,换供应商重来会出现重复文字和错位的工具调用。
    /// </summary>
    private static bool IsContent(LlmEvent ev) => ev is TextDelta or ThinkingDelta or ToolCallStart or ToolCallDelta or ToolCallEnd or BlockOpaque or MessageEnd;

    // ── 流 ──

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(() => ProduceAsync(model, request, context, channel.Writer, cts.Token), CancellationToken.None);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return ev;
            }
        }
        finally
        {
            // 消费者提前退出(或取消)时让生产者收尾;它永远不抛,等它是为了不留悬空的任务和连接。
            await cts.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(ModelRef model, LlmRequest request, RequestContext context, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        try
        {
            await RunAsync(model, request, context, writer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(new StreamError(AgentError.Create(AgentErrorCode.Aborted, "Aborted", false)));
        }
        catch (Exception e)
        {
            writer.TryWrite(new StreamError(AgentError.Create(AgentErrorCode.Unknown, $"Unexpected error in the model layer: {e.Message}", false)));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private sealed record AttemptOutcome(bool Ok, AgentError? Error, bool SawContent);

    private async Task RunAsync(ModelRef model, LlmRequest request, RequestContext context, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        // 本地校验失败按普通错误走(不抛):工作区标识会进 Anthropic 的 metadata.user_id,限制是 512 个字符(按码点数,不是 UTF-16 单元)。
        if (string.IsNullOrWhiteSpace(context.WorkspaceId) || context.WorkspaceId.EnumerateRunes().Count() > AnthropicUserIdMax)
        {
            writer.TryWrite(new StreamError(AgentError.Create(
                AgentErrorCode.Provider,
                $"The workspace identifier must be 1-{AnthropicUserIdMax} characters; the request was not sent.",
                false,
                messageKey: LlmErrorKeys.InvalidWorkspaceId,
                messageParams: new Dictionary<string, string> { ["max"] = AnthropicUserIdMax.ToString(CultureInfo.InvariantCulture) })));
            return;
        }

        if (request.Tools.FirstOrDefault(t => !ToolInfo.IsValidName(t.ExternalName)) is { } badTool)
        {
            writer.TryWrite(new StreamError(AgentError.Create(
                AgentErrorCode.Provider,
                $"Tool name '{badTool.ExternalName}' does not match ^[a-zA-Z0-9_-]{{1,64}}$; the request was not sent.",
                false,
                messageKey: LlmErrorKeys.InvalidToolName,
                messageParams: new Dictionary<string, string> { ["name"] = badTool.ExternalName })));
            return;
        }

        var runContext = context with { RunId = context.RunId ?? $"run_{_ids.NewId()}", SessionId = context.SessionId ?? "" };
        var candidates = Candidates(model.Alias, model.ProviderId);
        if (candidates.Count == 0)
        {
            writer.TryWrite(new StreamError(NoCandidateError(model)));
            return;
        }

        AgentError? lastError = null;
        AgentError? authError = null;
        var attemptOrdinal = 0;

        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var candidate = candidates[ci];
            if (ci > 0)
            {
                writer.TryWrite(new ProviderSwitch(candidates[ci - 1].Provider.Name, candidate.Provider.Name, lastError?.Message ?? "Upstream unavailable"));
            }

            var accountSwitches = 0;
            for (var attempt = 0; attempt < MaxNetworkAttempts; attempt++)
            {
                // 这一次用哪个账号:挑在发请求之前、闸门之前。换号是涌现出来的,不是一条 if:上一轮失败时 ReportFailure 把那个账号落了闸,
                // 这一轮 Select 自然返回下一个(写成显式的 SwitchAccount() 会造出第二个真相来源)。
                var account = _accounts?.Select(candidate.Provider.Id);

                // 账号表非空但一个都挑不出来 = 这家的全部账号都在限流/停用/待重登。绝不能回落到旧的单凭据槽位(那里装的是当前账号的镜像,
                // 用它发请求等于绕过刚立起来的闸门),交回外层候选循环。
                if (account is null && _accounts?.HasAccounts(candidate.Provider.Id) == true)
                {
                    lastError = AllAccountsLimitedError(candidate.Provider);
                    break;
                }

                var gateKey = account?.CredentialRef ?? candidate.Provider.Id;

                // 别人刚在这一家上吃了 429:在发请求之前先把这次退避等掉。等待不消耗 attempt(被别的流连累而推迟,不该算这条流自己的重试次数)。
                // 复用 ProviderRetry 事件:界面上要说的话是同一句。
                var (wait, gateReason) = GateWait(gateKey);
                if (wait > TimeSpan.Zero)
                {
                    writer.TryWrite(new ProviderRetry(attempt + 1, (int)wait.TotalMilliseconds, gateReason));
                    await _delayer.DelayAsync(wait, ct).ConfigureAwait(false);
                }

                attemptOrdinal++;
                var outcome = await AttemptAsync(candidate, request, runContext, attemptOrdinal, account, writer, ct).ConfigureAwait(false);
                if (outcome.Ok)
                {
                    return;
                }

                var error = outcome.Error!;
                if (error.Code == AgentErrorCode.Aborted)
                {
                    writer.TryWrite(new StreamError(error));
                    return;
                }

                // 缓存兼容性错误是配置不匹配而不是供应商不健康:保持请求形状不变,结束这次逻辑请求,不重试、不换供应商。
                if (error.Code == AgentErrorCode.CacheUnsupported)
                {
                    writer.TryWrite(new StreamError(error));
                    return;
                }

                RecordFailure(candidate.Provider.Id, error);
                lastError = error;
                if (error.Code == AgentErrorCode.Auth)
                {
                    authError ??= error;
                }

                // 退避时长在这里就算出来,早于下面的 return/break:闸门要在「这条流自己还重不重试」之前立起来
                // (这次可能是本条流的最后一次,但对其它并发流配额照样是空的)。
                var delay = RetryDelayFor(error, attempt);
                if (error.Code == AgentErrorCode.RateLimit)
                {
                    lock (_gate)
                    {
                        _rateGate[gateKey] = (Now + delay, error.Message);
                    }
                }

                // 账号级落闸(落库,重启后仍有效)+ 还有没有下一个账号可用。和上面的进程内闸门是两回事:那个管「这几秒别再发」,这个管「这个账号这一轮额度没了」。
                var nextAccount = account is null ? null : _accounts?.ReportFailure(account, error);

                // 只能在收到第一个内容字节之前切换或重试:上游已经吐了 500 个 token 和一个 tool_use 块,换个供应商重来
                // 就会产生重复输出和错位的工具调用。之后的失败是硬错误,由用户决定重发。
                if (outcome.SawContent)
                {
                    writer.TryWrite(new StreamError(error));
                    return;
                }

                // 换号不消耗重试次数:刚才那次失败是「那个账号的额度没了」,不是「这次请求不行」。
                // 算进 attempt 的话,三个账号的用户实际只能用到前两个。循环上限仍兜着底:每换一次都先给上一个落闸。
                if (nextAccount is not null && accountSwitches < MaxAccountSwitches)
                {
                    accountSwitches++;
                    attempt--;
                    continue;
                }

                var limit = error.Code == AgentErrorCode.Network ? MaxNetworkAttempts : MaxAttempts;
                if (!(error.Retryable && attempt < limit - 1))
                {
                    break;
                }

                // 没有这条事件,用户看到的就是白白冻结 30 秒。
                writer.TryWrite(new ProviderRetry(attempt + 1, (int)delay.TotalMilliseconds, error.Message));
                await _delayer.DelayAsync(delay, ct).ConfigureAwait(false);
            }
        }

        // auth 比「最后一个网络错误」更值得展示:它有明确的行动(去设置页填密钥)。
        writer.TryWrite(new StreamError(authError ?? lastError ?? AgentError.Create(AgentErrorCode.Unknown, "The upstream request failed.")));
    }

    // ── 一次 HTTP 尝试 ──

    private async Task<AttemptOutcome> AttemptAsync(
        Candidate c, LlmRequest request, RequestContext context, int attemptNumber, ProviderAccount? account,
        ChannelWriter<LlmEvent> writer, CancellationToken parent)
    {
        var protocol = c.Protocol;
        var startedAt = Now;
        var endpoint = WireProtocols.JoinUrl(c.Provider.BaseUrl, protocol.PathOf());
        var sawContent = false;
        int? httpStatus = null;
        string? responseModel = null;
        StopReason? stop = null;
        int? ttft = null;
        var toolCalls = 0;
        var usage = new TokenUsage();
        var cost = CostInfo.NotMetered;
        var recorded = false;

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(parent);
        void ResetIdle() => idle.CancelAfter(_options.IdleTimeout());
        var token = idle.Token;

        AttemptOutcome Finish(AttemptOutcome outcome)
        {
            if (recorded)
            {
                return outcome;
            }

            recorded = true;
            if (_usage is null)
            {
                return outcome;
            }

            try
            {
                var error = outcome.Error;
                _usage.OnAttempt(new UsageAttempt(
                    _ids.NewId(), startedAt, context.RunId!, context.WorkspaceId, context.SessionId ?? "", attemptNumber,
                    c.Provider.Id, c.Provider.Name, protocol, endpoint, c.Binding.Alias, c.Binding.UpstreamModel, responseModel, usage,
                    Math.Max(0, (int)(Now - startedAt).TotalMilliseconds), ttft, outcome.Ok, httpStatus ?? error?.Status,
                    error?.Code, error?.Message is { } m ? m[..Math.Min(m.Length, 4096)] : null, stop, toolCalls, cost));
            }
            catch (Exception)
            {
                // 遥测永远不能让请求失败。
            }

            return outcome;
        }

        try
        {
            ResetIdle();
            token.ThrowIfCancellationRequested();
            var credentialRef = account?.CredentialRef ?? c.Provider.CredentialRef;
            var credential = await _credentials.ResolveAsync(credentialRef, token).ConfigureAwait(false);
            if (credential is null)
            {
                return Finish(new AttemptOutcome(false, AgentError.Create(
                    AgentErrorCode.Auth,
                    $"Provider '{c.Provider.Name}' has no credential configured.",
                    false,
                    messageKey: LlmErrorKeys.MissingCredential,
                    messageParams: new Dictionary<string, string> { ["provider"] = c.Provider.Name }), sawContent));
            }

            var decoration = _transport.Decorate(c.Provider, protocol, credential, context);
            if (credential is SignatureCredential && decoration.Signer is null)
            {
                return Finish(new AttemptOutcome(false, AgentError.Create(
                    AgentErrorCode.Auth, "A signature credential needs a request signer for this provider.", false), sawContent));
            }

            var (body, fixedHeaders) = await BuildBodyAsync(c, request, protocol, context, decoration, token).ConfigureAwait(false);
            var url = WireProtocols.JoinUrl(c.Provider.BaseUrl, protocol.PathOf());
            var payload = body.ToJsonString();

            // 每次发送(首发、以及 401 之后那次重发)都重新签名:时间戳式签名复用旧值等于没重试。
            HttpRequestMessage Build(string? bearerOverride) =>
                WireRequests.BuildHttp(url, payload, fixedHeaders, credential, protocol, decoration, _options.UserAgent, bearerOverride);

            ResetIdle();
            var response = await _http.SendAsync(Build(null), token).ConfigureAwait(false);

            // 401 之后强制刷新一次,就一次:主动的过期检查覆盖不了本机时钟偏和服务端主动吊销。
            // 不交给外层重试循环:401 归为 auth 且不可重试,外层会切到下一个供应商,最终错误是一句和真实原因(token 过期)毫无关系的话。
            // 只在 OAuth 凭据上做,且此刻 sawContent 必然为 false(流还没开始),不存在重发导致重复输出的问题。
            if (response.StatusCode == HttpStatusCode.Unauthorized && credential is OAuthCredential)
            {
                await DrainAsync(response, token).ConfigureAwait(false);
                // 刷的必须是这次用的那条 ref:拿旧槽去刷,刷新后的 token 写回旧槽,而下一次请求读的是账号 ref——401 一直复发,且每次都「刷新成功」。
                var fresh = await _credentials.RefreshNowAsync(credentialRef, token).ConfigureAwait(false);
                ResetIdle();
                response = await _http.SendAsync(Build(fresh.AccessToken), token).ConfigureAwait(false);
            }

            using (response)
            {
                httpStatus = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    // 必须把 body 读完,否则连接不会被释放。
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    var error = protocol == WireProtocol.Anthropic
                        ? AnthropicErrors.FromHttp(httpStatus.Value, text, c.Provider.CacheTtl, c.Provider.Name)
                        : OpenAiCommon.ToAgentError(httpStatus.Value, text);
                    if (ParseRetryAfter(response.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null, Now) is { } after)
                    {
                        error = error with { RetryAfterMs = after };
                    }

                    return Finish(new AttemptOutcome(false, error, sawContent));
                }

                ResetIdle();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var sse = SseReader.ReadResponseAsync(stream, response.Content.Headers.ContentType?.MediaType, token);
                await foreach (var ev in WireCodecs.DecodeAsync(protocol, sse).WithCancellation(token).ConfigureAwait(false))
                {
                    if (IsContent(ev) || ev is MessageStart)
                    {
                        ResetIdle();
                    }

                    if (ev is StreamError streamError)
                    {
                        return Finish(new AttemptOutcome(false, streamError.Error, sawContent));
                    }

                    switch (ev)
                    {
                        case MessageStart start:
                            responseModel = start.Model;
                            break;
                        case ToolCallStart:
                            toolCalls++;
                            break;
                    }

                    if (ttft is null && ev is TextDelta or ThinkingDelta or ToolCallStart or BlockOpaque)
                    {
                        ttft = Math.Max(0, (int)(Now - startedAt).TotalMilliseconds);
                    }

                    if (IsContent(ev))
                    {
                        sawContent = true;
                    }

                    if (ev is MessageEnd end)
                    {
                        usage = end.Usage;
                        stop = end.Stop;

                        // 计价按单次请求做,不能等一轮结束拿累计值算:档位由该次请求的输入总量决定,时段价由该次请求的发起时刻决定。
                        // 传 startedAt 而不是 now(落库那条用的是同一个时刻,两处必须相同);传 upstreamModel 而不是回包里的名字
                        // (服务端会悄悄换名字,两边各查各的会在同一轮上报出两个不同的金额)。
                        cost = CostOfAttempt(c, end.Usage, startedAt);
                        writer.TryWrite(end with { LatencyMs = Math.Max(0, (int)(Now - startedAt).TotalMilliseconds), Cost = cost });
                        break;
                    }

                    // 给 MessageStart 补上「这一段是谁给的」。这里是唯一的 choke point:三条解码路径都从这过,解码器一个都不用改。
                    writer.TryWrite(ev is MessageStart ms
                        ? ms with { ProviderId = c.Provider.Id, Api = protocol.NameOf(), UpstreamModel = c.Binding.UpstreamModel }
                        : ev);
                }
            }

            if (stop is null)
            {
                return Finish(new AttemptOutcome(false, AgentError.Create(AgentErrorCode.Network, "The upstream connection closed before the response completed.", messageKey: LlmErrorKeys.IncompleteResponse), sawContent));
            }

            RecordSuccess(c.Provider.Id, (int)(Now - startedAt).TotalMilliseconds, account?.CredentialRef ?? c.Provider.Id);
            if (account is not null)
            {
                _accounts?.ReportSuccess(account);
            }

            return Finish(new AttemptOutcome(true, null, sawContent));
        }
        catch (Exception e)
        {
            return Finish(Classify(e, c, parent, idle, sawContent));
        }
    }

    private CostInfo CostOfAttempt(Candidate c, TokenUsage usage, DateTimeOffset startedAt)
    {
        if (_prices is null)
        {
            return CostInfo.NotMetered;
        }

        try
        {
            return PricingCalculator.CostOf(_prices, c.Provider.Id, c.Binding.UpstreamModel, usage, startedAt);
        }
        catch (Exception)
        {
            // 计价抛异常绝不能掀掉整条流。按「算不出」记(不按「没接计价」):钩子接了却抛了,这一轮的总额确实不可信。
            return CostInfo.Unknown;
        }
    }

    private AttemptOutcome Classify(Exception e, Candidate c, CancellationToken parent, CancellationTokenSource idle, bool sawContent)
    {
        if (parent.IsCancellationRequested)
        {
            return new AttemptOutcome(false, AgentError.Create(AgentErrorCode.Aborted, "Aborted", false), sawContent);
        }

        if (e is OperationCanceledException && idle.IsCancellationRequested)
        {
            var seconds = (int)Math.Ceiling(_options.IdleTimeout().TotalSeconds);
            return new AttemptOutcome(false, AgentError.Create(
                AgentErrorCode.Network,
                $"No response progress from {c.Provider.Name} for {seconds} seconds.",
                false,
                messageKey: LlmErrorKeys.UpstreamTimeout,
                messageParams: new Dictionary<string, string> { ["provider"] = c.Provider.Name, ["seconds"] = seconds.ToString(CultureInfo.InvariantCulture) }), sawContent);
        }

        switch (e)
        {
            case RequestPatchException or ThinkingAdapterException:
                return new AttemptOutcome(false, AgentError.Create(
                    AgentErrorCode.Provider, e.Message, false,
                    messageKey: e is RequestPatchException ? LlmErrorKeys.InvalidRequestPatch : LlmErrorKeys.ThinkingUnsupported,
                    messageParams: new Dictionary<string, string> { ["detail"] = e.Message, ["code"] = (e as ThinkingAdapterException)?.Code ?? "" }), sawContent);
            case ImageInputException image:
                return new AttemptOutcome(false, image.Error, sawContent);
            // 刷新凭据失败:error 已经在 resolver 里分好类了(网络故障可重试、上游明确拒绝不可重试),原样用,不要在这一层重新猜。
            case CredentialAuthException auth:
                return new AttemptOutcome(false, auth.Error, sawContent);
            default:
                return new AttemptOutcome(false, AgentError.Create(AgentErrorCode.Network, $"Failed to connect to '{c.Provider.Name}': {ExceptionChain(e)}"), sawContent);
        }
    }

    /// <summary>
    /// 异常连同内层原因:HttpRequestException 自己只有「An error occurred while sending the request」,
    /// 真正的原因(DNS、连接被拒、TLS 握手、代理)在 InnerException 里(issue #200)。
    /// </summary>
    public static string ExceptionChain(Exception e)
    {
        var messages = new List<string>();
        for (var current = e; current is not null && messages.Count < 4; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Any(m => m.Contains(message, StringComparison.Ordinal)))
            {
                messages.Add(message);
            }
        }

        return string.Join(" → ", messages);
    }

    private static async Task DrainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 只是为了释放连接。
        }
        finally
        {
            response.Dispose();
        }
    }

    // ── 编码管线 ──

    /// <summary>
    /// 每次尝试:历史转换 → 解析图片引用(只在发出去的副本上)→ 编码 → 思考参数适配 → 用户请求补丁(禁止改 model/messages/tools/鉴权)
    /// → 再次强制思考偏好 → 重新应用 Anthropic 缓存和身份(补丁不能覆盖)→ 传输装饰。
    /// </summary>
    private async Task<(System.Text.Json.Nodes.JsonObject Body, IReadOnlyDictionary<string, string> Headers)> BuildBodyAsync(
        Candidate c, LlmRequest request, WireProtocol protocol, RequestContext context, TransportDecoration decoration, CancellationToken ct)
    {
        var target = HistoryTarget.For(protocol, c.Provider.Id, c.Binding.UpstreamModel, c.Binding.SupportsImages);
        var messages = HistoryTransform.Prepare(request.Messages, target);
        messages = await ResolveImagesAsync(messages, context, ct).ConfigureAwait(false);
        var prepared = request with { Messages = messages };

        return WireRequests.EncodeBody(c.Provider, c.Binding, prepared, protocol, context, decoration);
    }

    private async ValueTask<IReadOnlyList<Message>> ResolveImagesAsync(IReadOnlyList<Message> messages, RequestContext context, CancellationToken ct)
    {
        if (_images is null || !messages.Any(HasUnresolvedImage))
        {
            return messages;
        }

        var output = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            var parts = new List<Part>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ImagePart { DataRef: var r } image when IsRef(r):
                    {
                        var resolvedRef = await _images.ResolveAsync(r, image.Mime, context, ct).ConfigureAwait(false);
                        var resolvedMime = DataUrls.TryParseImage(resolvedRef, out var mime, out _) ? mime : image.Mime;
                        parts.Add(image with { Mime = resolvedMime, DataRef = resolvedRef });
                        break;
                    }

                    case ToolResultPart { Output.Images: { } images } result when images.Any(i => IsRef(i.DataRef)):
                    {
                        var resolved = new List<ToolOutputImage>();
                        foreach (var image in images)
                        {
                            if (IsRef(image.DataRef))
                            {
                                var resolvedRef = await _images.ResolveAsync(image.DataRef, image.Mime, context, ct).ConfigureAwait(false);
                                var resolvedMime = DataUrls.TryParseImage(resolvedRef, out var mime, out _) ? mime : image.Mime;
                                resolved.Add(image with { Mime = resolvedMime, DataRef = resolvedRef });
                            }
                            else
                            {
                                resolved.Add(image);
                            }
                        }

                        parts.Add(result with { Output = result.Output with { Images = resolved } });
                        break;
                    }

                    default:
                        parts.Add(part);
                        break;
                }
            }

            output.Add(message with { Parts = parts });
        }

        return output;
    }

    private static bool IsRef(string dataRef) => dataRef.StartsWith("ncw://", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnresolvedImage(Message message) => message.Parts.Any(p =>
        p is ImagePart image && IsRef(image.DataRef)
        || p is ToolResultPart { Output.Images: { } images } && images.Any(i => IsRef(i.DataRef)));

    // ── 错误文案(只带 key + 参数,界面文字由控制端生成) ──

    private AgentError AllAccountsLimitedError(ProviderConfig provider)
    {
        var parameters = new Dictionary<string, string> { ["provider"] = provider.Name };
        if (_accounts?.EarliestRecoveryAt(provider.Id) is not { } recoveryAt)
        {
            return AgentError.Create(
                AgentErrorCode.NoHealthyProvider,
                $"None of the accounts of provider '{provider.Name}' are usable; check whether they were disabled or need to sign in again.",
                false,
                messageKey: LlmErrorKeys.AllAccountsUnusable,
                messageParams: parameters);
        }

        // 必须给得出「最早什么时候能再用」:没有这个数,用户会反复点重发,每一次都在给冷却期续命。时刻以时间戳下发,格式化是控制端的事。
        parameters["recoveryAt"] = recoveryAt.ToString(CultureInfo.InvariantCulture);
        return AgentError.Create(
            AgentErrorCode.RateLimit,
            $"All accounts of provider '{provider.Name}' are rate limited.",
            true,
            retryAfterMs: (int)Math.Max(0, recoveryAt - Now.ToUnixTimeMilliseconds()),
            messageKey: LlmErrorKeys.AllAccountsRateLimited,
            messageParams: parameters);
    }

    /// <summary>钉住时的三条分支都必须明说「不会自动切到其它供应商」:用户对这个应用的既有心智是「配了多家就会自动兜底」。</summary>
    private AgentError NoCandidateError(ModelRef model)
    {
        var alias = model.Alias;
        if (model.ProviderId is { } pinned)
        {
            var provider = _config.Providers.FirstOrDefault(p => p.Id == pinned);
            if (provider is null)
            {
                return AgentError.Create(
                    AgentErrorCode.NoHealthyProvider,
                    $"The pinned provider was deleted; '{alias}' will not fall back to another provider.",
                    false,
                    messageKey: LlmErrorKeys.PinnedProviderMissing,
                    messageParams: new Dictionary<string, string> { ["model"] = alias });
            }

            if (!provider.Enabled)
            {
                return AgentError.Create(
                    AgentErrorCode.NoHealthyProvider,
                    $"Provider '{provider.Name}' is disabled; '{alias}' will not fall back to another provider.",
                    false,
                    messageKey: LlmErrorKeys.PinnedProviderDisabled,
                    messageParams: new Dictionary<string, string> { ["model"] = alias, ["provider"] = provider.Name });
            }

            return AgentError.Create(
                AgentErrorCode.NoHealthyProvider,
                $"Provider '{provider.Name}' no longer has model '{alias}'.",
                false,
                messageKey: LlmErrorKeys.PinnedModelMissing,
                messageParams: new Dictionary<string, string> { ["model"] = alias, ["provider"] = provider.Name });
        }

        var bindings = _config.Bindings.Where(b => b.Alias == alias).ToList();
        if (bindings.Count == 0)
        {
            return AgentError.Create(
                AgentErrorCode.NoHealthyProvider,
                $"No model alias '{alias}' is configured.",
                false,
                messageKey: LlmErrorKeys.UnknownAlias,
                messageParams: new Dictionary<string, string> { ["model"] = alias });
        }

        var now = Now;
        bool cooling;
        lock (_gate)
        {
            cooling = bindings.Any(b => _health.TryGetValue(b.ProviderId, out var h) && h.CooldownUntil is { } until && until > now);
        }

        return AgentError.Create(
            AgentErrorCode.NoHealthyProvider,
            cooling ? $"All providers of '{alias}' are cooling down; they recover automatically." : $"'{alias}' has no enabled provider.",
            cooling,
            messageKey: cooling ? LlmErrorKeys.AllProvidersCooling : LlmErrorKeys.NoEnabledProvider,
            messageParams: new Dictionary<string, string> { ["model"] = alias });
    }
}
