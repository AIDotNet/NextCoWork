using System.Text.Json.Nodes;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using StoredQueuedInput = Ncw.Agent.Storage.Sessions.QueuedInput;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>
/// 一条排队消息(2.x 的 <c>QueuedInput</c>)。<see cref="Id"/> 就是最终那条用户消息的 id(2.x ★):看见同 id 的 <c>message_end</c> 才算送达。
/// <see cref="Options"/> 是入队瞬间冻结的发送档位;<see cref="PromotedOrder"/> 是引入顺序(0 = 没被引入)。
/// </summary>
public sealed record QueueItem(
    MessageId Id,
    IReadOnlyList<Part> Parts,
    QueueState State,
    long PromotedOrder,
    long EnqueuedAt,
    long? PromotedAt,
    SendOptions Options);

/// <summary>
/// 队列的权威全量快照(<c>queue_update</c> 的载荷,也是 <c>session/&lt;id&gt;</c> 主题的队列半边)。<see cref="Rev"/> <b>全局</b>单调递增
/// (不是每会话各数各的:会话条目在空了之后会被丢掉,重新建出来时每会话计数会回到 0,客户端就会把新快照当成旧的丢掉,2.x ★)。
/// </summary>
public sealed record QueueUpdate(string SessionId, long Rev, IReadOnlyList<QueueItem> Items);

/// <summary>队列操作的回执。<see cref="Accepted"/> = false:被拒绝(超上限、找不到、同 id 已存在),队列没变,调用方据此保留草稿而不是以为消息进了队列。</summary>
public sealed record QueueResult(bool Accepted, QueueUpdate Update, IReadOnlyList<Part>? Taken = null);

/// <summary>排队落盘失败:操作已回滚,内存里仍是失败前的队列(绝不回 accepted)。</summary>
public sealed class QueuePersistenceException(string sessionId, Exception inner)
    : Exception($"The queue of session {sessionId} could not be saved.", inner)
{
    public string SessionId { get; } = sessionId;
}

/// <summary>内部输入(后台子代理的报告、目标检查):不进用户队列,不被用户侧的整份替换抹掉,没落盘(2.x 同:重启后由各自的持久回执恢复)。</summary>
/// <param name="Parts">正文。</param>
/// <param name="Options">没有运行中的 Run 时起新 Run 用的档位;空 = 沿用这个会话上一次的发送档位。</param>
/// <param name="GoalId">目标检查的目标 id(定向消息,由目标层解释)。</param>
/// <param name="Done">送达回调:true = 看到了同 id 的 <c>message_end</c>(被消费),false = 没送出去(中止 / 出错 / 起不来 / 重试耗尽)。</param>
public sealed record InternalInput(IReadOnlyList<Part> Parts, SendOptions? Options = null, string? GoalId = null, Action<bool>? Done = null);

public enum InternalDelivery
{
    /// <summary>进了正在运行的 Run 的引导队列(回合边界注入)。</summary>
    Interjected,

    /// <summary>会话空闲:起了一个新的内部 Run。</summary>
    Launched,

    /// <summary>会话正在收尾 / 被历史操作占着:排到它空出来之后。</summary>
    Deferred,

    /// <summary>没送出去(没有档位、启动被拒)。</summary>
    Failed,
}

/// <summary>排队消息的持久层接缝。默认实现 <see cref="SessionInputQueueStore"/> 落在 kv 里(2.x 的 <c>session.input.&lt;id&gt;</c>);测试可以包一层注入失败。</summary>
public interface IQueueStore
{
    /// <summary>读回存档;不存在 / 结构对不上 / 过期 = null(整份丢弃,不做部分修复)。</summary>
    IReadOnlyList<QueueItem>? Load(string sessionId);

    /// <summary>整份写入(只含非终态条目)。失败必须抛。</summary>
    void Save(string sessionId, IReadOnlyList<QueueItem> items);
}

