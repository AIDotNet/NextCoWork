using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Extensions.Planning.Plans;

/// <summary>
/// <c>EnterPlanMode</c>(2.x <c>builtin/plan-file.ts</c>):需求澄清完了之后进入「文件化计划」。创建<b>唯一</b>一份 Write / Edit 此后能改的 Markdown 文件(<c>.plan/&lt;id&gt;.md</c>,只经执行环境),
/// 打开写入围栏,返回路径。一个会话同时只有一份计划;它跨后面的回合一直活着,直到被批准或拒绝。
/// </summary>
public sealed class EnterPlanModeTool(PlanService plans) : BuiltinTool(
    "EnterPlanMode",
    "Enter file-backed planning after the user requirements are clear. This creates the only Markdown file "
    + "that Write and Edit may change for the rest of this planning session and returns its path. Call it exactly once; "
    + "the file stays active across later turns until the plan is approved or rejected.",
    S.Build(S.Object([], ("reason", S.String("Why clarification is complete and planning can begin", maxLength: 2000)))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: false),
    "Start file-backed planning",
    ["Call EnterPlanMode only when the important questions are answered, and only once."])
{
    // 子代理没有人可审批计划
    public override bool IsEnabled(ToolContext ctx) => ctx.Depth == 0;

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        if (plans.ActiveFor(ctx.SessionId) is { } current)
        {
            throw Fail($"Plan mode is already active for {current.Path}.");
        }

        if (rt.Root is not { } root)
        {
            throw Fail("Plan mode requires an open workspace.");
        }

        var planId = plans.Options.Ids.NewId();
        var path = PlanFiles.PathOf(planId);
        var absolute = await rt.Environment.Paths.ResolveWithinAsync(root, path, ct).ConfigureAwait(false);
        // 空文件先落盘:ExitPlanMode 据此区分「还没写」和「文件没了」。Write 要求「覆盖已有文件前读过」,所以也记一次已读
        await rt.Environment.Fs.WriteStreamAsync(absolute, new MemoryStream([], writable: false), new FsWriteStreamOptions(PlanFiles.MaxBytes, CreateParents: true), ct).ConfigureAwait(false);
        rt.Reads.MarkRead(ctx.RunId, absolute);

        var plan = new ActivePlan(planId, path, absolute, ctx.SessionId);
        if (!plans.Plans.TryBegin(plan, out var existing))
        {
            throw Fail($"Plan mode is already active for {existing!.Path}.");
        }

        plans.ApplyFence(ctx, absolute);
        plans.Refresh(ctx.RunId);
        var content = DetailsJson.Compact(w =>
        {
            w.WriteString("type", "plan_file_entered");
            w.WriteString("planId", planId);
            w.WriteString("path", path);
            w.WriteString("instruction", $"Write the complete Markdown plan to {path}, then call ExitPlanMode.");
        });
        var details = DetailsJson.Create(ctx, "planEntered", w =>
        {
            w.WriteString("planId", planId);
            w.WriteString("path", path);
        });
        return ToolResult.Text(content, details);
    }
}

