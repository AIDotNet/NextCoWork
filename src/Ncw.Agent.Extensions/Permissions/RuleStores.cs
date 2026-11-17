using System.Collections.Concurrent;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;

namespace Ncw.Agent.Extensions.Permissions;

/// <summary>三个桶,优先级 deny &gt; ask &gt; allow(接线在 <see cref="PermissionDecisions"/>):deny 永远拒绝;ask 强制询问;allow 免审批。</summary>
public enum RuleBucket
{
    Allow,
    Ask,
    Deny,
}

public sealed record RuleBuckets(IReadOnlyList<string> Allow, IReadOnlyList<string> Ask, IReadOnlyList<string> Deny)
{
    public static RuleBuckets Empty { get; } = new([], [], []);

    public IReadOnlyList<string> Of(RuleBucket bucket) => bucket switch
    {
        RuleBucket.Allow => Allow,
        RuleBucket.Ask => Ask,
        _ => Deny,
    };

    public static RuleBuckets Merge(IEnumerable<RuleBuckets> all)
    {
        var allow = new List<string>();
        var ask = new List<string>();
        var deny = new List<string>();
        foreach (var b in all)
        {
            allow.AddRange(b.Allow.Where(r => !allow.Contains(r)));
            ask.AddRange(b.Ask.Where(r => !ask.Contains(r)));
            deny.AddRange(b.Deny.Where(r => !deny.Contains(r)));
        }

        return new RuleBuckets(allow, ask, deny);
    }
}

/// <summary>规则存放处需要知道的上下文:哪个会话、哪个执行环境、项目根(没有工作区时为 null,此时项目作用域读为空、写失败)。</summary>
public sealed record RuleStoreContext(string SessionId, IExecutionEnvironment Environment, EnvPath? Root);

public enum RuleWriteFailure
{
    /// <summary>没有项目根:存不下,明说而不是静默丢弃。</summary>
    NoWorkspace,

    /// <summary>文件读不懂:拒绝覆盖——用户点的只是一颗按钮,不是「清空我的配置」。</summary>
    Unreadable,

    /// <summary>桶已满(每类最多 <see cref="RuleLimits.MaxRulesPerBucket"/> 条)。</summary>
    Full,

    Io,
}

public readonly record struct RuleWriteResult(bool Ok, bool Added = false, RuleWriteFailure? Failure = null)
{
    public static RuleWriteResult Written => new(true, true);

    public static RuleWriteResult AlreadyPresent => new(true, false);

    public static RuleWriteResult Failed(RuleWriteFailure failure) => new(false, false, failure);
}

public static class RuleLimits
{
    /// <summary>一个桶的上限。规则是线性扫的,而且一份人手维护的清单长到这个数就已经没人读得懂了。</summary>
    public const int MaxRulesPerBucket = 500;
}

/// <summary>
/// 规则的持久化接口(注入,不依赖 Storage 的具体实现)。三个作用域各有一个实例:会话(<see cref="MemoryRuleStore.PerSession"/>)、
/// 项目(<see cref="ProjectRuleFile"/>:<c>&lt;项目&gt;/.next-cowork/settings.local.json</c>,经 <c>IExecutionEnvironment</c> 读写)、
/// 永久(宿主注入,例如 Storage 里的全局设置;没有注入时用 <see cref="MemoryRuleStore.Process"/>——只活到 Agent 退出)。
/// 读取失败(抛异常)按<b>失败即关闭</b>处理:权限扩展拒绝这次调用。
/// </summary>
public interface IPermissionRuleStore
{
    ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct);

    ValueTask<RuleWriteResult> AddAsync(RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct);
}

/// <summary>进程内存放。会话作用域用它(会话结束 / Agent 退出即忘);测试和「还没有全局存放处」时也用它。</summary>
public sealed class MemoryRuleStore : IPermissionRuleStore
{
    private readonly Func<RuleStoreContext, string> _keyOf;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<RuleBucket, List<string>>> _data = new(StringComparer.Ordinal);

