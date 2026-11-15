using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 一个插件的私有 kv(2.x <c>plugins.kv.&lt;id&gt;</c>:整张 <c>{ key: string }</c> map 存一行)。全局 / 工作区两个作用域在同一张表里分桶:工作区的键带 <c>\0w\0&lt;工作区&gt;\0</c> 前缀
/// (<see cref="HostGuards.NarrowStorageKey"/> 不允许控制字符,所以插件拼不出一个撞车的全局键)。配额 = 整张 map 的字节数。
/// </summary>
internal sealed class PluginKvBucket(IPluginKv kv, string storeKey, int quotaBytes)
{
    private const string WorkspacePrefix = "\0w\0";

    private readonly object _gate = new();

    private static string Compose(string? workspaceId, string key) => workspaceId is null ? key : $"{WorkspacePrefix}{workspaceId}\0{key}";

    public string? Get(string? workspaceId, string key)
    {
        lock (_gate)
        {
            return Read().TryGetValue(Compose(workspaceId, key), out var value) ? value : null;
        }
    }

    /// <summary>写;value 为 null = 删。超配额返回 false(没有写)。</summary>
    public bool Set(string? workspaceId, string key, string? value)
    {
        lock (_gate)
        {
            var map = Read();
            var composed = Compose(workspaceId, key);
            if (value is null)
            {
                map.Remove(composed);
            }
            else
            {
                map[composed] = value;
                if (UsedBytes(map) > quotaBytes)
                {
                    return false;
                }
            }

            Write(map);
            return true;
        }
    }

    public IReadOnlyList<string> Keys(string? workspaceId)
    {
        lock (_gate)
        {
            var prefix = workspaceId is null ? null : $"{WorkspacePrefix}{workspaceId}\0";
            return [.. Read().Keys.Where(k => prefix is null ? !k.StartsWith('\0') : k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => prefix is null ? k : k[prefix.Length..]).Order(StringComparer.Ordinal)];
        }
    }

    private const string SecretsKey = "\0secrets";

    /// <summary>记下插件写过哪些 secrets 键名(<see cref="IPluginSecrets"/> 不能列举,卸载时要按名字逐个清掉;这个保留键以 NUL 开头,插件拼不出来也看不见)。</summary>
    public void TrackSecret(string key, bool present)
    {
        lock (_gate)
        {
            var map = Read();
            var names = new HashSet<string>(SecretNames(map), StringComparer.Ordinal);
            if (present ? !names.Add(key) : !names.Remove(key))
            {
                return;
            }

            map[SecretsKey] = new JsonArray([.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n))]).ToJsonString();
            Write(map);
        }
    }

    public IReadOnlyList<string> TrackedSecrets()
    {
        lock (_gate)
        {
            return [.. SecretNames(Read())];
        }
    }

    /// <summary>清空整张 map(卸载)。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            kv.Set(storeKey, "{}");
        }
    }

    private static IEnumerable<string> SecretNames(Dictionary<string, string> map)
    {
        if (map.TryGetValue(SecretsKey, out var json) && JsonNode.Parse(json) is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    yield return name;
                }
            }
        }
    }

    private static long UsedBytes(Dictionary<string, string> map) => map.Sum(kv => (long)kv.Key.Length + kv.Value.Length);

    private Dictionary<string, string> Read()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (kv.Get(storeKey) is { } json && JsonNode.Parse(json) is JsonObject obj)
            {
                foreach (var (key, node) in obj)
                {
                    if (node is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        map[key] = text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // kv 是用户能手改的:一个坏值不该让插件的存储整个失灵
        }

        return map;
    }

    private void Write(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map)
        {
            obj[key] = value;
        }

        kv.Set(storeKey, obj.ToJsonString());
    }
}
