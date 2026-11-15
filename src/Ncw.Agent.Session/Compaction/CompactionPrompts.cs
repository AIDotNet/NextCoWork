using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// 压缩提示词与摘要的展平(取自 pi 的结构化摘要,R11)。<b>都是发给模型的英文,不是界面文字</b>(不翻译、不按界面语言切换;摘要用什么语言写由对话本身决定)。
/// <para>
/// 与 2.x 的差别(见 parity.md):2.x 把原始对话原样发过去 + 末尾一条九节压缩指令;3.0 把对话<b>展平成文本</b>
/// (<c>[User]: … [Assistant tool calls]: read(path=…)</c>)放进一条用户消息里,避免模型「接着对话往下说」或照着历史去调工具,
/// 摘要模板换成 pi 的:目标 / 约束 / 进展(已完成、进行中、受阻)/ 关键决定 / 下一步 / 关键上下文,再加累积的 <c>&lt;read-files&gt;</c> / <c>&lt;modified-files&gt;</c> 清单,
/// 以及<b>迭代摘要</b>(上次的摘要喂进去,更新而不是重写)。
/// </para>
/// </summary>
public static partial class CompactionPrompts
{
    public const string SummarySystem =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI coding assistant, "
        + "then produce a structured summary following the exact format specified. "
        + "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. "
        + "Do NOT call any tools. ONLY output the structured summary.";

    private const string Format = """
        ## Goal
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by the user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]

        Keep each section concise. Preserve exact file paths, function names, and error messages. Write the summary in the same language the user has been using in the conversation.
        """;

    public const string SummarizePrompt =
        "The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.\n\n"
        + "Use this EXACT format:\n\n" + Format;

    public const string UpdatePrompt =
        "The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.\n\n"
        + "Update the existing structured summary with new information. RULES:\n"
        + "- PRESERVE all existing information from the previous summary\n"
        + "- ADD new progress, decisions, and context from the new messages\n"
        + "- UPDATE the Progress section: move items from \"In Progress\" to \"Done\" when completed\n"
        + "- UPDATE \"Next Steps\" based on what was accomplished\n"
        + "- PRESERVE exact file paths, function names, and error messages\n"
        + "- If something is no longer relevant, you may remove it\n\n"
        + "Use this EXACT format:\n\n" + Format;

    /// <summary>工具结果展平时的字符上限(pi 的 2000)。</summary>
    public const int ToolResultMaxChars = 2000;

    public static string Prompt(bool iterative, string? instructions)
    {
        var extra = string.IsNullOrWhiteSpace(instructions) ? "" : $"\n\nAdditional focus: {instructions.Trim()}";
        return (iterative ? UpdatePrompt : SummarizePrompt) + extra;
    }

    /// <summary>摘要请求的唯一一条用户消息的正文。</summary>
    public static string RequestText(string conversation, string? previousSummary, string? instructions)
    {
        var text = new StringBuilder();
        text.Append("<conversation>\n").Append(conversation).Append("\n</conversation>\n\n");
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            text.Append("<previous-summary>\n").Append(previousSummary).Append("\n</previous-summary>\n\n");
        }

        text.Append(Prompt(!string.IsNullOrWhiteSpace(previousSummary), instructions));
        return text.ToString();
    }

    /// <summary>
    /// 把对话展平成文本(pi 的 <c>serializeConversation</c>):<c>[User]: …</c>、<c>[Assistant]: …</c>、<c>[Assistant tool calls]: read(path="/a"); …</c>、
    /// <c>[Tool result]: …</c>(超过 2000 字符截断并说明)。思考块和界面专用块不展平;图片是占位。
    /// </summary>
    public static string Serialize(IEnumerable<Message> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages)
        {
            var text = new List<string>();
            var calls = new List<string>();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart t when t.Text.Length > 0:
                        text.Add(t.Text);
                        break;
                    case ImagePart:
                        text.Add("[image]");
                        break;
                    case ToolCallPart c:
                        calls.Add($"{c.Name}({FormatArguments(c)})");
                        break;
                    case ToolResultPart r:
                        lines.Add($"[Tool result]: {Truncate(r.Output.Content, ToolResultMaxChars)}");
                        break;
                }
            }

            var label = message.Role == Role.User ? "[User]" : "[Assistant]";
            if (text.Count > 0)
            {
                lines.Add($"{label}: {string.Join("\n", text)}");
            }

            if (calls.Count > 0)
            {
                lines.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
            }
        }

        return string.Join("\n\n", lines);
    }

    private static string FormatArguments(ToolCallPart call)
    {
        if (call.Input.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return "";
        }

        return string.Join(", ", call.Input.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    public static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars
            ? text
            : $"{PromptText.Clamp(text, maxChars, "")}\n\n[... {text.Length - maxChars} more characters truncated]";

    /// <summary>模型原始输出 → 进上下文的摘要正文:去掉(可能出现的)<c>&lt;analysis&gt;</c> 草稿和 <c>&lt;summary&gt;</c> 包装,连续空行压成一个,trim。</summary>
    public static string FormatSummary(string raw)
    {
        var text = Analysis().Replace(raw, "");
        text = UnclosedAnalysis().Replace(text, ""); // 输出被截断时 analysis 可能没有闭合标签
        if (Summary().Match(text) is { Success: true } match)
        {
            text = match.Groups[1].Value;
        }

        return BlankRuns().Replace(text, "\n\n").Trim();
    }

    /// <summary>
    /// 边界消息里给模型看的那段续接语 + 摘要。<paramref name="autoContinue"/> 只在<b>自动</b>压缩时为真:那时 Run 正在跑,压完要立刻接着干,
    /// 必须告诉模型「别停下来问用户」,否则它会礼貌地复述一遍摘要然后问「要继续吗」,一个长任务就此停住(Claude Code 的 <c>suppressFollowUpQuestions</c>)。
    /// </summary>
    public static string Continuation(string summary, bool autoContinue)
    {
        const string head =
            "This session is being continued from a previous conversation that ran out of context. "
            + "The summary below covers the earlier portion of the conversation.\n\n";
        var tail = autoContinue
            ? "\n\nPlease continue the conversation from where we left it off without asking the user any further questions. "
              + "Continue with the last task that you were asked to work on."
            : "";
        return head + summary + tail;
    }

    [GeneratedRegex(@"<analysis>[\s\S]*?</analysis>", RegexOptions.IgnoreCase)]
    private static partial Regex Analysis();

    [GeneratedRegex(@"<analysis>[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnclosedAnalysis();

    [GeneratedRegex(@"<summary>([\s\S]*?)(?:</summary>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Summary();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();
}
