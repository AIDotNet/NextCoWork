using System.Text.Json.Nodes;
using Ncw.Agent.Catalog;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Providers;

/// <summary>
/// 目录 / 供应商领域类型 ↔ 协议 DTO(只有 Host 知道协议)。纯函数。
/// ★ 这里是凭据不外泄的最后一道关:映射只取摘要字段(<see cref="CredentialInfo"/> / <see cref="CredentialAuthInfo"/>),
/// 供应商的 <c>CredentialRef</c>、账号凭据 ref 都不映射出去;明文只有 <see cref="Reveal"/> 一个出口。
/// </summary>
internal static class ProviderMapping
{
    // ───────────────────────── 目录 ─────────────────────────

    public static string CurrencyName(Currency currency) => currency == Currency.Cny ? "cny" : "usd";

    public static Currency ParseCurrency(string? name) =>
        string.Equals(name, "cny", StringComparison.OrdinalIgnoreCase) ? Currency.Cny
        : string.Equals(name, "usd", StringComparison.OrdinalIgnoreCase) ? Currency.Usd
        : throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown currency '{name}'.");

    public static string EffortName(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        _ => "max",
    };

    private static PriceRatesDto ToDto(TokenRates r) =>
        new((double)r.Input, (double)r.Output, (double?)r.CacheRead, (double?)r.CacheWrite, (double?)r.CacheWrite1h, (double?)r.PerCall);

    public static ModelPriceDto? ToDto(ModelPricing? price) => price is null
        ? null
        : new ModelPriceDto(CurrencyName(price.Currency), [.. price.Tiers.Select(t => new PriceTierDto(t.UpToInputTokens, ToDto(t.Rate)))], price.Source);

    public static CatalogModelDto ToDto(CatalogModel m) => new(
        m.ProviderId, m.Id, m.Name, m.Family, m.ContextWindow, m.MaxOutputTokens,
        new CatalogCapabilitiesDto(m.Capabilities.ToolCall, m.Capabilities.Reasoning, m.Capabilities.Attachment, m.Capabilities.StructuredOutput,
            [.. m.Capabilities.InputModalities], [.. m.Capabilities.OutputModalities]),
        m.ReasoningEfforts is null ? null : [.. m.ReasoningEfforts.Select(EffortName)],
        ToDto(m.Price), m.Status, m.ReleaseDate, m.LastUpdated, m.Overridden);

    public static CatalogProviderDto ToDto(CatalogProvider p) => new(p.Id, p.Name, p.Api, p.Doc, [.. p.Env], p.Models.Count);

    public static CatalogErrorDto? ToDto(CatalogError? e) => e is null ? null : new CatalogErrorDto(e.Key, e.Detail, e.At.ToUnixTimeMilliseconds());

    public static CatalogStatusDto ToDto(CatalogStatus s) => new(
        s.Version, s.Source.ToString().ToLowerInvariant(), s.FetchedAt?.ToUnixTimeMilliseconds(), s.LastCheckedAt?.ToUnixTimeMilliseconds(),
        s.NextCheckAt?.ToUnixTimeMilliseconds(), s.ModelCount, s.ProviderCount, ToDto(s.LastError));

    public static ModelOverrideDto ToDto(ModelOverride o) => new(
        o.ProviderId, o.ModelId, o.DisplayName, o.ContextWindow, o.MaxOutputTokens, o.ToolCall, o.Reasoning, o.Vision,
        o.Price is { } p ? new PriceOverrideDto(CurrencyName(p.Currency), (double)p.Input, (double)p.Output, (double?)p.CacheRead, (double?)p.CacheWrite, (double?)p.CacheWrite1h) : null,
        o.Deleted);

    public static ModelOverride FromDto(ModelOverrideDto o)
    {
        if (string.IsNullOrWhiteSpace(o.ModelId))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A model id is required.");
        }

        PriceOverride? price = null;
        if (o.Price is { } p)
        {
            if (!double.IsFinite(p.Input) || !double.IsFinite(p.Output) || p.Input < 0 || p.Output < 0)
            {
                throw new ProtocolException(ErrorCodes.InvalidRequest, "A price must be a non-negative number.");
            }

            price = new PriceOverride(ParseCurrency(p.Currency), (decimal)p.Input, (decimal)p.Output, (decimal?)p.CacheRead, (decimal?)p.CacheWrite, (decimal?)p.CacheWrite1h);
        }

