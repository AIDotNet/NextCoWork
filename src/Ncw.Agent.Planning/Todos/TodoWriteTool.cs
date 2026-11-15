using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.TodoLists;

/// <summary>
/// <c>TodoWrite</c>——模型自己的任务清单。
/// <para>
/// ★ <b>完全无状态,这是它最反直觉也最重要的一点。</b>直觉上这个工具该往某个 store 里写一份清单,下一轮再读回来。不需要:每一轮请求都会把整条转录重放给模型,
/// 而这次调用的 <c>tool_call</c>(连同 <c>todos</c> 入参)就在转录里——<b>转录本身就是那份状态</b>。真去存一份的坏处是具体的:存下来的清单和转录里的会在中断 / 重试 / 编辑历史之后分叉,
/// 而模型看见的永远是转录里的那份,于是就有了一份谁也不看、却会显示在界面上的「幽灵清单」。所以这里只做两件事:<b>校验</b>(把会让清单失去意义的形状挡回去)和<b>回显</b>(让模型确认它写下的东西被收到了)。
/// </para>
/// 清单的形状、标记与从转录反推清单的纯函数在 <c>Ncw.Core.Domain.Todos</c>(客户端的清单卡片也用它们),本类只剩工具自己的入参 schema 与文案。
/// 回执里的增量(「上一份」)从转录读(<see cref="ITranscriptReader"/>):这次调用自己已经在库里但还没有回执,所以不会算进自己的「之前」。
/// </summary>
public sealed class TodoWriteTool(ITranscriptReader? transcript = null) : IAgentTool
{
    public const string Name = "TodoWrite";

    private const string Description =
        "Use this tool to manage a task list for the work in front of you. It helps you break the work up, "
        + "keeps track of where you are, and shows the user what you are doing.\n\n"
        + "When to use it:\n"
        + "- The task takes three or more steps, or touches several files\n"
        + "- The user gave you several things at once (numbered, or comma-separated)\n"
        + "- The task is non-trivial and the order matters\n"
        + "- As soon as you receive the request: write the list first, then start\n"
        + "- Before starting an item: mark it in_progress\n"
        + "- After each item finishes: read the result you just got, then update the list IMMEDIATELY, "
        + "before starting the next item. Do NOT batch completions up\n\n"
        + "When NOT to use it:\n"
        + "- A single, straightforward step\n"
        + "- Purely answering a question or explaining some code\n"
        + "- Anything you can finish in a couple of actions — here the list is just noise\n\n"
        + "How to use it:\n"
        + "- ALWAYS send the complete list, not just the entries that changed. What you send IS the list\n"
        + "- Send progress updates separately from work or verification calls, and wait for a successful "
        + "receipt before continuing. A rejected update leaves the previous list visible\n"
        + "- There are exactly three states: pending (not started or waiting on something), in_progress "
        + "(actively working on it now), completed (done and verified)\n"
        + "- IMPORTANT: AT MOST ONE item may be in_progress at a time, and while you are actually working "
        + "there should be exactly one. Zero items in_progress is correct when nothing is left to do or when "
        + "every remaining item is blocked. Marking everything in progress is the same as having no plan, "
        + "and the user sees \"it is doing five things at once\"\n"
        + "- Before your final answer: check it against what actually happened and send the complete list "
        + "if any status or blocker needs updating. An unchanged, accurate list needs no duplicate call. "
        + "When every listed task is complete, no item may be left in_progress\n"
        + "- A text reply is not a substitute for the tool call: never say an item is done without sending "
        + "the update, and never mark an item completed while the work or the verification for it is still "
        + "in flight\n"
        + "- content is imperative (\"Run the tests\"), activeForm is present continuous (\"Running the tests\"). "
        + "Both are required\n"
        + "- Mark completed ONLY when the work is actually done AND verified. If tests are still failing or the "
        + "implementation is half-finished, leave the affected item incomplete and describe what is left. "
        + "Use in_progress only while actively working on it; otherwise use pending\n"
        + "- NEVER mark something completed because you got stuck: leave it pending or in_progress and put the "
        + "reason — blocked, failed, or not verified yet — in that entry";

    private static readonly JsonElement Schema = S.Build(S.Object(
        ["todos"],
        ("todos", S.Array(
            "The complete task list. ALWAYS send the WHOLE list, never a delta",
            S.Object(
                ["content", "status", "activeForm"],
                ("content", S.String("The task, in imperative form, e.g. \"Run the tests\"", minLength: 1, maxLength: 500)),
                ("status", S.Enum("The task state", "pending", "in_progress", "completed")),
                ("activeForm", S.String("The present continuous form, e.g. \"Running the tests\" — this is what the UI shows while it is in progress", minLength: 1, maxLength: 500))),
            maxItems: Todos.MaxItems))));

    /// <summary>
    /// ★ <c>ReadOnly</c>。它不碰磁盘、不碰网络,而计划模式恰恰是最需要列清单的时候——标成非只读,计划模式下的权限闸门就会为它弹审批,模型连计划都写不顺。
    /// </summary>
    public ToolSpec Info { get; } = new(
        Name,
        Description,
        Schema,
        new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
        "Keep a task list for multi-step work",
        ["Send the complete list on every call, mark an item completed only when it is done and verified."]);

