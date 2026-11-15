using System.Text.Json;

namespace Ncw.Core.Domain;

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>
/// 一条待办。这个形状是三处共同承诺的:<c>TodoWrite</c> 的入参与回执、给模型看的上下文提醒、界面上的两张清单卡片(输入框上方那条 + 消息里那张)。
/// <see cref="ActiveForm"/> 是进行中时界面显示的那句「正在……」,<b>不进</b>给模型的回显。
/// </summary>
public sealed record TodoItem(string Content, TodoStatus Status, string ActiveForm);

/// <summary>清单里一次变动的汇总(<see cref="Todos.Plan"/>)。</summary>
public sealed record TodoChange(IReadOnlyList<TodoItem> Added, IReadOnlyList<TodoItem> Removed, int NewlyCompleted, int NewlyStarted)
{
    public static TodoChange None { get; } = new([], [], 0, 0);
}

/// <summary>转录里和待办有关的一条事实,按转录顺序:模型发起了一次调用,或一次调用有了回执。两端各自把自己的消息模型压成这个序列,判据只在 <see cref="Todos"/> 里有一份。</summary>
public abstract record TodoEvent
{
    private TodoEvent()
    {
    }

    /// <param name="Name">转录里记的外部名;撞名时可能带哈希后缀,所以调用方要按它查,不能写字面量。</param>
    /// <param name="Input">入参(来源是模型,一律当不可信)。</param>
    public sealed record Call(string CallId, string Name, JsonElement Input) : TodoEvent;

    public sealed record Result(string CallId, bool IsError) : TodoEvent;
}

/// <summary>
/// 待办的<b>形状、渲染标记与派生逻辑</b>——全仓唯一一处(AGENTS.md §3 规则 3)。
/// <para>
/// 待办没有表:当前清单 = 转录里<b>最近一次成功的</b> <c>TodoWrite</c> 调用的入参(2.x 的「完全无状态」设计)。这里全是纯函数:
/// Agent(工具回执的增量、状态块、压缩后附件、收尾前补报)和客户端(两张清单卡片、卡片上的增量标注)读同一份,不会各漂各的。
/// </para>
/// 判据缺一不可,漏掉任何一条界面上都是一份<b>看起来完全正常的错清单</b>:撞名后的外部名、被工具拒绝过的调用、还没有回执的调用、流式中的半截条目。
/// </summary>
public static class Todos
{
    /// <summary>再多就不是「计划」而是「清单癖」了——而且每一轮都要重发一遍。</summary>
    public const int MaxItems = 40;

    /// <summary>
    /// 清单的渲染标记。★ 上下文里那份快照和工具自己的回显必须长得一样,否则模型会以为那是两份不同的清单。
    /// </summary>
    public static string Mark(TodoStatus status) => status switch
    {
        TodoStatus.Completed => "[x]",
        TodoStatus.InProgress => "[~]",
        _ => "[ ]",
    };

    public static string WireName(TodoStatus status) => status switch
    {
        TodoStatus.Completed => "completed",
        TodoStatus.InProgress => "in_progress",
        _ => "pending",
    };

    public static bool TryParseStatus(string? wire, out TodoStatus status)
    {
        switch (wire)
        {
            case "pending":
                status = TodoStatus.Pending;
                return true;
            case "in_progress":
                status = TodoStatus.InProgress;
                return true;
            case "completed":
                status = TodoStatus.Completed;
                return true;
            default:
                status = default;
                return false;
        }
    }

    /// <summary>
    /// 入参 → 清单,<b>严格</b>收窄。三个判据缺一不可,缺了就当这份入参不是一份清单:
    /// 不是对象 / <c>todos</c> 不是数组 → <c>null</c>;单个条目缺 <c>content</c> / <c>activeForm</c>、或 <c>status</c> 不是三档之一 → <b>跳过这一条</b>
    /// (半份清单好过没有,但补不出一个字段:编出来的 <c>activeForm</c> 会变成界面上一行看似真实的「正在……」);一条都不剩 → <c>null</c>,不是空列表
    /// ——调用方据此区分「清单是空的」与「这里根本没有清单」。超过 <see cref="MaxItems"/> 的部分截掉(模型能绕过 schema 的 <c>maxItems</c>)。
    /// 与界面流式预览的宽松收窄<b>刻意不同</b>:那个跑在半截 JSON 上(缺 status 按 pending 算是为了边写边画),这里跑在已经跑完的调用上,缺字段只可能是模型写错了。
    /// </summary>
    public static IReadOnlyList<TodoItem>? Narrow(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<TodoItem>();
        var seen = 0;
        foreach (var entry in raw.EnumerateArray())
        {
            if (seen++ >= MaxItems)
            {
                break;
            }

            if (entry.ValueKind != JsonValueKind.Object
                || !TryString(entry, "content", out var content)
                || !TryString(entry, "activeForm", out var activeForm)
                || !TryString(entry, "status", out var wire)
                || !TryParseStatus(wire, out var status))
            {
                continue;
            }

            items.Add(new TodoItem(content, status, activeForm));
        }

        return items.Count > 0 ? items : null;
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = p.GetString() ?? "";
        return true;
    }

    /// <summary>
    /// 两张查询表:每个工具名最后一次<b>成功</b>写下的清单(<see cref="Latest"/>),和每次调用<b>之前</b>那一份(<see cref="Before"/>)。
    /// ★ 必须确认那次调用真的成功了:循环是先提交 <c>tool_call</c>、后校验的,所以被工具明确拒绝过的清单(两个 in_progress、空数组、坏 JSON)也原样躺在转录里。
    /// 盲取最近一条 = 把工具拒绝过的东西当成当前进度渲染出去。判据是配对的回执存在<b>且</b>不是错误;还没跑完的那次(没有回执)同样不算。
    /// </summary>
    public sealed record Index(IReadOnlyDictionary<string, IReadOnlyList<TodoItem>> Latest, IReadOnlyDictionary<string, IReadOnlyList<TodoItem>> Before);

