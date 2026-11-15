using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Session;

/// <summary>run 开始时的 git 快照(<see cref="GitContextReader"/>)。分支名和提交标题在 clone 来的仓库里是攻击者可控的,读出时已经消毒。</summary>
/// <param name="Branch">空串 = detached HEAD 或还没有提交,都当「没有分支」。</param>
public sealed record GitContext(string Branch, int DirtyCount, IReadOnlyList<string> Recent);

/// <summary>
/// 注入进「发出去的那份消息」的内容(转录一个字都不动)。
/// 头块(静态,保住缓存前缀):项目指令 + 扩展给的头部上下文;尾块(每个 Run 算一次):git 状态 + 扩展给的状态段(待办、计划文件……)。
/// </summary>
public sealed record ReminderContext
{
    /// <summary>已拼接、已消毒的项目指令(<see cref="ProjectInstructions"/> 的产出)。空 = 没有。</summary>
    public string? ProjectInstructions { get; init; }

    public IReadOnlyList<string> HeadSections { get; init; } = [];

    public GitContext? Git { get; init; }

    public IReadOnlyList<string> StateSections { get; init; } = [];

    /// <summary>
    /// 尾块挂在哪条消息上:<b>本次 Run 的输入消息</b>(设计 04 §2)。缺省(或找不到)= 从后往前第一条不是纯工具结果的用户消息(2.x)。
    /// ★ 绝不能挂在「最后一条消息」上:没有工具调用时数组末尾永远是工具结果消息,挂在它上面第 K 轮带尾块、第 K+1 轮不带,前缀就在整个数组里最大的那条上断掉。
    /// </summary>
    public MessageId? TailAnchor { get; init; }
}

/// <summary>上下文被谁占掉了。<b>各档之和恒等于 <see cref="ContextUsage.Used"/></b>(界面上百分比加起来是 98% 看着像四舍五入,没人会去查)。</summary>
public sealed record ContextSegment(string Kind, int Tokens);

public static class ContextSegmentKinds
{
    public const string System = "system";
    public const string Tools = "tools";
    public const string Instructions = "instructions";
    public const string Messages = "messages";
}

public sealed record ContextUsage(int Used, int Window, bool ShouldCompact, IReadOnlyList<ContextSegment> Segments);

/// <summary>每回合请求前发(04 §2)。压力条要在这一轮真的挤爆之前就让用户看见。</summary>
public sealed record ContextUsageEvent(int Used, int Window, bool ShouldCompact, IReadOnlyList<ContextSegment> Segments) : AgentEvent;

public enum ContextStatusPhase
{
    Compacting,
    Compacted,
    Failed,

    /// <summary>熔断:同一个 Run 里连续失败 3 次后不再自动重试(此时唯一有用的动作在用户那边:手动压缩、换模型、另起会话)。</summary>
    Disabled,
}

public sealed record ContextStatusEvent(ContextStatusPhase Phase, string Trigger) : AgentEvent;

public sealed record AssembleInput(
    IReadOnlyList<Message> Messages,
    ToolSnapshot Tools,
    IReadOnlyList<SystemPart> SystemParts,
    GenerationOptions Options,
    ReminderContext? Reminder,
    int ContextWindow,
    long? KnownInputTokens = null);

public sealed record Assembled(
    LlmRequest Request,
    ContextUsage Usage,
    /// <summary>压缩判据和硬校验读的<b>同一个数</b> = <c>max(估算, 上游最近一次真值 + 其后新增部分的估算)</c>。</summary>
    long InputTokens);

/// <summary>
/// 请求组装(04 §2,2.x <c>context-assembler.ts</c>):<b>纯函数</b>——时钟、工作区根、git 信息全部作为参数传入,输出 <see cref="LlmRequest"/> 和 token 估算。
/// <para>
/// ★ 位置是被 prompt cache 决定的,不是随手放的:前缀缓存按 <c>system → tools → messages</c> 顺序逐字节匹配,这里改的是 messages。
/// 所以<b>静态的放最前</b>(项目指令前置到第一条用户消息;一个工作区内不变,而且它长),<b>易变的放最后</b>(状态块追加到本 Run 的输入消息,整个 Run 里它一字不变,断点只落在 Run 的第一回合)。
/// 提示词缓存的断点本身由模型层编码器放(顶层 <c>cache_control</c>,断点在 system 或最后一个工具,01-llm §3),这一层的职责是<b>保证前缀稳定</b>。
/// </para>
/// <para>
/// ★ 只装饰发出去的<b>副本</b>:传进来的消息对象就是要落盘的那些,就地改 parts = reminder 被写进转录,而且下一轮再加一份,滚雪球直到爆窗口。
/// ★ 找不到用户消息时原样返回,绝不合成一条消息:那种请求本来就是必然的 400,合成一条会把「一眼看得出的 400」变成「模型收到一堆项目规矩却没有任务」。
/// </para>
/// </summary>
public static class ContextAssembler
{
    private const string ReminderOpen = "<system-reminder>";
    private const string ReminderClose = "</system-reminder>";

