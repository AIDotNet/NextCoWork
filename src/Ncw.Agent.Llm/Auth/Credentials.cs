using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Auth;

/// <summary>
/// 供应商凭据:同一个槽里可能是一把 API Key,也可能是一组 OAuth token,或一对签名密钥。
/// 存储格式沿用 2.x(凭据表里的那个字符串,NCK1 加密由 Storage 层负责):API Key 是裸字符串,其余是带 <c>kind</c> 的 JSON。
/// </summary>
public abstract record ProviderCredential;

public sealed record ApiKeyCredential(string ApiKey) : ProviderCredential;

/// <summary>
/// <paramref name="ExpiresAt"/> 是<b>绝对毫秒时间戳</b>(相对值落盘就开始腐烂);null = 过期时间未知,不是「永不过期」:
/// 编一个假的过期时间比承认不知道更糟,未知时不主动刷新,靠 401 之后的强制刷新兜底。
/// </summary>
public sealed record OAuthCredential(
    string Issuer,
    string AccessToken,
    string RefreshToken,
    long? ExpiresAt,
    string AccountId,
    string? Email = null,
    string? PlanType = null,
    bool NeedsReauth = false,
    long? RefreshedAt = null) : ProviderCredential;

public enum SignatureScheme
{
    TencentTc3,
    AwsSigV4,
}

/// <summary>签名凭据(签名式鉴权):一对长期密钥,没有可刷新的东西。私钥永远不进请求头,每次请求重新签名。</summary>
public sealed record SignatureCredential(
    SignatureScheme Scheme,
    string AccessKeyId,
    string SecretKey,
    string? Region = null,
    string? SessionToken = null) : ProviderCredential;

/// <summary>OAuth 授权服务器的身份。加一家只需要在这里加一个值。</summary>
public static class OAuthIssuers
{
    public static readonly IReadOnlyList<string> All = ["chatgpt", "zcode-zai", "zcode-bigmodel", "kimi-code", "grok-build", "ollama-cloud"];

    public static bool IsKnown(string? issuer) => issuer is not null && All.Contains(issuer);
}

// ── 存储形状(源生成,不用反射) ──

internal sealed class OAuthCredentialDto
{
    public string Kind { get; set; } = "oauth";

    public string Issuer { get; set; } = "";

    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExpiresAt { get; set; }

    public string AccountId { get; set; } = "";

    public string? Email { get; set; }

    public string? PlanType { get; set; }

    public bool? NeedsReauth { get; set; }

    public long? RefreshedAt { get; set; }
}

internal sealed class SignatureCredentialDto
{
    public string Kind { get; set; } = "signature";

    public string Scheme { get; set; } = "";

    public string AccessKeyId { get; set; } = "";

    public string SecretKey { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Region { get; set; }

    public string? SessionToken { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OAuthCredentialDto))]