        return new ModelOverride(o.ProviderId, o.ModelId, o.DisplayName, o.ContextWindow, o.MaxOutputTokens, o.ToolCall, o.Reasoning, o.Vision, price, o.Deleted);
    }

    public static CatalogRefreshResultDto ToDto(CatalogRefreshResult r) => new(
        r.Outcome switch
        {
            CatalogRefreshOutcome.Updated => CatalogRefreshOutcomes.Updated,
            CatalogRefreshOutcome.NotModified => CatalogRefreshOutcomes.NotModified,
            CatalogRefreshOutcome.Rejected => CatalogRefreshOutcomes.Rejected,
            _ => CatalogRefreshOutcomes.Failed,
        },
        r.ModelCount, ToDto(r.Error));

    // ───────────────────────── 供应商 ─────────────────────────

    public static string CacheTtlName(AnthropicCacheTtl ttl) => ttl == AnthropicCacheTtl.OneHour ? "1h" : "5m";

    public static ProviderDto ToDto(ProviderRecord p, bool hasCredential, string? oauthIssuer) => new(
        p.Id, p.Name, p.Protocol.NameOf(), p.BaseUrl, p.Priority, p.Enabled, CacheTtlName(p.CacheTtl),
        p.ProtocolOptions?.ToJsonString(), hasCredential, oauthIssuer);

    public static ModelAliasDto ToDto(ModelAliasRecord a)
    {
        var overrides = a.Extra["catalogOverrides"] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray()
            : [];
        return new ModelAliasDto(
            a.Alias, a.ProviderId, a.UpstreamModel, a.ContextWindow, a.MaxOutputTokens, a.DisplayName, a.ProtocolOverride?.NameOf(), a.Priority, a.Modality,
            a.IsEnabled, a.SupportsImages, overrides, a.Extra["thinkingConfig"]?.ToJsonString(), a.Extra["reasoningEfforts"]?.ToJsonString());
    }

    public static ProviderProblemDto ToDto(ProviderFileProblem p) => new(p.ProviderId, p.Key, p.Detail);

    public static CredentialAuthDto? ToDto(CredentialAuthInfo? a) =>
        a is null ? null : new CredentialAuthDto(a.Issuer, a.AccountId, a.Email, a.PlanType, a.ExpiresAt, a.Expired, a.NeedsReauth);

    public static string? SchemeName(SignatureScheme? scheme) => scheme switch
    {
        SignatureScheme.TencentTc3 => "tencentTc3",
        SignatureScheme.AwsSigV4 => "awsSigV4",
        _ => null,
    };

    public static CredentialInfoDto ToDto(CredentialInfo i) => new(i.HasKey, i.Last4, ToDto(i.Auth), SchemeName(i.SignatureScheme), i.AccessKeyId);

    public static RevealedCredentialDto Reveal(RevealedCredential credential) => credential switch
    {
        RevealedApiKey k => new RevealedCredentialDto("apiKey", ApiKey: k.ApiKey),
        RevealedOAuth o => new RevealedCredentialDto("oauth", AccessToken: o.AccessToken, RefreshToken: o.RefreshToken),
        RevealedSignature s => new RevealedCredentialDto("signature", Scheme: SchemeName(s.Scheme), AccessKeyId: s.AccessKeyId, SecretKey: s.SecretKey, Region: s.Region, SessionToken: s.SessionToken),
        _ => throw new ProtocolException(ErrorCodes.NotFound, "No key has been configured."),
    };

    public static ProviderAccountDto ToDto(ProviderAccountView v)
    {
        var r = v.Row;
        return new ProviderAccountDto(
            r.Id, r.ProviderId, r.Issuer, r.Label, r.Order, r.Enabled, r.Current, r.NeedsReauth,
            r.Limit is { } l ? new AccountLimitDto(l.Until, l.Since, l.Source switch
            {
                AccountLimitSource.Http429 => "http429",
                AccountLimitSource.QuotaExhausted => "quotaExhausted",
                _ => "manual",
            }, l.Reason) : null,
            r.Quota is { } q ? new QuotaDto(Window(q.Primary), Window(q.Secondary), q.CapturedAt) : null,
            r.CreatedAt, r.UpdatedAt, ToDto(v.Auth));

        static QuotaWindowDto? Window(QuotaWindow? w) => w is null ? null : new QuotaWindowDto(w.UsedPercent, w.WindowMinutes, w.ResetsAt);
    }

    public static ModelOptionDto ToDto(ModelOption o) => new(
        o.ProviderId, o.ProviderName, o.Alias, o.DisplayName, o.UpstreamModel, o.ContextWindow, o.MaxOutputTokens, o.Enabled, o.ProviderEnabled, o.SupportsImages, o.IsChatModel,
        o.ThinkingMode?.ToString().ToLowerInvariant(), o.ReasoningEfforts is null ? null : [.. o.ReasoningEfforts.Select(EffortName)], o.Protocol.NameOf());

    public static ProviderTestResultDto ToDto(ProviderTestResult r) => new(r.Ok, r.LatencyMs, r.Status, r.ErrorKey, r.Detail);

    public static OAuthProgressDto ToDto(OAuthProgress p) => new(
        p.ProviderId, p.Phase switch
        {
            OAuthPhase.Waiting => OAuthPhases.Waiting,
            OAuthPhase.Succeeded => OAuthPhases.Succeeded,
            OAuthPhase.Failed => OAuthPhases.Failed,
            _ => OAuthPhases.Cancelled,
        },
        p.AuthorizeUrl, p.UserCode, p.VerificationUrl, p.ExpiresInSeconds, p.NeedsPastedCode, p.ErrorKey, p.Detail);

    public static string ResolutionIssueName(ModelResolutionIssue issue) => issue switch
    {
        ModelResolutionIssue.None => ModelResolutionIssues.None,
        ModelResolutionIssue.UnknownAlias => ModelResolutionIssues.UnknownAlias,
        ModelResolutionIssue.PinnedProviderMissing => ModelResolutionIssues.PinnedProviderMissing,
        ModelResolutionIssue.PinnedProviderDisabled => ModelResolutionIssues.PinnedProviderDisabled,
        ModelResolutionIssue.PinnedModelMissing => ModelResolutionIssues.PinnedModelMissing,
        _ => ModelResolutionIssues.NoEnabledProvider,
    };

    public static ModelResolutionDto ToDto(ModelResolution r) => r.Binding is { } b
        ? new ModelResolutionDto(true, ModelResolutionIssues.None, b.ProviderId, b.Alias, b.UpstreamModel, b.ContextWindow, b.MaxOutputTokens, b.SupportsImages,
            r.Protocol?.NameOf(), r.Info?.DisplayName, ToDto(r.Info?.Price))
        : new ModelResolutionDto(false, ResolutionIssueName(r.Issue), null, null, null, 0, 0, false, null, null, null);

    // ───────────────────────── 错误 ─────────────────────────

    /// <summary>
    /// 业务错误(<see cref="ProviderException"/>)→ 协议错误:粗粒度的错误码给路由 / 通用处理,稳定的 key + 参数放在 <c>details</c>(<see cref="ProviderErrorDto"/>),控制端据此翻译。
    /// 诊断文本里的 URL / 响应片段已由 Catalog 层脱敏;参数里也不含任何凭据。
    /// </summary>
    public static ProtocolException ToProtocol(ProviderException e)
    {
        var (code, retryable) = e.Key switch
        {
            ProviderErrorKeys.UnknownProvider or ProviderErrorKeys.UnknownAlias or ProviderErrorKeys.UnknownAccount or ProviderErrorKeys.NoCredential => (ErrorCodes.NotFound, false),
            ProviderErrorKeys.AliasExists or ProviderErrorKeys.LoginCancelled => (ErrorCodes.Conflict, false),
            ProviderErrorKeys.NoOAuth => (ErrorCodes.Unsupported, false),
            ProviderErrorKeys.ManagedProvider => (ErrorCodes.Forbidden, false),
            ProviderErrorKeys.FetchFailed or ProviderErrorKeys.FetchTimeout => (ErrorCodes.Unavailable, true),
            ProviderErrorKeys.FetchRejected or ProviderErrorKeys.FetchNotFound or ProviderErrorKeys.FetchEmpty or ProviderErrorKeys.LoginFailed
                or ProviderErrorKeys.NoLoginInProgress or ProviderErrorKeys.FileBroken => (ErrorCodes.Unavailable, false),
            _ => (ErrorCodes.InvalidRequest, false),
        };
        return new ProtocolException(code, e.Message, retryable,
            FrameCodec.EncodePayload(new ProviderErrorDto(e.Key, new Dictionary<string, string>(e.Parameters)), PayloadShapes.GetShape<ProviderErrorDto>()));
    }
}
