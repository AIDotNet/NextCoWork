using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>一次判定的输入。<see cref="Model"/> 空 = 回落到 <see cref="Fallback"/>(本 Run 的模型):模型和供应商是<b>成对</b>的,回落了模型就必须同时回落供应商。</summary>
public sealed record EvaluateGoalInput(
    ILlmClient Llm,
    ModelRef? Model,
    ModelRef Fallback,
    string Question,
    IReadOnlyList<Message> Messages,
    RequestContext Context,
    Func<ModelRef, ModelBinding?> ResolveModel,
    TimeSpan Timeout,
    TimeProvider? Clock = null);

/// <summary>
/// 判定调用(2.x <c>goal/evaluate.ts</c>):一次<b>无工具、禁思考、只读对话</b>的旁路模型请求(形状照抄压缩的旁路调用)。
/// <para>
/// ★ 任何失败都收敛成 <see cref="GoalVerdict.Skipped"/>,<b>不</b>收敛成 <c>NotMet</c>:把工具的故障算在用户的目标头上,会让一次供应商抖动变成一条「你的目标没达成」。
/// </para>
/// <para>三条正确性硬约束(都不是风格问题):</para>
/// <list type="number">
/// <item><b>一次判定只有一个墙钟。</b>两次尝试(0.5 窗口 → <c>context_length</c> → 0.25 窗口)共用同一个取消令牌和同一个定时器。各给一个的话,最坏要等两倍超时,而它挂在回合末——用户看到的只是「这一轮就是不结束」。</item>
/// <item><b>只有完整的 <c>end_turn</c> 才算一次回复。</b>手上那段 JSON 可能是写完的,但流断在半路、或止于 <c>max_tokens</c> / <c>tool_use</c> 时,那是一个被打断的回答;拿它当结论与「拿被中断的证据判达成」是同一件事。</item>
/// <item><b>中断之后绝不返回 <c>Met</c>。</b>判定器的全部价值在于它独立确认了一遍;中断让这一点不再成立。宁可 <c>Skipped</c>,也不让用户那一下「停止」变成一条「目标已达成」。</item>
/// </list>
/// </summary>
public static class GoalEvaluator
{
    /// <summary>判定器读多少上下文:模型窗口 × 这个比例。窗口未知时按 1e6 当大窗口算。</summary>
    private const double TranscriptBudgetRatio = 0.5;

    /// <summary>第一次裁完仍然 <c>context_length</c> 时的重试比例。</summary>
    private const double TranscriptRetryRatio = 0.25;

    private const int AssumedLargeWindow = 1_000_000;

    /// <summary>判定器只要一个 JSON 对象,1K 绰绰有余;给多了只会让它写小作文。</summary>
    public const int MaxOutputTokens = 1024;

    private abstract record Attempt
    {
        public sealed record Done(GoalVerdict Verdict) : Attempt;

        public sealed record RetrySmaller : Attempt;
    }

