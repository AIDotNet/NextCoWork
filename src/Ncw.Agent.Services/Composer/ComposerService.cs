using Ncw.Agent.Services.Settings;

namespace Ncw.Agent.Services.Composer;

/// <summary>输入框补全看到的会话(只要这几个字段)。</summary>
public sealed record ComposerSession(string Id, string WorkspaceId, string? ParentSessionId, string Mode, bool IsSkillSource);

/// <summary>一条消息的可见文本和时间(<see cref="CreatedAt"/> 用来判断回复是不是比最新提问还旧)。</summary>
public sealed record ComposerMessage(string Text, long CreatedAt);

public sealed record SuggestionContext(IReadOnlyList<string> RecentUserMessages, string LastAgentText);

public sealed record SuggestInput(string WorkspaceId, string SessionId, ModelPick Model, SuggestionContext Context);

public sealed record InlineContext(string Project, string? Branch, IReadOnlyList<string> Files, IReadOnlyList<string> Commands, IReadOnlyList<string> RecentPrompts, string LastAgentText);

public sealed record InlineInput(string WorkspaceId, string? SessionId, ModelPick Model, InlineContext Context, string Draft);

/// <summary>补全的数据来源(Host 用会话仓库 / 设置 / 目标服务实现;测试用假的)。</summary>
public interface IComposerSource
{
    DeviceSettings Settings();

    ComposerSession? Session(string sessionId);

    bool HasActiveGoal(string sessionId);

    /// <summary>用户亲手发的消息,<b>新的在前</b>。</summary>
    IReadOnlyList<ComposerMessage> RecentSessionPrompts(string sessionId, int limit);

    ComposerMessage? LastVisibleAssistant(string sessionId);

    IReadOnlyList<string> RecentWorkspacePrompts(string workspaceId);
}

/// <summary>补全的模型调用(Host 用路由实现)。失败一律返回空串:补全是便利,不是功能。</summary>
public interface IComposerModel
{
    Task<string> SuggestAsync(SuggestInput input, CancellationToken ct);

    Task<string> CompleteAsync(InlineInput input, CancellationToken ct);
}

public sealed record SuggestRequest(string RequestId, string WorkspaceId, string SessionId, long TriggerKey, string Model, string? ModelProviderId);

public sealed record InlineRequest(string RequestId, string WorkspaceId, string? SessionId, string Draft, string Project, string? Branch, IReadOnlyList<string> Files, IReadOnlyList<string> Commands);

public sealed record ComposerReply(string RequestId, string Text);

/// <summary>
/// L2 的单飞 + 合流(2.x 的 <c>SuggestionJobs</c>):
/// <list type="bullet">
/// <item>同一会话、同一个 triggerKey:<b>合流</b>,所有等待者拿同一个结果(多个控制端、重复请求只花一次钱);</item>
/// <item>同一会话、新的 triggerKey:旧的作废(取消),它的等待者拿到空串;</item>
/// <item>等待者取消:摘掉它;<b>最后一个</b>等待者离开时才真正取消上游。</item>
/// </list>
/// </summary>
public sealed class SuggestionJobs(TimeProvider clock, TimeSpan timeout)
{
    private sealed class Job(long triggerKey, CancellationTokenSource cts)
    {
        public long TriggerKey { get; } = triggerKey;

        public CancellationTokenSource Cts { get; } = cts;

        public HashSet<string> Waiters { get; } = new(StringComparer.Ordinal);

        public Task<string> Result { get; set; } = null!;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _waiterIndex = new(StringComparer.Ordinal);

    public Task<string> RunAsync(string sessionId, long triggerKey, string waiter, Func<CancellationToken, Task<string>> work)
    {
        Job job;
        lock (_gate)
        {
            if (_jobs.TryGetValue(sessionId, out var existing) && existing.TriggerKey == triggerKey && !existing.Cts.IsCancellationRequested)
            {
                existing.Waiters.Add(waiter);
                _waiterIndex[waiter] = sessionId;
                return SettleAsync(existing, waiter);
            }

            if (existing is not null)
            {
                Abort(sessionId, existing);
            }

            var cts = new CancellationTokenSource(timeout, clock);
            job = new Job(triggerKey, cts);
            job.Waiters.Add(waiter);
            _jobs[sessionId] = job;
            _waiterIndex[waiter] = sessionId;
        }

        job.Result = RunWorkAsync(sessionId, job, work);
        return SettleAsync(job, waiter);
    }

