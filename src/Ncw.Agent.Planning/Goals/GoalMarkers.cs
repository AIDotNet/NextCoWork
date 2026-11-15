using System.Text.Json;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// 目标标记的转录口(Storage 的接缝)。目标本身是进程内状态,落盘的是助手消息上那条 <c>goal_status</c> 标记——它跟着转录走完整的持久化路径,不需要第二份存储,也就不会出现两份不一致。
/// 写标记是<b>带外</b>的(Run 正在写转录的时候用户也可以清掉目标),所以用「比较并替换」:只换 parts,前提是它还是读出来时的那一份。
/// </summary>
public interface IGoalTranscript
{
    bool Exists(string sessionId);

    /// <summary>最后一条助手消息(原样的 parts JSON 也在里面,比较并替换用)。没有 = null。</summary>
    GoalMessage? LastAssistant(string sessionId);

    /// <summary>最后一条带 <c>goal_status</c> 块的助手消息(重载恢复只需要它:不必把整段转录解析一遍)。没有 = null。</summary>
    GoalMessage? LastWithGoalStatus(string sessionId);

    /// <summary>只换一条消息的 parts;现存的不再是 <paramref name="expectedPartsJson"/> 时返回 false(调用方重读重试)。</summary>
    bool TryReplaceParts(string sessionId, string messageId, string expectedPartsJson, string newPartsJson);
}

/// <summary>一条消息 + 它原样的 parts JSON(往里追加一个块时不重新编码别的块:2.x 写的、本版本不认识的字段原样保留)。</summary>
public sealed record GoalMessage(Message Message, string PartsJson);

public sealed class SessionGoalTranscript(SessionRepository sessions) : IGoalTranscript
{
    public bool Exists(string sessionId) => sessions.Exists(sessionId);

    public GoalMessage? LastAssistant(string sessionId) => Wrap(sessions.LastAssistant(sessionId));

    public GoalMessage? LastWithGoalStatus(string sessionId) => Wrap(sessions.LastGoalStatus(sessionId));

    public bool TryReplaceParts(string sessionId, string messageId, string expectedPartsJson, string newPartsJson) =>
        sessions.ReplacePartsIfUnchanged(sessionId, messageId, expectedPartsJson, newPartsJson);

    private static GoalMessage? Wrap(MessageRecord? record) => record is null ? null : new GoalMessage(MessageCodec.FromRecord(record), record.PartsJson);
}

/// <summary>
/// 重载恢复(2.x <c>goal/restore.ts</c>):从转录反扫「这条会话还挂着一个目标吗」。
/// 恢复<b>只重建状态,不重开一轮 Run</b>(用户的明确选择;和「永不恢复运行中状态」不冲突:恢复的是目标——一个待满足的条件,不是一个运行中的 Run)。用户下次发消息时,回合末判定自然接上。
/// </summary>
public static class GoalRestore
{
    /// <summary>
    /// 从后往前找<b>最后一条</b> <c>goal_status</c>:是 met / failed / cleared → 那个目标已经结束,返回 <c>null</c>;是「未达成」→ 目标当时是活的,返回它的条件;一条都没有 → 从来没设过目标。
    /// ★ 只认最后一条:一条会话先后设过三个目标时,前两条的标记仍然留在转录里(那是历史,该留着),但只有最后一条描述的是「现在」。
    /// ★ 三个终态<b>都要判</b>。只判 met 的话,一个被用户亲手清掉的目标会在重载后自己回来——而他清它正是因为不想再跑了。
    /// </summary>
    public static string? RestorableCondition(IEnumerable<Message> messages)
    {
        if (LastStatus(messages) is not { } last || last.Met || last.Failed == true || last.Cleared == true)
        {
            return null;
        }

        var condition = GoalConditions.Normalize(last.Condition);
        return condition.Length == 0 || condition.Length > GoalConditions.Max ? null : condition;
    }

