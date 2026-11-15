namespace Ncw.Agent.Llm;

public enum AgentErrorCode
{
    /// <summary>凭据无效或缺失:结束整个 Run。</summary>
    Auth,
    RateLimit,
    ContextLength,
    Network,
    /// <summary>调用方主动取消。不是故障。</summary>
    Aborted,
    ToolFailed,
    /// <summary>上游返回了无法归一化的东西。</summary>
    Provider,
    /// <summary>供应商明确拒绝了提示词缓存字段:配置问题,不重试、不换供应商。</summary>
    CacheUnsupported,
    /// <summary>别名下没有可用的供应商:结束整个 Run。</summary>
    NoHealthyProvider,
    Conflict,
    Unknown,
}

/// <summary>
/// 错误。<paramref name="Message"/> 是英文诊断串或上游原文;本层本地生成的错误同时带 <paramref name="MessageKey"/> 和
/// <paramref name="MessageParams"/>,由控制端翻译成界面文字。
/// </summary>
public sealed record AgentError(
    AgentErrorCode Code,
    string Message,
    bool Retryable,
    int? Status = null,
    int? RetryAfterMs = null,
    string? MessageKey = null,
    IReadOnlyDictionary<string, string>? MessageParams = null)
{
    /// <summary>默认可重试性:网络和限流可重试,其余不可。</summary>
    public static AgentError Create(
        AgentErrorCode code,
        string message,
        bool? retryable = null,
        int? status = null,
        int? retryAfterMs = null,
        string? messageKey = null,
        IReadOnlyDictionary<string, string>? messageParams = null) =>
        new(code, message, retryable ?? code is AgentErrorCode.Network or AgentErrorCode.RateLimit, status, retryAfterMs, messageKey, messageParams);

    /// <summary>只有这两类会终止整个 Run(01-llm §5)。</summary>
    public bool IsFatal => Code is AgentErrorCode.Auth or AgentErrorCode.NoHealthyProvider;
}

/// <summary>本层本地错误的 MessageKey(控制端按它翻译,参数在 MessageParams 里)。</summary>
public static class LlmErrorKeys
{
    public const string InvalidResponse = "llm.invalidResponse";
    public const string IncompleteResponse = "llm.incompleteResponse";
    public const string UpstreamTimeout = "llm.upstreamTimeout";
    public const string CacheUnsupported = "llm.cacheUnsupported";
    public const string CacheUnsupportedUnnamed = "llm.cacheUnsupportedUnnamed";
    public const string MissingCredential = "llm.missingCredential";
    public const string AllAccountsUnusable = "llm.allAccountsUnusable";
    public const string AllAccountsRateLimited = "llm.allAccountsRateLimited";
    public const string PinnedProviderMissing = "llm.pinnedProviderMissing";
    public const string PinnedProviderDisabled = "llm.pinnedProviderDisabled";
    public const string PinnedModelMissing = "llm.pinnedModelMissing";
    public const string UnknownAlias = "llm.unknownAlias";
    public const string AllProvidersCooling = "llm.allProvidersCooling";
    public const string NoEnabledProvider = "llm.noEnabledProvider";
    public const string InvalidWorkspaceId = "llm.invalidWorkspaceId";
    public const string InvalidToolName = "llm.invalidToolName";
    public const string ThinkingUnsupported = "llm.thinkingUnsupported";
    public const string InvalidRequestPatch = "llm.invalidRequestPatch";
    public const string ImageInput = "llm.imageInput";
}
