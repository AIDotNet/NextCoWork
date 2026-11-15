using System.Text.Json;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// <c>ProposeGoal</c>——模型自己提一个完成条件,由一个独立判定器确认达成(2.x <c>builtin/goal.ts</c>)。
/// ★ <b>这个工具的危险之处在于它改变的是「什么时候可以停」</b>,而不是它写了什么。所以「这次调用到底能不能成立」必须在<b>调它之前</b>就判死:
/// 一次注定被拒的调用白花一轮,而模型从错误里学到的东西比什么都糟。<see cref="IsEnabled"/> 就是那道判定(快照消费),<c>ExecuteAsync</c> 进门再核一遍同一份——快照取在这一轮开头,
/// 而设置、计划模式、待批状态都会在回合之中变化。
/// </summary>
public sealed class ProposeGoalTool(GoalService goals) : IAgentTool
{
    public const string Name = "ProposeGoal";

    private static readonly string Description =
        "Propose a completion condition for this session's work — a goal that keeps you working until a separate evaluator confirms it is met. Non-blocking: the proposal renders alongside your work, so keep working while it is handled.\n"
        + "ask_user true (the default) asks the user first, with a one-keypress approval dialog. If they decline you will not be notified — do not ask about the decision and do not re-propose the same or a reworded condition. Set ask_user false — which sets the goal directly, with no dialog — ONLY when the user's own words in this conversation stated this outcome as what they want; if you inferred it from their intent or the task's shape — or are in doubt — ask. Either path confirms a set goal with a kickoff message; until that message arrives, no new goal is active.\n"
        + "Propose only when the user has asked for an outcome with a verifiable end state (\"make the tests pass\", \"migrate every call site\") and the work spans multiple turns. Not for one-off tasks, and never to widen scope: the condition must follow from their request.\n"
        + $"The evaluator verifies the condition from the conversation alone — it cannot run commands or read files — so state one measurable end state with its check (e.g. \"bun test exits 0\"), in at most {GoalConditions.ProposalMax} characters. One goal is active at a time; a newly approved or directly set proposal replaces the current one.";

    /// <summary>直接设立时的回话。★ 不提「审批结果」:那条结果不会再来。</summary>
    private const string SetMessage = "The goal is set for this session. A kickoff message follows — keep working; do not stop and do not wait for anything.";

    /// <summary>
    /// 挂起等审批时的回话。★ 三句「别做」是这段的全部重量:审批结果<b>不会</b>通知模型,所以它必须在这一刻被告知「等不到、别问、别提第二次」,
    /// 否则它会在这里停住、或者过两轮把同一个条件换个说法再提一遍。
    /// </summary>
    private const string PendingMessage =
        "The proposal is with the user for a one-keypress decision. Keep working — do not wait for the decision, do not ask the user about it, and do not propose this condition again, reworded or not. No new goal is active until a kickoff message arrives.";

    private static readonly System.Text.Json.JsonElement Schema = S.Build(S.Object(
        ["condition"],
        ("condition", S.String(
            "One measurable end state with its check, e.g. \"bun test exits 0\". The evaluator verifies it from the conversation alone — it cannot run commands or read files.",
            minLength: 1, maxLength: GoalConditions.ProposalMax)),
        // 默认 true:不写这个默认值的话,模型漏传一次就等于替用户做了「不用问你」的决定
        ("ask_user", S.Boolean("Ask the user for a one-keypress approval first. Set false ONLY when the user's own words in this conversation stated this outcome as what they want."))));

    /// <summary>readOnly:不碰磁盘、不碰网络;计划模式下可用与否由第 5 道门判(不靠这一位)。<c>ConcurrencySafe = false</c>:它改共享状态。</summary>
    public ToolSpec Info { get; } = new(
        Name, Description, Schema,
        new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false, ConcurrencySafe: false),
        "Propose a verifiable completion condition for multi-turn work",
        []);

    public bool IsEnabled(ToolContext ctx) => goals.CanPropose(ctx.RunId, ctx.SessionId, ctx.Depth);

    public ToolArgumentCheck PrepareArguments(JsonElement input)
    {
        var result = JsonSchemaValidator.Validate(Info.InputSchema, input);
        return result.IsValid ? ToolArgumentCheck.Valid(result.Value) : ToolArgumentCheck.Invalid($"Invalid arguments — {result.Error}");
    }

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ValueTask.FromResult(ToolResult.Error(check.Error!));
        }

        // ★ 与快照同一份判定,再核一遍
        if (!goals.CanPropose(ctx.RunId, ctx.SessionId, ctx.Depth))
        {
            return ValueTask.FromResult(ToolResult.Error("Goal proposals are unavailable in this session. Do not retry — continue with the work."));
        }

        var condition = GoalConditions.Normalize(check.Input.Str("condition") ?? "");
        // 零宽字符撑起来的「条件」看起来写了东西,判定器收到的却是一个空条件
        if (condition.Length == 0)
        {
            return ValueTask.FromResult(ToolResult.Error(
                "The condition is empty once invisible characters are stripped. State one measurable end state the evaluator can confirm from the conversation alone."));
        }

        // ★ 超长拒绝,不截断:截断后的条件是一个用户没同意的目标
        if (condition.Length > GoalConditions.ProposalMax)
        {
            return ValueTask.FromResult(ToolResult.Error(
                $"The condition is {condition.Length} characters, over the {GoalConditions.ProposalMax}-character limit. Shorten it and propose again — it is not truncated for you."));
        }

        var askUser = check.Input.Bool("ask_user") ?? true;
        // ★ 只等派发,不等人回答
        GoalProposalStatus status;
        try
        {
            status = goals.Propose(ctx.RunId, ctx.SessionId, ctx.Depth, condition, askUser);
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(ToolResult.Error("Goal proposals are unavailable in this session. Do not retry — continue with the work."));
        }

        // ★ 审批结果不进工具结果:批没批由 kickoff 消息告诉模型。多一个字段 = 模型会开始等一个它本来就等不到的回答。
        var text = DetailsJson.Compact(w =>
        {
            w.WriteString("condition", condition);
            w.WriteBoolean("askUser", askUser);
            w.WriteString("status", status == GoalProposalStatus.Set ? "set" : "pending");
            w.WriteString("message", status == GoalProposalStatus.Set ? SetMessage : PendingMessage);
        });
        return ValueTask.FromResult(ToolResult.Text(text));
    }
}
