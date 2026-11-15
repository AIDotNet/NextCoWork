using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests.Support;

/// <summary>「每个工具调用都有紧邻的结果」(02 §8 第一条)的检查:从模型的视角看一份历史。</summary>
public static class Pairing
{
    /// <summary>每个助手消息里的调用,在紧随其后、下一条助手消息之前的用户消息里都有结果;每个结果都对应前面的调用。</summary>
    public static void AssertPaired(IReadOnlyList<Message> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != Role.Assistant)
            {
                continue;
            }

            var calls = message.Parts.OfType<ToolCallPart>().Select(c => c.CallId).ToList();
            if (calls.Count == 0)
            {
                continue;
            }

            // 紧随其后必须是一条用户消息,且里面装着这些调用的结果(不能被别的消息隔开)。
            Assert.True(i + 1 < messages.Count, $"calls {string.Join(",", calls)} have no following message");
            var next = messages[i + 1];
            Assert.Equal(Role.User, next.Role);
            var results = next.Parts.OfType<ToolResultPart>().Select(r => r.CallId).ToList();
            Assert.Equal(calls.OrderBy(c => c), results.OrderBy(c => c));
        }

        Assert.Empty(Orphans.Find(messages));
    }
}
