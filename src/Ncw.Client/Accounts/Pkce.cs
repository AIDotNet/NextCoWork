using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ncw.Client.Accounts;

/// <summary>一次授权请求的 PKCE 材料:<c>code_verifier</c> 只留在客户端,<c>code_challenge</c>(S256)交给授权端点。</summary>
public sealed record PkcePair(string Verifier, string Challenge);

/// <summary>
/// PKCE(RFC 7636)与一次性 <c>state</c>。随机数只来自 .NET 内置的密码学随机源(<see cref="RandomNumberGenerator"/>),不自研、不用 <c>Random</c>:
/// verifier 可预测 = PKCE 整个失效,state 可预测 = CSRF 防线整个失效。我们是公开客户端(没有 client_secret),PKCE 是授权码不被截获利用的唯一依据。
/// </summary>
public static class Pkce
{
    /// <summary>verifier 的随机字节数:48 字节 = 64 个 base64url 字符(RFC 7636 §4.1 要求 43–128)。</summary>
    public const int VerifierBytes = 48;

    /// <summary>state 的随机字节数:24 字节 = 32 个字符。</summary>
    public const int StateBytes = 24;

    public static PkcePair Create()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(VerifierBytes));
        return new PkcePair(verifier, ChallengeOf(verifier));
    }

    /// <summary>RFC 7636 §4.2:<c>BASE64URL(SHA256(ASCII(verifier)))</c>,不带填充。单独公开是为了能对规范附录 B 的已知向量做断言。</summary>
    public static string ChallengeOf(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));

    /// <summary>RFC 7636 §4.1:43–128 个 unreserved 字符(<c>A-Z a-z 0-9 - . _ ~</c>)。</summary>
    public static bool IsValidVerifier(string? verifier)
    {
        if (verifier is not { Length: >= 43 and <= 128 })
        {
            return false;
        }

        foreach (var c in verifier)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>常数时间比较两个 state(不因为前缀相同而更慢)。</summary>
    public static bool StateEquals(string? expected, string? actual) =>
        expected is { Length: > 0 } && actual is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
