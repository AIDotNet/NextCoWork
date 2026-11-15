using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Catalog;

/// <summary>OAuth 登录态在列表 / 广播里可以给控制端看的那部分。这里<b>一个 token 字符都没有</b>。</summary>
/// <param name="ExpiresAt">null = 过期时间未知(有的家 <c>expires_in</c> 就是 null),不是永不过期;不该渲染成日期,更不该当成已过期。</param>
/// <param name="Expired">由 Agent 按自己的时钟算好,不让控制端拿本地时间去比(两端不是同一个时钟源)。未知时恒为 false。</param>
public sealed record CredentialAuthInfo(string Issuer, string AccountId, string? Email, string? PlanType, long? ExpiresAt, bool Expired, bool NeedsReauth);

/// <summary>设置页列表 / 初始化路径只返回摘要信息,永不顺带回传明文。</summary>
/// <param name="Last4">API Key 的后四位。OAuth 为 null(access token 每小时都变,零识别价值);签名凭据为 null(尾四位是 SecretKey 的尾四位,把密钥的一部分印在屏幕上)。</param>
public sealed record CredentialInfo(
    bool HasKey,
    string? Last4,
    CredentialAuthInfo? Auth = null,
    SignatureScheme? SignatureScheme = null,
    string? AccessKeyId = null);

/// <summary>只有用户显式点击「查看」才经专用方法返回一次;列表、广播和初始化路径都不得携带它。</summary>
public abstract record RevealedCredential;

public sealed record RevealedApiKey(string ApiKey) : RevealedCredential;

public sealed record RevealedOAuth(string AccessToken, string RefreshToken) : RevealedCredential;

public sealed record RevealedSignature(SignatureScheme Scheme, string AccessKeyId, string SecretKey, string? Region, string? SessionToken) : RevealedCredential;

public static class CredentialInfos
{
    public static CredentialInfo Of(string? plaintext, long nowMs)
    {
        switch (CredentialFormat.Parse(plaintext))
        {
            case null:
                return new CredentialInfo(false, null);
            case ApiKeyCredential key:
                return new CredentialInfo(true, key.ApiKey.Length <= 4 ? key.ApiKey : key.ApiKey[^4..]);
            case SignatureCredential signature:
                // 签名凭据的身份标识是 accessKeyId(不是秘密),不回 last4。
                return new CredentialInfo(true, null, null, signature.Scheme, signature.AccessKeyId);
            case OAuthCredential oauth:
                return new CredentialInfo(true, null, AuthOf(oauth, nowMs));
            default:
                return new CredentialInfo(false, null);
        }
    }

    public static CredentialAuthInfo AuthOf(OAuthCredential oauth, long nowMs) => new(
        oauth.Issuer,
        oauth.AccountId,
        oauth.Email,
        oauth.PlanType,
        oauth.ExpiresAt,
        oauth.ExpiresAt is { } expires && expires <= nowMs,
        oauth.NeedsReauth);

    public static RevealedCredential? Reveal(string? plaintext) => CredentialFormat.Parse(plaintext) switch
    {
        ApiKeyCredential key => new RevealedApiKey(key.ApiKey),
        OAuthCredential oauth => new RevealedOAuth(oauth.AccessToken, oauth.RefreshToken),
        SignatureCredential s => new RevealedSignature(s.Scheme, s.AccessKeyId, s.SecretKey, s.Region, s.SessionToken),
        _ => null,
    };
}

/// <summary>账号行 + 它那条凭据的摘要(<c>Auth</c> 读密文失败时缺席,其余字段照常下发:一条坏凭据不该让整家的账号列表在界面上凭空消失)。</summary>
public sealed record ProviderAccountView(ProviderAccountRow Row, CredentialAuthInfo? Auth);

/// <summary>「这家走不走 OAuth」查的是预设,不是凭据——还没登录时凭据是空的,照凭据判会把一家 OAuth 供应商认成 API Key 供应商,界面画出一个填了也没用的密钥框。</summary>
public interface IProviderPresets
{
    string? OAuthIssuerOf(string providerId);
}

/// <summary>2.x 预设表里带 <c>oauthIssuer</c> 的几家(<c>shared/domain/presets.ts</c>)。完整预设目录(地址、协议、图标)属于客户端,不在 Agent。</summary>
public sealed class BuiltinProviderPresets : IProviderPresets
{
    public static readonly BuiltinProviderPresets Instance = new();

    private static readonly Dictionary<string, string> Issuers = new(StringComparer.Ordinal)
    {
        ["codex"] = "chatgpt",
        ["grok-build"] = "grok-build",
        ["ollama-cloud"] = "ollama-cloud",
        ["kimi-coding"] = "kimi-code",
        ["zhipu-coding"] = "zcode-bigmodel",
        ["zai-coding"] = "zcode-zai",
    };

    public string? OAuthIssuerOf(string providerId) => Issuers.GetValueOrDefault(providerId);
}
