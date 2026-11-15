using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 孤儿工具调用:有调用、没有结果。「每个工具调用都必须紧跟它的结果」是最重要的不变量(02 §7)——
/// 漏掉一个,下一次请求就是 400(Anthropic 要求 tool_result 紧邻,报错指向消息数组,看起来像编码器的 bug)。
/// </summary>
public static class Orphans
{
    /// <summary>全部已出现但没有任何结果的调用,按出现顺序。</summary>
    public static IReadOnlyList<ToolCallPart> Find(IEnumerable<Message> messages)
    {
        var opened = new List<ToolCallPart>();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ToolCallPart call:
                        opened.Add(call);
                        break;
                    case ToolResultPart result:
                        closed.Add(result.CallId);
                        break;
                }
            }
        }

        return opened.Where(c => !closed.Contains(c.CallId)).ToList();
    }
}