    private async Task<string> RunWorkAsync(string sessionId, Job job, Func<CancellationToken, Task<string>> work)
    {
        try
        {
            return await work(job.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ""; // 一切失败都是「没有建议」
        }
        finally
        {
            lock (_gate)
            {
                if (_jobs.TryGetValue(sessionId, out var current) && current == job)
                {
                    _jobs.Remove(sessionId);
                }

                foreach (var w in job.Waiters)
                {
                    if (_waiterIndex.TryGetValue(w, out var s) && s == sessionId)
                    {
                        _waiterIndex.Remove(w);
                    }
                }
            }

            job.Cts.Dispose(); // 超时定时器不留着(IsCancellationRequested 在释放之后仍可读)
        }
    }

    /// <summary>摘掉一个等待者。返回 true = 它确实在等。</summary>
    public bool Cancel(string waiter)
    {
        lock (_gate)
        {
            if (!_waiterIndex.Remove(waiter, out var sessionId))
            {
                return false;
            }

            if (_jobs.TryGetValue(sessionId, out var job))
            {
                job.Waiters.Remove(waiter);
                if (job.Waiters.Count == 0)
                {
                    Abort(sessionId, job);
                }
            }

            return true;
        }
    }

    /// <summary>控制端断开:它所有的等待者一起摘掉。</summary>
    public void DropClient(string clientId)
    {
        List<string> waiters;
        lock (_gate)
        {
            waiters = [.. _waiterIndex.Keys.Where(w => w.StartsWith(clientId + ":", StringComparison.Ordinal))];
        }

        foreach (var waiter in waiters)
        {
            Cancel(waiter);
        }
    }

    private void Abort(string sessionId, Job job)
    {
        job.Cts.Cancel();
        if (_jobs.TryGetValue(sessionId, out var current) && current == job)
        {
            _jobs.Remove(sessionId);
        }
    }

    /// <summary>等待者被摘掉(或整份作废)之后拿到的必须是空串,而不是那份它已经不要的结果。</summary>
    private async Task<string> SettleAsync(Job job, string waiter)
    {
        // job.Result 在 RunAsync 里同步之外才赋值:等到它有了为止(Task.Yield 让出,不会长等:赋值就在下一行)。
        while (job.Result is null)
        {
            await Task.Yield();
        }

        var text = await job.Result.ConfigureAwait(false);
        bool stillWaiting;
        lock (_gate)
        {
            stillWaiting = job.Waiters.Contains(waiter);
        }

        return !stillWaiting || job.Cts.IsCancellationRequested ? "" : text;
    }
}

/// <summary>L3 的单飞:每个(控制端, 会话)一个槽,新请求让旧的立刻作废并拿到空串。</summary>
public sealed class InlineSlots
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string RequestId, CancellationTokenSource Cts)> _slots = new(StringComparer.Ordinal);

    public CancellationTokenSource Acquire(string slot, string requestId, TimeProvider clock, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(slot, out var old))
            {
                old.Cts.Cancel();
            }

