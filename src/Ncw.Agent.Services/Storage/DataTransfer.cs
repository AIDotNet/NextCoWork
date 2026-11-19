using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Services.Storage;

public sealed record ExportInfo(string ExportId, long Bytes, long Workspaces, long Sessions, long Messages);

public sealed record ExportChunk(byte[] Data, bool Eof);

public sealed record ImportUpload(string UploadId, long Received);

/// <param name="ConfirmToken">应用导入(会覆盖较旧的同 id 会话)的确认令牌。</param>
public sealed record ImportPreview(string UploadId, long Version, long Workspaces, long Sessions, long Messages, long NewCount, long OverwriteCount, long SkippedCount, string ConfirmToken);

public sealed record ImportResult(long Imported, long Overwritten, long Skipped, long WorkspacesImported, long SessionsImported, long MessagesImported);

/// <summary>
/// 数据导出 / 导入(2.x 的 <c>storage:export</c> / <c>importPreview</c> / <c>importApply</c>;文件对话框在客户端,Agent 只经协议分块收发字节)。
/// <list type="bullet">
/// <item><b>格式</b>:JSON <c>{ type: "nextcowork-data-export", version: 1, exportedAt, workspaces[], sessions[{ session, messages[] }] }</c>,会话和消息的字段名沿用 2.x(<c>workspaceId</c> / <c>createdAt</c> / <c>parts</c>……)。
/// 不含任何凭据(2.x 的「加密导出密钥」随供应商模块的导入导出一起做,见 docs/parity.md),也不含子代理转录(不是用户的对话)。</item>
/// <item><b>合并规则</b>(2.x 的 <c>dataMergeDecision</c>,预览与应用共用一个函数):本地没有 → 新增;两边都有有限的 <c>updatedAt</c> 且导入的更新 → 覆盖;其余一律保留本地(时间戳缺失时的安全做法)。工作区没有 <c>updatedAt</c>,有就跳过。</item>
/// <item><b>传输</b>:文件落在数据根的 <c>transfer/</c> 下,协议按偏移分块读写(单块 ≤ 1 MiB,帧上限 4 MiB);id 是随机十六进制(不接受路径);1 小时过期自动清;导入上限 128 MiB。</item>
/// <item>应用导入在一个事务里;应用前必须出示预览签发的确认令牌。</item>
/// </list>
/// </summary>
public sealed class DataTransfer(StorageEnvironment env, SessionRepository sessions, WorkspaceRepository workspaces, ConfirmTokens tokens)
{
    public const string ExportType = "nextcowork-data-export";
    public const int ExportVersion = 1;
    public const long MaxImportBytes = 128L * 1024 * 1024;
    public const int MaxChunkBytes = 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private string Dir => Path.Combine(env.DataRoot, "transfer");

    private static bool IsId(string id) => id.Length is > 0 and <= 64 && id.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string PathOf(string kind, string id)
    {
        if (!IsId(id))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.transfer.invalidId", "Not a transfer id.");
        }

