using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// 上下文占用的估算(2.x <c>context-assembler.ts</c> 的 <c>estimateMessages / estimateTools</c>)。文字部分用 <see cref="TokenEstimate"/>(客户端思考卡片共用的同一个函数)。
/// 每个块、每条消息、每个工具在上游都有固定的结构开销;一张图 1600;界面专用的块(<c>goal_status</c>、<c>compact_boundary</c>)编码为空、计 0。
/// 这是<b>估算</b>:触发压缩和硬性校验读的是 <c>max(估算, 上游最近一次真值 + 其后新增部分的估算)</c>。
/// </summary>
public static class TokenEstimator
{
    public const int PartOverheadTokens = 8;
    public const int MessageOverheadTokens = 4;
    public const int ToolOverheadTokens = 12;
    public const int ImageTokens = 1600;

    public static int Text(string? text) => TokenEstimate.OfText(text);

    /// <summary>上游回报的「这次请求实际送上去的提示词有多大」:缓存读写同样占着窗口,Anthropic 的 <c>input_tokens</c> 不含它们(2.x 的 <c>promptTokensOf</c>)。</summary>
    public static long PromptTokens(TokenUsage usage) =>
        usage.InputTokens + (usage.CacheReadInputTokens ?? 0) + (usage.CacheCreationInputTokens ?? 0);

    public static int Messages(IEnumerable<Message> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += MessageOverheadTokens;
            foreach (var part in message.Parts)
            {
                total += Part(part);
            }
        }

        return total;
    }

    public static int Part(Part part)
    {
        if (part is GoalStatusPart or CompactBoundaryPart)
        {
            return 0; // 只给界面的标记:不花正文、也不花块头
        }

        var body = part switch
        {
            TextPart t => Text(t.Text),
            ThinkingPart t => Text(t.Text),
            ToolCallPart c => Text(c.Name) + Text(SafeJson(c.Input)),
            ToolResultPart r => Text(r.Output.Content) + ((r.Output.Images?.Count ?? 0) * ImageTokens),
            ImagePart => ImageTokens, // dataRef 是个引用,但上游收到的是真图——按图算
            FileRefPart f => Text(f.Path),
            ErrorPart e => Text(e.Error.Message),
            _ => 0,
        };
        return body + PartOverheadTokens;
    }

    /// <summary>★ 工具定义要算进上下文:挂了几个 MCP server 的工作区,工具 schema 能占掉两三万 token。</summary>
    public static int Tools(IEnumerable<ToolInfo> tools) => tools.Sum(Tool);

    public static int Tool(ToolInfo tool) =>
        ToolOverheadTokens + Text(tool.ExternalName) + Text(tool.Description) + Text(SafeJson(tool.InputSchema));

    private static string SafeJson(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? "" : element.GetRawText();
}
