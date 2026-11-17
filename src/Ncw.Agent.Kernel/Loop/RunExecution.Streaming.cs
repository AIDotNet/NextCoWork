using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Loop;

internal sealed partial class RunExecution
{
    /// <summary>一次上游请求的累积状态。<see cref="Id"/> 在第一个内容事件到来时才分配并发 start。</summary>
    private sealed class Streaming(TimeProvider clock)
    {
        public StreamAssembler Asm { get; } = new(clock);

        /// <summary>已分配的消息 id(第一个内容事件、或者最终提交时分配;空消息永远不会消耗一个 id)。</summary>
        public MessageId? Id { get; set; }

        /// <summary>已经发过 <see cref="MessageStartEvent"/>。</summary>
        public bool Started { get; set; }
    }

    private void EnsureStarted(Streaming streaming)
    {
        streaming.Id ??= NewId();
        if (!streaming.Started)
        {
            streaming.Started = true;
            Emit(new MessageStartEvent(streaming.Id.Value, Role.Assistant, streaming.Asm.Model, streaming.Asm.ProviderId));
        }
    }

    /// <summary>
    /// 一次请求 + 断流续传。★ 重试只在模型层(首字节之前);这里唯一的「重试」是流中断(Network 错误)时丢弃已累积内容、按退避表重来,
    /// 而且只在本回合尚未执行任何工具时(工具在流结束之后才执行,所以这里天然满足——重放的工具副作用是零个)。
    /// 只认 <c>Network</c>,刻意不放宽到全部可重试:限流、5xx、冷却中的 NoHealthyProvider 走模型层那条信息更全的路(认 Retry-After、会换供应商)。
    /// 断流那一次已经生成的内容整个丢弃:保留半截回答试过,更糟(历史以一条助手消息结尾 + 对话里多出一个割裂的气泡)。
    /// </summary>
    private async Task<Streaming> StreamWithResumeAsync(Llm.LlmRequest request)
    {
        for (var resume = 0; ; resume++)
        {
            var streaming = await StreamOnceAsync(request).ConfigureAwait(false);
            var error = streaming.Asm.Error;
            if (error is not { Code: AgentErrorCode.Network } || resume >= _options.ResumeDelays.Count)
            {
                return streaming;
            }

            // 丢弃这一次:不进转录、不进历史。先告诉界面丢掉增量,再发退避提示(复用 provider_retry,界面三处已经在画它)。
            _pending = null;
            if (streaming is { Started: true, Id: { } discarded })
            {
                Emit(new MessageDiscardEvent(discarded));
            }

            var delay = _options.ResumeDelays[resume];
            Emit(new ProviderRetryEvent(resume + 1, (int)delay.TotalMilliseconds, error.Message));
            await _delayer.DelayAsync(delay, _ct).ConfigureAwait(false);
            _ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// 消费一整条流。取消令牌触发或上游报「已取消」时抛出取消异常,<see cref="_pending"/> 保留着半截回复交给中止收尾。
    /// 「流结束了却没有 MessageEnd」和「撞上输出上限且没有工具调用」两种结局被合成 <see cref="StreamError"/>(2.x ★)。
    /// </summary>
    private async Task<Streaming> StreamOnceAsync(LlmRequest request)
    {
        var streaming = new Streaming(_clock);
        _pending = streaming;
        var asm = streaming.Asm;
        var context = new RequestContext(_input.WorkspaceId, _input.RunId.Value, _input.SessionId);
        var enumerator = _llm.StreamAsync(_input.Model, request, context, _ct).GetAsyncEnumerator(_ct);
        Task<bool>? move = null;
        try
        {
            await ConsumeAsync(streaming, enumerator, m => move = m).ConfigureAwait(false);
        }
        finally
        {
            await DisposeEnumeratorAsync(enumerator, move).ConfigureAwait(false);
        }

        _ct.ThrowIfCancellationRequested();
        if (asm.Error is { Code: AgentErrorCode.Aborted })
        {
            throw new AbortSignalException();
        }

        if (asm.Error is null && !asm.Completed)
        {
            asm.Apply(new StreamError(AgentError.Create(
                AgentErrorCode.Network,
                "The upstream connection closed before the response completed.",
                messageKey: LlmErrorKeys.IncompleteResponse)));
        }

        if (asm.Error is null && asm.Stop == StopReason.MaxTokens && !asm.Parts().OfType<ToolCallPart>().Any())
        {
            asm.Apply(new StreamError(AgentError.Create(
                AgentErrorCode.Provider,
                "The response reached the model output limit.",
                false,
                messageKey: LoopErrorKeys.OutputLimit)));
        }

        return streaming;
    }

    private async Task ConsumeAsync(Streaming streaming, IAsyncEnumerator<LlmEvent> enumerator, Action<Task<bool>> track)
    {
        var asm = streaming.Asm;
        while (true)
        {
            Task<bool> move = enumerator.MoveNextAsync().AsTask();
            track(move);
            bool has;
            try
            {
                // WaitAsync:一个不认取消令牌的(卡住的)上游也不能拖住中止。
                has = await move.WaitAsync(_ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                _ = move.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (Exception e)
            {
                // 模型层承诺不抛;万一抛了,当作未知错误,保留已收到的内容。
                _diagnostics.Warn("The model layer threw from its stream.", e);
                asm.Apply(new StreamError(AgentError.Create(AgentErrorCode.Unknown, $"Unexpected error in the model layer: {e.Message}", false)));
                break;
            }

            if (!has)
            {
                break;
            }

            _ct.ThrowIfCancellationRequested();
            var ev = enumerator.Current;
            asm.Apply(ev);
            Forward(streaming, ev);
        }
    }

    /// <summary>
    /// 释放枚举器。取消时上游可能还有一个 MoveNext 在途(不认令牌的上游尤其如此),而异步迭代器在 MoveNext 进行中不能释放:
    /// 那种情况放到后台,等它结束再释放,不拖住中止。
    /// </summary>
    private async Task DisposeEnumeratorAsync(IAsyncEnumerator<LlmEvent> enumerator, Task<bool>? move)
    {
        if (move is { IsCompleted: false })
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await move.ConfigureAwait(false);
                }
                catch
                {
                    // 在途的 MoveNext 怎么结束都行;这里只是等它。
                }

                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _diagnostics.Warn("Disposing the upstream stream failed.", e);
                }
            }, CancellationToken.None);
            return;
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _diagnostics.Warn("Disposing the upstream stream failed.", e);
        }
    }

    private void Forward(Streaming streaming, LlmEvent ev)
    {
        switch (ev)
        {
            case TextDelta text:
                EnsureStarted(streaming);
                Emit(new MessageDeltaEvent(streaming.Id!.Value, text.Index, MessageDeltaKind.Text, text.Text));
                break;
            case ThinkingDelta thinking:
                EnsureStarted(streaming);
                Emit(new MessageDeltaEvent(streaming.Id!.Value, thinking.Index, MessageDeltaKind.Thinking, thinking.Text));
                break;
            case ToolCallStart start:
                EnsureStarted(streaming);
                Emit(new MessageDeltaEvent(streaming.Id!.Value, start.Index, MessageDeltaKind.ToolArgs, "", start.CallId, start.Name));
                break;
            case ToolCallDelta delta:
                EnsureStarted(streaming);
                Emit(new MessageDeltaEvent(streaming.Id!.Value, delta.Index, MessageDeltaKind.ToolArgs, delta.ArgsDelta, delta.CallId));
                break;
            case ProviderRetry retry:
                Emit(new ProviderRetryEvent(retry.Attempt, retry.DelayMs, retry.Reason));
                break;
            case ProviderSwitch change:
                Emit(new ProviderSwitchEvent(change.From, change.To, change.Reason));
                break;
        }
    }

    /// <summary>
    /// 折成助手消息。出错或中止时没收尾的工具调用块丢弃(不制造孤儿);中止的消息标 Aborted(回放时整条跳过)。
    /// 消息 id 沿用已经发给界面的那个。
    /// </summary>
    private Message BuildAssistant(Streaming streaming, bool aborted)
    {
        var asm = streaming.Asm;
        var message = asm.ToMessage(streaming.Id ?? default, Now());
        if (asm.Error is not null || aborted)
        {
            var cut = asm.PendingToolCalls.Select(c => c.CallId).ToHashSet(StringComparer.Ordinal);
            if (cut.Count > 0)
            {
                message = message with { Parts = message.Parts.Where(p => p is not ToolCallPart c || !cut.Contains(c.CallId)).ToList() };
            }
        }

        if (aborted)
        {
            message = message with { Stop = StopReason.Aborted };
        }

        if (message.Parts.Count > 0 && streaming.Id is null)
        {
            streaming.Id = NewId();
            message = message with { Id = streaming.Id.Value };
        }

        return message;
    }
}
