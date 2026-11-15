using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

public sealed partial class GoalService
{
    /// <summary>
    /// 回合末判定(2.x <c>handleTurnEnd</c>;目标就是一条 <c>Stop</c> 钩子):模型不再调用工具、本来要停的时候来问这里。
    /// <list type="bullet">
    /// <item>子 Run / 已中断:不表态。</item>
    /// <item>会话名下有后台子代理在跑:<b>推迟</b>——不叫判定器(它读的转录里那一轮本来就还没结束),标记推迟态、挂定时器;到了检查点才注入一条 check-in。</item>
    /// <item>否则判定:达成 / 判不可能 → 清目标、收尾(<c>Finish</c>);未达成 → 轮数 +1、记下理由、注入续跑提示;没判成(超时 / 出错)→ 不算一轮,也不清目标。</item>
    /// <item>★ <b>空转刹车</b>:本该续跑,但连续 <see cref="GoalConditions.IdleStreakCap"/> 个回合没有调用过工具 → 强制结束并警告(目标留着)。这是唯一的「回合上限」:停下来的是「反复空转」,不是「跑了很久」。达成 / 不可能不受它拦。</item>
    /// </list>
    /// ★ 裁决属于<b>被判定的那个身份</b>:判定期间目标被换掉(哪怕条件一字不改)、被用户清掉、或调用方中断,那份裁决作废——新目标不受影响。
    /// </summary>
    public async Task<GoalTurnDecision> HandleTurnEndAsync(TurnEnd input, GoalRunContext context, bool isSubagent, CancellationToken ct)
    {
        if (isSubagent || ct.IsCancellationRequested)
        {
            return new GoalTurnDecision.None();
        }

        var sessionId = input.SessionId;
        var goalBefore = Store.Get(sessionId);
        var background = _options.BackgroundWork(sessionId);
        var deferred = false;
        if (goalBefore is not null && background.Count > 0)
        {
            deferred = true;
            var tokens = GoalTokens(sessionId, goalBefore);
            Store.Update(sessionId, g => g with { DeferredSince = g.DeferredSince ?? Now, Tokens = tokens });
            lock (_gate)
            {
                _contexts.TryAdd(sessionId, context);
            }

            ArmCheckin(sessionId);
            Log("goal_evaluated", ("outcome", "deferred"), ("iterations", goalBefore.Iterations), ("origin", OriginName(goalBefore.Origin)), ("activeAgents", background.Count), ("durationMs", 0));
        }
        else if (goalBefore is not null)
        {
            if (goalBefore.DeferredSince is not null)
            {
                Store.Update(sessionId, g => g with { DeferredSince = null, CheckinCount = 0, LastDeferralPassAt = null });
            }

            lock (_gate)
            {
                ClearTimerLocked(sessionId);
            }
        }

        var started = Now;
        GoalVerdict? verdict = null;
        if (goalBefore is not null && !deferred)
        {
            // 判定和目标共用一个取消:目标被清掉 / 替换时,还没回来的判定一起取消
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Store.Signal(sessionId) ?? CancellationToken.None);
            verdict = await EvaluateAsync(input, goalBefore, context, linked.Token).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            return new GoalTurnDecision.None();
        }

        var goal = Store.Get(sessionId);
        // 裁决属于被判定的那个身份,不属于一个条件相同的替换者
        var unchanged = goal is not null && goal.Id == goalBefore?.Id;
        GoalStatusPart? status = null;
        var reason = "";
        if (unchanged && !deferred && verdict is not null and not GoalVerdict.Skipped)
        {
            reason = verdict switch
            {
                GoalVerdict.Met m => m.Reason,
                GoalVerdict.NotMet n => n.Reason,
                GoalVerdict.Impossible i => i.Reason,
                _ => "",
            };
            var iterations = goal!.Iterations + 1;
            var tokens = GoalTokens(sessionId, goal);
            var outcome = verdict is GoalVerdict.Met ? "met" : verdict is GoalVerdict.Impossible ? "impossible" : "not_met";
            var durationMs = (long)Math.Max(0, (Now - goal.SetAt).TotalMilliseconds);
            status = new GoalStatusPart(
                verdict is GoalVerdict.Met, goal.Condition, reason, verdict is GoalVerdict.Impossible ? true : null,
                Iterations: iterations, DurationMs: durationMs, Tokens: tokens, Id: _options.Ids.NewId(), CreatedAt: Now.ToUnixTimeMilliseconds());
            Store.Update(sessionId, g => g with { Iterations = iterations, LastReason = reason, Tokens = tokens });
            Log("goal_evaluated", ("outcome", outcome), ("durationMs", (Now - started).TotalMilliseconds), ("iterations", iterations), ("origin", OriginName(goal.Origin)), ("activeAgents", 0));
            if (verdict is GoalVerdict.Met or GoalVerdict.Impossible)
            {
                Deactivate(sessionId, verdict is GoalVerdict.Met ? GoalClearReason.Met : GoalClearReason.Impossible);
                return new GoalTurnDecision.Finish(status, $"goal {outcome}");
            }
        }
        else if (unchanged && verdict is GoalVerdict.Skipped)
        {
            Log("goal_evaluated", ("outcome", "error"), ("durationMs", (Now - started).TotalMilliseconds), ("iterations", goal!.Iterations), ("origin", OriginName(goal.Origin)), ("activeAgents", 0));
        }

