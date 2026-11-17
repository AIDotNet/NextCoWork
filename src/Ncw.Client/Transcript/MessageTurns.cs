using Ncw.Protocol.Methods;

namespace Ncw.Client.Transcript;

/// <summary>
/// 会话里「一轮」的边界(纯函数,工具条的删除回复 / 分支用):一轮 = 一条<b>可见提问</b>加上它引出的全部助手消息和工具回执,直到下一条可见提问。
/// 可见提问的判定和转录里用户行的判定是同一条:用户消息、不是内部消息、至少有文字 / 文件引用 / 图片之一(只有工具回执的「用户」消息、压缩边界、后台汇报都不算)。
/// </summary>
public static class MessageTurns
{
    /// <summary>一条消息是不是用户可见的提问。</summary>
    public static bool IsQuestion(MessageDto message) =>
        message.Role == "user" && !message.Internal
        && MessageParts.Parse(message.PartsJson).Any(p => p.Type is "text" or "file_ref" or "image");

    /// <summary>
    /// 引出 <paramref name="messageId"/> 的那条提问(它自己是提问就是它自己)的 id;这条消息之前没有任何可见提问(或消息不存在)返回 null。
    /// 「从这里分支」用它:<c>sessions.branch</c> 要的是提问 id。
    /// </summary>
    public static string? QuestionOf(IReadOnlyList<MessageDto> messages, string messageId)
    {
        var at = IndexOf(messages, messageId);
        for (var i = at; i >= 0; i--)
        {
            if (IsQuestion(messages[i]))
            {
                return messages[i].Id;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="messageId"/> 所在这一轮里,AI 回复的范围:第一条到最后一条助手消息的 id(<c>sessions.deleteReply</c> 的 <c>fromId..toId</c>,设备端会再吃掉紧随其后的工具回执)。
    /// 一次回复在 Agent 循环里常常是好几条助手消息(文字、工具调用、再文字),所以「删除回复」删的是这一轮 AI 的<b>全部</b>回复、保留提问,而不是点到的那一段。
    /// 这条消息是提问 / 不存在 / 这一轮还没有助手消息返回 null。
    /// </summary>
    public static (string From, string To)? ReplyOf(IReadOnlyList<MessageDto> messages, string messageId)
    {
        var at = IndexOf(messages, messageId);
        if (at < 0 || IsQuestion(messages[at]))
        {
            return null;
        }

        var start = at;
        while (start > 0 && !IsQuestion(messages[start - 1]))
        {
            start--;
        }

        var end = at;
        while (end + 1 < messages.Count && !IsQuestion(messages[end + 1]))
        {
            end++;
        }

        var assistants = Enumerable.Range(start, end - start + 1).Where(i => messages[i].Role == "assistant").ToList();
        return assistants.Count == 0 ? null : (messages[assistants[0]].Id, messages[assistants[^1]].Id);
    }

    private static int IndexOf(IReadOnlyList<MessageDto> messages, string id)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
