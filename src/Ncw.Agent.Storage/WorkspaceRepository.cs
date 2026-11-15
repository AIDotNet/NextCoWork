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
    public const string LocalScope = ConfigScope.Local;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>当前作用域:kv 表里的 <c>config-profile.scope</c>,没有或坏了就是 local。每次现读,不缓存。</summary>
    public string CurrentScope() => ConfigScope.Current(database);

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

    /// <summary>
    /// 登记一个 SSH 环境里的目录(2.x 的 <c>createSshWorkspace</c>:<c>environment = { kind: 'connection', connectionId }</c>,名字默认 <c>&lt;连接名&gt;: &lt;目录名&gt;</c>)。
    /// 同一个环境里同一个目录只登记一次:再添加时只更新最近打开时间,返回已有的。<paramref name="rootPath"/> 是远程主机上的规范路径(由调用方经环境 realpath 得到)。
    /// </summary>
    public WorkspaceRecord AddRemote(string connectionId, string rootPath, string? name, string connectionName)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (List().FirstOrDefault(w => w.EnvironmentKind == "connection" && w.ConnectionId == connectionId && w.RootPath == rootPath) is { } existing)
        {
            var touched = (JsonObject)existing.Json.DeepClone();
            touched["lastOpenedAt"] = now;
            touched.Remove("unavailable");
            return Put(touched, now)!;
        }

        var baseName = rootPath.TrimEnd('/', '\\');
        var leaf = baseName[(Math.Max(baseName.LastIndexOf('/'), baseName.LastIndexOf('\\')) + 1)..];
        var json = new JsonObject
        {
            ["id"] = Ulid.Prefixed("ws", _clock),
            ["name"] = string.IsNullOrWhiteSpace(name) ? $"{connectionName}: {(leaf.Length > 0 ? leaf : rootPath)}" : name.Trim(),
            ["rootPath"] = rootPath,
            ["environment"] = new JsonObject { ["kind"] = "connection", ["connectionId"] = connectionId },
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

    /// <summary>
    /// 改一个工作区的 <c>settings</c> 段(项目设置页)。<paramref name="change"/> 直接改传进来的对象;段里 3.0 不认识的键(技能选装等)原样保留,
    /// 工作区 JSON 的其它部分、最近打开时间也不动。不存在或属于别的作用域返回 null。
    /// </summary>
    public WorkspaceRecord? UpdateSettings(string id, Action<JsonObject> change)
    {
        if (Get(id) is not { } record)
        {
            return null;
        }

        var json = (JsonObject)record.Json.DeepClone();
        if (json["settings"] is not JsonObject settings)
        {
            settings = DefaultSettings();
            json["settings"] = settings;
        }

        change(settings);
        return Put(json, record.LastOpenedAt);
    }

    /// <summary>
    /// 导入一个工作区(数据导入):已经有同 id 的就不动(返回 null,工作区没有 <c>updatedAt</c>,2.x 的合并规则是「有就跳过」);否则按它的 JSON 原样登记。
    /// 撞上别的账户作用域的同 id 时抛 <see cref="StorageException"/>(不顶替)。
    /// </summary>
    public WorkspaceRecord? ImportIfAbsent(JsonObject json)
    {
        if (json["id"]?.GetValue<string>() is not { Length: > 0 } id)
        {
            throw new StorageException("An imported workspace needs an id.");
        }

        if (Get(id) is not null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var copy = (JsonObject)json.DeepClone();
        copy["lastOpenedAt"] = json["lastOpenedAt"]?.GetValue<long>() ?? now;
        return Put(copy, copy["lastOpenedAt"]!.GetValue<long>());
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
