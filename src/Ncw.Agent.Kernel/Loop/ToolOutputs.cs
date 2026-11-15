using System.Text;
using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>工具结果 → 记录里的 <see cref="ToolOutput"/>:拼接、截断、校验 AfterTool 的返回值。</summary>
internal static class ToolOutputs
{
    /// <summary>文本块用换行拼成一段,图片进 Images。</summary>
    public static ToolOutput ToOutput(ToolResult result)
    {
        var text = new StringBuilder();
        List<ToolOutputImage>? images = null;
        foreach (var part in result.Content)
        {
            switch (part)
            {
                case TextPart t:
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(t.Text);
                    break;
                case ImagePart image:
                    (images ??= []).Add(new ToolOutputImage(image.Mime, image.DataRef));
                    break;
            }
        }

        return new ToolOutput(text.ToString(), images, result.Truncated, result.OriginalBytes, result.Details);
    }

    /// <summary>
    /// 文本超过 <paramref name="maxChars"/> 个字符就在产出侧截断并标记(2.x <c>truncateToolOutput</c>)。
    /// 没超限时<b>原样返回</b>,保留工具自己标好的 <c>Truncated / OriginalBytes</c>(工具在更靠近数据的地方截断,标记更准)。
    /// <c>OriginalBytes</c> 沿用 2.x 的口径:原文的字符数(2.x 的字段名叫 originalBytes,值是 <c>content.length</c>)。
    /// </summary>
    public static ToolResult Truncate(ToolResult result, int maxChars)
    {
        var text = ToOutput(result).Content;
        if (text.Length <= maxChars)
        {
            return result;
        }

        var keep = maxChars;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--; // 不把一个代理对从中间切开
        }

        List<Part> content = [new TextPart(text[..keep] + LoopMessages.TruncationNote(text.Length, keep))];
        content.AddRange(result.Content.OfType<ImagePart>());
        return result with { Content = content, Truncated = true, OriginalBytes = text.Length };
    }

    /// <summary>
    /// AfterTool 的返回值只允许两种改动:在原内容后面追加块、把 IsError 从 false 翻成 true。
    /// 其它(改写已有内容、改 Details、改 Terminate、把错误翻成成功)一律忽略。
    /// </summary>
    public static ToolResult GuardAfterTool(ToolResult original, ToolResult returned)
    {
        var content = original.Content;
        if (returned.Content.Count > original.Content.Count && original.Content.SequenceEqual(returned.Content.Take(original.Content.Count)))
        {
            content = returned.Content;
        }

        return original with { Content = content, IsError = original.IsError || returned.IsError };
    }
}
