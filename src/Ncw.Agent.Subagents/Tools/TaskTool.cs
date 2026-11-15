using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>给模型的错误 / 回执文字里要用的 <c>MessageKey</c>(控制端翻译;文字本身是英文)。</summary>
public static class SubagentErrorKeys
{
    /// <summary>子代理跑完了,但一个字都没说。参数:<c>agent</c>。</summary>
    public const string NoOutput = "subagent.error.noOutput";
}

/// <summary>
/// <c>Task</c> —— 派一个子代理去独立完成一件事(2.x <c>kernel/tool/builtin/task.ts</c>)。
/// <para>
/// 它为什么值得存在:子代理拿到的是一个<b>全新的上下文窗</b>,父代理那几十轮对话、几万字符的文件内容,一个字都不会带过去,回来的也只有一段结论。
/// 「在这个仓库里找出所有调用了 X 的地方」这种要翻二十个文件的活,可以在<b>不污染主对话</b>的前提下做完。
/// </para>
/// <para>
/// 描述是按<b>当前清单</b>拼的,不是写死的常量:可用的 <c>subagent_type</c> 清单逐字写在 description 里(照搬 CC),模型判断「这活派给谁」唯一的依据就是那几行。
/// 清单变了(<see cref="AgentRegistry.Replace"/>)<see cref="Info"/> 就重拼,清单没变就是同一个对象(提示词缓存稳定)。每次现拼、不缓存到清单之外:缓存就意味着「用户新加的 agent 文件要重启才生效」,而没有任何地方会提示他。
/// </para>
/// 本工具不碰存储、不碰 Run 注册表,全部经 <see cref="ISubagentLauncher"/> 这条窄缝。
/// </summary>
public sealed class TaskTool : IAgentTool
{
    public const string Name = "Task";

    private static readonly JsonElement Schema = S.Build(S.Object(
        ["description", "prompt", "subagent_type"],
        ("description", S.String("A 3-5 word description of the task, shown on the card in the UI, e.g. \"Find config readers\"", minLength: 1, maxLength: 200)),
        ("prompt", S.String(
            "The full task for the subagent. IMPORTANT: the subagent CANNOT SEE THIS CONVERSATION, so every piece of context it needs — and exactly what you want back — must be written here",
            minLength: 1, maxLength: 50_000)),
        ("subagent_type", S.String("The name of the subagent to use, chosen from the list in this tool's description", minLength: 1, maxLength: 64)),
        ("run_in_background", S.Boolean("If true, start the subagent and continue this conversation without waiting for its report"))));

    private readonly ISubagentLauncher? _launcher;
    private readonly Func<IReadOnlyList<AgentDefinition>> _agents;
    private readonly Lock _gate = new();
    private IReadOnlyList<AgentDefinition>? _describedFor;
    private ToolSpec? _info;

    /// <param name="launcher">null = 这个环境派不了子代理(纯内核):调用时给一句人话而不是崩溃。</param>
    /// <param name="agents">当前清单(每次问 <see cref="Info"/> 时现取;返回同一个列表对象 = 内容没变)。</param>
    public TaskTool(ISubagentLauncher? launcher, Func<IReadOnlyList<AgentDefinition>> agents)
    {
        _launcher = launcher;
        _agents = agents;
    }

    public TaskTool(ISubagentLauncher? launcher, IReadOnlyList<AgentDefinition> agents)
        : this(launcher, () => agents)
    {
    }