    public static async Task<GoalVerdict> EvaluateAsync(EvaluateGoalInput input, CancellationToken ct)
    {
        // ★ 模型和供应商是成对的:回落了模型就必须同时回落 providerId,否则请求会带着「本次 Run 的模型 + 判定模型那家的 providerId」出去。
        var model = input.Model is { Alias: var alias } && !string.IsNullOrWhiteSpace(alias) ? input.Model with { Alias = alias.Trim() } : input.Fallback;
        if (string.IsNullOrWhiteSpace(model.Alias))
        {
            return new GoalVerdict.Skipped(GoalSkipReason.NoModel);
        }

        var binding = input.ResolveModel(model);
        if (binding is null)
        {
            return new GoalVerdict.Skipped(GoalSkipReason.NoModel);
        }

        if (input.Messages.Count == 0)
        {
            return new GoalVerdict.Skipped(GoalSkipReason.TranscriptEmpty);
        }

        // ★ 已经中断的调用一次请求都不发。
        if (ct.IsCancellationRequested)
        {
            return new GoalVerdict.Skipped(GoalSkipReason.Error);
        }

        var window = Math.Min(binding.ContextWindow > 0 ? binding.ContextWindow : AssumedLargeWindow, AssumedLargeWindow);
        // 判定器要的是一个 JSON 结论,能不思考就不思考(快、便宜)。但不能硬发 off:关不掉推理的模型会把它拒成不可重试的错——表现是每个回合末的判定全部 skipped,
        // 而用户看到的只是「目标永远判不出来」。AuxiliaryThinking 在关不掉的模型上降到最低可用档,绝不抛(压缩、权限审核、会话标题共用这一个规则)。
        var thinking = CompactBinding.AuxiliaryThinking(ThinkingLevel.Off, binding);
        var maxOutput = Math.Max(1, Math.Min(MaxOutputTokens, window));

        using var timeout = new CancellationTokenSource(input.Timeout < TimeSpan.Zero ? TimeSpan.Zero : input.Timeout, input.Clock ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var control = new AttemptControl(linked.Token, ct, () => timeout.IsCancellationRequested);

        var first = await RunOnceAsync(input, model, TranscriptBudget(window, TranscriptBudgetRatio, input.Question, maxOutput), control, thinking, maxOutput).ConfigureAwait(false);
        if (first is Attempt.Done firstDone)
        {
            return firstDone.Verdict;
        }

        // ★ 裁到一半仍然 context_length:不是「历史太长」,是我们的估算偏低(长 CJK、大段 JSON 工具输出都会把字符/词元比拉低)。按 0.25 再来一次,仍然不行就认输——
        // 第三次只会是同一个结果,而每一次都是一次真实的上游请求。
        // ★ 第二次之前先看墙钟:定时器可能就在这两次之间到点了,而那时 stream 是一次注定被丢弃的请求,一个字节都不该发出去。
        if (control.Token.IsCancellationRequested)
        {
            return AbortOrError(control);
        }

        var second = await RunOnceAsync(input, model, TranscriptBudget(window, TranscriptRetryRatio, input.Question, maxOutput), control, thinking, maxOutput).ConfigureAwait(false);
        return second is Attempt.Done secondDone ? secondDone.Verdict : new GoalVerdict.Skipped(GoalSkipReason.Error);
    }

    /// <summary>
    /// 这次判定能给转录多少 token。★ system / question / 输出<b>都要从窗口里先扣掉</b>:它们和转录抢的是同一个窗口,漏扣的那部分会以「上游报 context_length」的形式回来——而那正好触发一次白白多打的上游请求。
    /// </summary>
    private static int TranscriptBudget(int window, double ratio, string question, int maxOutput)
    {
        var reserved = TokenEstimate.OfText(GoalPrompts.EvaluatorSystem) + TokenEstimate.OfText(question) + maxOutput;
        return Math.Max(0, (int)Math.Floor(window * ratio) - reserved);
    }

    /// <summary>两次尝试共用的那点状态:同一个令牌(调用方的中断 + 墙钟),和「到点了没有」。</summary>
    private sealed record AttemptControl(CancellationToken Token, CancellationToken Caller, Func<bool> TimedOut);

    private static async Task<Attempt> RunOnceAsync(
        EvaluateGoalInput input, ModelRef model, int budgetTokens, AttemptControl control, ThinkingLevel thinking, int maxOutput)
    {
        var selection = GoalTranscript.Select(input.Messages, budgetTokens);
        // ★ 预算里一条证据都留不下时不发请求:发出去的那份会是一条只有问题、没有转录的判定——判定器对它的回答必然是「证据不足」,而那在 Stop 上表现为「这一轮永远停不下来」。
        if (selection.KeptLines == 0)
        {
            return new Attempt.Done(new GoalVerdict.Skipped(GoalSkipReason.TranscriptEmpty));
        }

        var now = (input.Clock ?? TimeProvider.System).GetUtcNow();
        var prompt = $"{selection.Text}\n\n{input.Question}";
        var message = new Message(new MessageId($"{input.Context.RunId}:goal-evaluate"), Role.User, [new TextPart(prompt)], now);
        var request = new LlmRequest(GoalPrompts.EvaluatorSystem, [message], [], new GenerationOptions(maxOutput, thinking));

        var text = new StringBuilder();
        var ended = false;
        try
        {
            // ★ 上游未必认令牌(假流、某些 SDK)。Abortable 补上这一层:即便生成器完全不看令牌,超时 / 中断也能立刻从这里脱身。
            await foreach (var ev in Abortable(input.Llm.StreamAsync(model, request, input.Context, control.Token), control.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case MessageEnd end:
                        // 只有完整的 end_turn 才算一次可判的回复
                        ended = end.Stop == StopReason.EndTurn;
                        break;
                    case StreamError error:
                        return error.Error.Code == AgentErrorCode.ContextLength
                            ? new Attempt.RetrySmaller()
                            : new Attempt.Done(new GoalVerdict.Skipped(GoalSkipReason.Error));
                }
            }
        }
        catch (Exception)
        {
            // 中断、超时、上游抛错——三种都收敛成 Skipped,绝无可能是 Met。
            return new Attempt.Done(AbortOrError(control));
        }

        // ★ 收官前最后一道闸:流跑完了、JSON 也拿到了,但调用方在这中间中断了(或墙钟到点了)。这时候那份材料是完整的、结论却已经不该有了——
        // 谁先发生都不能算数,因为判定结果最终会变成「目标已达成」这类不可撤销的陈述。
        if (control.Token.IsCancellationRequested)
        {
            return new Attempt.Done(AbortOrError(control));
        }

        // 没有 MessageEnd,或止于 max_tokens / tool_use / refusal:手上那段 JSON 可能恰好是完整的,但那次回答没有结束。
        // 判定器没有工具,出现 tool_use 只说明上游没照契约走;被截断的回答同样不能当证据。
        return ended
            ? new Attempt.Done(GoalVerdicts.Parse(text.ToString()))
            : new Attempt.Done(new GoalVerdict.Skipped(GoalSkipReason.Error));
    }

    /// <summary>一次失败该记成什么。超时与「调用方中断」必须分开:前者是「对面没反应」(用户据此去查那一家的连通性);后者是用户按了停止,不是故障。</summary>
    private static GoalVerdict AbortOrError(AttemptControl control)
    {
        if (control.Caller.IsCancellationRequested)
        {
            return new GoalVerdict.Skipped(GoalSkipReason.Error);
        }

        return control.TimedOut() ? new GoalVerdict.Skipped(GoalSkipReason.Timeout) : new GoalVerdict.Skipped(GoalSkipReason.Error);
    }

    /// <summary>
    /// 让一个可能不看令牌的异步流也能被取消:每次取下一个元素都和令牌竞速。取消时<b>不释放</b>枚举器(对一个卡在 await 里的异步迭代器调 <c>DisposeAsync</c> 是未定义行为),直接放弃它。
    /// </summary>
    internal static async IAsyncEnumerable<T> Abortable<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        var finished = false;
        try
        {
            for (;;)
            {
                var next = enumerator.MoveNextAsync().AsTask();
                var winner = await Task.WhenAny(next, cancelled.Task).ConfigureAwait(false);
                if (winner != next)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (!await next.ConfigureAwait(false))
                {
                    finished = true;
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            if (finished)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>转录 → 判定器读的那段纯文本,按预算从后往前裁(2.x <c>renderTranscript</c>)。</summary>
public static class GoalTranscript
{
    /// <param name="Text">拼好的正文(含截断前缀)。预算里一条证据都留不下时只剩那条前缀。</param>
    /// <param name="KeptLines">留下来的证据<b>行</b>数。0 = 这次判定没有可读的证据,调用方据此直接 skipped。</param>
    public sealed record Selection(string Text, int KeptLines);

    /// <summary>一个回合:从一条「带非 tool_result 语义块的 user 消息」开始,到下一个这样的消息为止。</summary>
    private sealed record Turn(List<string> Lines, int Messages);

    public static string Render(IReadOnlyList<Message> messages, int budgetTokens) => Select(messages, budgetTokens).Text;

    /// <summary>
    /// ★ 裁掉的部分<b>替换成一条显式前缀</b>,不能静默丢(见 <see cref="GoalPrompts.TruncatedTranscriptPrefix"/>)。
    /// ★ 按<b>回合</b>边界裁,不按消息、更不按字符:从一条 tool_result 的中间切一刀,留下的半截 JSON 比不留更糟(判定器会把它当成一次失败的工具调用)。
    /// ★★ 最新那一个回合<b>自己</b>就装不下时,留下的停在空——整份转录都不发。这是刻意的:只发更旧的回合,等于让判定器拿着一份缺了最新一轮的证据去判「达成」,
    /// 而那条前缀说的是「更早的那些被裁掉了」,方向正好相反。宁可这次不判,也不给一份误导性的材料。
    /// </summary>
    public static Selection Select(IReadOnlyList<Message> messages, int budgetTokens)
    {
        var candidates = Turns(messages).Where(t => t.Lines.Count > 0).ToList();
        // 整条转录里没有一个可读的块(UI-only 的 thinking / goal_status 不算)
        if (candidates.Count == 0)
        {
            return new Selection("", 0);
        }

        var budget = Math.Max(0, budgetTokens);
        var total = messages.Count;
        var kept = new List<Turn>();
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var next = new List<Turn> { candidates[i] };
            next.AddRange(kept);
            // 前缀本身也占预算(estimate 算的是最终发出去的那整段):不算它,一条刚好卡在预算上的转录会多出近百个 token——而超发的代价是一次 context_length 重试。
            // 从后往前贪心,一遇到装不下就停:留下的因此永远是「最近的若干个完整回合」。
            var keptAll = next.Count == candidates.Count;
            var omitted = keptAll ? 0 : total - next.Sum(t => t.Messages);
            if (TokenEstimate.OfText(Compose(next, omitted)) > budget)
            {
                break;
            }

            kept = next;
        }

        var allKept = kept.Count == candidates.Count;
        var omittedCount = allKept ? 0 : total - kept.Sum(t => t.Messages);
        return new Selection(Compose(kept, omittedCount), kept.Sum(t => t.Lines.Count));
    }

    private static string Compose(IReadOnlyList<Turn> turns, int omitted)
    {
        var body = string.Join('\n', turns.SelectMany(t => t.Lines));
        if (omitted <= 0)
        {
            return body;
        }

        var prefix = GoalPrompts.TruncatedTranscriptPrefix(omitted);
        return body.Length == 0 ? prefix : $"{prefix}\n{body}";
    }

    private static List<Turn> Turns(IReadOnlyList<Message> messages)
    {
        var starts = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (StartsTurn(messages[i]))
            {
                starts.Add(i);
            }
        }

        var turns = new List<Turn>();
        // ★ 第一条回合起点之前的内容(通常是些没配对上的 tool_result)自成一组,但它不是一个回合边界——保留后缀永远不从它开始,
        // 否则截出来的第一行就是一条「没人调用的工具输出」,判定器会把它读成一次失败。
        if (starts.Count == 0 || starts[0] != 0)
        {
            turns.Add(Collect(messages, 0, starts.Count > 0 ? starts[0] : messages.Count));
        }

        for (var k = 0; k < starts.Count; k++)
        {
            turns.Add(Collect(messages, starts[k], k + 1 < starts.Count ? starts[k + 1] : messages.Count));
        }

        return turns;
    }

    private static Turn Collect(IReadOnlyList<Message> messages, int from, int to)
    {
        var lines = new List<string>();
        for (var i = from; i < to; i++)
        {
            var body = BodyOf(messages[i]);
            // 只有 UI-only 的块(thinking / goal_status)的消息不占行,也不占预算
            if (body.Length > 0)
            {
                lines.Add(GoalPrompts.TranscriptLine(messages[i].Role == Role.User ? "user" : "assistant", body));
            }
        }

        return new Turn(lines, to - from);
    }

    /// <summary>
    /// 一条消息是不是一个<b>回合的起点</b>。★ 判据不能只是 <c>role == user</c>:工具回执也是 user 消息。把它们当成回合起点,裁剪就会从一条孤立的 tool_result 切起——而那正是要避免的那件事。
    /// </summary>
    private static bool StartsTurn(Message message) =>
        message.Role == Role.User && message.Parts.Any(p => p is not ToolResultPart && PartBody(p).Length > 0);

    private static string BodyOf(Message message) =>
        string.Join('\n', message.Parts.Select(PartBody).Where(s => s.Length > 0));

    /// <summary>
    /// 一个内容块的正文。★ tool_result 带上 <b>callId 与错误状态</b>:判定器要能把这行输出归因到那一次调用上。只给一段裸输出的话,一次失败的工具调用和一次成功的看起来一模一样,
    /// 而「<c>bun test</c> 报了错」和「<c>bun test</c> 全绿」是两个相反的结论。<c>thinking</c> / <c>goal_status</c>(我们自己盖的章)/ 图片 / 错误块都不进。
    /// 后台子代理的汇报全文已经在同一条消息的文字块里,所以汇报标记块不再单独取摘要(2.x 的 subagent 块是独立的)。
    /// </summary>
    private static string PartBody(Part part) => part switch
    {
        TextPart t => t.Text,
        ToolCallPart c => $"{c.Name} {SafeJson(c.Input)}",
        ToolResultPart r => $"{r.CallId} {(r.IsError ? "[error]" : "[ok]")} {r.Output.Content}",
        FileRefPart f => f.Path,
        _ => "",
    };

    private static string SafeJson(JsonElement input) => input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
}