/// <summary>
/// <see cref="IQueueStore"/> 的 Storage 实现。草稿和队列合成一个 kv 键(2.x);3.0 里草稿是客户端本地状态,但这一行里的草稿和未知字段照旧原样保留(往返不丢),
/// 队列部分是 Agent 的权威状态。只存 <see cref="Ncw.Agent.Kernel.Loop.QueueState.Pending"/> / <see cref="Ncw.Agent.Kernel.Loop.QueueState.Promoted"/>(终态条目离开队列时直接移出)。
/// </summary>
public sealed class SessionInputQueueStore(SessionInputStore inputs) : IQueueStore
{
    public IReadOnlyList<QueueItem>? Load(string sessionId)
    {
        var state = inputs.Read(sessionId);
        if (state?.Queued is not { } queued)
        {
            return null;
        }

        var items = new List<QueueItem>(queued.Count);
        foreach (var stored in queued)
        {
            if (QueueCodec.FromStorage(stored) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public void Save(string sessionId, IReadOnlyList<QueueItem> items)
    {
        var existing = inputs.Read(sessionId);
        var state = new SessionInputState
        {
            Draft = existing?.Draft ?? "",
            Queued = [.. items.Select(QueueCodec.ToStorage)],
            Extra = existing?.Extra,
        };
        inputs.Write(sessionId, state);
    }
}

/// <summary>存档形状 ↔ <see cref="QueueItem"/>(2.x 的 <c>text</c> + <c>attachments</c> + <c>options</c>)。不走反射:<see cref="JsonObject"/> 只放原语。</summary>
internal static class QueueCodec
{
    public static StoredQueuedInput ToStorage(QueueItem item)
    {
        var text = string.Join("\n\n", item.Parts.OfType<TextPart>().Select(p => p.Text));
        var attachments = new List<QueuedAttachment>();
        foreach (var part in item.Parts)
        {
            switch (part)
            {
                case ImagePart image:
                    attachments.Add(new QueuedAttachment { Kind = "image", Name = FileNameOf(image.DataRef), Url = image.DataRef });
                    break;
                case FileRefPart file:
                    attachments.Add(new QueuedAttachment { Kind = "file", Name = file.Name, Url = file.Path });
                    break;
            }
        }

        return new StoredQueuedInput
        {
            Id = item.Id.Value,
            Text = text,
            Attachments = attachments,
            Status = item.State == QueueState.Promoted ? "promoted" : "pending",
            Options = OptionsToJson(item.Options),
            EnqueuedAt = item.EnqueuedAt,
            PromotedAt = item.State == QueueState.Promoted ? item.PromotedAt : null,
        };
    }

    public static QueueItem? FromStorage(StoredQueuedInput stored)
    {
        if (stored.Id is not { Length: > 0 } id || stored.Status is not ("pending" or "promoted"))
        {
            return null;
        }

        var parts = new List<Part>();
        if (!string.IsNullOrEmpty(stored.Text))
        {
            parts.Add(new TextPart(stored.Text));
        }

        foreach (var attachment in stored.Attachments ?? [])
        {
            if (attachment.Url is not { Length: > 0 } url)
            {
                continue;
            }

            parts.Add(attachment.Kind == "image" ? new ImagePart(MimeOfExt(url), url) : new FileRefPart(url, attachment.Name ?? FileNameOf(url)));
        }

        var promoted = stored.Status == "promoted";
        var enqueuedAt = stored.EnqueuedAt ?? 0;
        return new QueueItem(new MessageId(id), parts, promoted ? QueueState.Promoted : QueueState.Pending, 0, enqueuedAt, promoted ? stored.PromotedAt ?? enqueuedAt : null, OptionsFromJson(stored.Options));
    }

    public static JsonObject OptionsToJson(SendOptions options)
    {
        var json = new JsonObject
        {
            ["model"] = options.Model.Alias,
            ["thinking"] = options.Thinking.ToString().ToLowerInvariant(),
            ["maxContext"] = options.MaxContext,
        };
        if (options.Model.ProviderId is { } provider)
        {
            json["modelProviderId"] = provider;
        }

        if (options.Mode.Length > 0)
        {
            json["mode"] = options.Mode;
        }

        if (options.PermissionMode.Length > 0)
        {
            json["permissionMode"] = options.PermissionMode;
        }

        return json;
    }

    /// <summary>宽容地读(2.x 写的档位里还有别的键,不认识的忽略):缺什么都有缺省,不抛。</summary>
    public static SendOptions OptionsFromJson(JsonObject? json)
    {
        string? Str(string key) => json?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var thinking = Enum.TryParse<ThinkingLevel>(Str("thinking"), ignoreCase: true, out var level) ? level : ThinkingLevel.Auto;
        var maxContext = json?["maxContext"] is JsonValue m && m.TryGetValue<bool>(out var b) && b;
        return new SendOptions(new ModelRef(Str("modelProviderId"), Str("model") ?? ""), thinking, maxContext, Str("mode") ?? "", Str("permissionMode") ?? "");
    }

    private static string FileNameOf(string url)
    {
        var i = url.LastIndexOf('/');
        return i < 0 ? url : url[(i + 1)..];
    }

    private static string MimeOfExt(string url) => Path.GetExtension(url).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "image/png",
    };
}

/// <summary>
/// 下一批该发哪些条目、怎么合并(2.x 的 <c>pickNextBatch</c> / <c>mergeBatch</c> / <c>batchToParts</c>),纯函数。
/// </summary>
public static class QueueBatches
{
    /// <summary>单条文本上限(同时挡住「合并后必然超上下文」和「kv 单行被撑爆」)。</summary>
    public const int MaxText = 32_000;

    /// <summary>
    /// ★ 有 promoted 就只发 promoted(按引入顺序),一条 pending 都不捎带:用户点了插话,表达的是「先说这个」,而不是「顺便也把队首发了」。
    /// ★ 没有 promoted 时退回 FIFO 取一条——对改造前行为的兼容承诺。(注意:这与 <see cref="RunInputQueues.TakeFollowUps"/>「全部交出」不同,续跑以 2.x 为准。)
    /// </summary>
    public static IReadOnlyList<QueueItem> PickNext(IReadOnlyList<QueueItem> queue)
    {
        var promoted = queue.Where(q => q.State == QueueState.Promoted).OrderBy(q => q.PromotedOrder).ToList();
        if (promoted.Count > 0)
        {
            return promoted;
        }

        var next = queue.FirstOrDefault(q => q.State == QueueState.Pending);
        return next is null ? [] : [next];
    }

    public sealed record Merged(IReadOnlyList<Part> Parts, IReadOnlyList<MessageId> DeferredIds);

    /// <summary>
    /// 合并成<b>一次</b>输入(为什么不连发多个 Run:Run 是串行的,发 N 个等于把插话重新变回排队)。
    /// ★ 超限时退回 pending 并汇报,不静默截断:截断会让用户以为消息发出去了,而后半句永远消失了。首条无论多长都必须完整发出。
    /// </summary>
    public static Merged Merge(IReadOnlyList<QueueItem> batch)
    {
        var taken = new List<QueueItem>();
        var deferred = new List<MessageId>();
        var used = 0;
        foreach (var item in batch)
        {
            var cost = TextOf(item).Length + (taken.Count > 0 ? 2 : 0);
            // 已经取了至少一条、再加这条就超限 → 剩下的全部退回,不再继续尝试塞小的:乱序会让用户看到的发送顺序与他排的顺序对不上。
            if (taken.Count > 0 && used + cost > MaxText)
            {
                deferred.Add(item.Id);
                continue;
            }

            taken.Add(item);
            used += cost;
        }

        var parts = new List<Part>();
        var text = string.Join("\n\n", taken.Select(TextOf).Where(t => t.Length > 0));
        if (text.Length > 0)
        {
            parts.Add(new TextPart(text));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in taken.SelectMany(t => t.Parts))
        {
            var key = part switch
            {
                ImagePart image => "i:" + image.DataRef,
                FileRefPart file => "f:" + file.Path,
                _ => null,
            };
            if (key is not null && seen.Add(key))
            {
                parts.Add(part);
            }
        }

        return new Merged(parts, deferred);
    }

    private static string TextOf(QueueItem item) => string.Join("\n\n", item.Parts.OfType<TextPart>().Select(p => p.Text.Trim()).Where(t => t.Length > 0));
}
