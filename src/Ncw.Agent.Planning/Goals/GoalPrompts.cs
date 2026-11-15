namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// 目标机制发给模型的全部文案常量(2.x <c>goal/prompt.ts</c>)。每一条都进模型上下文:判定器的 system / user、续跑注入、kickoff、check-in。
/// 它们的行为是被调过的——<c>"insufficient evidence in transcript"</c> 这句话在英文 prompt 里的权重,换成中文就是另一个判定器。所以全是英文常量,<b>不是</b>界面文字
/// (界面上的药丸、状态行、卡片、通知由控制端按结构化数据翻译,一个字都不在这里)。纯常量 + 纯函数,零 IO:判定的正确性几乎全在措辞上,所以逐行单测。
/// </summary>
public static class GoalPrompts
{
    /// <summary>
    /// 判定器的 system。★ 逐字来自 Claude Code 2.1.259。改动它等于改判定器的行为,不要顺手润色。
    /// </summary>
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

    /// <summary>判定器的 user。转录正文拼在它<b>前面</b>。</summary>
    public static string EvaluatorQuestion(string condition) => string.Join('\n',
    [
        "Based on the conversation transcript above, has the following stopping condition been satisfied? Answer based on transcript evidence only.",
        $"Condition: {condition}",
    ]);

    /// <summary>
    /// 转录被预算裁掉一段时补的那条前缀。★★ 这不是客套话,它是<b>判定正确性的一部分</b>:不写的话,被裁掉的那段历史会让判定器把「我看不到」读成「没发生过」,
    /// 于是稳定地误判未达成——而那正好是一个长会话最容易触发的路径。逐字来自 CC。
    /// </summary>
    public static string TruncatedTranscriptPrefix(int omitted) =>
        $"[Earlier conversation truncated to fit the hook evaluator's context window — {omitted} earlier messages omitted. Evaluate the condition against the recent transcript below; if the required evidence may be in the omitted prefix, return {{\"ok\": false, \"reason\": \"insufficient evidence in transcript\"}}.]";

    /// <summary>续跑注入的前缀。★ 它同时是<b>格式</b>也是<b>锚点</b>:诊断和「这条是我们自己注入的」都按它认,所以只在这里出现一次。</summary>
    public const string StopHookFeedbackPrefix = "Stop hook feedback:";

    /// <summary>判未达成时发给主模型的那条 internal 消息。格式与 CC 一致。</summary>
    public static string ContinuationMessage(string condition, string reason) => $"{StopHookFeedbackPrefix}\n[{condition}]: {reason}";

    /// <summary>目标生效时发给主模型的那条 internal 消息。</summary>
    public static string KickoffMessage(string condition) =>
        $"A session-scoped Stop hook is now active with condition: \"{condition}\". Briefly acknowledge the goal, then immediately start (or continue) working toward it — treat the condition itself as your directive and do not pause to ask the user what to do. The hook will block stopping until the condition holds. It auto-clears once the condition is met — do not tell the user to run /goal clear after success; that's only for clearing a goal early.";

    private const string IdleSummarySuffix = " · idle check-ins paused until your next message";
    private const string IdleBodySuffix = " NextCoWork won't wake this session for another check-in until the user sends a message, so say clearly where things stand.";

    /// <summary>
    /// 后台推迟的 check-in 正文。两种分支照搬 CC:后台还在跑 → 让模型去看它们的进度;后台已经不在了 → 直接继续。<c>«»</c> 是 CC 用来包住条件原文的定界符,保留。
    /// <paramref name="final"/> = 这是空闲唤醒的最后一次,追加「不再自动唤醒」那两句。
    /// </summary>
    public static string CheckinMessage(string condition, double deferredMinutes, IReadOnlyList<BackgroundTaskLine> tasks, bool final)
    {
        var minutes = Math.Max(1, (int)Math.Round(deferredMinutes, MidpointRounding.AwayFromZero));
        if (tasks.Count > 0)
        {
            var summary = $"Goal check-in: «{condition}» is still active, and evaluation has been deferred for {minutes} min because background work is still running:";
            var list = string.Join('\n', tasks.Select(task => $"- {task.TaskId} · {task.Type} · {task.Description}"));
            const string body = "Check on their progress (e.g. read their output). If they are progressing, say so briefly and keep waiting; if they are stuck or no longer needed, fix or stop them and continue toward the goal.";
            return string.Join('\n', [final ? summary + IdleSummarySuffix : summary, list, "", final ? body + IdleBodySuffix : body]);
        }

        var head = $"Goal check-in: «{condition}» is still active.";
        var text = $"Its evaluation was deferred for {minutes} min while background work ran, and that work is no longer running (it finished or was stopped without reporting back). Continue toward the goal.";
        return final ? $"{head}{IdleSummarySuffix}\n{text}{IdleBodySuffix}" : $"{head}\n{text}";
    }

    /// <summary>
    /// 转录里的一行。★ 只保留判定用得上的几类内容:<c>thinking</c> <b>不进</b>——它是模型的草稿,一段「我觉得应该已经好了」会被判定器当成证据,而那恰恰是最不该采信的证据。
    /// </summary>
    public static string TranscriptLine(string role, string body) => $"{role}: {body}";
}

/// <summary>check-in 里列出的一个后台任务。</summary>
public sealed record BackgroundTaskLine(string TaskId, string Type, string Description);
