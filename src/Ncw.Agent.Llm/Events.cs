namespace Ncw.Agent.Llm;

public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    Refusal,
    Aborted,
}

/// <summary>
/// 一次请求的用量。<see cref="ReasoningTokens"/> 是 <see cref="OutputTokens"/> 的子集,计价和累加时绝不能再加一遍;
/// <see cref="InputTokens"/> 不含缓存读写(OpenAI 的 prompt_tokens 含缓存,解码时已扣掉)。
/// </summary>
public sealed record TokenUsage(
    long InputTokens = 0,
    long OutputTokens = 0,
    long? CacheCreationInputTokens = null,
    long? CacheReadInputTokens = null,
    long? CacheCreation1hInputTokens = null,
    long? ReasoningTokens = null)
{
    /// <summary>逐项相加;可选项两边都没有时保持缺省。</summary>
    public TokenUsage Add(TokenUsage other)
    {
        static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
        return new TokenUsage(
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            Sum(CacheCreationInputTokens, other.CacheCreationInputTokens),
            Sum(CacheReadInputTokens, other.CacheReadInputTokens),
            Sum(CacheCreation1hInputTokens, other.CacheCreation1hInputTokens),
            Sum(ReasoningTokens, other.ReasoningTokens));
    }
}

public enum Currency
{
    Usd,
    Cny,
}

public enum CostState
{
    /// <summary>这条链路没接计价:累加方保持已有的累计值不动。</summary>
    NotMetered,

    /// <summary>接了计价但查不到价格(或用到了没有价格的 token 类别):累加方把整轮锁定为「未知」。</summary>
    Unknown,

    /// <summary>算出来了,累加。</summary>
    Known,
}

/// <summary>
/// 费用三态(01-llm §2):没接计价 / 查不到价格 / 有值。金额是整数微单位(百万分之一货币单位)。
/// 不同币种不能相加(没有汇率源):相加得到 <see cref="CostState.Unknown"/>。
/// </summary>
public readonly record struct CostInfo(CostState State, long Micros = 0, Currency Currency = Currency.Usd)
{
    public static CostInfo NotMetered => new(CostState.NotMetered);

    public static CostInfo Unknown => new(CostState.Unknown);

    public static CostInfo Of(long micros, Currency currency) => new(CostState.Known, micros, currency);

    /// <summary>
    /// 累加一次请求的费用。Unknown 一旦出现就锁死整轮;NotMetered 不改变已有的累计;币种不同判 Unknown。
    /// </summary>
    public CostInfo Add(CostInfo next)
    {
        if (State == CostState.Unknown || next.State == CostState.Unknown)
        {
            return Unknown;
        }

        if (next.State == CostState.NotMetered)
        {
            return this;
        }

        if (State == CostState.NotMetered)
        {
            return next;
        }

        return Currency == next.Currency ? Of(Micros + next.Micros, Currency) : Unknown;
    }
}

/// <summary>流式事件(01-llm §2)。<c>Index</c> 是内容块下标,块可以交错,按下标归位。</summary>
public abstract record LlmEvent;

/// <summary>
/// <paramref name="Model"/> 是上游回包里的真实模型名(用于显示);<paramref name="ProviderId"/> / <paramref name="Api"/> / <paramref name="UpstreamModel"/>
/// 由路由补上(解码器自己不知道也不该知道),三者合起来就是这条助手消息的 <see cref="Provenance"/>。
/// </summary>
public sealed record MessageStart(string Model, string? ProviderId = null, string? Api = null, string? UpstreamModel = null) : LlmEvent;

public sealed record TextDelta(int Index, string Text) : LlmEvent;

public sealed record ThinkingDelta(int Index, string Text) : LlmEvent;

public sealed record ToolCallStart(int Index, string CallId, string Name) : LlmEvent;

public sealed record ToolCallDelta(int Index, string CallId, string ArgsDelta) : LlmEvent;

public sealed record ToolCallEnd(int Index, string CallId) : LlmEvent;

/// <summary>整个块的厂商透传数据(签名、加密推理)。和同下标的思考文字合进同一个思考块。</summary>
public sealed record BlockOpaque(int Index, ProtocolOpaque Opaque) : LlmEvent;

public sealed record ProviderRetry(int Attempt, int DelayMs, string Reason) : LlmEvent;

public sealed record ProviderSwitch(string From, string To, string Reason) : LlmEvent;

/// <summary><paramref name="LatencyMs"/> 是这一次上游请求从发出到本条消息收完的耗时(路由补上);<paramref name="Cost"/> 同理按单次请求计价。</summary>
public sealed record MessageEnd(StopReason Stop, TokenUsage Usage, int? LatencyMs = null, CostInfo Cost = default) : LlmEvent;

/// <summary>流返回后永不抛异常:所有错误(包括取消)都变成这个事件,最终消息保留已收到的内容和用量。</summary>
public sealed record StreamError(AgentError Error) : LlmEvent;