        return Path.Combine(Dir, $"{kind}-{id}.json");
    }

    private void Sweep()
    {
        if (!Directory.Exists(Dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < env.Clock.GetUtcNow().UtcDateTime - Retention)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // 下次再清
            }
        }
    }

    // ───────────────────────── 导出 ─────────────────────────

    public ExportInfo CreateExport()
    {
        Directory.CreateDirectory(Dir);
        Sweep();
        var root = new JsonObject
        {
            ["type"] = ExportType,
            ["version"] = ExportVersion,
            ["exportedAt"] = DateTimeOffset.FromUnixTimeMilliseconds(env.Now).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        var ws = new JsonArray();
        foreach (var w in workspaces.List())
        {
            ws.Add((JsonNode?)w.Json.DeepClone());
        }

        root["workspaces"] = ws;
        var list = new JsonArray();
        long messages = 0;
        foreach (var session in sessions.ListAll(topLevelOnly: true))
        {
            var history = sessions.History(session.Id);
            messages += history.Count;
            var ms = new JsonArray();
            foreach (var m in history)
            {
                ms.Add((JsonNode?)MessageJson(m));
            }

            list.Add((JsonNode?)new JsonObject { ["session"] = SessionJson(session), ["messages"] = ms });
        }

        root["sessions"] = list;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var path = PathOf("export", id);
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(path, bytes);
        return new ExportInfo(id, bytes.Length, ws.Count, list.Count, messages);
    }

    public ExportChunk ReadExport(string exportId, long offset, int length)
    {
        var path = PathOf("export", exportId);
        if (!File.Exists(path))
        {
            throw new StorageOperationException(StorageFailure.NotFound, "storage.transfer.notFound", "The export no longer exists.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset < 0 || offset > stream.Length)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.transfer.offset", "The offset is outside the file.");
        }

        stream.Position = offset;
        var buffer = new byte[Math.Clamp(length, 1, MaxChunkBytes)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return new ExportChunk(buffer.AsSpan(0, read).ToArray(), stream.Position >= stream.Length);
    }

    public void DiscardExport(string exportId)
    {
        var path = PathOf("export", exportId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ───────────────────────── 导入 ─────────────────────────

    /// <summary>
    /// 分块上传导入文件。<paramref name="uploadId"/> 为 null = 新开一份(此时 <paramref name="offset"/> 必须是 0);块必须按顺序到(偏移 = 已收到的字节数:重发同一块不会重复追加,乱序是 invalid)。
    /// </summary>
    public ImportUpload WriteImport(string? uploadId, long offset, byte[] data)
    {
        if (data.Length > MaxChunkBytes)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.transfer.chunkTooLarge", "A chunk is at most 1 MiB.");
        }

        Directory.CreateDirectory(Dir);
        if (uploadId is null)
        {
            Sweep();
            uploadId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        }

        var path = PathOf("import", uploadId);
        var have = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset > have || (offset < have && offset + data.Length > have))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.transfer.offset", "Chunks must arrive in order.");
        }

        if (offset == have)
        {
            if (have + data.Length > MaxImportBytes)
            {
                throw new StorageOperationException(StorageFailure.Invalid, "storage.transfer.tooLarge", "The import file is too large.");
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(data);
            have += data.Length;
        }

        // offset < have 且整块都在已收到的范围内:重发(幂等),不追加。
        return new ImportUpload(uploadId, have);
    }

    public void DiscardImport(string uploadId)
    {
        var path = PathOf("import", uploadId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private JsonObject Load(string uploadId)
    {
        var path = PathOf("import", uploadId);
        if (!File.Exists(path))
        {
            throw new StorageOperationException(StorageFailure.NotFound, "storage.transfer.notFound", "The upload no longer exists.");
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.import.notJson", "The import file is not valid JSON.");
        }

        if (root is null || root["type"]?.GetValue<string>() != ExportType || root["workspaces"] is not JsonArray || root["sessions"] is not JsonArray)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.import.unsupported", "The import file format is not supported.");
        }

        var version = root["version"]?.GetValue<long>() ?? 0;
        if (version is < 1 or > ExportVersion)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.import.version", "The import file is newer than this app understands.");
        }

        return root;
    }

    /// <summary>2.x 的 <c>dataMergeDecision</c>:预览和应用共用。本地没有 → new;两边都有有限的 updatedAt 且导入的更新 → overwrite;其余 skip。</summary>
    public static string MergeDecision(long? localUpdatedAt, bool localExists, long? incomingUpdatedAt)
    {
        if (!localExists)
        {
            return "new";
        }

        return localUpdatedAt is { } l && incomingUpdatedAt is { } i && i > l ? "overwrite" : "skip";
    }

    public ImportPreview PreviewImport(string uploadId)
    {
        var root = Load(uploadId);
        long news = 0, overwrites = 0, skipped = 0, messages = 0;
        foreach (var w in root["workspaces"]!.AsArray())
        {
            var id = w?["id"]?.GetValue<string>();
            Count(MergeDecision(null, id is not null && workspaces.Get(id) is not null, null), ref news, ref overwrites, ref skipped);
        }

        foreach (var item in root["sessions"]!.AsArray())
        {
            var s = item?["session"];
            var id = s?["id"]?.GetValue<string>();
            messages += item?["messages"]?.AsArray().Count ?? 0;
            var local = id is null ? null : sessions.Get(id);
            Count(MergeDecision(local?.UpdatedAt, local is not null, s?["updatedAt"]?.GetValue<long>()), ref news, ref overwrites, ref skipped);
        }

        return new ImportPreview(uploadId, root["version"]!.GetValue<long>(), root["workspaces"]!.AsArray().Count, root["sessions"]!.AsArray().Count, messages, news, overwrites, skipped,
            tokens.Issue("import:" + uploadId));

        static void Count(string decision, ref long n, ref long o, ref long k)
        {
            switch (decision)
            {
                case "new": n++; break;
                case "overwrite": o++; break;
                default: k++; break;
            }
        }
    }

    public ImportResult ApplyImport(string uploadId, string? confirmToken)
    {
        if (!tokens.Redeem(confirmToken, "import:" + uploadId))
        {
            throw new StorageOperationException(StorageFailure.Unconfirmed, "storage.confirmationRequired", "Importing needs a fresh confirmation token from a preview of this upload.");
        }

        if (env.HasActiveWork())
        {
            throw new StorageOperationException(StorageFailure.Busy, "storage.busy", "An agent run or a history operation is in progress; try again when it has finished.");
        }

        var root = Load(uploadId);
        long imported = 0, overwritten = 0, skipped = 0, workspaceCount = 0, sessionCount = 0, messageCount = 0;
        env.Database.InTransaction(() =>
        {
            foreach (var w in root["workspaces"]!.AsArray())
            {
                if (w is not JsonObject json)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    if (workspaces.ImportIfAbsent(json) is not null)
                    {
                        imported++;
                        workspaceCount++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (StorageException)
                {
                    skipped++; // 撞上别的账户作用域的同 id:不顶替
                }
            }

            foreach (var item in root["sessions"]!.AsArray())
            {
                if (item?["session"] is not JsonObject s || ParseSession(s) is not { } record || item["messages"] is not JsonArray list)
                {
                    skipped++;
                    continue;
                }

                var local = sessions.Get(record.Id);
                var decision = MergeDecision(local?.UpdatedAt, local is not null, s["updatedAt"]?.GetValue<long>());
                if (decision == "skip")
                {
                    skipped++;
                    continue;
                }

                try
                {
                    sessions.Put(record);
                    var history = list.Select(ParseMessage).OfType<MessageRecord>().ToList();
                    sessions.ReplaceHistory(record.Id, history);
                    messageCount += history.Count;
                    sessionCount++;
                    if (decision == "overwrite")
                    {
                        overwritten++;
                    }
                    else
                    {
                        imported++;
                    }
                }
                catch (StorageException)
                {
                    skipped++;
                }
            }
        });
        DiscardImport(uploadId);
        return new ImportResult(imported, overwritten, skipped, workspaceCount, sessionCount, messageCount);
    }

    // ───────────────────────── 会话 / 消息 ↔ JSON(字段名沿用 2.x) ─────────────────────────

    private static JsonObject SessionJson(SessionRecord s)
    {
        var o = new JsonObject
        {
            ["id"] = s.Id, ["workspaceId"] = s.WorkspaceId, ["origin"] = s.Origin, ["title"] = s.Title, ["model"] = s.Model, ["mode"] = s.Mode, ["thinking"] = s.Thinking,
            ["rootPathAtCreation"] = s.RootPathAtCreation, ["archived"] = s.Archived, ["favorited"] = s.Favorited, ["createdAt"] = s.CreatedAt, ["updatedAt"] = s.UpdatedAt,
        };
        if (s.TitleSource is not null)
        {
            o["titleSource"] = s.TitleSource;
        }

        if (s.ModelProviderId is not null)
        {
            o["modelProviderId"] = s.ModelProviderId;
        }

        if (s.PermissionMode != "ask")
        {
            o["permissionMode"] = s.PermissionMode;
        }

        return o;
    }

    private static SessionRecord? ParseSession(JsonObject s)
    {
        static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
        static long Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        static bool Flag(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var x) && x;
        if (Str(s, "id") is not { Length: > 0 } id || Str(s, "workspaceId") is not { } workspaceId)
        {
            return null;
        }

        return new SessionRecord(
            id, workspaceId, null, Str(s, "origin") ?? "user", Str(s, "title") ?? SessionRepository.DefaultTitle, Str(s, "titleSource"), Str(s, "model") ?? "", Str(s, "modelProviderId"),
            SessionRepository.NormalizeMode(Str(s, "mode")), Str(s, "thinking") ?? "auto", Str(s, "rootPathAtCreation") ?? "", null, Flag(s, "archived"), Flag(s, "favorited"),
            Num(s, "createdAt"), Num(s, "updatedAt"), SessionRepository.NormalizePermissionMode(Str(s, "permissionMode")));
    }

    private static JsonObject MessageJson(MessageRecord m)
    {
        JsonNode? parts;
        try
        {
            parts = JsonNode.Parse(m.PartsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            parts = new JsonArray();
        }

        var o = new JsonObject { ["id"] = m.Id, ["role"] = m.Role, ["parts"] = parts ?? new JsonArray(), ["createdAt"] = m.CreatedAt };
        if (m.Internal)
        {
            o["internal"] = true;
        }

        if (m.MetaJson is not null)
        {
            try
            {
                o["meta"] = JsonNode.Parse(m.MetaJson);
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏 meta 不导出
            }
        }

        return o;
    }

    private static MessageRecord? ParseMessage(JsonNode? node)
    {
        if (node is not JsonObject o || o["id"]?.GetValue<string>() is not { Length: > 0 } id || o["role"]?.GetValue<string>() is not { } role || o["parts"] is not JsonArray parts)
        {
            return null;
        }

        return new MessageRecord(
            id, role, parts.ToJsonString(), o["createdAt"] is JsonValue c && c.TryGetValue<long>(out var at) ? at : 0, 1, o["internal"] is JsonValue i && i.TryGetValue<bool>(out var flag) && flag,
            o["meta"]?.ToJsonString());
    }
}