    public ToolArgumentCheck PrepareArguments(JsonElement input)
    {
        var result = JsonSchemaValidator.Validate(Info.InputSchema, input);
        return result.IsValid ? ToolArgumentCheck.Valid(result.Value) : ToolArgumentCheck.Invalid($"Invalid arguments — {result.Error}");
    }

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        // 直接调用(没经过 Loop 的预检)也要有校验:再过一遍是幂等的
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ValueTask.FromResult(ToolResult.Error(check.Error!));
        }

        var todos = Todos.Narrow(check.Input) ?? [];
        if (todos.Count == 0)
        {
            return ValueTask.FromResult(ToolResult.Error(
                "The task list is empty. Either send at least one task, or do not call this tool at all — "
                + "an empty list wipes out the plan the user can already see."));
        }

        // ★ 这条校验是有实际作用的,不是洁癖:多个 in_progress 时,界面上那行「正在……」不知道该显示哪一条,而模型也就失去了「我现在在做哪一步」这个锚点。
        // 拒绝并说清楚,比默默接受一份没有意义的清单要好。(上界是 1 而不是「必须等于 1」:整份清单都不活跃是合法的。)
        var active = todos.Where(t => t.Status == TodoStatus.InProgress).ToList();
        if (active.Count > 1)
        {
            return ValueTask.FromResult(ToolResult.Error(
                $"At most one item may be in_progress at a time, and you sent {active.Count}: "
                + $"{string.Join(", ", active.Select(t => $"\"{t.Content}\""))}. "
                + "Keep only the one you are ACTUALLY working on right now, and set the rest back to pending."));
        }

        var done = todos.Count(t => t.Status == TodoStatus.Completed);
        var body = string.Join('\n', todos.Select(t => $"{Todos.Mark(t.Status)} {t.Content}"));

        // 回执要顺便告诉模型这次更新动了什么:它看不见自己改了什么——尤其是「丢掉一项」。上一份从转录取,不新存一份状态;取不到(第一次写、历史被压缩)就只说计数,不编造差异。
        var previous = transcript is null ? null : Todos.Latest(TodoTranscript.EventsOf(transcript.History(ctx.SessionId)), call.Name);
        var plan = Todos.Plan(todos, previous);
        var changes = new List<string>();
        if (plan.NewlyCompleted > 0)
        {
            changes.Add($"{plan.NewlyCompleted} completed");
        }

        if (plan.NewlyStarted > 0)
        {
            changes.Add($"{plan.NewlyStarted} started");
        }

        if (plan.Added.Count > 0)
        {
            changes.Add($"{plan.Added.Count} added");
        }

        if (plan.Removed.Count > 0)
        {
            changes.Add($"{plan.Removed.Count} dropped");
        }

        var header = $"Todo list updated ({done}/{todos.Count} completed{(changes.Count == 0 ? "" : " · " + string.Join(" · ", changes))}):";
        var dropped = plan.Removed.Count == 0
            ? ""
            : $"\nYou dropped {plan.Removed.Count} item(s) that were on the previous list: "
                + $"{string.Join(", ", plan.Removed.Select(t => $"\"{t.Content}\""))}. "
                + "If that was not deliberate, put them back or say why they are gone.";

        // 清单全绿和「还有没做完的」必须给出两条不同的下一步:一份已经全绿的清单上再催「做完一项就标掉」没有对象可指,只会把模型推回循环。
        // 反过来,没做完时不能默认放行,也不强迫全绿——受阻项本来就该留在清单上。
        // ★ 这几句逐项提醒不能因为「描述里已经写过」就删掉:回执是模型真正读到最后一次的地方——描述可能在长对话里被压缩掉,回执不会。
        var text = done == todos.Count
            ? $"{header}\n{body}{dropped}\n\n"
                + "All listed tasks are complete. If the user's request is now fully addressed, give the final "
                + "answer and stop — do not keep calling this tool on a finished list. If it is not, keep the "
                + "list honest by adding what is left."
            : $"{header}\n{body}{dropped}\n\n"
                + "Keep working through this list. As soon as an item is done, call this tool again to mark it — "
                + "do NOT wait and mark them all at the end. Before your final answer, check it against what "
                + "actually happened and send a complete update if needed; an unchanged, accurate list needs no "
                + "duplicate call. Items that are blocked, failed, or not verified yet stay unfinished, with the "
                + "reason in their own entry.";
        return ValueTask.FromResult(ToolResult.Text(text, DetailsOf(todos, plan)));
    }

    /// <summary>只给界面的结构化数据(清单卡片 + 这次的增量):和模型看到的回执同源,但不含 <c>activeForm</c> 以外的任何文案。</summary>
    private static JsonElement DetailsOf(IReadOnlyList<TodoItem> todos, TodoChange plan)
    {
        var details = new JsonObject
        {
            ["kind"] = "todo",
            ["todos"] = Jsn.ArrayOf(todos.Select(t => (JsonNode?)new JsonObject
            {
                ["content"] = t.Content,
                ["status"] = Todos.WireName(t.Status),
                ["activeForm"] = t.ActiveForm,
            })),
            ["added"] = plan.Added.Count,
            ["removed"] = plan.Removed.Count,
            ["newlyCompleted"] = plan.NewlyCompleted,
            ["newlyStarted"] = plan.NewlyStarted,
        };
        return Jsn.ToElement(details);
    }
}
