using System.Security.Cryptography;
using System.Text;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// SSH 环境保存的凭据(NCK1,存在 <c>credentials</c> 表;<b>明文只在 Agent 里</b>,永远不进任何 DTO / 事件 / 日志)。ref 沿用 2.x 的 <c>connection:&lt;id&gt;:password</c>(接管 2.x 数据后原样可用),
/// 3.0 新增:跳板机的密码(<c>…:password:&lt;主机名&gt;</c>)、私钥口令(<c>…:passphrase:&lt;私钥路径哈希&gt;</c>)、代理密码(<c>…:proxyPassword</c>)。
/// </summary>
internal sealed class ConnectionSecrets(CredentialStore store)
{
    public static string PasswordRef(string connectionId) => $"connection:{connectionId}:password";

    public static string HopPasswordRef(string connectionId, string hostName) => $"connection:{connectionId}:password:{hostName}";

    public static string PassphraseRef(string connectionId, string keyPath) =>
        $"connection:{connectionId}:passphrase:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyPath)))[..16].ToLowerInvariant()}";

    public static string ProxyPasswordRef(string connectionId) => $"connection:{connectionId}:proxyPassword";

    public string? Get(string reference) => store.Get(reference);

    public bool Has(string reference)
    {
        try
        {
            return store.Get(reference) is { Length: > 0 };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Set(string reference, string value) => store.Set(reference, value);

    public void Remove(string reference) => store.Remove(reference);

    /// <summary>删掉这个环境的全部凭据(环境被删除时)。</summary>
    public void RemoveAll(string connectionId) => store.RemoveByPrefix($"connection:{connectionId}:");
}
