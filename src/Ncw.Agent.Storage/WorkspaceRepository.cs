using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Core.Ids;

namespace Ncw.Agent.Storage;

/// <summary>一个工作区(= 3.0 的「项目」)。完整 JSON 保留在 <see cref="Json"/> 里,3.0 不认识的字段原样保留。</summary>
public sealed record WorkspaceRecord(
    string Id,
    string Name,
    string RootPath,
    string EnvironmentKind,
    string? ConnectionId,
    long CreatedAt,
    long LastOpenedAt,
    JsonObject Json);

/// <summary>
/// 2.x 的 <c>workspaces</c> 表(id / last_opened_at / json / owner)。规则照搬 2.x 的 <c>repo.ts</c>:
/// <list type="bullet">
/// <item>按当前账户作用域收窄,收窄写在 SQL 的 WHERE 里(读回来再过滤,漏一处就是「别的账户的项目出现在列表里」)。</item>
/// <item>别的作用域的工作区当作不存在;撞上别的作用域已有的 id 时拒绝,不顶替。</item>
/// <item>同一个本机目录只登记一次:再添加时只更新 last_opened_at。</item>
/// <item>新建时的 JSON 形状与 2.x 一致(environment 缺省 = 本机,settings 用 2.x 的出厂默认值)。</item>
/// </list>
/// </summary>
public sealed class WorkspaceRepository(AgentDatabase database, TimeProvider? clock = null)
{
    /// <summary>2.x 的「未登录」作用域。</summary>
    public const string LocalScope = "local";

    private const string ScopeKey = "config-profile.scope";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>当前作用域:kv 表里的 <c>config-profile.scope</c>,没有或坏了就是 local。每次现读,不缓存。</summary>
    public string CurrentScope()
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT json FROM kv WHERE key = $k";
            command.Parameters.AddWithValue("$k", ScopeKey);
            if (command.ExecuteScalar() is not string json)
            {
                return LocalScope;
            }

            try
            {
                return JsonNode.Parse(json) is JsonValue value && value.TryGetValue<string>(out var scope) && scope.Length > 0 ? scope : LocalScope;
            }
            catch (JsonException)
            {
                return LocalScope;
            }
        }
    }

    public IReadOnlyList<WorkspaceRecord> List()
    {
        var scope = CurrentScope();
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT json FROM workspaces WHERE owner = $o ORDER BY last_opened_at DESC";
            command.Parameters.AddWithValue("$o", scope);
            using var reader = command.ExecuteReader();
            var list = new List<WorkspaceRecord>();
            while (reader.Read())
            {
                if (Parse(reader.GetString(0)) is { } record)
                {
                    list.Add(record);
                }
            }

            return list;
        }
    }

    public WorkspaceRecord? Get(string id)
    {
        var scope = CurrentScope();
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT owner, json FROM workspaces WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetString(0) == scope ? Parse(reader.GetString(1)) : null;
        }
    }

    /// <summary>登记一个本机目录。已经登记过(路径归一化后相同)就只更新最近打开时间,返回已有的。</summary>
    public WorkspaceRecord AddLocal(string rootPath, string? name = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (List().FirstOrDefault(w => w.EnvironmentKind == "local" && SamePath(w.RootPath, rootPath)) is { } existing)
        {
            var touched = (JsonObject)existing.Json.DeepClone();
            touched["lastOpenedAt"] = now;
            touched.Remove("unavailable");
            return Put(touched, now)!;
        }

        var json = new JsonObject
        {
            ["id"] = Ulid.Prefixed("ws", _clock),
            ["name"] = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(rootPath) is { Length: > 0 } n ? n : rootPath : name.Trim(),
            ["rootPath"] = rootPath,
            ["settings"] = DefaultSettings(),
            ["createdAt"] = now,
            ["lastOpenedAt"] = now,
        };
        return Put(json, now)!;
    }

    /// <summary>删除当前作用域下的一个工作区;不存在或属于别的作用域时返回 false。</summary>
    public bool Remove(string id)
    {
        var scope = CurrentScope();
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM workspaces WHERE id = $id AND owner = $o";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$o", scope);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>连接配置的显示名(SSH 项目在界面上显示主机名)。</summary>
    public string? ConnectionName(string connectionId)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT json FROM connection_profiles WHERE id = $id";
            command.Parameters.AddWithValue("$id", connectionId);
            if (command.ExecuteScalar() is not string json)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(json)?["name"]?.GetValue<string>();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    private WorkspaceRecord? Put(JsonObject json, long lastOpenedAt)
    {
        var id = json["id"]!.GetValue<string>();
        var scope = CurrentScope();
        lock (database.Gate)
        {
            using (var owner = database.Connection.CreateCommand())
            {
                owner.CommandText = "SELECT owner FROM workspaces WHERE id = $id";
                owner.Parameters.AddWithValue("$id", id);
                if (owner.ExecuteScalar() is string existingOwner && existingOwner != scope)
                {
                    throw new StorageException($"Workspace {id} belongs to another account scope.");
                }
            }

            using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ($id, $t, $j, $o)
                ON CONFLICT (id) DO UPDATE SET last_opened_at = excluded.last_opened_at, json = excluded.json
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$t", lastOpenedAt);
            command.Parameters.AddWithValue("$j", json.ToJsonString());
            command.Parameters.AddWithValue("$o", scope);
            command.ExecuteNonQuery();
        }

        return Parse(json.ToJsonString());
    }

    private static WorkspaceRecord? Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json || json["id"]?.GetValue<string>() is not { } id)
            {
                return null;
            }

            var environment = json["environment"] as JsonObject;
            var kind = environment?["kind"]?.GetValue<string>() ?? "local";
            return new WorkspaceRecord(
                id,
                json["name"]?.GetValue<string>() ?? id,
                json["rootPath"]?.GetValue<string>() ?? "",
                kind,
                kind == "connection" ? environment?["connectionId"]?.GetValue<string>() : null,
                json["createdAt"]?.GetValue<long>() ?? 0,
                json["lastOpenedAt"]?.GetValue<long>() ?? 0,
                json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // 一行坏数据不能让整个列表失败。
            return null;
        }
    }

    /// <summary>2.x 的 DEFAULT_WORKSPACE_SETTINGS(shared/domain/workspace.ts)。</summary>
    private static JsonObject DefaultSettings() => new()
    {
        ["permissionMode"] = "ask",
        ["defaultModel"] = "",
        ["defaultMode"] = "code",
        ["defaultThinking"] = "auto",
        ["webSearch"] = true,
        ["maxContext"] = false,
        ["activeSkillIds"] = new JsonArray(),
        ["skillSelectionMode"] = "all",
    };

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
