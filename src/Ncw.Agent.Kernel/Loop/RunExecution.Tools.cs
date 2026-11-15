using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Loop;

internal sealed partial class RunExecution
{
    private sealed class CallSlot(ToolCallPart call)
    {
        public ToolCallPart Call { get; } = call;

        public IAgentTool? Tool { get; set; }

        public JsonElement Input { get; set; } = call.Input;

        public ToolResultPart? Result { get; set; }

        public bool Terminate { get; set; }

        public volatile bool Ended;
    }

    private sealed record ToolBatch(bool AllTerminate);

    /// <summary>本 Run 里提交的助手消息带的调用 id / 已发 tool_start 的 / 已发 tool_end 的(并行执行时多线程访问)。</summary>
    private readonly ConcurrentDictionary<string, bool> _runCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _startedCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _endedCalls = new(StringComparer.Ordinal);
    private readonly FileMutationQueue _fileMutations = new();

    private sealed class ProgressSink(RunExecution run, CallSlot slot) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
            // 进度是易失的;调用结束(或中止)之后迟到的进度直接丢弃,不然界面上会出现结束后又动起来的卡片。
            if (!slot.Ended)
            {
                run.Emit(new ToolProgressEvent(slot.Call.CallId, value));
            }
        }
    }

    // ───────────────────────── 工具执行 ─────────────────────────

    /// <summary>
    /// 02 §4:① 顺序预检(每个调用先 tool_start;参数 JSON 无效 / 工具不存在 / 参数校验失败 / 被拒绝都变成可见的失败结果,不影响同批其他调用);
    /// ② 执行(批内任何工具 ConcurrencySafe == false → 顺序执行,遇到中止立即停;否则并行);
    /// ③ 收尾:截断、AfterTool、tool_end,结果按调用顺序写成<b>一条</b> tool_result 消息,在收尾里提交
    /// (中止前已完成的结果也要落库——不然它们会被当成孤儿补成「已中断」,做过的事被记成没做,模型会重做一遍,2.x ★)。
    /// 没有结果的调用(尚未开始或被中止)得到「已中止」结果。<b>中止异常原样传播</b>(提交完之后),不包装成工具失败(R15)。
    /// </summary>
    private async Task<ToolBatch> ExecuteToolsAsync(IReadOnlyList<ToolCallPart> calls, ToolSnapshot snapshot)
    {
        var slots = calls.Select(c => new CallSlot(c)).ToArray();
        Exception? failure = null;
        try
        {
            var runnable = new List<CallSlot>();
            foreach (var slot in slots)
            {
                if (await PreflightAsync(slot, snapshot).ConfigureAwait(false))
                {
                    runnable.Add(slot);
                }
            }

            if (runnable.Any(s => !s.Tool!.Info.Traits.ConcurrencySafe))
            {
                foreach (var slot in runnable)
                {
                    _ct.ThrowIfCancellationRequested();
                    await ExecuteSlotAsync(slot).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.WhenAll(runnable.Select(ExecuteSlotAsync)).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        var aborted = failure is OperationCanceledException && _ct.IsCancellationRequested;
        var filler = aborted ? LoopMessages.Interrupted : LoopMessages.RunFailed;
        foreach (var slot in slots.Where(s => s.Result is null))
        {
            Fail(slot, filler);
        }

        await CommitToolResultsAsync(slots).ConfigureAwait(false);

        if (failure is not null)
        {
            if (aborted)
            {
                throw new OperationCanceledException(_ct);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // 所有结果都 Terminate(且都不是错误)才结束 Run(pi 规则 / 03 §1);2.x 是任一个 stopRun 就停,见 parity。
        return new ToolBatch(slots.All(s => s.Terminate));
    }

    private async Task CommitToolResultsAsync(CallSlot[] slots)
    {
        var parts = slots.Select(s => (Part)s.Result!).ToList();
        await CommitAnnouncedAsync(new Message(NewId(), Role.User, parts, Now())).ConfigureAwait(false);
    }

    /// <summary>模型这一轮撞上输出上限:不执行任何调用,每个都得到「请重发」结果。</summary>
    private async Task FailAllAsync(IReadOnlyList<ToolCallPart> calls, string text)
    {
        var slots = calls.Select(c => new CallSlot(c)).ToArray();
        foreach (var slot in slots)
        {
            Fail(slot, text);
        }

        await CommitToolResultsAsync(slots).ConfigureAwait(false);
    }

    private void EnsureToolStarted(CallSlot slot)
    {
        if (_startedCalls.TryAdd(slot.Call.CallId, true))
        {
            // ★ 下发 / 回传 / 展示三处用的都是外部名,用户才能把审批弹窗、工具卡片和转录对上号。
            Emit(new ToolStartEvent(slot.Call.CallId, slot.Call.Name, slot.Call.Input, _input.Environment));
        }
    }

    /// <summary>把一个调用收尾:恰好发一次 tool_end(失败也必须发,否则界面卡片一直转)。</summary>
    private void Complete(CallSlot slot, ToolResultPart result, long durationMs, bool terminate = false)
    {
        slot.Result = result;
        slot.Terminate = terminate && !result.IsError;
        slot.Ended = true;
        if (_endedCalls.TryAdd(slot.Call.CallId, true))
        {
            Emit(new ToolEndEvent(slot.Call.CallId, result.Output, result.IsError, durationMs));
        }
    }

    private void Fail(CallSlot slot, string text)
    {
        EnsureToolStarted(slot);
        Complete(slot, new ToolResultPart(slot.Call.CallId, new ToolOutput(text), true), 0);
    }

    /// <summary>预检一个调用。返回 true = 放行,可以执行。</summary>
    private async Task<bool> PreflightAsync(CallSlot slot, ToolSnapshot snapshot)
    {
        _ct.ThrowIfCancellationRequested();
        var call = slot.Call;
        EnsureToolStarted(slot);

        if (call.InputError is { } inputError)
        {
            // 不去执行,把原文回给模型(InputError 里带着原始参数)——只说「参数错了」它无从下手。
            Fail(slot, LoopMessages.InvalidArguments(inputError));
            return false;
        }

        var tool = snapshot.Find(call.Name);
        if (tool is null)
        {
            // 模型会编工具名;这一轮进行中工具也可能被下线。两种都是工具错误,不是崩溃。
            Fail(slot, LoopMessages.UnknownTool(call.Name, snapshot.Names));
            return false;
        }

        slot.Tool = tool;
        var check = SafePrepareArguments(tool, call.Input);
        if (!check.IsValid)
        {
            Fail(slot, check.Error!);
            return false;
        }

        slot.Input = check.Input;
        PreflightDecision decision;
        try
        {
            decision = await Abortable(_hooks.BeforeTool(new ToolInvocation(call.CallId, call.Name, tool, slot.Input), _ct), _ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // 预检钩子(权限)出错 = 失败即关闭:拒绝,不执行。
            _diagnostics.Warn("BeforeTool failed; the call is denied.", e);
            Fail(slot, $"The tool call was not permitted because the permission check failed: {e.Message}");
            return false;
        }

        _ct.ThrowIfCancellationRequested();
        switch (decision)
        {
            case PreflightDecision.DenyDecision deny:
                // 拒绝要让模型看见:系统提示词里写了「被拒绝时不要试图绕开」,前提是它知道自己被拒了。
                Fail(slot, deny.Reason ?? LoopMessages.DefaultDenied);
                return false;
            case PreflightDecision.EditDecision edit:
            {
                // allow_edited 的入参是用户改过的——用原值执行等于无视用户的修改;改写后的参数重新校验。
                var recheck = SafePrepareArguments(tool, edit.Input);
                if (!recheck.IsValid)
                {
                    Fail(slot, recheck.Error!);
                    return false;
                }

                slot.Input = recheck.Input;
                return true;
            }

            default:
                return true;
        }
    }

    private ToolArgumentCheck SafePrepareArguments(IAgentTool tool, JsonElement input)
    {
        try
        {
            return tool.PrepareArguments(input);
        }
        catch (Exception e)
        {
            return ToolArgumentCheck.Invalid($"The tool arguments could not be validated: {e.Message}");
        }
    }

    private async Task ExecuteSlotAsync(CallSlot slot)
    {
        var call = slot.Call;
        var tool = slot.Tool!;
        var invocation = new ToolInvocation(call.CallId, call.Name, tool, slot.Input);
        var started = _clock.GetTimestamp();
        _ct.ThrowIfCancellationRequested();

        ToolResult result;
        try
        {
            var context = new ToolContext(_input.RunId, call.CallId, _input.SessionId, _input.Depth, _input.Environment, new ProgressSink(this, slot), _fileMutations);
            if (_hooks.EnrichToolContext is { } enrich)
            {
                context = enrich(context);
            }

            // Abortable:中止时立即返回,不等一个不认取消令牌的工具(它继续在后台跑,异常被观察掉)。
            result = await Abortable(tool.ExecuteAsync(invocation, context, _ct), _ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            // ★ 中止原样抛出,不伪装成工具失败:伪装的话模型会看到「工具失败了」然后继续往下跑——用户点了停止,对话却还在动。
            throw;
        }
        catch (Exception e)
        {
            // 其余异常收敛成工具错误(含工具自己内部超时抛的 OperationCanceledException):进记录、继续循环。
            result = ToolResult.Error(LoopMessages.ToolFailed(e));
        }

        // 截断在产出侧;然后 AfterTool(只能追加、只能翻转 IsError),再发 tool_end——界面、记录、模型看到的是同一份。
        result = ToolOutputs.Truncate(result, _options.MaxToolOutputChars);
        try
        {
            var after = await Abortable(_hooks.AfterTool(invocation, result, _ct), _ct).ConfigureAwait(false);
            result = ToolOutputs.GuardAfterTool(result, after);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // 后置钩子里的异常不该拖垮工具循环(失败已经记进诊断了)。
            _diagnostics.Warn("AfterTool failed; the result is used as is.", e);
        }

        var output = ToolOutputs.ToOutput(result);
        var part = new ToolResultPart(call.CallId, output, result.IsError, result.Subagent);
        Complete(slot, part, (long)_clock.GetElapsedTime(started).TotalMilliseconds, result.Terminate);
    }
}
