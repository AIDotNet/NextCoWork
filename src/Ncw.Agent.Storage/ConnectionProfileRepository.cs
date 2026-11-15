using System.Text.Json.Nodes;

namespace Ncw.Agent.Storage;

/// <summary>
/// 一个连接配置(2.x 的 <c>connection_profiles</c> 行:<c>{ id, kind:'ssh', name, target, authMethod?, platform, enabled, revision, createdAt, updatedAt }</c>)。
/// 完整 JSON 保留在 <see cref="Json"/> 里:3.0 不认识的键(2.x 以后加的)读写时原样保留。<b>凭据不在这里</b>(密码、口令在 <c>credentials</c> 表,NCK1)。
/// </summary>
public sealed record ConnectionRecord(string Id, string Name, bool Enabled, long Revision, long CreatedAt, long UpdatedAt, JsonObject Json);

/// <summary>
/// 2.x 的 <c>connection_profiles</c> 表(id / json):SSH 环境的配置。3.0 沿用同一张表和同一个 JSON 形状(接管 2.x 数据后原样可用),
/// 3.0 新增的字段(<c>proxy</c> 等)只增键。不按账户作用域收窄(2.x 的连接配置就是设备级的)。
/// </summary>
public sealed class ConnectionProfileRepository(AgentDatabase database)
{
    public IReadOnlyList<ConnectionRecord> List() =>
        [.. database.Rows("SELECT json FROM connection_profiles ORDER BY rowid", r => Parse(r.GetString(0))).OfType<ConnectionRecord>()];

    public ConnectionRecord? Get(string id) =>
        database.Row("SELECT json FROM connection_profiles WHERE id = $id", r => Parse(r.GetString(0))!, ("$id", id));

    /// <summary>写入(新建或整行替换)。<paramref name="json"/> 里必须有 <c>id</c>。</summary>
    public void Put(JsonObject json)
    {
        var id = json["id"]?.GetValue<string>() ?? throw new StorageException("A connection profile needs an id.");
        database.Run("""
            INSERT INTO connection_profiles (id, json) VALUES ($id, $j)
            ON CONFLICT (id) DO UPDATE SET json = excluded.json
            """, ("$id", id), ("$j", json.ToJsonString()));
    }

    public bool Remove(string id) => database.Run("DELETE FROM connection_profiles WHERE id = $id", ("$id", id)) > 0;

    private static ConnectionRecord? Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json || json["id"]?.GetValue<string>() is not { Length: > 0 } id || json["kind"]?.GetValue<string>() != "ssh")
            {
                return null;
            }

            return new ConnectionRecord(
                id,
                json["name"]?.GetValue<string>() ?? id,
                json["enabled"]?.GetValue<bool>() ?? true,
                json["revision"]?.GetValue<long>() ?? 1,
                json["createdAt"]?.GetValue<long>() ?? 0,
                json["updatedAt"]?.GetValue<long>() ?? 0,
                json);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // 一行坏数据不能让整个列表失败。
            return null;
        }
    }
}