[JsonSerializable(typeof(SignatureCredentialDto))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;

public static class CredentialFormat
{
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// 解析存储的字符串。判据是「能不能解成一个带已知 kind 的对象」,而不是版本号字段(版本号要写迁移)。
    /// 解析失败一律退回「当作裸 API key」,绝不抛:一把恰好以 <c>{</c> 开头的 key 抛异常,用户看到的是「还没有配置密钥」——一次无法自查的假故障。
    /// OAuth / 签名记录缺任何必需字段判坏(返回 null),不退回当裸 key(那会拿整段 JSON 去当 Bearer)。
    /// </summary>
    public static ProviderCredential? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (raw[0] != '{')
        {
            return new ApiKeyCredential(raw);
        }

        if (!Jsn.TryParse(raw, out var o) || !o.IsObject())
        {
            return new ApiKeyCredential(raw);
        }

        switch (o.Str("kind"))
        {
            case "api-key":
                return NonEmpty(o.Str("apiKey")) is { } key ? new ApiKeyCredential(key) : null;

            case "signature":
            {
                var scheme = o.Str("scheme") switch
                {
                    "tencent-tc3" => (SignatureScheme?)SignatureScheme.TencentTc3,
                    "aws-sigv4" => SignatureScheme.AwsSigV4,
                    _ => null,
                };
                var accessKeyId = NonEmpty(o.Str("accessKeyId"));
                var secretKey = NonEmpty(o.Str("secretKey"));
                if (scheme is null || accessKeyId is null || secretKey is null)
                {
                    return null;
                }

                // region 只有 null 与省略两种合法的「没有」;是个东西但不是字符串则判坏。
                var regionProp = o.Prop("region");
                if (regionProp is { ValueKind: not (JsonValueKind.Null or JsonValueKind.String) })
                {
                    return null;
                }

                return new SignatureCredential(scheme.Value, accessKeyId, secretKey, regionProp?.GetString(), NonEmpty(o.Str("sessionToken")));
            }

            case "oauth":
            {
                var issuer = o.Str("issuer");
                var access = NonEmpty(o.Str("accessToken"));
                var refresh = NonEmpty(o.Str("refreshToken"));
                var account = NonEmpty(o.Str("accountId"));

                // expiresAt 有三种合法情况:有限数字、显式 null、整个键缺失(后两种都是「不知道」)。只有「是个东西但不是数字」才判记录坏掉。
                long? expiresAt = null;
                if (o.Prop("expiresAt") is { } exp && exp.ValueKind != JsonValueKind.Null)
                {
                    if (exp.ValueKind != JsonValueKind.Number || !exp.TryGetDouble(out var d) || !double.IsFinite(d))
                    {
                        return null;
                    }

                    expiresAt = (long)d;
                }

                if (!OAuthIssuers.IsKnown(issuer) || access is null || refresh is null || account is null)
                {
                    return null;
                }

                return new OAuthCredential(
                    issuer!, access, refresh, expiresAt, account,
                    NonEmpty(o.Str("email")), NonEmpty(o.Str("planType")),
                    o.Bool("needsReauth") == true,
                    o.Count("refreshedAt"));
            }

            default:
                return new ApiKeyCredential(raw);
        }
    }

    /// <summary>
    /// API Key 回吐的是<b>裸字符串</b>,不是 JSON:这是降级兼容的守门线——退回旧版本时库里那把 key 照样能用;
    /// 重写成 JSON 的话旧版本会把整个 JSON 当 Bearer 发出去,401 且没有任何线索指向存储格式变了。
    /// </summary>
    public static string Serialize(ProviderCredential credential) => credential switch
    {
        ApiKeyCredential key => key.ApiKey,
        OAuthCredential o => System.Text.Json.JsonSerializer.Serialize(new OAuthCredentialDto
        {
            Issuer = o.Issuer,
            AccessToken = o.AccessToken,
            RefreshToken = o.RefreshToken,
            ExpiresAt = o.ExpiresAt,
            AccountId = o.AccountId,
            Email = o.Email,
            PlanType = o.PlanType,
            NeedsReauth = o.NeedsReauth ? true : null,
            RefreshedAt = o.RefreshedAt,
        }, LlmJsonContext.Default.OAuthCredentialDto),
        SignatureCredential s => System.Text.Json.JsonSerializer.Serialize(new SignatureCredentialDto
        {
            Scheme = s.Scheme == SignatureScheme.TencentTc3 ? "tencent-tc3" : "aws-sigv4",
            AccessKeyId = s.AccessKeyId,
            SecretKey = s.SecretKey,
            Region = s.Region,
            SessionToken = s.SessionToken,
        }, LlmJsonContext.Default.SignatureCredentialDto),
        _ => throw new ArgumentOutOfRangeException(nameof(credential)),
    };

    /// <summary>
    /// 发请求时真正塞进鉴权头的那个串。签名凭据在这条路上一律抛错:它的「发请求」是整套签名重算,
    /// 静默返回半段内容会发出一个畸形头,报错在上游那一侧。
    /// </summary>
    public static string BearerOf(ProviderCredential credential) => credential switch
    {
        ApiKeyCredential key => key.ApiKey,
        OAuthCredential oauth => oauth.AccessToken,
        _ => throw new InvalidOperationException("A signature credential cannot be sent as a Bearer token."),
    };
}

/// <summary>凭据存取(实现由 Storage 层的 CredentialStore 适配;内部是 NCK1 加密)。</summary>
public interface ICredentialStore
{
    ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct);

    ValueTask SetAsync(string credentialRef, string value, CancellationToken ct);
}

public static class CredentialRefs
{
    /// <summary>账号密文的 ref:<c>provider:{providerId}#{accountId}</c>。前缀仍是 <c>provider:</c>,云同步按这个前缀打脏标记。</summary>
    public static string ForAccount(string providerId, string accountId) => $"provider:{providerId}#{accountId}";
}