    /// <summary>
    /// ★ <c>ReadOnly: false</c> 白送一道闸门:计划模式的只读快照会直接把这个工具摘掉——计划模式下没法借子代理绕开只读围栏,不用写一行代码。
    /// 标成只读是<b>错</b>的,哪怕子代理自己只用了读工具:它跑的是一整条 agent 循环,能用哪些工具由它的定义决定,而不是由这个字段决定。
    /// <c>Destructive: false</c>:派一个子代理这个动作本身不破坏任何东西,真正的破坏性操作由子代理里的那个工具自己去过权限闸门——子代理里的每一次工具调用都会再走一遍权限判定。
    /// 联网由子代理里那些工具各自声明。
    /// </summary>
    public ToolSpec Info
    {
        get
        {
            var agents = _agents();
            lock (_gate)
            {
                if (_info is null || !ReferenceEquals(_describedFor, agents))
                {
                    _info = new ToolSpec(Name, BuildDescription(agents), Schema, new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: false));
                    _describedFor = agents;
                }

                return _info;
            }
        }
    }

    public ToolArgumentCheck PrepareArguments(JsonElement input)
    {
        var result = Ncw.Agent.Tools.Schema.JsonSchemaValidator.Validate(Schema, input);
        return result.IsValid ? ToolArgumentCheck.Valid(result.Value) : ToolArgumentCheck.Invalid($"Invalid arguments — {result.Error}");
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        var args = check.Input;
        var type = args.Str("subagent_type") ?? "";

        // ★ 深度闸门放在第一行,而且这条消息要说清「为什么」和「那你该做什么」。只说「达到上限」的话,模型会换一个 subagent_type 再试一次——它会以为是那个子代理的问题。
        if (ctx.Depth >= SubagentLimits.MaxDepth)
        {
            return ToolResult.Error(
                $"The subagent nesting limit of {SubagentLimits.MaxDepth} levels has been reached. "
                + "Do this step yourself with Read / Glob / Grep and the other tools — do NOT launch another subagent.");
        }

        if (_launcher is null)
        {
            return ToolResult.Error("Subagents cannot be launched in this environment. Do this step yourself.");
        }

        var outcome = await _launcher.SpawnAsync(
            new SubagentRequest(ctx.RunId, ctx.CallId, type, args.Str("prompt") ?? "", args.Str("description") ?? "", args.Bool("run_in_background") == true),
            ct).ConfigureAwait(false);

        switch (outcome)
        {
            // ★ 「还没派出去就被拒」原样转交,不加任何前缀:启动器给的 reason 已经是一段完整的人话,再包一层「子代理 X 失败:」会让模型以为那个子代理存在但坏掉了,然后原样重试一次。
            case SubagentOutcome.Refused refused:
                return ToolResult.Error(refused.Reason);

            case SubagentOutcome.Background background:
                return BackgroundResult(background);

            case SubagentOutcome.Finished finished:
                return FinishedResult(finished, type, ct);

            default:
                return ToolResult.Error($"The subagent {type} ended in an unexpected state.");
        }
    }

    private static ToolResult BackgroundResult(SubagentOutcome.Background outcome)
    {
        // ★「已排队」要说成排队,不能说成「已启动」:并发满的时候这次派发是真的还没开始跑。谎报成已启动的话,模型会在几秒后就去等它的产出,而它可能还要等上几分钟。
        var text = outcome.Detached
            ? $"The user moved this subagent to the background while it was still running (run {outcome.ChildRunId}). "
              + "It keeps working on its own and its final report will be delivered to you when it finishes — "
              + "do NOT launch it again. Continue with the parent task."
            : (outcome.Queued
                ? $"The subagent is queued behind the running ones (run {outcome.ChildRunId}); it starts automatically as soon as a concurrency slot frees up. "
                : $"The subagent started in the background (run {outcome.ChildRunId}). ")
              + "Continue with the parent task; its progress and final report remain available in the task panel.";
        return ToolResult.Text(text) with
        {
            Subagent = new SubagentReceipt(outcome.ChildRunId.Value, "running", Background: true, Color: outcome.Color),
        };
    }

    private static ToolResult FinishedResult(SubagentOutcome.Finished outcome, string type, CancellationToken ct)
    {
        var childRun = outcome.ChildRunId.Value;
        switch (outcome.Status)
        {
            case RunStatus.Done:
                // ★「跑完了但一个字都没说」要当失败报,不能返回一个空的成功结果:空结果被当成成功的话,父代理会认为「查过了,没有」并据此往下做——而真实情况是这次调查根本没发生。
                if (string.IsNullOrWhiteSpace(outcome.Text))
                {
                    var error = AgentError.Create(
                        AgentErrorCode.Unknown,
                        $"The subagent {type} finished without producing any text. Retry once with a more specific prompt, or do this step yourself.",
                        messageKey: SubagentErrorKeys.NoOutput,
                        messageParams: new Dictionary<string, string> { ["agent"] = type });
                    return ToolResult.Error(error.Message) with
                    {
                        Subagent = new SubagentReceipt(childRun, "error", Color: outcome.Color, Error: error),
                    };
                }

                return ToolResult.Text(outcome.Text) with
                {
                    Subagent = new SubagentReceipt(childRun, "done", Preview(outcome.Text), Color: outcome.Color),
                };

            case RunStatus.Error:
            {
                // ★ 网络故障要明说它是瞬时的:子 Run 走到这里时,会话层已经续跑过几次了——这里是最后一道防线,而父代理是唯一还能做点什么的人。
                // 只甩一句「failed: 连接失败」的话,模型读不出「这跟我的提示词无关、原样再派一次就可能成」。
                var error = outcome.Error ?? AgentError.Create(AgentErrorCode.Unknown, "unknown error");
                var transient = error.Code == AgentErrorCode.Network
                    ? " This was a transient network failure and the subagent already retried on its own; dispatching the same task again may well succeed."
                    : "";
                return ToolResult.Error($"The subagent {type} failed: {error.Message}{transient}") with
                {
                    Subagent = new SubagentReceipt(childRun, "error", Color: outcome.Color, Error: error),
                };
            }

            default:
                // ★ 父被中止时会级联中止子,此时父自己也正在中止收尾:原样抛出中止,不包成工具失败——返回一个「正常的失败结果」会和中止收尾的孤儿 tool_result 补齐打架,
                // 同一个 callId 出现两条结果,下一轮上行就是 400。
                // 只有子代理被单独停掉(用户在卡片上点了停止,父 Run 并没有被中止)时才走下面那一支:父 Run 还活着,要给模型一个清楚的结果。
                ct.ThrowIfCancellationRequested();
                return ToolResult.Error(
                    $"The subagent {type} was stopped by the user before it finished. Do not launch it again unless the user asks; carry on without its result or ask the user how to proceed.") with
                {
                    Subagent = new SubagentReceipt(childRun, "aborted", Color: outcome.Color),
                };
        }
    }

    /// <summary>卡片上那截预览(前 240 字,不切开代理对)。</summary>
    internal static string Preview(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= SubagentLimits.SummaryChars)
        {
            return trimmed;
        }

        var keep = SubagentLimits.SummaryChars;
        if (char.IsHighSurrogate(trimmed[keep - 1]))
        {
            keep--;
        }

        return trimmed[..keep];
    }

    /// <summary>清单里每个子代理占的那一行。<c>tools</c> 省略时照 CC 写成 <c>*</c>。</summary>
    private static string Describe(AgentDefinition agent) =>
        $"- {agent.Name}: {agent.Description} (Tools: {(agent.Tools is null ? "*" : string.Join(", ", agent.Tools))})";

    internal static string BuildDescription(IReadOnlyList<AgentDefinition> agents)
    {
        var catalog = agents.Count == 0
            ? "(No subagents are available — do not call this tool)"
            : string.Join('\n', agents.Select(Describe));
        var sb = new StringBuilder();
        sb.Append("Launches a subagent to handle a multi-step task independently.\n\n")
            .Append("Available subagent types, and the tools each one may use:\n")
            .Append(catalog).Append("\n\n")
            .Append("You MUST name the one you want with `subagent_type`, copied verbatim from the list above.\n\n")
            .Append("When to use this tool:\n")
            .Append("- You are searching the codebase for a keyword or a file and are not confident you will find the ")
            .Append("right match in the first few tries — hand it over instead of reading twenty search hits into your ")
            .Append("own context\n")
            .Append("- Reaching a conclusion means opening many files, and you only need the conclusion\n")
            .Append("- One subagent description CLEARLY matches the task\n\n")
            .Append("When NOT to use this tool:\n")
            .Append("- You already know which file to read — use `Read` directly, a subagent is only slower\n")
            .Append("- You are searching for a specific class or function name — use `Glob` / `Grep` directly\n")
            .Append("- The task takes two or three steps — do it yourself\n")
            .Append("- The task needs back-and-forth with the user: IMPORTANT, A SUBAGENT CANNOT ASK THE USER ANYTHING. ")
            .Append("It gets no answer, so it will guess one and carry on\n\n")
            .Append("Things you must know before using it:\n")
            .Append("1. IMPORTANT: THE SUBAGENT CANNOT SEE THIS CONVERSATION. All it sees is what you write in `prompt`. ")
            .Append("Put the background, the constraints, and what you want returned into it — whatever you leave out, ")
            .Append("it will invent\n")
            .Append("2. IMPORTANT: IT SENDS YOU EXACTLY ONE MESSAGE and then it is finished. You cannot ask a follow-up ")
            .Append("or add anything: either accept the result, or launch it again with a clearer `prompt`\n")
            .Append("3. EVERY CALL IS STATELESS. Nothing is shared between subagents, or between two calls to the same one\n")
            .Append("4. Say explicitly WHAT YOU WANT: research and report back, or actually change the code. If you do ")
            .Append("not say, a subagent with write access may well start editing\n")
            .Append("5. CHECK ITS OUTPUT before you act on it. Subagents make mistakes like you do, and all you get is the ")
            .Append("conclusion, never the reasoning behind it\n")
            .Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"6. Nesting is capped at {SubagentLimits.MaxDepth} levels — a subagent launching a subagent is refused\n"))
            .Append("7. Set run_in_background=true only when the parent conversation can continue without the result. ")
            .Append("Background runs remain observable and can be stopped from the task panel.");
        return sb.ToString();
    }
}