    /// <summary>最后一条目标标记本身(同一条消息里有多条时取靠后的)。</summary>
    public static GoalStatusPart? LastStatus(IEnumerable<Message> messages)
    {
        GoalStatusPart? last = null;
        foreach (var message in messages)
        {
            if (message.Role != Role.Assistant)
            {
                continue;
            }

            foreach (var part in message.Parts)
            {
                if (part is GoalStatusPart status)
                {
                    last = status;
                }
            }
        }

        return last;
    }
}

/// <summary>
/// 往最后一条助手消息上追加目标标记(2.x 的 <c>recordStatus</c> / <c>prepareGoalMessage</c>)。设定与清除的标记追加到最后一条助手消息上,<b>不新增消息</b>;
/// 转录末尾还没有助手消息时先入队,等有了(<see cref="FlushPending"/>)再落上去——队列只放一次,下一条助手消息不会再被补一遍(同一条标记补两遍 = 界面上两条「已设定」)。
/// </summary>
public sealed class GoalMarkers(IGoalTranscript transcript, IIdGenerator ids, TimeProvider clock, Action<string, Exception?>? warn = null)
{
    private const int CasRetries = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<GoalStatusPart>> _pending = new(StringComparer.Ordinal);

    /// <summary>给标记补上 id 和创建时间(合并去重用),然后写上去。返回写成功的那条消息(没有助手消息 / 写失败 = null,标记已入队)。</summary>
    public Message? Record(string sessionId, GoalStatusPart part)
    {
        var stamped = part with { Id = part.Id ?? ids.NewId(), CreatedAt = part.CreatedAt ?? clock.GetUtcNow().ToUnixTimeMilliseconds() };
        return TryAttach(sessionId, [stamped]) ?? Defer(sessionId, stamped);
    }

    /// <summary>有助手消息可以挂了:把排着的标记一次性落上去。没有排着的就什么都不做。</summary>
    public Message? FlushPending(string sessionId)
    {
        List<GoalStatusPart> parts;
        lock (_gate)
        {
            if (!_pending.Remove(sessionId, out parts!))
            {
                return null;
            }
        }

        var message = TryAttach(sessionId, parts);
        if (message is null)
        {
            lock (_gate)
            {
                // 还是挂不上(没有助手消息 / 写失败):放回队首,保序
                _pending[sessionId] = [.. parts, .. _pending.GetValueOrDefault(sessionId) ?? []];
            }
        }

        return message;
    }

    public int PendingCount(string sessionId)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(sessionId, out var list) ? list.Count : 0;
        }
    }

    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            _pending.Remove(sessionId);
        }
    }

    private Message? Defer(string sessionId, GoalStatusPart part)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(sessionId, out var list))
            {
                _pending[sessionId] = list = [];
            }

            list.Add(part);
        }

        return null;
    }

    private Message? TryAttach(string sessionId, IReadOnlyList<GoalStatusPart> parts)
    {
        try
        {
            for (var attempt = 0; attempt < CasRetries; attempt++)
            {
                // 标记只挂在助手消息上,哪怕此刻最后一条是工具结果
                if (transcript.LastAssistant(sessionId) is not { } last)
                {
                    return null;
                }

                var newJson = AppendParts(last.PartsJson, parts);
                if (transcript.TryReplaceParts(sessionId, last.Message.Id.Value, last.PartsJson, newJson))
                {
                    return last.Message with { Parts = [.. last.Message.Parts, .. parts] };
                }
            }
        }
        catch (Exception e)
        {
            warn?.Invoke("A goal marker could not be written.", e);
        }

        return null;
    }

    /// <summary>把标记追加到原样的 parts 数组文本后面,不重新编码已有的块。</summary>
    internal static string AppendParts(string partsJson, IReadOnlyList<GoalStatusPart> parts)
    {
        var added = MessageCodec.PartsToJson([.. parts]);
        var inner = added.AsSpan().Trim()[1..^1].ToString();
        var existing = partsJson.TrimEnd();
        if (!existing.EndsWith(']'))
        {
            throw new JsonException("The stored parts are not a JSON array.");
        }

        var body = existing[..^1].TrimEnd();
        return body.EndsWith('[') ? $"{body}{inner}]" : $"{body},{inner}]";
    }
}
