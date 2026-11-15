namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// 按消息 id 改写转录的**跨度**规则(2.x <c>shared/agent/history-edit.ts</c> 与 <c>session-clone.ts</c> 的 <c>turnEndIndex</c>)。
/// 删除的单位是跨度而不是一条消息:一轮问答在存储里是 <c>user → assistant(含 tool_call) → user(tool_result 回执) → assistant …</c>,
/// 只删可见的那两条会留下失去配对 tool_call 的 tool_result,下一次发消息才会炸。
/// 纯函数,只读 <see cref="MessageRecord"/>;库里的改写由 <see cref="SessionRepository"/> 在完整历史上做。
/// </summary>
public static class HistoryEdit
{
    /// <summary>一整轮的跨度 [start, end):从这条提问起,一直吃到下一条可见提问之前。找不到(或不是 user)返回 null。</summary>
    public static (int Start, int End)? TurnSpan(IReadOnlyList<MessageRecord> messages, string userMessageId)
    {
        var start = IndexOf(messages, userMessageId, "user");
        if (start < 0)
        {
            return null;
        }

        var end = start + 1;
        while (end < messages.Count && !MessageShape.IsTurnStart(messages[end]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>分支的切点:<c>userMessageId</c> 那一轮的结束位置(不含);与 <see cref="TurnSpan"/> 同一个口径。</summary>
    public static int? TurnEndIndex(IReadOnlyList<MessageRecord> messages, string userMessageId) => TurnSpan(messages, userMessageId)?.End;

    /// <summary>
    /// 一条助手回复的跨度:<c>fromId..toId</c> 两条助手消息之间的全部,再吃掉紧随其后的纯工具回执
    /// (回复以 tool_call 收尾时它的 tool_result 在后面,留下来就是一条失去配对的 tool_result)。
    /// 可见提问、后台汇报、压缩边界都不吃。
    /// </summary>
    public static (int Start, int End)? ReplySpan(IReadOnlyList<MessageRecord> messages, string fromId, string toId)
    {
        var start = IndexOf(messages, fromId, "assistant");
        var last = IndexOf(messages, toId, "assistant");
        if (start < 0 || last < start)
        {
            return null;
        }

        var end = last + 1;
        while (end < messages.Count && MessageShape.IsToolResultOnly(messages[end].Role, messages[end].PartsJson))
        {
            end++;
        }

        return (start, end);
    }

    public static List<MessageRecord> RemoveSpan(IReadOnlyList<MessageRecord> messages, (int Start, int End) span) =>
        [.. messages.Take(span.Start), .. messages.Skip(span.End)];

    /// <summary>
    /// 编辑一条提问后的转录;<paramref name="truncate"/> = 从这条起重跑(这条和之后的全部不要了,新提问由调用方另起一次 Run)。
    /// 找不到这条 user 消息返回 null。★ 改写后的消息按 2.x 的 <c>userMessage()</c> 重新构造:<c>internal</c> 标记不保留。
    /// </summary>
    public static List<MessageRecord>? EditUserMessage(IReadOnlyList<MessageRecord> messages, string messageId, string text, bool truncate)
    {
        var index = IndexOf(messages, messageId, "user");
        if (index < 0)
        {
            return null;
        }

        if (truncate)
        {
            return [.. messages.Take(index)];
        }

        var original = messages[index];
        var edited = new MessageRecord(original.Id, "user", MessageShape.EditedParts(original.PartsJson, text), original.CreatedAt);
        return [.. messages.Select((m, i) => i == index ? edited : m)];
    }

    private static int IndexOf(IReadOnlyList<MessageRecord> messages, string id, string role)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == id && messages[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }
}