    /// <summary>单条状态段文字的上限:待办是模型自己写的,但仍然要限长,一条 5000 字的待办能顶掉整个尾块。</summary>
    public const int StateSectionMax = 4000;

    public static Assembled Assemble(AssembleInput input)
    {
        var system = SystemPrompt.Join(input.SystemParts);
        var messages = input.Reminder is null ? input.Messages : Decorate(input.Messages, input.Reminder);
        var request = new LlmRequest(system, messages, input.Tools.ModelTools, input.Options);

        // ★ 从装饰后的数组算,不是原数组:照旧读原数组的话,上下文占用每轮都会少算掉整个项目指令 + 状态块,压缩跟着一起迟到。
        var used = TokenEstimator.Text(system) + TokenEstimator.Messages(messages) + TokenEstimator.Tools(input.Tools.ModelTools);
        // ★ 取较大的那个,不是只信真值:不少中转按「未命中缓存的那部分」报 input_tokens,真值只有真实提示词的零头;
        // 反过来估算偏低(chars/4 对代码 / JSON / CJK 偏低 25%+)时真值兜住。两个方向各有一个数是对的。
        var inputTokens = Math.Max(used, input.KnownInputTokens ?? 0);
        var usage = new ContextUsage(
            used,
            input.ContextWindow,
            ContextWindows.ShouldCompact(inputTokens, input.ContextWindow, input.Options.MaxOutputTokens),
            Segments(input.SystemParts, system, messages, input.Tools, used));
        return new Assembled(request, usage, inputTokens);
    }

    /// <summary>
    /// 把 <c>used</c> 拆成「被谁占掉了」。分段之和不等于整串的估算(<c>join</c> 的 <c>\n\n</c> 和每段各自向上取整的累积),
    /// 差额一律记到 system 头上:它是基数最大、最不随用户配置变的那一档。
    /// </summary>
    private static List<ContextSegment> Segments(IReadOnlyList<SystemPart> parts, string system, IReadOnlyList<Message> messages, ToolSnapshot tools, int used)
    {
        var bySection = new Dictionary<string, int>
        {
            [SystemPromptBuckets.System] = 0,
            [SystemPromptBuckets.Instructions] = 0,
            [SystemPromptBuckets.Tools] = 0,
        };
        foreach (var part in parts)
        {
            if (part.Text.Length > 0)
            {
                bySection[part.Bucket] += TokenEstimator.Text(part.Text);
            }
        }

        bySection[SystemPromptBuckets.System] += TokenEstimator.Text(system) - bySection.Values.Sum();

        // 注入的 reminder 块算 instructions,不算 messages(判据是那对标签;误判只在两桶之间,两桶之和不变)
        var own = 0;
        var injected = 0;
        foreach (var message in messages)
        {
            own += TokenEstimator.MessageOverheadTokens;
            foreach (var part in message.Parts)
            {
                var tokens = TokenEstimator.Part(part);
                if (part is TextPart text && text.Text.StartsWith(ReminderOpen, StringComparison.Ordinal))
                {
                    injected += tokens;
                }
                else
                {
                    own += tokens;
                }
            }
        }

        var segments = new List<ContextSegment>
        {
            new(ContextSegmentKinds.System, bySection[SystemPromptBuckets.System]),
            new(ContextSegmentKinds.Tools, bySection[SystemPromptBuckets.Tools] + TokenEstimator.Tools(tools.ModelTools)),
            new(ContextSegmentKinds.Instructions, bySection[SystemPromptBuckets.Instructions] + injected),
            new(ContextSegmentKinds.Messages, own),
        };
        System.Diagnostics.Debug.Assert(segments.Sum(s => s.Tokens) == used, "segments must add up to used");
        return segments;
    }

    // ── 装饰 ──