        string? inject = null;
        if (status is not null && goal is not null)
        {
            inject = GoalPrompts.ContinuationMessage(goal.Condition, reason.Length > 0 ? reason : "insufficient evidence in transcript");
        }
        else if (deferred && unchanged && goal is not null && Now >= CheckinDue(goal))
        {
            inject = GoalPrompts.CheckinMessage(goal.Condition, (Now - (goal.DeferredSince ?? Now)).TotalMinutes, background, final: false);
            AdvanceCheckin(sessionId, goal, "turn_end");
            ArmCheckin(sessionId);
        }

        if (inject is null)
        {
            return deferred ? new GoalTurnDecision.Finish(null, "goal deferred", AcceptPendingInput: true) : new GoalTurnDecision.None();
        }

        if (input.StoppedTurnStreak >= GoalConditions.IdleStreakCap)
        {
            Log("goal_idle_streak_stopped", ("streak", input.StoppedTurnStreak), ("iterations", goal?.Iterations ?? 0));
            return new GoalTurnDecision.Finish(status, "idle streak cap reached", AcceptPendingInput: true, IdleStreak: input.StoppedTurnStreak);
        }

        return new GoalTurnDecision.Continue(inject, status);
    }

    private async Task<GoalVerdict> EvaluateAsync(TurnEnd input, ActiveGoal goal, GoalRunContext context, CancellationToken ct)
    {
        try
        {
            return await GoalEvaluator.EvaluateAsync(
                new EvaluateGoalInput(
                    _options.Llm, context.EvaluatorModel, context.Model, GoalPrompts.EvaluatorQuestion(goal.Condition), input.Transcript,
                    new RequestContext(context.WorkspaceId, $"{input.RunId}:goal", input.SessionId), _options.ResolveModel, _options.EvaluationTimeout, _options.Clock),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new GoalVerdict.Skipped(GoalSkipReason.Error);
        }
        catch (Exception e)
        {
            // 判定器自己的故障不是用户目标的错:skipped
            _options.Log.Warn("The goal evaluator failed.", e);
            return new GoalVerdict.Skipped(GoalSkipReason.Error);
        }
    }

    // ───────────────────────── 推迟与空闲唤醒 ─────────────────────────

    private DateTimeOffset CheckinDue(ActiveGoal goal) =>
        (goal.LastDeferralPassAt ?? goal.DeferredSince ?? Now) + GoalConditions.CheckinInterval(goal.CheckinCount, _options.ResolveCheckinInterval());

    private void ArmCheckin(string sessionId)
    {
        lock (_gate)
        {
            ClearTimerLocked(sessionId);
        }

        var goal = Store.Get(sessionId);
        GoalRunContext? context;
        lock (_gate)
        {
            context = _contexts.GetValueOrDefault(sessionId);
        }

        if (goal?.DeferredSince is null || context is null)
        {
            return;
        }

        // 到顶之后只是挂空定时器(到点什么都不注入,再挂下一个):让「用户开口 → 重新有额度」之前不再打扰
        var capped = goal.IdleCheckinCount >= GoalConditions.IdleCheckinCap;
        var interval = GoalConditions.CheckinInterval(goal.CheckinCount, _options.ResolveCheckinInterval());
        TimeSpan delay;
        if (capped)
        {
            delay = interval;
        }
        else
        {
            DateTimeOffset due;
            lock (_gate)
            {
                due = _checkinFailures.TryGetValue(sessionId, out var failure) ? failure.RetryAt : CheckinDue(goal);
            }

            delay = due - Now;
            if (delay < GoalConditions.CheckinMinDelay)
            {
                delay = GoalConditions.CheckinMinDelay;
            }
        }

        var goalId = goal.Id;
        var timer = _options.Clock.CreateTimer(static state => ((Action)state!)(), (Action)(() => OnCheckinTimer(sessionId, goalId)), delay, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            ClearTimerLocked(sessionId);
            _timers[sessionId] = timer;
        }
    }

    private void OnCheckinTimer(string sessionId, string goalId)
    {
        lock (_gate)
        {
            ClearTimerLocked(sessionId);
        }

        var current = Store.Get(sessionId);
        if (current is null || current.Id != goalId || current.DeferredSince is null)
        {
            return;
        }

        if (!_options.SessionExists(sessionId))
        {
            EndSession(sessionId);
            return;
        }

        GoalRunContext? context;
        lock (_gate)
        {
            context = _contexts.GetValueOrDefault(sessionId);
        }

        try
        {
            if (_options.IsIdle(sessionId) && current.IdleCheckinCount < GoalConditions.IdleCheckinCap)
            {
                var parts = new List<Part> { new TextPart(GoalPrompts.CheckinMessage(
                    current.Condition, (Now - (current.DeferredSince ?? Now)).TotalMinutes, _options.BackgroundWork(sessionId),
                    final: current.IdleCheckinCount + 1 >= GoalConditions.IdleCheckinCap)) };
                if (Inject(sessionId, parts, current.Id, context))
                {
                    lock (_gate)
                    {
                        _checkinFailures.Remove(sessionId);
                    }

                    AdvanceCheckin(sessionId, current, "idle_timer");
                }
                else
                {
                    DeferCheckinDelivery(sessionId, current);
                }
            }
        }
        catch (Exception e)
        {
            _options.Log.Warn("A goal check-in failed.", e);
            DeferCheckinDelivery(sessionId, current);
        }

        ArmCheckin(sessionId);
    }

    /// <summary>
    /// 投递一条内部输入给这个目标。目标身份对不上(已被换掉 / 暂停)= 不投。Run 在跑进它的引导队列(用户侧的整份替换抹不掉),会话空闲就起一个新的内部 Run,
    /// 会话在收尾 / 被历史操作占着就排到之后。没有任何档位可用时返回 false(不静默丢)。
    /// </summary>
    internal bool Inject(string sessionId, IReadOnlyList<Part> parts, string goalId, GoalRunContext? context)
    {
        if (Store.Get(sessionId)?.Id != goalId)
        {
            return false;
        }

        var delivery = _options.Deliver(sessionId, new InternalInput(parts, context?.Options, goalId));
        if (delivery == InternalDelivery.Failed)
        {
            return false;
        }

        // 状态照常广播:检查已经发出去了(界面上「推迟中」的计数变了)
        Store.Update(sessionId, g => g);
        return true;
    }

    /// <summary>投递失败:不消耗三次空闲检查的额度,按指数退避重试(2.x:到顶之前每次失败间隔翻倍)。</summary>
    private void DeferCheckinDelivery(string sessionId, ActiveGoal goal)
    {
        if (Store.Get(sessionId)?.Id != goal.Id)
        {
            return;
        }

        int attempts;
        lock (_gate)
        {
            attempts = (_checkinFailures.TryGetValue(sessionId, out var failure) ? failure.Attempts : 0) + 1;
            _checkinFailures[sessionId] = (attempts, Now + GoalConditions.CheckinInterval(goal.CheckinCount + attempts, _options.ResolveCheckinInterval()));
        }

        Log("goal_checkin_delivery_failed", ("attempts", attempts), ("deferredMs", (Now - (goal.DeferredSince ?? Now)).TotalMilliseconds));
    }

    private void AdvanceCheckin(string sessionId, ActiveGoal goal, string trigger)
    {
        if (Store.Get(sessionId)?.Id != goal.Id)
        {
            return;
        }

        var idle = goal.IdleCheckinCount + (trigger == "idle_timer" ? 1 : 0);
        Store.Update(sessionId, g => g with { CheckinCount = goal.CheckinCount + 1, LastDeferralPassAt = Now, IdleCheckinCount = idle });
        Log("goal_checkin_injected", ("trigger", trigger), ("deferredMs", (Now - (goal.DeferredSince ?? Now)).TotalMilliseconds), ("checkinCount", goal.CheckinCount + 1), ("idleCheckinCount", idle));
    }
}
