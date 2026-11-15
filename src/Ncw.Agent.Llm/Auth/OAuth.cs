using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Auth;

/// <summary>上游 HTTP 的唯一出口(可注入):路由、OAuth 刷新都经过它,测试里用假的 <see cref="HttpMessageHandler"/> 包一层即可。</summary>
public interface IUpstreamHttp
{
    /// <summary>必须以「响应头到达即返回」的方式发送(流式响应体由调用方读)。</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class HttpClientUpstream(HttpClient client) : IUpstreamHttp
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
}

/// <summary>换 token 或刷新之后得到的身份。<paramref name="RefreshToken"/> 为空表示上游没回新的(沿用旧的)。</summary>
public sealed record OAuthIdentity(
    string AccessToken,
    string? RefreshToken,
    long? ExpiresAt,
    string AccountId,
    string? Email = null,
    string? PlanType = null);

/// <summary>从 token 端点的响应里提取身份;缺必需字段返回 null(按「刷新回来的凭据不完整,需要重新登录」处理)。</summary>
public delegate OAuthIdentity? OAuthIdentityParser(JsonElement tokenResponse, long nowMs);

public sealed record OAuthRefreshContext(IUpstreamHttp Http, long NowMs, CancellationToken Ct);

/// <summary>这家自己实现的刷新(不是标准 <c>grant_type=refresh_token</c> 的那些)。确知失效时抛 <see cref="CredentialAuthException"/>,其它异常按网络故障处理。</summary>
public delegate Task<OAuthIdentity?> OAuthRefreshHook(OAuthCredential credential, OAuthRefreshContext context);

/// <summary>一家 OAuth 的规格:一家一行数据,不是一堆 if。各家细节(ChatGPT、GLM、Kimi、Grok、Ollama)按 2.x <c>oauth/issuers/*</c> 逐个移植。</summary>
public sealed record OAuthProviderSpec(
    string Issuer,
    string TokenUrl,
    string ClientId,
    OAuthIdentityParser Identity,
    string? Scope = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? ExtraTokenParams = null,
    OAuthRefreshHook? Refresh = null);

public interface IOAuthRegistry
{
    OAuthProviderSpec? Get(string issuer);
}

public sealed class OAuthRegistry(IEnumerable<OAuthProviderSpec> specs) : IOAuthRegistry
{
    private readonly Dictionary<string, OAuthProviderSpec> _specs = specs.ToDictionary(s => s.Issuer);

    public OAuthProviderSpec? Get(string issuer) => _specs.GetValueOrDefault(issuer);
}

/// <summary>授权方式本身。四种形状没有交集,所以是各自的流程,不是一个「可选字段袋」。</summary>
public enum OAuthGrantKind
{
    /// <summary>标准授权码 + PKCE(回环或手动粘贴)。</summary>
    AuthorizationCode,

    /// <summary>RFC 8628 设备授权码。</summary>
    DeviceCode,

    /// <summary>服务端发起的授权 flow,回环换码与轮询赛跑(ZCode)。</summary>
    CliPoll,

    /// <summary>密钥绑定(Ollama):全程没有 token,每个请求用私钥现场签名。</summary>
    KeyBinding,
}

/// <summary>登录过程中要告诉界面的事(打开授权页、显示配对码)。界面文字由控制端生成。</summary>
public interface IOAuthLoginSink
{
    void OpenAuthorizeUrl(string url);

    void ShowDeviceCode(string userCode, string verificationUrl, int expiresInSeconds);
}

/// <summary>OAuth 登录流程的抽象(M3 只定义接口;各家实现随供应商模块移植)。成功返回要落库的凭据。</summary>
public interface IOAuthLogin
{
    string Issuer { get; }

    OAuthGrantKind Grant { get; }

    Task<ProviderCredential> LoginAsync(IOAuthLoginSink sink, CancellationToken ct);
}

/// <summary>
/// PKCE(RFC 7636)。我们是 public client,没有 client_secret 可言:PKCE 补的是「截到授权码的人拿码去换 token」这个洞。
/// 必须用密码学随机源(verifier / state 可预测 = PKCE / CSRF 防线整个失效)。
/// </summary>
public static class Pkce
{
    public sealed record Pair(string Verifier, string Challenge)
    {
        public string Method => "S256";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>verifier 是 43–128 个 unreserved 字符:64 字节 base64url = 86 字符。</summary>
    public static Pair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new Pair(verifier, ChallengeOf(verifier));
    }

    /// <summary>RFC 7636 §4.2。单独导出是为了对规范附录 B 的已知向量做断言。</summary>
    public static string ChallengeOf(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string RandomState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>用定时安全比较比对回调里的 state;长度不等先短路(<see cref="CryptographicOperations.FixedTimeEquals"/> 对不等长不抛,但这样更清楚)。</summary>
    public static bool StateMatches(string expected, string? received)
    {
        if (received is null)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(received);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
