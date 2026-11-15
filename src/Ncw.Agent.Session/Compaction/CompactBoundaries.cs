using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// 压缩边界(2.x <c>shared/agent/compaction.ts</c>)——「转录里哪一段还发给模型」的<b>唯一判据</b>。
/// 压缩的产物是转录里的一条 <c>Internal</c> 用户消息,它以一个 <see cref="CompactBoundaryPart"/> 打头,后面跟着续接语 + 摘要 + 重附的文件。
/// <b>最后一个边界之前</b>的消息从此不再发给模型,之后的原样发。装配请求、手动压缩、界面画分隔线读的都是这里:
/// 三处各写一份「从哪切」,就会复现旧检查点表那种「界面说压过了、请求里却还是全量」的错位,而且零报错。
/// 故意不看消息 id、时间戳、token 数:边界就是一条消息,删轮 / 编辑重跑把它删掉,它描述的压缩自然随之失效。
/// </summary>
public static class CompactBoundaries
{
    public static CompactBoundaryPart? BoundaryOf(Message message) => message.Parts.OfType<CompactBoundaryPart>().FirstOrDefault();

    /// <summary>最后一个边界所在的下标;没有压缩过返回 -1。</summary>
    public static int LastIndex(IReadOnlyList<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (BoundaryOf(messages[i]) is not null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 发给模型的那一段:最后一个边界(含)之后的全部消息。★ 边界消息自己必须保留:摘要就住在它的文字块里;
    /// 边界块本身由编码器丢掉(只给界面)。没压缩过时返回一份拷贝,不是同一个数组引用。
    /// </summary>
    public static IReadOnlyList<Message> ForModel(IReadOnlyList<Message> messages)
    {
        var at = LastIndex(messages);
        return at < 0 ? [.. messages] : [.. messages.Skip(at)];
    }
}
