using System.IO.MemoryMappedFiles;
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
    /// <summary>
    /// 读最近一份有效快照:核对哈希后把正文交给 <paramref name="read"/>,返回它的结果和元数据;没有缓存 / 被截断或改过返回 null。
    /// 正文<b>只在回调期间有效</b>(文件实现直接映射文件,不把几 MB 的正文读进 GC 堆),不要留着。
    /// </summary>
    (T Result, CatalogCacheMeta Meta)? Load<T>(Func<ReadOnlySpan<byte>, CatalogCacheMeta, T> read);

    /// <summary>原子地换成新的一份(快照与元数据一起)。</summary>
    void Save(byte[] body, CatalogCacheMeta meta);

    /// <summary>
    /// 新下载的一份先写进草稿(文件实现:同目录的临时文件,不经过 GC 堆),核对、解析通过后 <see cref="ICatalogDraft.Commit"/> 原子替换;
    /// 不提交就释放 = 丢弃。
    /// </summary>
    ICatalogDraft BeginDraft();

    /// <summary>304 时只更新元数据(「上次检查时间」)。</summary>
    void SaveMeta(CatalogCacheMeta meta);
}

/// <summary>一份还没采用的新正文(<see cref="ICatalogStore.BeginDraft"/>)。</summary>
public interface ICatalogDraft : IDisposable
{
    /// <summary>往里写正文(只写)。</summary>
    Stream Body { get; }

    /// <summary>写完以后读:正文只在回调期间有效(文件实现是映射)。</summary>
    T Read<T>(Func<ReadOnlySpan<byte>, T> read);

    /// <summary>原子地换成这一份(先换正文再换元数据,和 <see cref="ICatalogStore.Save"/> 一样)。</summary>
    void Commit(CatalogCacheMeta meta);
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

    /// <summary>
    /// 正文用内存映射读(docs/design/03 §12 的同一类问题):整份读进 <c>byte[]</c> 会在大对象堆上留下几 MB 用过即弃的数组,
    /// 空闲的 Agent 很久都不做完整 GC,这块就一直占着物理内存。映射的是文件页,可回收、不算进程的脏内存,用完立即解除映射。
    /// 在 <see cref="_gate"/> 里读:<see cref="Save"/> 不会在映射期间替换文件(Windows 上映射着的文件不能被改名覆盖)。
    /// </summary>
    public (T Result, CatalogCacheMeta Meta)? Load<T>(Func<ReadOnlySpan<byte>, CatalogCacheMeta, T> read)
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
                if (meta is null)
                {
                    return null;
                }

                // 哈希对不上 = 被截断或被改过:当作没有缓存,不拿一份来历不明的数据当快照。
                return Mapped(BodyPath, body => string.Equals(Hash(body), meta.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? ((T Result, CatalogCacheMeta Meta)?)(read(body, meta), meta)
                    : null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public ICatalogDraft BeginDraft()
    {
        System.IO.Directory.CreateDirectory(Directory_);
        return new Draft(this, Path.Combine(Directory_, $"models-dev.json.{Environment.ProcessId}.{Guid.NewGuid():N}.download"));
    }

    /// <summary>把整个文件映射进来交给 <paramref name="read"/>;空文件给空正文。</summary>
    private static unsafe T Mapped<T>(string path, Func<ReadOnlySpan<byte>, T> read)
    {
        var length = new FileInfo(path).Length;
        if (length == 0)
        {
            return read([]);
        }

        if (length > int.MaxValue)
        {
            throw new IOException($"{path} is too large to map.");
        }

        using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        using var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        var handle = view.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref pointer);
        try
        {
            return read(new ReadOnlySpan<byte>(pointer + view.PointerOffset, (int)length));
        }
        finally
        {
            handle.ReleasePointer();
        }
    }

    private sealed class Draft(FileCatalogStore store, string path) : ICatalogDraft
    {
        private readonly FileStream _body = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920);
        private bool _committed;

        public Stream Body => _body;

        public T Read<T>(Func<ReadOnlySpan<byte>, T> read)
        {
            _body.Dispose();   // 写完了:关掉写入流再映射(它独占打开,不关映射不了)
            return Mapped(path, read);
        }

        public void Commit(CatalogCacheMeta meta)
        {
            _body.Dispose();
            lock (store._gate)
            {
                // 先换正文再换元数据:中途崩溃时哈希对不上,下次启动会当作没有缓存。
                File.Move(path, store.BodyPath, overwrite: true);
                WriteAtomic(store.MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta, CatalogJsonContext.Default.CatalogCacheMeta));
            }

            _committed = true;
        }

        public void Dispose()
        {
            _body.Dispose();
            if (!_committed)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 删不掉的临时文件下次启动也不会被当成缓存(名字不对)
                }
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

    public (T Result, CatalogCacheMeta Meta)? Load<T>(Func<ReadOnlySpan<byte>, CatalogCacheMeta, T> read) =>
        _value is { } v ? (read(v.Body, v.Meta), v.Meta) : null;

    public void Save(byte[] body, CatalogCacheMeta meta) => _value = (body, meta);

    public void SaveMeta(CatalogCacheMeta meta) => _value = _value is { } v ? (v.Body, meta) : null;

    public ICatalogDraft BeginDraft() => new Draft(this);

    private sealed class Draft(MemoryCatalogStore store) : ICatalogDraft
    {
        private readonly MemoryStream _body = new();

        public Stream Body => _body;

        public T Read<T>(Func<ReadOnlySpan<byte>, T> read) => read(_body.GetBuffer().AsSpan(0, (int)_body.Length));

        public void Commit(CatalogCacheMeta meta) => store._value = (_body.ToArray(), meta);

        public void Dispose() => _body.Dispose();
    }
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
