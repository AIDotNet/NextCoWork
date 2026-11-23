using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Routing;

public sealed record ProviderConfig(
    string Id,
    string Name,
    string BaseUrl,
    WireProtocol Protocol,
    int Priority,
    bool Enabled,
    string CredentialRef,
    AnthropicCacheTtl CacheTtl = AnthropicCacheTtl.FiveMinutes);

/// <summary>一个别名在一个供应商上的绑定(同一个别名可以由多个供应商提供——这正是别名表存在的理由)。</summary>
public sealed record ModelBinding(
    string Alias,
    string ProviderId,
    string UpstreamModel,
    int ContextWindow,
    int MaxOutputTokens,
    bool Enabled = true,
    bool SupportsImages = true,
    WireProtocol? ProtocolOverride = null,
    ThinkingConfig? Thinking = null,
    IReadOnlyList<ReasoningEffort>? ReasoningEfforts = null,
    AdapterPreset Preset = AdapterPreset.Auto,
    IReadOnlyList<PatchRule>? Patches = null);

/// <summary>配置来源。用属性而不是快照:设置页改完供应商立刻生效,不必重建路由。</summary>
public interface IProviderConfigSource
{
    IReadOnlyList<ProviderConfig> Providers { get; }

    IReadOnlyList<ModelBinding> Bindings { get; }

    /// <summary>旁路开关落在路由器:关掉时只取优先级最高的一家,不做健康评分和切换(延迟最低、路径最短)。</summary>
    bool FailoverEnabled { get; }
}

/// <summary>图片引用解析:<c>ncw://</c> → <c>data:</c> URL。只在发出去的副本上做,存储里的消息永远保持引用。</summary>
public interface IImageResolver
{
    /// <summary>失败时抛 <see cref="ImageInputException"/>。</summary>
    ValueTask<string> ResolveAsync(string dataRef, string mime, RequestContext context, CancellationToken ct);
}

public sealed class ImageInputException(AgentError error) : Exception(error.Message)
{
    public AgentError Error { get; } = error;
}

public sealed record SignedRequest(IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyDictionary<string, string>? Query = null);

/// <summary>
/// 签名式鉴权(Ollama 等):头和 query 在每次发送时现算(时间戳式签名复用旧值等于没重试;401 之后的重发也重新签)。
/// 签名用的 path 是最终 URL 的 pathname,不是编码器的相对路径。私钥永远不进请求头。
/// </summary>
public interface IRequestSigner
{
    SignedRequest Sign(string method, string path);
}

/// <summary>凭据与供应商决定的那部分请求形状(额外的头、被钉死的 body 字段、签名)。供应商自己的硬约束压过模型级自定义。</summary>
public sealed record TransportDecoration(
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<string>? DropHeaders = null,
    Func<JsonObject, JsonObject>? Body = null,
    IRequestSigner? Signer = null);

/// <summary>传输装饰(01-llm §3):供应商相关的处理放在这里,编解码器保持纯函数。默认恒等。</summary>
public interface ITransportDecorator
{
    TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context);
}

public sealed class NoTransportDecoration : ITransportDecorator
{
    public static readonly NoTransportDecoration Instance = new();

    public TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context) => new();
}

/// <summary>每次 HTTP 尝试一条用量记录(01-llm §8):写入时冻结费用,不回算。落库由 Storage 层的实现负责。</summary>
public sealed record UsageAttempt(
    string Id,
    DateTimeOffset At,
    string RunId,
    string WorkspaceId,
    string SessionId,
    int Attempt,
    string ProviderId,
    string ProviderName,
    WireProtocol Protocol,
    string Endpoint,
    string Alias,
    string UpstreamModel,
    string? ResponseModel,
    TokenUsage Usage,
    int LatencyMs,
    int? TimeToFirstTokenMs,
    bool Ok,
    int? HttpStatus,
    AgentErrorCode? ErrorKind,
    string? ErrorMessage,
    StopReason? Stop,
    int ToolCalls,
    CostInfo Cost);

public interface IUsageSink
{
    /// <summary>同步调用;实现抛出的异常会被路由吞掉(遥测永远不能让请求失败)。</summary>
    void OnAttempt(UsageAttempt attempt);
}

/// <summary>退避睡眠。可注入,测试里不必真的睡。</summary>
public interface IDelayer
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class TimeProviderDelayer(TimeProvider clock) : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, clock, ct);
}

public sealed class LlmRouterOptions
{
    /// <summary>网络抖动的退避基数(毫秒级自愈的故障,退得越快恢复越快)。</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 限流(429)和可重试的 5xx 的退避基数,刻意和 <see cref="BaseDelay"/> 不是一个数:配额窗口是分钟级的、网关 5xx 的恢复窗口至少是秒级,
    /// 500ms 起步的退避三次机会两秒内用光,用户看到的是「报错一条接一条地刷」。有 Retry-After 时只听它的。
    /// </summary>
    public TimeSpan RateLimitFloor { get; init; } = TimeSpan.FromSeconds(4);

    public TimeSpan ProviderErrorFloor { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>退避硬上限。<c>Retry-After: 3600</c> 是真会出现的,照着睡一小时和卡死没有区别。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>空闲超时,每个事件重置;默认 600 秒,可配 60–3600。每次请求时重新求值,改完立刻对下一条请求生效。</summary>
    public Func<TimeSpan> IdleTimeout { get; init; } = () => TimeSpan.FromSeconds(600);

    /// <summary>设置项(秒)→ 空闲超时:可配 60–3600,越界夹到边界。</summary>
    public static Func<TimeSpan> IdleTimeoutFromSettings(Func<int> seconds) =>
        () => TimeSpan.FromSeconds(Math.Clamp(seconds(), MinIdleSeconds, MaxIdleSeconds));

    public const int MinIdleSeconds = 60;

    public const int MaxIdleSeconds = 3600;

    public string UserAgent { get; init; } = "NextCoWork";

    /// <summary>连续失败到这个数就判不健康并冷却。</summary>
    public int UnhealthyAfter { get; init; } = 3;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record ProviderHealth(
    string ProviderId,
    bool Healthy,
    double Score,
    int ConsecutiveFailures,
    DateTimeOffset? CooldownUntil,
    int? LastLatencyMs,
    string? LastError,
    DateTimeOffset LastCheckedAt);
