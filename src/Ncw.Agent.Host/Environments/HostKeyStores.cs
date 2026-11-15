using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Environments;

/// <summary>已知主机里的一把钉扎的主机密钥(kv 里的存储形状)。</summary>
internal sealed record KnownHostEntry(string Algorithm, string Fingerprint);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, KnownHostEntry[]>))]
internal sealed partial class EnvironmentsJsonContext : JsonSerializerContext;

/// <summary>
/// 设备上的已知主机(<see cref="IHostKeyStore"/> 的持久实现):kv 表 <c>v3.ssh.knownHosts</c> 里一份 JSON,
/// <c>{ "<i>主机名</i>": [ { algorithm, fingerprint } … ] }</c>。主机名沿用 known_hosts 的写法(默认端口是裸主机名,否则 <c>[host]:port</c>),按算法钉扎。
/// 语义(06 §3「主机指纹」):首次出现由 <see cref="IHostKeyCallback"/> 确认后 <see cref="PinAsync"/>;和钉扎不一致时绝不覆盖——只有用户明确「忘记这台主机」才 <see cref="RemoveAsync"/>。
/// 读到坏 JSON 时<b>失败即关闭</b>(抛异常 = 连接失败,不当作「没有钉扎」:那会让用户在已被篡改的库上重新确认指纹);写入遇到读不懂的内容也拒绝覆盖。
/// </summary>
internal sealed class StoredHostKeyStore(KvStore kv) : IHostKeyStore, IHostKeyCatalog
{
    public const string Key = "v3.ssh.knownHosts";

    private readonly Lock _lock = new();

    public ValueTask<IReadOnlyList<PinnedHostKey>> GetAsync(string hostName, CancellationToken ct)
    {
        lock (_lock)
        {
            var all = Load();
            return ValueTask.FromResult<IReadOnlyList<PinnedHostKey>>(
                all.TryGetValue(hostName, out var entries) ? [.. entries.Select(static e => new PinnedHostKey(e.Algorithm, e.Fingerprint))] : []);
        }
    }

    public ValueTask PinAsync(string hostName, PinnedHostKey key, CancellationToken ct)
    {
        lock (_lock)
        {
            var all = Load();
            var entries = all.TryGetValue(hostName, out var existing) ? existing.Where(e => e.Algorithm != key.Algorithm).ToList() : [];
            entries.Add(new KnownHostEntry(key.Algorithm, key.FingerprintSha256));
            all[hostName] = [.. entries];
            Save(all);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string hostName, CancellationToken ct)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.Remove(hostName))
            {
                Save(all);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<(string HostName, PinnedHostKey Key)>> ListAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<(string, PinnedHostKey)>>(
                [.. Load().OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase).SelectMany(static kv => kv.Value.Select(e => (kv.Key, new PinnedHostKey(e.Algorithm, e.Fingerprint))))]);
        }
    }

    private Dictionary<string, KnownHostEntry[]> Load()
    {
        if (kv.Get(Key) is not { } json)
        {
            return new Dictionary<string, KnownHostEntry[]>(StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonSerializer.Deserialize(json, EnvironmentsJsonContext.Default.DictionaryStringKnownHostEntryArray)
            ?? throw new JsonException("The known hosts are not an object.");
        return new Dictionary<string, KnownHostEntry[]>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, KnownHostEntry[]> all) =>
        kv.Set(Key, JsonSerializer.Serialize(new Dictionary<string, KnownHostEntry[]>(all), EnvironmentsJsonContext.Default.DictionaryStringKnownHostEntryArray));
}