            var cts = new CancellationTokenSource(timeout, clock);
            _slots[slot] = (requestId, cts);
            return cts;
        }
    }

    public void Release(string slot, string requestId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(slot, out var entry) && entry.RequestId == requestId)
            {
                _slots.Remove(slot);
            }
        }
    }

    public void DropClient(string clientId)
    {
        lock (_gate)
        {
            foreach (var (slot, entry) in _slots.ToList())
            {
                if (slot.StartsWith(clientId + ":", StringComparison.Ordinal))
                {
                    entry.Cts.Cancel();
                    _slots.Remove(slot);
                }
            }
        }
    }

    public bool Cancel(string clientId, string requestId)
    {
        lock (_gate)
        {
            foreach (var (slot, entry) in _slots.ToList())
            {
                if (slot.StartsWith(clientId + ":", StringComparison.Ordinal) && entry.RequestId == requestId)
                {
                    entry.Cts.Cancel();
                    _slots.Remove(slot);
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// 输入框补全的三条频道(2.x 的 <c>ipc/composer.ts</c>,设计见 docs/composer-autocomplete.md):建议下一步(L2)、历史指令(L1)、打字续写(L3)。
/// ★ <b>一切失败都是「没有建议」</b>:补全是便利,不是功能。这里不抛任何会在输入框上弹出来的错,拿不准就返回空串。
/// ★ <b>设备这一侧再判一次</b>控制端判过的条件(开关、会话类型、目标、密钥):控制端的判断只是省一次往返,真正的口径在这里(方法可以被直接调用)。
/// ★ 依赖全部注入(<see cref="IComposerSource"/> / <see cref="IComposerModel"/> / 时钟),去重、取消、超时这几条最容易写错的时序规则可以直接单测。
/// 控制端断开时 Host 经连接关闭回调调 <see cref="DropClient"/>;漏掉的靠超时(建议 20 秒、续写 4 秒)收掉。
/// </summary>
public sealed class ComposerService(IComposerSource source, IComposerModel model, TimeProvider clock, Action<string>? log = null, TimeSpan? suggestTimeout = null, TimeSpan? inlineTimeout = null)
{
    public const int DraftLimit = 2_000;
    public const int PromptCount = 3;
    public const int PromptLimit = 300;
    public const int AgentTailLimit = 1_500;

    private readonly SuggestionJobs _jobs = new(clock, suggestTimeout ?? TimeSpan.FromSeconds(20));
    private readonly InlineSlots _slots = new();
    private readonly TimeSpan _inlineTimeout = inlineTimeout ?? TimeSpan.FromSeconds(4);

    private static bool ValidId(string? id) => id is { Length: >= 8 and <= 128 };

    /// <summary>能不能为这条会话猜下一句:顶层会话(不是子代理转录)、不是 Skill 提炼会话、不是计划模式、属于这个项目。</summary>
    private static bool Suggestable(ComposerSession? session, string workspaceId) =>
        session is not null && session.WorkspaceId == workspaceId && session.ParentSessionId is null && !session.IsSkillSource && session.Mode != "plan";

    public async Task<ComposerReply> SuggestNextAsync(string clientId, SuggestRequest request)
    {
        var requestId = ValidId(request.RequestId) ? request.RequestId : "";
        var none = new ComposerReply(requestId, "");
        try
        {
            if (requestId.Length == 0 || request.SessionId.Length == 0 || request.WorkspaceId.Length == 0)
            {
                return none;
            }

            var settings = source.Settings();
            if (!settings.PromptSuggestions)
            {
                return none;
            }

            var session = source.Session(request.SessionId);
            if (!Suggestable(session, request.WorkspaceId) || session is null)
            {
                return none;
            }

            // 目标还挂着 = 判定器马上会再踢一轮,这时冒出一句建议只会一闪即逝
            if (source.HasActiveGoal(session.Id))
            {
                return none;
            }

            // 设置里点名了补全模型就用它(整对);否则跟随输入框此刻的模型,不换家
            var pick = settings.Completion.IsSet ? settings.Completion : new ModelPick(request.Model, string.IsNullOrEmpty(request.ModelProviderId) ? null : request.ModelProviderId);
            if (!pick.IsSet)
            {
                return none;
            }

            if (BuildContext(source.RecentSessionPrompts(session.Id, 20), source.LastVisibleAssistant(session.Id)) is not { } context)
            {
                return none;
            }

            var text = await _jobs.RunAsync(session.Id, request.TriggerKey, $"{clientId}:{requestId}",
                ct => model.SuggestAsync(new SuggestInput(session.WorkspaceId, session.Id, pick, context), ct)).ConfigureAwait(false);
            return new ComposerReply(requestId, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"[composer] suggestNext failed: {e.Message}");
            return none;
        }
    }

    /// <summary>
    /// 从库里读出来的消息拼成上下文。返回 null = 这一轮没什么可猜的,不发请求。
    /// ★ 回复比最新那条提问还旧 → null:用户刚发的那句还没有回复落库(或者这一轮根本没产出文字),拿上一轮的回复去猜,猜的是一段过时的对话。
    /// </summary>
    public static SuggestionContext? BuildContext(IReadOnlyList<ComposerMessage> promptsNewestFirst, ComposerMessage? lastAssistant)
    {
        if (lastAssistant is null || lastAssistant.Text.Trim() is not { Length: > 0 } agentText)
        {
            return null;
        }

        if (promptsNewestFirst.Count > 0 && lastAssistant.CreatedAt < promptsNewestFirst[0].CreatedAt)
        {
            return null;
        }

        var recent = promptsNewestFirst
            .Take(PromptCount)
            .Select(m => ComposerText.TakeGraphemes(m.Text.Trim(), PromptLimit))
            .Where(static t => t.Length > 0)
            .Reverse()
            .ToList();
        return new SuggestionContext(recent, ComposerText.LastGraphemes(agentText, AgentTailLimit));
    }

    /// <summary>历史指令(L1):本地补全开着才有;零请求、零成本。</summary>
    public IReadOnlyList<string> History(string workspaceId)
    {
        try
        {
            return source.Settings().LocalCompletion ? source.RecentWorkspacePrompts(workspaceId) : [];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"[composer] history failed: {e.Message}");
            return [];
        }
    }

    public async Task<ComposerReply> CompleteAsync(string clientId, InlineRequest request)
    {
        var requestId = ValidId(request.RequestId) ? request.RequestId : "";
        var none = new ComposerReply(requestId, "");
        if (requestId.Length == 0 || request.WorkspaceId.Length == 0 || request.Draft.Length == 0 || request.Draft.Length > DraftLimit)
        {
            return none;
        }

        var settings = source.Settings();
        // ★ 两个条件都要:开关开着,而且点名了模型——这一层没有「跟随会话」(会话模型多半是大模型,首 token 一两秒,灰字到的时候用户早打过去了)
        if (!settings.InlineCompletion || !settings.InlineCompletionModel.IsSet)
        {
            return none;
        }

        // ★ 草稿像带着密钥就不发给补全模型(隐私底线,设备再判一次)
        if (ComposerText.LooksSecret(request.Draft))
        {
            return none;
        }

        var slot = $"{clientId}:{request.SessionId ?? "-"}";
        using var cts = _slots.Acquire(slot, requestId, clock, _inlineTimeout);
        try
        {
            IReadOnlyList<string> recent = [];
            var lastAgent = "";
            string? sessionId = null;
            if (request.SessionId is { } id && source.Session(id) is { } session && session.WorkspaceId == request.WorkspaceId && session.ParentSessionId is null)
            {
                sessionId = session.Id;
                recent = [.. source.RecentSessionPrompts(session.Id, 3).Select(static m => m.Text.Trim()).Where(static t => t.Length > 0).Reverse()];
                lastAgent = source.LastVisibleAssistant(session.Id)?.Text ?? "";
            }

            var context = new InlineContext(request.Project, string.IsNullOrEmpty(request.Branch) ? null : request.Branch, [.. request.Files.Take(50)], [.. request.Commands.Take(50)], recent, lastAgent);
            var text = await model.CompleteAsync(new InlineInput(request.WorkspaceId, sessionId, settings.InlineCompletionModel, context, request.Draft), cts.Token).ConfigureAwait(false);
            return cts.IsCancellationRequested ? none : new ComposerReply(requestId, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!cts.IsCancellationRequested)
            {
                log?.Invoke($"[composer] complete failed: {e.Message}");
            }

            return none;
        }
        finally
        {
            _slots.Release(slot, requestId);
        }
    }

    /// <summary>控制端断开:它在途的建议 / 续写一起取消(协议层的连接关闭回调触发)。</summary>
    public void DropClient(string clientId)
    {
        _jobs.DropClient(clientId);
        _slots.DropClient(clientId);
    }

    public void Cancel(string clientId, string requestId)
    {
        if (!ValidId(requestId))
        {
            return;
        }

        if (!_jobs.Cancel($"{clientId}:{requestId}"))
        {
            _slots.Cancel(clientId, requestId);
        }
    }
}
