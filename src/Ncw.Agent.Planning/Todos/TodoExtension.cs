using System.Collections.Concurrent;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.TodoLists;

public sealed class TodoOptions
{
    /// <summary>转录里记的 <c>TodoWrite</c> 外部名。一方工具的外部名恒等于内部 id(冲突时后来者被丢弃,不改名),所以默认 <c>TodoWrite</c> 就是对的。</summary>
    public string ToolName { get; init; } = TodoWriteTool.Name;

    /// <summary>读会话转录(状态块里的待办、回执里的增量)。<c>null</c> = 两者都不提供。</summary>
    public ITranscriptReader? Transcript { get; init; }

    public IIdGenerator Ids { get; init; } = new UlidIdGenerator();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// 这个会话里有更要紧的事在管收尾吗(目标在续跑、计划模式、后台子代理还在跑):是就不补报(补报只是进度核对,不能覆盖它们的裁决,也不能催计划模式写平行清单)。
    /// 返回 true 时补报额度<b>不</b>消耗。
    /// </summary>
    public Func<string, bool>? YieldTo { get; init; }
}

/// <summary>
/// 待办的一方扩展(06 §3):工具 <c>TodoWrite</c>、<c>before_run</c>(状态块里的待办段,每个 Run 算一次)、<c>prepare_turn</c>(记下本回合工具有没有下发)、
/// <c>turn_end</c>(收尾前补报一次)、<c>compact_attachments</c>(压缩后重附最新清单)、<c>run_end</c>(摘掉 Run 级登记)。可关闭(不注册就等于不存在)。
/// 待办没有表:一切都从转录推出来,持久化就是转录本身(AGENTS.md §3 规则 1/3)。
/// </summary>
public sealed class TodoExtension(TodoOptions? options = null) : IAgentExtension
{
    private sealed class RunTodos(bool subagent, IEnumerable<MessageId> inputIds)
    {
        public TodoReconciler Reconciler { get; } = new();

        public bool Subagent { get; } = subagent;

        public HashSet<MessageId> InputIds { get; } = [.. inputIds];

        /// <summary>本 Run 自己的消息从 <c>Transcript</c> 的哪个下标开始(第一回合开始时定下)。</summary>
        public int StartIndex { get; set; } = -1;

        public bool Advertised { get; set; }
    }

    private readonly TodoOptions _options = options ?? new TodoOptions();
    private readonly ConcurrentDictionary<RunId, RunTodos> _runs = new();

    public string Id => "todos";

    /// <summary>在目标(500)之后:收尾裁决按 Order 取第一个不是 <c>Stop</c> 的,目标的续跑 / 强停 / 推迟优先于清单补报。</summary>
    public int Order => 600;

    internal int OpenRuns => _runs.Count;

    public void Register(IExtensionRegistry registry)
    {
        registry.RegisterTool(new TodoWriteTool(_options.Transcript));
        registry.Mutate(SessionExtensionPoints.BeforeRun, OnBeforeRun);
        registry.Mutate(LoopExtensionPoints.PrepareTurn, OnPrepareTurn);
        registry.Gate(LoopExtensionPoints.TurnEnd, OnTurnEnd);
        registry.Mutate(SessionExtensionPoints.CompactAttachments, (draft, _, _) =>
        {
            if (Todos.Latest(TodoTranscript.EventsOf(draft.History), _options.ToolName) is { Count: > 0 } todos)
            {
                draft.Texts.Add(TodoSections.AfterCompaction(todos, _options.ToolName));
            }

            return ValueTask.CompletedTask;
        });
        registry.On(LoopExtensionPoints.RunEnd, (_, ctx, _) =>
        {
            _runs.TryRemove(ctx.RunId, out _);
            return ValueTask.CompletedTask;
        });
    }

    private ValueTask OnBeforeRun(RunDraft draft, ExtensionContext ctx, CancellationToken ct)
    {
        _runs[ctx.RunId] = new RunTodos(draft.Request.Depth > 0, draft.Request.Input.Select(m => m.Id));
        if (_options.Transcript is { } transcript
            && Todos.Latest(TodoTranscript.EventsOf(transcript.History(ctx.SessionId)), _options.ToolName) is { } todos)
        {
            // ★ 每个 Run 算一次:状态块的文案写着 "as of the start of this run"。模型在本 Run 中途自己改了清单的话,每回合现算就意味着状态块每回合都变,前缀会从那里往后整体作废;
            // 它这一轮刚写的那次 TodoWrite 就在最近几条转录里,它看得见。
            lock (draft.StateSections)
            {
                draft.StateSections.Add(TodoSections.Current(todos));
            }
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnPrepareTurn(TurnDraft draft, ExtensionContext ctx, CancellationToken ct)
    {
        if (_runs.TryGetValue(ctx.RunId, out var run))
        {
            if (run.StartIndex < 0)
            {
                var at = -1;
                for (var i = 0; i < draft.Context.Transcript.Count; i++)
                {
                    if (run.InputIds.Contains(draft.Context.Transcript[i].Id))
                    {
                        at = i;
                        break;
                    }
                }

                // 没有输入消息(「继续」)= 本 Run 还没有提交任何东西
                run.StartIndex = at >= 0 ? at : draft.Context.Transcript.Count;
            }

            // 本回合的快照里有没有它:没有就不能催模型去调一个它没有的工具
            run.Advertised = draft.Tools.Find(_options.ToolName) is not null;
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask<TurnEndDecision> OnTurnEnd(TurnEnd end, ExtensionContext ctx, CancellationToken ct)
    {
        if (!_runs.TryGetValue(end.RunId, out var run) || _options.YieldTo?.Invoke(end.SessionId) == true)
        {
            return ValueTask.FromResult(TurnEndDecision.Stop);
        }

        var mine = end.Transcript.Skip(Math.Max(0, run.StartIndex)).ToList();
        var note = run.Reconciler.Check(mine, run.Advertised ? _options.ToolName : null, ct.IsCancellationRequested, run.Subagent);
        if (note is null)
        {
            return ValueTask.FromResult(TurnEndDecision.Stop);
        }

        // 补报后还要经过后面的收尾裁决;次数上限独立于会被工具调用清零的空转计数
        var message = new Message(new MessageId(_options.Ids.NewId()), Role.User, [new TextPart(note)], _options.Clock.GetUtcNow(), Internal: true);
        return ValueTask.FromResult(TurnEndDecision.Continue(message));
    }
}
