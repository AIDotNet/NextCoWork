using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Workspace.Git;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

internal sealed record GitCredentialIndexEntry(string Host, string Username);

internal sealed record GitCredentialIndex(GitCredentialIndexEntry[] Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitCredentialIndex))]
internal sealed partial class GitCredentialJsonContext : JsonSerializerContext;

/// <summary>
/// Git 主机的 HTTPS 凭据库(02 A6「Git 接力」):令牌经 <c>NCK1</c> 加密存进 <c>credentials</c> 表(ref = <c>git:https:&lt;host&gt;</c>;云端模式下主密钥由 KMS 包裹),
/// kv 表里只有「有哪些主机、用户名是什么」的索引(没有令牌)。令牌只经 <see cref="Environment"/> 变成 git 的环境变量,从不出现在 DTO、事件、日志、命令行或 <c>.git/config</c>。
/// </summary>
internal sealed class GitCredentialVault(CredentialStore credentials, KvStore kv)
{
    private const string IndexKey = "v3.git.credentials";
    private const int MaxHosts = 64;
    private readonly object _gate = new();

    public static string RefOf(string host) => $"git:https:{host}";

    /// <summary>规范化主机键:小写 <c>host[:port]</c>;不合法返回 null。</summary>
    public static string? NormalizeHost(string? host)
    {
        var text = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length > 255 || !Uri.TryCreate("https://" + text + "/", UriKind.Absolute, out var uri)
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || text.Contains('/') || text.Contains('@'))
        {
            return null;
        }

        return GitCloner.HostKey(uri);
    }

    public IReadOnlyList<GitCredentialIndexEntry> List()
    {
        lock (_gate)
        {
            return ReadIndex();
        }
    }

    public void Set(string host, string username, string token)
    {
        lock (_gate)
        {
            var index = ReadIndex().Where(e => e.Host != host).ToList();
            if (index.Count >= MaxHosts)
            {
                throw new ProtocolException(ErrorCodes.TooLarge, "Too many Git credentials");
            }

            credentials.Set(RefOf(host), token); // 先写令牌再写索引:索引里的每一项都一定有令牌
            index.Add(new GitCredentialIndexEntry(host, username));
            WriteIndex(index);
        }
    }

    public bool Remove(string host)
    {
        lock (_gate)
        {
            var index = ReadIndex().ToList();
            var removed = index.RemoveAll(e => e.Host == host) > 0;
            WriteIndex(index);
            credentials.Remove(RefOf(host));
            return removed;
        }
    }

    /// <summary>所有凭据(含令牌;只给 <see cref="Environment"/> 用)。令牌解不开的条目跳过(坏数据不该让 git 全部失败)。</summary>
    public IReadOnlyList<GitHttpCredential> Resolve()
    {
        var list = new List<GitHttpCredential>();
        foreach (var entry in List())
        {
            try
            {
                if (credentials.Get(RefOf(entry.Host)) is { Length: > 0 } token)
                {
                    list.Add(new GitHttpCredential(entry.Host, entry.Username, token));
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
            }
        }

        return list;
    }

    /// <summary>每次起 git 进程现取的环境变量。</summary>
    public IReadOnlyDictionary<string, string> Environment() => GitCredentialEnvironment.Build(Resolve());

    public GitCredentialsResult ToResult() => new([.. List().Select(static e => new GitCredentialDto(e.Host, e.Username))]);

    private List<GitCredentialIndexEntry> ReadIndex()
    {
        if (kv.Get(IndexKey) is not { } json)
        {
            return [];
        }

        try
        {
            return [.. JsonSerializer.Deserialize(json, GitCredentialJsonContext.Default.GitCredentialIndex)?.Entries ?? []];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteIndex(List<GitCredentialIndexEntry> entries) =>
        kv.Set(IndexKey, JsonSerializer.Serialize(new GitCredentialIndex([.. entries.OrderBy(static e => e.Host, StringComparer.Ordinal)]), GitCredentialJsonContext.Default.GitCredentialIndex));
}

/// <summary><c>git.credentials.*</c>(方法表在 <see cref="GitCredentialsMethods"/>)。角色(owner)、承载、幂等键由路由统一检查。</summary>
internal static class GitCredentialHandlers
{
    public static void Register(MethodRouter router, Func<GitCredentialVault> vault)
    {
        router.Handle(GitCredentialsMethods.Set, (p, _, _) =>
        {
            var host = GitCredentialVault.NormalizeHost(p.Host)
                ?? throw Services.ServiceMapping.Invalid("git.credentials.host", "The Git host is not valid");
            var token = p.Token?.Trim() ?? "";
            if (token.Length == 0 || token.Length > GitCredentialsMethods.MaxTokenLength || token.Any(static c => char.IsControl(c)))
            {
                throw Services.ServiceMapping.Invalid("git.credentials.token", "The Git token is empty or too long");
            }

            var username = p.Username?.Trim() is { Length: > 0 } u ? u : "x-access-token";
            if (username.Length > 128 || username.Any(static c => char.IsControl(c) || c == ':'))
            {
                throw Services.ServiceMapping.Invalid("git.credentials.username", "The Git user name is not valid");
            }

            var v = vault();
            v.Set(host, username, token);
            return ValueTask.FromResult(v.ToResult());
        });
        router.Handle(GitCredentialsMethods.Remove, (p, _, _) =>
        {
            var host = GitCredentialVault.NormalizeHost(p.Host)
                ?? throw Services.ServiceMapping.Invalid("git.credentials.host", "The Git host is not valid");
            var v = vault();
            v.Remove(host);
            return ValueTask.FromResult(v.ToResult());
        });
        router.Handle(GitCredentialsMethods.List, (_, _, _) => ValueTask.FromResult(vault().ToResult()));
    }
}
