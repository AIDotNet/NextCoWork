using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Core.Domain;

namespace Ncw.Agent.Hooks;

public sealed class LlmHookEvaluatorOptions
{
    public required ILlmClient Llm { get; init; }

    public required Func<ModelRef, ModelBinding?> ResolveModel { get; init; }

    /// <summary>按会话 id 读出当前已落盘的消息转录(含边界;本判定器从最后一个压缩边界之后读)。</summary>
    public required Func<string, IReadOnlyList<Message>> ReadMessages { get; init; }

    /// <summary>按 <c>(sessionId, runId)</c> 解析本次 Run 正在使用的模型(别名 + 供应商成对)。</summary>
    public required Func<string, string, ModelRef?> ActiveModel { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// <c>prompt</c> 型钩子的旁路 LLM 判定器(<see cref="IHookEvaluator"/>)。
/// 与目标判定器共用 Claude Code 同一套停止条件判定系统提示词、按回合边界从后往前裁切转录(0.5 窗口 → <c>context_length</c> → 0.25 窗口)、
/// 无工具、<c>MaxOutputTokens = 1024</c>、<see cref="CompactBinding.AuxiliaryThinking"/>(<c>Off</c> 降级)与容错 JSON 解析。
/// 任何取消 / 超时 / 无模型 / 解析失败都收敛为 <see cref="EvaluationKind.Skipped"/>(fail-open)。
/// </summary>
public sealed partial class LlmHookEvaluator(LlmHookEvaluatorOptions options) : IHookEvaluator
{
    private const double TranscriptBudgetRatio = 0.5;
    private const double TranscriptRetryRatio = 0.25;
    private const int AssumedLargeWindow = 1_000_000;
    public const int MaxOutputTokens = 1024;

    /// <summary>★ 逐字来自 Claude Code 2.1.259。同时触发回放测试里对判定路由的系统前缀识别。</summary>
    public static readonly string EvaluatorSystem = string.Join('\n',
    [
        "You are evaluating a stop-condition hook in Claude Code. Read the conversation transcript carefully, then judge whether the user-provided condition is satisfied.",
        "Your response must be a JSON object with one of these shapes:",
        "- {\"ok\": true, \"reason\": \"<quote evidence from the transcript that satisfies the condition>\"}",
        "- {\"ok\": false, \"reason\": \"<quote what is missing or what blocks the condition>\"}",
        "- {\"ok\": false, \"impossible\": true, \"reason\": \"<explain why the condition can never be satisfied>\"}",
        "Always include a \"reason\" field, quoting specific text from the transcript whenever possible. If the transcript does not contain clear evidence that the condition is satisfied, return {\"ok\": false, \"reason\": \"insufficient evidence in transcript\"}.",
        "Only use {\"ok\": false, \"impossible\": true} when the condition is genuinely unachievable in this session — for example: the condition is self-contradictory, it depends on a resource or capability that is unavailable, or the assistant has explicitly tried, exhausted reasonable approaches, and stated it cannot be done. Apply your own judgment when deciding this — the assistant claiming the goal is impossible is evidence, not proof; independently confirm the condition is genuinely unachievable rather than deferring to the assistant's self-assessment. Do not use it just because the goal has not been reached yet or because progress is slow. When in doubt, return {\"ok\": false} without \"impossible\".",
    ]);

    private abstract record Attempt
    {
        public sealed record Done(EvaluationVerdict Verdict) : Attempt;

        public sealed record RetrySmaller : Attempt;
    }

    private sealed record AttemptControl(CancellationToken Token, CancellationToken Caller, Func<bool> TimedOut);

    public async ValueTask<EvaluationVerdict> EvaluateAsync(EvaluationRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "cancelled");
        }

        var model = options.ActiveModel(request.SessionId, request.RunId);
        if (model is null || string.IsNullOrWhiteSpace(model.Alias))
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "no_model");
        }

        var binding = options.ResolveModel(model);
        if (binding is null)
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "no_model");
        }

        var rawMessages = options.ReadMessages(request.SessionId);
        var messages = SliceSinceLastBoundary(rawMessages);
        if (messages.Count == 0)
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "transcript_empty");
        }

        var window = Math.Min(binding.ContextWindow > 0 ? binding.ContextWindow : AssumedLargeWindow, AssumedLargeWindow);
        var thinking = CompactBinding.AuxiliaryThinking(ThinkingLevel.Off, binding);
        var maxOutput = Math.Max(1, Math.Min(MaxOutputTokens, window));

        using var timeout = new CancellationTokenSource(request.Timeout < TimeSpan.Zero ? TimeSpan.Zero : request.Timeout, options.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var control = new AttemptControl(linked.Token, ct, () => timeout.IsCancellationRequested);

        var first = await RunOnceAsync(request, model, messages, TranscriptBudget(window, TranscriptBudgetRatio, request.Question, maxOutput), control, thinking, maxOutput).ConfigureAwait(false);
        if (first is Attempt.Done firstDone)
        {
            return firstDone.Verdict;
        }

        if (control.Token.IsCancellationRequested)
        {
            return AbortOrTimeout(control);
        }

        var second = await RunOnceAsync(request, model, messages, TranscriptBudget(window, TranscriptRetryRatio, request.Question, maxOutput), control, thinking, maxOutput).ConfigureAwait(false);
        return second is Attempt.Done secondDone ? secondDone.Verdict : new EvaluationVerdict(EvaluationKind.Skipped, "error");
    }

    private async Task<Attempt> RunOnceAsync(
        EvaluationRequest request, ModelRef model, IReadOnlyList<Message> messages, int budgetTokens, AttemptControl control, ThinkingLevel thinking, int maxOutput)
    {
        var selection = SelectTranscript(messages, budgetTokens);
        if (selection.KeptLines == 0)
        {
            return new Attempt.Done(new EvaluationVerdict(EvaluationKind.Skipped, "transcript_empty"));
        }

        var now = options.Clock.GetUtcNow();
        var prompt = $"{selection.Text}\n\n{request.Question}";
        var message = new Message(new MessageId($"{request.RunId}:hook-evaluate"), Role.User, [new TextPart(prompt)], now);
        var llmRequest = new LlmRequest(EvaluatorSystem, [message], [], new GenerationOptions(maxOutput, thinking));
        var ctx = new RequestContext(request.WorkspaceId, request.RunId, request.SessionId);

        var text = new StringBuilder();
        var ended = false;
        try
        {
            await foreach (var ev in Abortable(options.Llm.StreamAsync(model, llmRequest, ctx, control.Token), control.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case MessageEnd end:
                        ended = end.Stop == StopReason.EndTurn;
                        break;
                    case StreamError error:
                        return error.Error.Code == AgentErrorCode.ContextLength
                            ? new Attempt.RetrySmaller()
                            : new Attempt.Done(new EvaluationVerdict(EvaluationKind.Skipped, "error"));
                }
            }
        }
        catch (Exception)
        {
            return new Attempt.Done(AbortOrTimeout(control));
        }

        if (control.Token.IsCancellationRequested)
        {
            return new Attempt.Done(AbortOrTimeout(control));
        }

        return ended
            ? new Attempt.Done(ParseVerdict(text.ToString()))
            : new Attempt.Done(new EvaluationVerdict(EvaluationKind.Skipped, "error"));
    }

    private static int TranscriptBudget(int window, double ratio, string question, int maxOutput)
    {
        var reserved = TokenEstimate.OfText(EvaluatorSystem) + TokenEstimate.OfText(question) + maxOutput;
        return Math.Max(0, (int)Math.Floor(window * ratio) - reserved);
    }

    private static EvaluationVerdict AbortOrTimeout(AttemptControl control)
    {
        if (control.Caller.IsCancellationRequested)
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "cancelled");
        }

        return control.TimedOut()
            ? new EvaluationVerdict(EvaluationKind.Skipped, "timeout")
            : new EvaluationVerdict(EvaluationKind.Skipped, "error");
    }

    internal static EvaluationVerdict ParseVerdict(string text)
    {
        var payload = ExtractJson(text);
        if (payload is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new EvaluationVerdict(EvaluationKind.Skipped, "parse_error");
        }

        string? reason = null;
        if (obj.TryGetProperty("reason", out var r))
        {
            if (r.ValueKind != JsonValueKind.String)
            {
                return new EvaluationVerdict(EvaluationKind.Skipped, "parse_error");
            }

            reason = r.GetString();
        }

        var impossible = false;
        if (obj.TryGetProperty("impossible", out var imp))
        {
            if (imp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new EvaluationVerdict(EvaluationKind.Skipped, "parse_error");
            }

            impossible = imp.GetBoolean();
        }

        var trimmed = reason?.Trim() ?? "";
        if (ok.GetBoolean())
        {
            return new EvaluationVerdict(EvaluationKind.Met, trimmed);
        }

        return new EvaluationVerdict(impossible ? EvaluationKind.Impossible : EvaluationKind.NotMet, trimmed);
    }

    private static JsonElement? ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var candidates = new List<string> { trimmed };
        try
        {
            if (Fenced().Match(trimmed) is { Success: true } fenced)
            {
                candidates.Add(fenced.Groups[1].Value.Trim());
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            candidates.Add(trimmed[start..(end + 1)]);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Fenced();

    private static IReadOnlyList<Message> SliceSinceLastBoundary(IReadOnlyList<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Parts.Any(static p => p is CompactBoundaryPart))
            {
                return messages.Skip(i).ToList();
            }
        }

        return messages;
    }

    private sealed record Turn(List<string> Lines, int Messages);

    private sealed record Selection(string Text, int KeptLines);

    private static Selection SelectTranscript(IReadOnlyList<Message> messages, int budgetTokens)
    {
        var candidates = Turns(messages).Where(t => t.Lines.Count > 0).ToList();
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

        var prefix = $"[Earlier conversation truncated to fit the hook evaluator's context window — {omitted} earlier messages omitted. Evaluate the condition against the recent transcript below; if the required evidence may be in the omitted prefix, return {{\"ok\": false, \"reason\": \"insufficient evidence in transcript\"}}.]";
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
            if (body.Length > 0)
            {
                lines.Add($"{(messages[i].Role == Role.User ? "user" : "assistant")}: {body}");
            }
        }

        return new Turn(lines, to - from);
    }

    private static bool StartsTurn(Message message) =>
        message.Role == Role.User && message.Parts.Any(p => p is not ToolResultPart && PartBody(p).Length > 0);

    private static string BodyOf(Message message) =>
        string.Join('\n', message.Parts.Select(PartBody).Where(s => s.Length > 0));

    private static string PartBody(Part part) => part switch
    {
        TextPart t => t.Text,
        ToolCallPart c => $"{c.Name} {SafeJson(c.Input)}",
        ToolResultPart r => $"{r.CallId} {(r.IsError ? "[error]" : "[ok]")} {r.Output.Content}",
        FileRefPart f => f.Path,
        _ => "",
    };

    private static string SafeJson(JsonElement input) => input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();

    private static async IAsyncEnumerable<T> Abortable<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
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
