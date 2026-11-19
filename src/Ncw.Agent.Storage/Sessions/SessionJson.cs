using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// <c>sessions.json</c> 列的形状(2.x 的 <c>Session</c> 对象)。System.Text.Json 源生成,不走反射。
/// 属性的声明顺序就是写出的键顺序,与 2.x <c>JSON.stringify</c> 对同一个对象的输出一致;
/// 不认识的字段(2.x 或以后版本写进去的)落在 <see cref="Extra"/>,写回时原样保留。
/// </summary>
internal sealed class SessionJson
{
    public string? Id { get; set; }

    public string? WorkspaceId { get; set; }

    public string? ParentSessionId { get; set; }

    public string? Origin { get; set; }

    public string? Title { get; set; }

    public string? TitleSource { get; set; }

    public string? Model { get; set; }

    public string? ModelProviderId { get; set; }

    public string? Mode { get; set; }

    public string? Thinking { get; set; }

    public string? RootPathAtCreation { get; set; }

    public SkillSourceJson? SkillSource { get; set; }

    public string? Status { get; set; }

    public bool? Archived { get; set; }

    public bool? Favorited { get; set; }

    public long? CreatedAt { get; set; }

    public long? UpdatedAt { get; set; }

    public string? PermissionMode { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SkillSourceJson
{
    public string? SessionId { get; set; }
}

/// <summary>一条排队的输入(2.x 的 <c>QueuedInput</c>)。<c>options</c> 是发送选项的快照,Storage 不解释。</summary>
public sealed class QueuedInput
{
    public string? Id { get; set; }

    public string? Text { get; set; }

    public List<QueuedAttachment>? Attachments { get; set; }

    /// <summary><c>pending</c> / <c>promoted</c>;终态条目在进入终态时就从数组里移除。</summary>
    public string? Status { get; set; }

    public JsonObject? Options { get; set; }

    public long? EnqueuedAt { get; set; }

    public long? PromotedAt { get; set; }

    public string? ConsumedByRunId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class QueuedAttachment
{
    public string? Kind { get; set; }

    public string? Name { get; set; }

    public string? Url { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 落盘的会话输入:草稿 + 插入队列合成一个键 <c>session.input.&lt;会话 id&gt;</c>(2.x 的 <c>SessionInputState</c>)。
/// 3.0 里草稿是客户端本地状态,但 kv 里这个字段照旧保留(往返不丢),队列部分是 Agent 的权威状态。
/// </summary>
public sealed class SessionInputState
{
    public const int Version = 1;

    /// <summary>2.x 恢复时丢弃多久以前的存档:30 天。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    public int? V { get; set; } = Version;

    public string? Draft { get; set; } = "";

    public List<QueuedInput>? Queued { get; set; } = [];

    public long? SavedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// 读回存档时的校验(2.x <c>isValidSessionInput</c>):宁可整份丢弃也不做部分修复 ——
    /// 结构对不上意味着写它的代码和读它的代码不是同一个版本。
    /// </summary>
    public bool IsValid(long nowMs) =>
        V == Version
        && Draft is not null
        && Queued is not null
        && SavedAt is { } saved
        && nowMs - saved <= (long)Ttl.TotalMilliseconds
        && Queued.All(q => q.Id is not null && q.Text is not null && q.Attachments is not null
                           && q.Status is "pending" or "promoted" && q.Options is not null && q.EnqueuedAt is not null);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionJson))]
[JsonSerializable(typeof(SessionInputState))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 写库用的实例:不转义非 ASCII(2.x 的 <c>JSON.stringify</c> 原样输出中文,<c>JsonSerializer</c> 默认会转成 <c>\uXXXX</c>)。
    /// 读写的 JSON 语义等价,但保持文本一致,人读库、2.x 读库、<c>LIKE</c> 兜底搜索都更省心。
    /// </summary>
    public static StorageJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 带自定义选项构造的实例不继承上面的特性设置,要重复一遍。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}