    private MemoryRuleStore(Func<RuleStoreContext, string> keyOf) => _keyOf = keyOf;

    /// <summary>每个会话一份。</summary>
    public static MemoryRuleStore PerSession() => new(static c => c.SessionId);

    /// <summary>整个进程一份(所有会话、所有项目共享)。</summary>
    public static MemoryRuleStore Process() => new(static _ => "*");

    /// <summary>忘掉一个会话的规则(会话删除时)。</summary>
    public void Forget(string key)
    {
        lock (_lock)
        {
            _data.Remove(key);
        }
    }

    public ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(_keyOf(context), out var buckets))
            {
                return ValueTask.FromResult(RuleBuckets.Empty);
            }

            return ValueTask.FromResult(new RuleBuckets(
                buckets.GetValueOrDefault(RuleBucket.Allow)?.ToList() ?? [],
                buckets.GetValueOrDefault(RuleBucket.Ask)?.ToList() ?? [],
                buckets.GetValueOrDefault(RuleBucket.Deny)?.ToList() ?? []));
        }
    }

    public ValueTask<RuleWriteResult> AddAsync(RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(_keyOf(context), out var buckets))
            {
                buckets = [];
                _data[_keyOf(context)] = buckets;
            }

            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = [];
                buckets[bucket] = list;
            }

            if (list.Contains(rule))
            {
                return ValueTask.FromResult(RuleWriteResult.AlreadyPresent);
            }

            if (list.Count >= RuleLimits.MaxRulesPerBucket)
            {
                return ValueTask.FromResult(RuleWriteResult.Failed(RuleWriteFailure.Full));
            }

            list.Add(rule);
            return ValueTask.FromResult(RuleWriteResult.Written);
        }
    }
}

/// <summary>
/// <c>&lt;项目&gt;/.next-cowork/settings.local.json</c> 的权限规则读写(2.x <c>kernel/local-settings.ts</c> 的权限部分;hooks 段随 Hooks 扩展,但本类<b>原样保留</b>它和所有未知键)。
/// 约定(这个文件会继续长,所以三条从第一天就立住):① 分段命名,每类设置一个顶层键;② 读的时候宽容(认不出来的段 / 规则忽略,绝不因为一行看不懂的东西就拒绝整个文件);
/// ③ 写的时候原样保留未知键。另:读不懂的文件<b>拒绝覆盖</b>;同一文件的写入串行;远程(SSH)环境<b>每次重读、读不了就失败</b>(失败即关闭:缓存的规则说不清属于哪一次连接)。
/// </summary>
public sealed class ProjectRuleFile : IPermissionRuleStore
{
    public const string DirectoryName = ".next-cowork";

    public const string FileName = "settings.local.json";

    public const int Version = 1;