/// <summary>
/// <c>ExitPlanMode</c>:读计划文件、发起 <c>plan_approval</c> 交互。回答后<b>重新读文件</b>(用户可能在审阅时编辑了保存):批准或拒绝都结束计划并 <c>Terminate</c>(Run 到此为止);
/// 要求修订则保持计划、继续这个 Run。三种「文件有问题」的情形都是工具错误、计划保持原样:空 / 是目录 / 超过 1 MiB;文件已经没了则把计划一并放掉(否则模型既写不成这一份、也重开不了新的)。
/// </summary>
public sealed class ExitPlanModeTool(PlanService plans) : BuiltinTool(
    "ExitPlanMode",
    "Read the active Markdown plan and present it to the user for review. Call only after the plan file is complete. "
    + "A revision response continues this run; approval or rejection ends it.",
    S.Build(S.Object([])),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Present the plan for review",
    ["Call ExitPlanMode exactly once per draft. If the user asks for revisions, update the same file and call it again."])
{
    public override bool IsEnabled(ToolContext ctx) => ctx.Depth == 0;

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        if (plans.ActiveFor(ctx.SessionId) is not { } active)
        {
            throw Fail("Plan mode is not active. Call EnterPlanMode first.");
        }

        var interactions = plans.Options.PermissionRuns?.Get(ctx.RunId)?.Interactions;
        if (interactions is not { CanInteract: true })
        {
            throw Fail("Plan review is not available in this environment.");
        }

        var fs = ctx.Runtime.Environment.Fs;
        FsEntryInfo stat;
        try
        {
            stat = await fs.StatAsync(active.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
        {
            // ★ 文件没了就把计划一起放掉。计划活着的时候允许列表不下发 EnterPlanMode——不清掉的话,模型既写不成这一份(路径已失效),也重开不了新的一份,只能在同一条报错上原地打转。
            Leave(ctx);
            throw Fail($"The plan file no longer exists: {active.Path}. Call EnterPlanMode to start a new plan.");
        }

        if (stat.IsDirectory)
        {
            throw Fail($"The plan path is a directory, not a Markdown file: {active.Path}");
        }

        if (stat.Size == 0)
        {
            throw Fail($"The plan file is empty: {active.Path}. Write the plan before exiting Plan mode.");
        }

        if (stat.Size > PlanFiles.MaxBytes)
        {
            throw Fail($"The plan file is too large ({stat.Size} bytes; limit {PlanFiles.MaxBytes}).");
        }

        var plan = await fs.ReadTextAsync(active.Absolute, PlanFiles.MaxBytes, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plan))
        {
            throw Fail($"The plan file contains no plan text: {active.Path}.");
        }

        InteractionResponse response;
        try
        {
            response = await interactions.RequestAsync(new PlanApproval(plan, active.PlanId, active.Path), ct).ConfigureAwait(false);
        }
        catch (InteractionRefusedException)
        {
            throw Fail("Plan review is not available in this environment.");
        }

        if (response is not PlanApprovalResponse approval)
        {
            throw Fail("Plan review returned an invalid response.");
        }

        // 审阅期间编辑器可能保存过:现在重读一遍,让批准始终绑定在一份真实的、非空的文件上,而不是最初展示的那份快照。
        FsEntryInfo latest;
        try
        {
            latest = await fs.StatAsync(active.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
        {
            throw Fail($"The plan file became invalid before review completed: {active.Path}");
        }

        if (latest.IsDirectory || latest.Size == 0 || latest.Size > PlanFiles.MaxBytes)
        {
            throw Fail($"The plan file became invalid before review completed: {active.Path}");
        }

        var reread = await fs.ReadTextAsync(active.Absolute, PlanFiles.MaxBytes, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reread))
        {
            throw Fail($"The plan file became empty before review completed: {active.Path}");
        }

        var action = PlanFiles.WireName(approval.Action);
        var receipt = DetailsJson.Compact(w =>
        {
            w.WriteString("type", "plan_file");
            w.WriteString("planId", active.PlanId);
            w.WriteString("path", active.Path);
            w.WriteString("action", action);
            if (approval.Feedback is { } feedback)
            {
                w.WriteString("feedback", feedback);
            }
        });
        var suffix = approval.Feedback is { } fb ? $"\n\nUser feedback:\n{fb}" : "";
        var ends = approval.Action != PlanApprovalAction.RequestRevision;
        // ★ 计划的生命周期到此为止——但只在批准 / 拒绝时。要求修订的那一支必须留着:修订往往发生在下一轮(Run 已经结束),计划一旦清掉,下一轮的 Write / Edit 又会被摘掉,而模型手里还攥着那个路径。
        if (ends)
        {
            Leave(ctx);
        }

        var details = DetailsJson.Create(ctx, "planReview", w =>
        {
            w.WriteString("planId", active.PlanId);
            w.WriteString("path", active.Path);
            w.WriteString("action", action);
        });
        return ToolResult.Text(receipt + suffix, details, terminate: ends);
    }

    private void Leave(ExecutionToolContext ctx)
    {
        plans.Plans.Leave(ctx.SessionId);
        plans.ApplyFence(ctx, null);
        plans.Refresh(ctx.RunId);
    }
}