    public static Index Build(IEnumerable<TodoEvent> events)
    {
        var all = events as IReadOnlyList<TodoEvent> ?? [.. events];
        // 先把 callId → 成功与否收成一张表,省得对每个候选再正向扫一遍
        var ok = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var e in all)
        {
            if (e is TodoEvent.Result r)
            {
                ok[r.CallId] = !r.IsError;
            }
        }

        var latest = new Dictionary<string, IReadOnlyList<TodoItem>>(StringComparer.Ordinal);
        var before = new Dictionary<string, IReadOnlyList<TodoItem>>(StringComparer.Ordinal);
        foreach (var e in all)
        {
            if (e is not TodoEvent.Call call)
            {
                continue;
            }

            // ★ 先记「这次之前是什么」再决定要不要采纳本次:本次的结果此刻可能还没提交,就算已提交,它也不该出现在自己的「之前」里。
            if (latest.TryGetValue(call.Name, out var known))
            {
                before[call.CallId] = known;
            }

            if (!ok.GetValueOrDefault(call.CallId))
            {
                continue;
            }

            if (Narrow(call.Input) is { } todos)
            {
                latest[call.Name] = todos;
            }
        }

        return new Index(latest, before);
    }

    /// <summary>
    /// 「当前的待办清单」:<paramref name="toolName"/> 最近一次成功写下的那份;没有 = <c>null</c>。
    /// ★ <paramref name="toolName"/> 是转录里记的<b>外部名</b>,要从工具注册表查,不能写字面量,也不能从本回合的工具快照里取(快照被过滤过,窄的子代理会看不见自己刚写的清单);
    /// 两种错都是静默的。压缩吃不掉它:压缩只动 <c>tool_result</c> / <c>thinking</c> / <c>image</c>,<c>tool_call</c> 原样保留。
    /// </summary>
    public static IReadOnlyList<TodoItem>? Latest(IEnumerable<TodoEvent> events, string toolName) =>
        Build(events).Latest.GetValueOrDefault(toolName);

    /// <summary>
    /// 「这次调用<b>之前</b>那份清单」的查询(算增量用)。不需要工具名:转录里每次调用自己带着名字,按名字分组即可,撞名后缀在这一侧不存在。
    /// ★ 返回 <c>null</c> 的含义是「<b>不知道</b>」,不是「空的」:老转录里第一次调用之前也可能有过清单(被压缩掉了),所以调用方在 <c>null</c> 时不做增量标注,而不是把整份清单标成「新增」。
    /// </summary>
    public static Func<string, IReadOnlyList<TodoItem>?> HistoryLookup(IEnumerable<TodoEvent> events)
    {
        var before = Build(events).Before;
        return callId => before.GetValueOrDefault(callId);
    }

    /// <summary>
    /// 这次更新改了什么。配对按 <c>Content</c>(清单项没有 id):同名的项按出现顺序一对一消耗,所以重复内容不会被算成一次新增加一次移除。
    /// 顺序变化、<c>ActiveForm</c> 的措辞变化都不算变化——它们不影响清单说了什么。
    /// ★ <paramref name="previous"/> 为 <c>null</c> 时全空,即「不标增量」而不是「整份都是新增」:后者是错的,而错的增量比没有增量更糟(用户会以为模型刚刚凭空加了五项)。
    /// </summary>
    public static TodoChange Plan(IReadOnlyList<TodoItem> todos, IReadOnlyList<TodoItem>? previous)
    {
        if (previous is null)
        {
            return TodoChange.None;
        }

        var remaining = new List<TodoItem>(previous);
        var added = new List<TodoItem>();
        var newlyCompleted = 0;
        var newlyStarted = 0;
        foreach (var item in todos)
        {
            var at = remaining.FindIndex(p => p.Content == item.Content);
            TodoItem? before = null;
            if (at >= 0)
            {
                before = remaining[at];
                remaining.RemoveAt(at);
            }

            if (before is null)
            {
                added.Add(item);
            }

            var changed = before is null || before.Status != item.Status;
            if (changed && item.Status == TodoStatus.Completed)
            {
                newlyCompleted++;
            }

            if (changed && item.Status == TodoStatus.InProgress)
            {
                newlyStarted++;
            }
        }

        return new TodoChange(added, remaining, newlyCompleted, newlyStarted);
    }

    /// <summary>清单是否全部完成(空清单不算完成:它不是一份清单)。</summary>
    public static bool AllCompleted(IReadOnlyList<TodoItem> todos) => todos.Count > 0 && todos.All(t => t.Status == TodoStatus.Completed);
}

/// <summary>
/// 界面上一张清单的样子(输入框上方那条、消息里那张、手机详情页都用它)。全是结构化的值,文案由各端按当前语言格式化。
/// 「对客户端的全量状态」:同一份清单、同一个函数,首帧和后续更新是同一个类型。
/// </summary>
public sealed record TodoView(IReadOnlyList<TodoItem> Items, int Completed, int Total, TodoItem? Current, bool IsComplete)
{
    public static TodoView Of(IReadOnlyList<TodoItem> items)
    {
        var done = items.Count(t => t.Status == TodoStatus.Completed);
        return new TodoView(items, done, items.Count, items.FirstOrDefault(t => t.Status == TodoStatus.InProgress), Todos.AllCompleted(items));
    }
}
