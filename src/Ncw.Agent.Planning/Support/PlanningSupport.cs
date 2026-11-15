using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning;

/// <summary>
/// 读一个会话当前的完整转录(从库里读;待办 / 目标都是「从记录推出」的,不另存)。工具执行时,同一个 Run 里已经提交的消息都在里面:
/// 每条消息先落库、再进内存、再发 <c>message_end</c>。生产实现 <see cref="SessionTranscriptReader"/> 读 Storage;测试可以直接给一个列表。
/// </summary>
public interface ITranscriptReader
{
    IReadOnlyList<Message> History(string sessionId);
}

public sealed class SessionTranscriptReader(SessionRepository sessions) : ITranscriptReader
{
    public IReadOnlyList<Message> History(string sessionId) => [.. sessions.History(sessionId).Select(MessageCodec.FromRecord)];
}

public sealed class FixedTranscript(Func<string, IReadOnlyList<Message>> read) : ITranscriptReader
{
    public IReadOnlyList<Message> History(string sessionId) => read(sessionId);
}

/// <summary>把 Llm 的消息压成 Core 的待办事实序列(两端各自的映射都只有这几行,判据只在 <see cref="Todos"/> 里有一份)。</summary>
public static class TodoTranscript
{
    public static IEnumerable<TodoEvent> EventsOf(IEnumerable<Message> messages)
    {
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ToolCallPart call:
                        yield return new TodoEvent.Call(call.CallId, call.Name, call.Input);
                        break;
                    case ToolResultPart result:
                        yield return new TodoEvent.Result(result.CallId, result.IsError);
                        break;
                }
            }
        }
    }
}

/// <summary>工具结果 <c>Details</c> 的手写 JSON(只给界面,永远不发给模型)。带执行环境的工具照 <c>BuiltinTool.Details</c> 的写法带上 <c>environment</c>(设备 + 环境,AGENTS.md §5)。</summary>
internal static class DetailsJson
{
    public static JsonElement Create(ToolContext ctx, string kind, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            if (ctx is ExecutionToolContext { Runtime: var runtime })
            {
                w.WriteStartObject("environment");
                w.WriteString("deviceId", runtime.EnvironmentId.DeviceId);
                w.WriteString("environment", runtime.EnvironmentId.Environment);
                w.WriteString("name", runtime.Environment.DisplayName);
                w.WriteString("type", runtime.Environment.Kind == Ncw.Agent.Environments.EnvironmentKind.Local ? "local" : "ssh");
                w.WriteEndObject();
            }

            body(w);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 发给<b>模型</b>看的结果 JSON(ProposeGoal 的结果、EnterPlanMode / ExitPlanMode 的回执)。不能用默认编码器:它把所有非 ASCII 字符和 <c>' &lt; &gt; &amp; +</c> 都写成 <c>\uXXXX</c>,
    /// 中文的目标条件 / 计划反馈会被模型逐字读成转义序列(token 成倍增加、可读性下降);2.x 的 <c>JSON.stringify</c> 只转义引号、反斜杠和控制字符,这里的宽松编码器与它一致。
    /// </summary>
    public static string Compact(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
