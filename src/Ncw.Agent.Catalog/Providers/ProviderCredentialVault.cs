using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 凭据 ref 的账户作用域换算(2.x <c>config-profile.ts</c> 的 <c>physicalCredentialRef</c>):<c>local</c> 沿用裸 ref
/// (作用域列出现之前的所有行都归 local),账户作用域用 <c>&lt;accountId&gt;\u0000&lt;logicalRef&gt;</c>;平台账户自己的显式键永不加前缀。
/// </summary>
public static class CredentialScopes
{
    public const string Local = "local";

    public const char Separator = '\0';

    private static readonly string[] GlobalRefs = ["nextcowork:client-access-token", "nextcowork:client-refresh-token"];

    private static readonly string[] GlobalPrefixes = ["config-sync:", "nextcowork:sync-"];

    public static bool IsGlobal(string reference) =>
        GlobalRefs.Contains(reference, StringComparer.Ordinal) || GlobalPrefixes.Any(p => reference.StartsWith(p, StringComparison.Ordinal));

    /// <summary>逻辑 ref → 库里那行的物理键。</summary>
    public static string Physical(string logicalRef, string scope) =>
        scope == Local || IsGlobal(logicalRef) ? logicalRef : $"{scope}{Separator}{logicalRef}";

    /// <summary>库里那行的物理键 → 逻辑 ref;不属于该作用域时返回 null。</summary>
    public static string? Logical(string physicalRef, string scope)
    {
        if (scope == Local)
        {
            // 含分隔符的行属于某个账户作用域,不是 local 的。
            return physicalRef.Contains(Separator) || IsGlobal(physicalRef) ? null : physicalRef;
        }

        var prefix = $"{scope}{Separator}";
        return physicalRef.StartsWith(prefix, StringComparison.Ordinal) ? physicalRef[prefix.Length..] : null;
    }
}

/// <summary>凭据存取 + 删除。路由、刷新器、供应商服务都经过它(逻辑 ref 进,物理 ref 落库)。</summary>
public interface ICredentialVault : ICredentialStore
{
    /// <summary>删密文。不读主密钥、不做加密:主密钥损坏时「退出登录」「删供应商」这类动作不能因此失败。</summary>
    ValueTask RemoveAsync(string credentialRef, CancellationToken ct);

    /// <summary>同步删除(和账号行在同一个数据库事务里删,见 <see cref="ProviderAccountRepository"/>)。</summary>
    void Remove(string credentialRef);
}

/// <summary>
/// <see cref="ICredentialVault"/> 的 NCK1 实现(<see cref="CredentialStore"/>):读写都走 NCK1;
/// 读不到时对「文件里写着密钥的供应商」(外部工具手写的 <c>apiKey</c> / <c>credential</c>)退回 providers.json,
/// 写入后清掉文件里的明文,于是一个供应商任何时刻只有一个凭据来源。
/// </summary>
public sealed class ProviderCredentialVault(CredentialStore store, ProviderFileStore? file = null, Func<string>? scope = null) : ICredentialVault
{
    private string Scope => scope?.Invoke() ?? CredentialScopes.Local;

    public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct)
    {
        var stored = store.Get(CredentialScopes.Physical(credentialRef, Scope));
        if (stored is null && file is not null && ProviderRefs.FileOwnedProviderId(credentialRef) is { } id)
        {
            stored = file.GetCredential(id);
        }

        return ValueTask.FromResult(stored);
    }

    public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct)
    {
        if (value.Length == 0)
        {
            return RemoveAsync(credentialRef, ct);
        }

        store.Set(CredentialScopes.Physical(credentialRef, Scope), value);
        ClearFileCopy(credentialRef);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string credentialRef, CancellationToken ct)
    {
        Remove(credentialRef);
        return ValueTask.CompletedTask;
    }

    public void Remove(string credentialRef)
    {
        store.Remove(CredentialScopes.Physical(credentialRef, Scope));
        ClearFileCopy(credentialRef);
    }

    private void ClearFileCopy(string credentialRef)
    {
        if (file is not null && ProviderRefs.FileOwnedProviderId(credentialRef) is { } id && file.GetCredential(id) is not null)
        {
            file.RemoveCredential(id);
        }
    }
}

/// <summary>内存实现(测试用)。</summary>
public sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _values = [];

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Seed(string credentialRef, string value) => _values[credentialRef] = value;

    public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) =>
        ValueTask.FromResult(_values.GetValueOrDefault(credentialRef));

    public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct)
    {
        _values[credentialRef] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string credentialRef, CancellationToken ct)
    {
        _values.Remove(credentialRef);
        return ValueTask.CompletedTask;
    }

    public void Remove(string credentialRef) => _values.Remove(credentialRef);
}