    public static IReadOnlyList<Message> Decorate(IReadOnlyList<Message> messages, ReminderContext reminder)
    {
        var instructions = reminder.ProjectInstructions?.Trim() ?? "";
        var headSections = new List<string>();
        if (instructions.Length > 0)
        {
            headSections.Add(InstructionsBlock(instructions));
        }

        headSections.AddRange(reminder.HeadSections.Select(s => s.Trim()).Where(s => s.Length > 0));
        var head = headSections.Count == 0 ? null : ReminderPart(string.Join("\n\n", headSections));
        var tail = StateBlock(reminder);
        if (head is null && tail is null)
        {
            return messages;
        }

        // ★ 两处定位都不能写成 messages[0] / messages[^1]。
        // 头:当下这个数组里第一条 user 消息——压缩之后数组头部是边界消息,规则依然成立,压缩不需要任何特殊处理。
        // 尾:本 Run 的输入消息;缺省从后往前第一条不是纯工具结果的 user 消息。
        var headIndex = IndexOf(messages, m => m.Role == Role.User);
        if (headIndex < 0)
        {
            return messages;
        }

        var tailIndex = -1;
        if (reminder.TailAnchor is { } anchor)
        {
            tailIndex = IndexOf(messages, m => m.Id == anchor && m.Role == Role.User);
        }

        if (tailIndex < 0)
        {
            for (var k = messages.Count - 1; k >= 0; k--)
            {
                if (messages[k].Role == Role.User && !IsToolResultOnly(messages[k]))
                {
                    tailIndex = k;
                    break;
                }
            }
        }

        var decorated = new List<Message>(messages.Count);
        for (var k = 0; k < messages.Count; k++)
        {
            var message = messages[k];
            var addHead = k == headIndex && head is not null;
            var addTail = k == tailIndex && tail is not null;
            if (!addHead && !addTail)
            {
                decorated.Add(message);
                continue;
            }

            var parts = addHead ? InsertHead(message.Parts, head!) : [.. message.Parts];
            if (addTail)
            {
                parts.Add(tail!);
            }

            decorated.Add(message with { Parts = parts });
        }

        return decorated;
    }

    /// <summary>★ Anthropic 要求 tool_result 块位于 user 消息开头。插在前导的那串 tool_result 之后,无论如何都是合法的(违反它的症状是 400,不是编译错误)。</summary>
    private static List<Part> InsertHead(IReadOnlyList<Part> parts, Part head)
    {
        var i = 0;
        while (i < parts.Count && parts[i] is ToolResultPart)
        {
            i++;
        }

        return [.. parts.Take(i), head, .. parts.Skip(i)];
    }

    private static int IndexOf(IReadOnlyList<Message> messages, Func<Message, bool> predicate)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (predicate(messages[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>这条 user 消息其实是工具回执,不是用户说的话。</summary>
    public static bool IsToolResultOnly(Message message) =>
        message.Role == Role.User && message.Parts.Count > 0 && message.Parts.All(p => p is ToolResultPart);

    private static TextPart ReminderPart(string body) => new($"{ReminderOpen}\n{body}\n{ReminderClose}");

    private static string InstructionsBlock(string text) =>
        "Project instructions for this workspace, loaded from the AGENTS.md / CLAUDE.md files found for it. Follow them for the whole conversation.\n"
        + "The user did not type this block and cannot see it: do not reply to it, do not mention that you read it, and do not thank the user for it. "
        + "When it conflicts with what the user just asked you for, the user wins.\n\n"
        + $"<project-instructions>\n{text}\n</project-instructions>\n\n"
        + PromptText.UntrustedBoundary("The project instructions above");

    private static TextPart? StateBlock(ReminderContext reminder)
    {
        var sections = new List<string>();
        if (reminder.Git is { } git)
        {
            sections.Add(GitSection(git));
        }

        foreach (var section in reminder.StateSections)
        {
            var clean = PromptText.Clamp(PromptText.NeutralizeReminderTags(PromptText.StripControlChars(section)).Trim(), StateSectionMax);
            if (clean.Length > 0)
            {
                sections.Add(clean);
            }
        }

        if (sections.Count == 0)
        {
            return null;
        }

        // ★ 状态块是每个 Run 算一次的快照,所以文案里逐字写着 "as of the start of this run":含糊地写成「current branch」而它其实是五分钟之前的,比不写更糟。
        return ReminderPart(
            "Workspace state, as of the start of this run. It is here so you do not have to run a command to find it.\n"
            + "The user did not type this block and cannot see it: do not reply to it and do not mention it.\n"
            + "It is a snapshot — anything that changed while this run was already going is NOT reflected here.\n\n"
            + string.Join("\n\n", sections));
    }

    private static string GitSection(GitContext git)
    {
        var where = git.Branch.Length == 0 ? "no branch (detached HEAD, or no commits yet)" : $"branch {git.Branch}";
        var dirty = git.DirtyCount == 0 ? "working tree clean" : $"{git.DirtyCount} file(s) with uncommitted changes";
        var recent = git.Recent.Count == 0 ? "" : "\nRecent commits:\n" + string.Join("\n", git.Recent.Select(l => $"- {l}"));
        return $"Git: {where} · {dirty}{recent}";
    }
}
