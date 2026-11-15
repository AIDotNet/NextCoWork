using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Catalog;

/// <summary>随快照一起存的元数据(02 A8「存储」:ETag、拉取时间、哈希)。</summary>
public sealed record CatalogCacheMeta(
    string? ETag,
    string? LastModified,
    DateTimeOffset FetchedAt,
    DateTimeOffset? CheckedAt,
    string Sha256,
    int ModelCount);

/// <summary>最近一份有效快照的落盘。接口化是为了测试(内存实现)和云端(将来换存储)。</summary>
public interface ICatalogStore
{
    (byte[] Body, CatalogCacheMeta Meta)? Load();

    /// <summary>原子地换成新的一份(快照与元数据一起)。</summary>
    void Save(byte[] body, CatalogCacheMeta meta);

    /// <summary>304 时只更新元数据(「上次检查时间」)。</summary>
    void SaveMeta(CatalogCacheMeta meta);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CatalogCacheMeta))]
[JsonSerializable(typeof(ModelOverrideFile))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>数据根下的 <c>catalog/models-dev.json</c>(原始响应体)和 <c>catalog/models-dev.meta.json</c>。写入是「临时文件 + 改名」,崩在中间不会留下半份。</summary>
public sealed class FileCatalogStore(string dataRoot) : ICatalogStore
{
    private readonly object _gate = new();

    private string Directory_ => Path.Combine(dataRoot, "catalog");

    private string BodyPath => Path.Combine(Directory_, "models-dev.json");

    private string MetaPath => Path.Combine(Directory_, "models-dev.meta.json");

    public (byte[] Body, CatalogCacheMeta Meta)? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(BodyPath) || !File.Exists(MetaPath))
                {
                    return null;
                }

                var meta = JsonSerializer.Deserialize(File.ReadAllBytes(MetaPath), CatalogJsonContext.Default.CatalogCacheMeta);
                var body = File.ReadAllBytes(BodyPath);
                // 哈希对不上 = 被截断或被改过:当作没有缓存,不拿一份来历不明的数据当快照。
                return meta is not null && string.Equals(Hash(body), meta.Sha256, StringComparison.OrdinalIgnoreCase) ? (body, meta) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void Save(byte[] body, CatalogCacheMeta meta)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory_);
            // 先换正文再换元数据:中途崩溃时哈希对不上,下次启动会当作没有缓存。
            WriteAtomic(BodyPath, body);
            WriteAtomic(MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta, CatalogJsonContext.Default.CatalogCacheMeta));
        }
    }

    public void SaveMeta(CatalogCacheMeta meta)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory_);
            WriteAtomic(MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta, CatalogJsonContext.Default.CatalogCacheMeta));
        }
    }

    public static string Hash(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // 临时文件本来就没写出来
            }

            throw;
        }
    }
}

/// <summary>内存实现(测试和不落盘的场景)。</summary>
public sealed class MemoryCatalogStore : ICatalogStore
{
    private (byte[] Body, CatalogCacheMeta Meta)? _value;

    public (byte[] Body, CatalogCacheMeta Meta)? Value => _value;

    public (byte[] Body, CatalogCacheMeta Meta)? Load() => _value;

    public void Save(byte[] body, CatalogCacheMeta meta) => _value = (body, meta);

    public void SaveMeta(CatalogCacheMeta meta) => _value = _value is { } v ? (v.Body, meta) : null;
}

// ── 用户覆盖 ──

/// <summary>
/// 用户自定义价格(带币种,整请求一档)。和 models.dev 同步来的价格不同:同步价格由我们判断币种(只收 USD 一手厂商),
/// 用户填的价格币种是用户说了算。
/// </summary>
public sealed record PriceOverride(Currency Currency, decimal Input, decimal Output, decimal? CacheRead = null, decimal? CacheWrite = null, decimal? CacheWrite1h = null);

/// <summary>
/// 用户对一个模型的本地覆盖(02 A8「合并顺序」最后一层,2.x 的 <c>model</c> 模块:改名、覆盖、删除)。
/// <see cref="ProviderId"/> 为空 = 对所有供应商上这个模型 id 生效;非空 = 同时匹配 models.dev 的供应商 id 和用户配置的供应商 id。
/// 字段为空 = 不覆盖(跟随同步数据)。<see cref="Deleted"/> = 从目录里隐藏这个模型(同步数据再来也不会回来)。
/// </summary>
public sealed record ModelOverride(
    string? ProviderId,
    string ModelId,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    bool? ToolCall = null,
    bool? Reasoning = null,
    bool? Vision = null,
    PriceOverride? Price = null,
    bool Deleted = false)
{
    /// <summary>覆盖的身份:(供应商或空, 模型 id 不分大小写)。</summary>
    public string Key => $"{ProviderId ?? ""}\u0000{ModelId.ToLowerInvariant()}";

    public bool Matches(string? providerId, string modelId) =>
        string.Equals(ModelId, modelId, StringComparison.OrdinalIgnoreCase) && (ProviderId is null || ProviderId == providerId);

    /// <summary>没有任何字段被覆盖也没有删除标记,等于不存在。</summary>
    public bool IsEmpty => !Deleted && DisplayName is null && ContextWindow is null && MaxOutputTokens is null
        && ToolCall is null && Reasoning is null && Vision is null && Price is null;
}

internal sealed record ModelOverrideFile(IReadOnlyList<ModelOverride> Overrides);

public interface ICatalogOverrideStore
{
    IReadOnlyList<ModelOverride> Load();

    void Save(IReadOnlyList<ModelOverride> overrides);
}

/// <summary>存在 kv 表的 <c>v3.catalog.overrides</c> 里(3.0 自己的键,不碰 2.x 的键)。</summary>
public sealed class KvOverrideStore(KvStore kv) : ICatalogOverrideStore
{
    public const string Key = "v3.catalog.overrides";

    public IReadOnlyList<ModelOverride> Load()
    {
        if (kv.Get(Key) is not { } json)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ModelOverrideFile)?.Overrides ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<ModelOverride> overrides) =>
        kv.Set(Key, JsonSerializer.Serialize(new ModelOverrideFile(overrides), CatalogJsonContext.Default.ModelOverrideFile));
}

public sealed class MemoryOverrideStore : ICatalogOverrideStore
{
    public IReadOnlyList<ModelOverride> Stored { get; private set; } = [];

    public IReadOnlyList<ModelOverride> Load() => Stored;

    public void Save(IReadOnlyList<ModelOverride> overrides) => Stored = overrides.ToList();
}