    private sealed record Cached(string Key, RuleBuckets Buckets);

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.Ordinal);

    public static EnvPath PathOf(EnvPath root) => root.Join(DirectoryName, FileName);

    private static string CacheKey(RuleStoreContext c, EnvPath path) => $"{c.Environment.Id}|{path.Value}";

    private static bool Strict(RuleStoreContext c) => c.Environment.Kind != EnvironmentKind.Local;

    // ───────────────────────── 读 ─────────────────────────

    public async ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct)
    {
        if (context.Root is not { } root)
        {
            return RuleBuckets.Empty;
        }

        var path = PathOf(root);
        var strict = Strict(context);
        var fs = context.Environment.Fs;

        string key;
        try
        {
            var info = await fs.StatAsync(path, ct).ConfigureAwait(false);
            key = $"{info.ModifiedAt.UtcTicks}:{info.Size}";
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            // 文件不存在 = 这个项目没有任何本地规则,是正常状态,不是错误。
            return RuleBuckets.Empty;
        }

        // 本机按 mtime+size 缓存(每次工具调用都会问一次;用户在编辑器里手改是预期用法,所以不能读一次就当永远)。
        // 远程一条都不进缓存:判新鲜本来就要一次网络往返,省下的那次读不值得用「缓存的规则说不清属于哪一次连接」换。
        if (!strict)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(CacheKey(context, path), out var hit) && hit.Key == key)
                {
                    return hit.Buckets;
                }
            }
        }

        RuleBuckets buckets;
        try
        {
            var text = await fs.ReadTextAsync(path, ct: ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text, DocOptions);
            buckets = Normalize(doc.RootElement);
        }
        catch (JsonException) when (!strict)
        {
            // 手改坏了的文件按「什么都没配」处理:这条路径上唯一比「少一条授权」更糟的结局,就是让一个 JSON 语法错误拦下用户的整轮运行。
            buckets = RuleBuckets.Empty;
        }

        if (!strict)
        {
            lock (_cacheLock)
            {
                _cache[CacheKey(context, path)] = new Cached(key, buckets);
            }
        }

        return buckets;
    }

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static IReadOnlyList<string> NormalizeBucket(JsonElement permissions, string name)
    {
        var result = new List<string>();
        if (permissions.ValueKind != JsonValueKind.Object || !permissions.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString()?.Trim() is not { Length: > 0 } rule)
            {
                continue;
            }

            // 读不懂的规则直接丢掉:留着它只会在某天以「我明明写了」的形式变成一次误放行。
            if (!PermissionRules.IsValid(rule) || result.Contains(rule))
            {
                continue;
            }

            result.Add(rule);
            if (result.Count >= RuleLimits.MaxRulesPerBucket)
            {
                break;
            }
        }

        return result;
    }

    internal static RuleBuckets Normalize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("permissions", out var permissions))
        {
            return RuleBuckets.Empty;
        }

        return new RuleBuckets(NormalizeBucket(permissions, "allow"), NormalizeBucket(permissions, "ask"), NormalizeBucket(permissions, "deny"));
    }

    // ───────────────────────── 写 ─────────────────────────

    public async ValueTask<RuleWriteResult> AddAsync(RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct)
    {
        if (context.Root is not { } root)
        {
            return RuleWriteResult.Failed(RuleWriteFailure.NoWorkspace);
        }

        var path = PathOf(root);
        var gate = _writeLocks.GetOrAdd(CacheKey(context, path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false); // 同一份文件的写入串起来:并行工具调用可以同时点「以后都允许」
        try
        {
            var fs = context.Environment.Fs;
            JsonDocument? existing = null;
            try
            {
                string? text = null;
                try
                {
                    text = await fs.ReadTextAsync(path, ct: ct).ConfigureAwait(false);
                }
                catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
                {
                }

                if (text is not null)
                {
                    try
                    {
                        existing = JsonDocument.Parse(text, DocOptions);
                    }
                    catch (JsonException)
                    {
                        // 读不懂就不写。覆盖过去等于把用户手写的那些段静默删掉,而他点的只是一颗「以后都允许」。
                        return RuleWriteResult.Failed(RuleWriteFailure.Unreadable);
                    }
                }

                var raw = existing?.RootElement is { ValueKind: JsonValueKind.Object } r ? r : default;
                var current = raw.ValueKind == JsonValueKind.Object ? Normalize(raw) : RuleBuckets.Empty;
                var list = current.Of(bucket);
                if (list.Contains(rule))
                {
                    return RuleWriteResult.AlreadyPresent;
                }

                if (list.Count >= RuleLimits.MaxRulesPerBucket)
                {
                    return RuleWriteResult.Failed(RuleWriteFailure.Full);
                }

                var updated = new RuleBuckets(
                    bucket == RuleBucket.Allow ? [.. current.Allow, rule] : current.Allow,
                    bucket == RuleBucket.Ask ? [.. current.Ask, rule] : current.Ask,
                    bucket == RuleBucket.Deny ? [.. current.Deny, rule] : current.Deny);
                var bytes = Render(raw, updated);
                await fs.WriteBytesAsync(path, bytes, new FsWriteOptions(CreateParents: true), ct).ConfigureAwait(false);
                lock (_cacheLock)
                {
                    _cache.Remove(CacheKey(context, path));
                }

                return RuleWriteResult.Written;
            }
            catch (EnvironmentException e) when (e.Code != EnvironmentErrorCode.Cancelled)
            {
                return RuleWriteResult.Failed(RuleWriteFailure.Io);
            }
            finally
            {
                existing?.Dispose();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 写出整份文件:顶层与 <c>permissions</c> 段的未知键原样带过去(新版本写下的东西,旧版本改一次规则不能抹掉);
    /// <c>version</c> 写成当前版本;三个桶写成归一化之后的清单。
    /// </summary>
    private static byte[] Render(JsonElement raw, RuleBuckets buckets)
    {
        using var stream = new MemoryStream();
        // NewLine 固定为 \n:这个文件和 2.x 共用格式、会跟着项目跨机器走,不能在 Windows 上写成 \r\n(默认是 Environment.NewLine)
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            var wroteVersion = false;
            var wrotePermissions = false;
            if (raw.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in raw.EnumerateObject())
                {
                    if (p.NameEquals("version"))
                    {
                        w.WriteNumber("version", Version);
                        wroteVersion = true;
                    }
                    else if (p.NameEquals("permissions"))
                    {
                        w.WritePropertyName("permissions");
                        WritePermissions(w, p.Value, buckets);
                        wrotePermissions = true;
                    }
                    else
                    {
                        p.WriteTo(w);
                    }
                }
            }

            if (!wroteVersion)
            {
                w.WriteNumber("version", Version);
            }

            if (!wrotePermissions)
            {
                w.WritePropertyName("permissions");
                WritePermissions(w, default, buckets);
            }

            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WritePermissions(Utf8JsonWriter w, JsonElement rawPermissions, RuleBuckets buckets)
    {
        w.WriteStartObject();
        if (rawPermissions.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in rawPermissions.EnumerateObject())
            {
                if (!p.NameEquals("allow") && !p.NameEquals("ask") && !p.NameEquals("deny"))
                {
                    p.WriteTo(w);
                }
            }
        }

        foreach (var (name, list) in new[] { ("allow", buckets.Allow), ("ask", buckets.Ask), ("deny", buckets.Deny) })
        {
            w.WriteStartArray(name);
            foreach (var rule in list)
            {
                w.WriteStringValue(rule);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }
}

/// <summary>把三个作用域的存放处合成一本「规则簿」:读 = 三处的并集;写 = 按作用域写到对应的一处。</summary>
public sealed class PermissionRuleBook(IPermissionRuleStore session, IPermissionRuleStore project, IPermissionRuleStore global)
{
    public IPermissionRuleStore Session { get; } = session;

    public IPermissionRuleStore Project { get; } = project;

    public IPermissionRuleStore Global { get; } = global;

    /// <summary>默认装配:会话 = 进程内按会话;项目 = <c>settings.local.json</c>;永久 = 进程内(宿主应注入真正持久的实现)。</summary>
    public static PermissionRuleBook CreateDefault(IPermissionRuleStore? global = null) =>
        new(MemoryRuleStore.PerSession(), new ProjectRuleFile(), global ?? MemoryRuleStore.Process());

    public IPermissionRuleStore Store(RememberScope scope) => scope switch
    {
        RememberScope.Session => Session,
        RememberScope.Project => Project,
        _ => Global,
    };

    public async ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct)
    {
        var s = await Session.ReadAsync(context, ct).ConfigureAwait(false);
        var p = await Project.ReadAsync(context, ct).ConfigureAwait(false);
        var g = await Global.ReadAsync(context, ct).ConfigureAwait(false);
        return RuleBuckets.Merge([p, s, g]);
    }

    public ValueTask<RuleWriteResult> AddAsync(RememberScope scope, RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct) =>
        Store(scope).AddAsync(context, bucket, rule, ct);
}
