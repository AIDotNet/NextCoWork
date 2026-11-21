using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ncw.Agent.Catalog;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>子 Run 结束了(<c>SubagentStop</c> Hook 的触发点;Hooks 扩展订阅 <see cref="SubagentService.Stopped"/>)。</summary>
public sealed record SubagentStopped(string ParentSessionId, RunId ParentRunId, string CallId, RunId ChildRunId, string ChildSessionId, string AgentType, RunStatus Status, bool Background);

public sealed class SubagentServiceDeps
{
    public required SessionRepository Sessions { get; init; }

    /// <summary>和 Host 装配顶层 Run 用的是同一个工厂:设置快照、模式提示词都由它给,子代理只在其上改深度和角色提示词。</summary>
    public required IRunRequestFactory Requests { get; init; }

    public required AgentCatalog Agents { get; init; }

    public ISubagentSettingsSource Settings { get; init; } = new FixedSubagentSettings();

    public ISubagentModels Models { get; init; } = PassThroughSubagentModels.Instance;

    /// <summary>权限状态登记表。null = 不接权限(纯内核测试);Host 必须给,否则权限扩展对子代理的每一次工具调用都是「没有登记 → 拒绝」。</summary>
    public PermissionRuns? PermissionRuns { get; init; }

    /// <summary>交互闸门:给子 Run 开一个 <c>depth &gt; 0</c> 的作用域(不能提问)。</summary>
    public InteractionGate? Gate { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    /// <summary>工作区 id → (执行环境, 项目根);<c>isolation: worktree</c> 在该项目根上创建临时 git worktree。</summary>
    public Func<string, (IExecutionEnvironment Environment, EnvPath Root)?>? ResolveProject { get; init; }
}

/// <summary>
/// 子代理编排(2.x <c>runtime.ts</c> 的 <c>spawnSubagentFor</c> / <c>childRequestFor</c> / <c>monitorChildRun</c> + <c>session-runtime.ts</c> 的汇报部分),05 §5。
/// 它同时是三个角色:<see cref="ISubagentLauncher"/>(<c>Task</c> 工具的窄缝)、<see cref="IRunExtension"/>(子 Run 的权限 / 交互登记与清理、名额释放通知、父会话 settle 后补写回执)、
/// <see cref="ISlotOccupancy"/>(谁在占着名额)。因为 <see cref="RunRegistry"/> 在构造时就要拿到扩展列表,而本类又要拿注册表,所以先造本类、造完注册表再 <see cref="Bind"/>。
/// <para><b>启动</b>(<see cref="SpawnAsync"/>):</para>
/// <list type="number">
/// <item>深度检查(<see cref="SubagentLimits.MaxDepth"/> = 2)。</item>
/// <item>未知的 Agent 名称 → 拒绝,并列出可用的 Agent。<b>不悄悄回退</b>到通用 Agent:名字敲错的用户会拿到一份「看起来对」的、由错误代理产出的结果,而且永远不会发现。</item>
/// <item>子 RunId <c>&lt;父 RunId&gt;:sub:&lt;N&gt;</c>;子会话 <c>&lt;父会话&gt;:sub:&lt;子 RunId&gt;</c>,<c>parent_session_id</c> 记真实父会话(永远不解析 id)。</item>
/// <item>子请求:深度 +1,模式 <c>code</c>,权限 <c>min(父实时值, 定义值)</c>,「最大上下文」继承,联网开关跟随父(权限状态上)。</item>
/// <item>进入子代理队列(<see cref="SubagentQueue"/>):名额到手才建 Run。</item>
/// </list>
/// 子 Run 结束后<b>等驱动真正结束</b>(<c>RunHandle.Completion</c>,而不只是 <c>run_end</c>——历史在 <c>run_end</c> 之后才写),再读子会话最后一条助手消息,更新父会话里的持久回执,发 <c>subagent_end</c>,通知 <see cref="Stopped"/>。
/// </summary>
public sealed class SubagentService : ISubagentLauncher, IRunExtension, ISlotOccupancy, IDisposable
{
    internal sealed class ChildRun(RunId id, RunId parentRunId, string parentSessionId, string workspaceId, string callId, string childSessionId, AgentDefinition definition, string description, PermissionMode definedMode)
    {
        public RunId Id { get; } = id;

        public RunId ParentRunId { get; } = parentRunId;

        public string ParentSessionId { get; } = parentSessionId;

        public string WorkspaceId { get; } = workspaceId;

        public string CallId { get; } = callId;

        public string ChildSessionId { get; } = childSessionId;

        public AgentDefinition Definition { get; } = definition;

        public string Description { get; } = description;

        public PermissionMode DefinedMode { get; } = definedMode;

        /// <summary>
        /// ★ 一个<b>可变</b>的标记而不是启动时读一次的布尔值:前台子代理可以在半路被用户转去后台(<see cref="DetachForeground"/>),而「要不要按后台收尾」要在<b>结束那一刻</b>读。
        /// 派出时读一次的话,被转走的那个会按前台收尾——回执上没有 pending,结论也不会送回主代理,用户转出去的那份活就这样没了下文。
        /// </summary>
        public volatile bool Background;

        public RunHandle? Handle;

        /// <summary>父 Run 中止 → 中止这个子 Run 的登记(子 Run 结束时摘掉)。</summary>
        public CancellationTokenRegistration AbortLink;

        public SubagentWorktree? Worktree;
    }

    private sealed record ChildResult(RunStatus Status, string Text, AgentError? Error, DateTimeOffset? EndedAt, TokenUsage? Usage, WorktreeInfo? Worktree = null);

    private sealed record ChildStart(ChildRun Child, Task<ChildResult> Result);

    private readonly SubagentServiceDeps _deps;
    private readonly SubagentQueue _queue;
    private readonly SubagentReports _reports;
    private readonly ConcurrentDictionary<RunId, ChildRun> _children = new();
    private readonly ConcurrentDictionary<RunId, Func<bool>> _foreground = new();
    private readonly ConcurrentDictionary<RunId, List<Action>> _endWatchers = new();
    private RunRegistry? _registry;
    private long _sequence;

    public SubagentService(SubagentServiceDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _deps = deps;
        Receipts = new ReceiptStore(deps.Sessions, deps.Log);
        _reports = new SubagentReports(() => Registry, Receipts, deps.Log);
        _reports.Changed += change => RaiseEach(ReportChanged, change);
        _queue = new SubagentQueue(() => deps.Settings.Current().Capacity, this, deps.Clock);
    }

    /// <summary>父会话里回执的持久化(也给测试和 Host 的手动汇报用)。</summary>
    public ReceiptStore Receipts { get; }

    public AgentCatalog Agents => _deps.Agents;

    /// <summary>汇报进度变了。</summary>
    public event Action<ReportChange>? ReportChanged;

    /// <summary>子 Run 结束(<c>SubagentStop</c> Hook 的触发点)。回调里的异常被吞掉并记诊断。</summary>
    public event Action<SubagentStopped>? Stopped;

    /// <summary>当前排队的派发数(日志与测试用)。</summary>
    public int QueuedCount => _queue.Size;

    private RunRegistry Registry => _registry ?? throw new InvalidOperationException("SubagentService.Bind(registry) has not been called.");

    /// <summary>造完 <see cref="RunRegistry"/> 之后调一次。</summary>
    public void Bind(RunRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (Interlocked.CompareExchange(ref _registry, registry, null) is not null)
        {
            throw new InvalidOperationException("SubagentService is already bound to a registry.");
        }
    }

    internal bool TryGetChild(RunId runId, out ChildRun child) => _children.TryGetValue(runId, out child!);

    // ───────────────────────── 启动 ─────────────────────────

    public async Task<SubagentOutcome> SpawnAsync(SubagentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registry = Registry;
        if (registry.Get(request.ParentRunId) is not { State: RunState.Running } parent)
        {
            return new SubagentOutcome.Refused("The run that launched this subagent is no longer active. Do this step yourself.");
        }

        // ★ 深度在这里再断言一次,尽管 Task 工具第一行已经判过:那一道是给模型看的(告诉它为什么、以及改做什么),这一道是给代码看的——防的是将来别的调用方绕过 Task 直接调这里。
        if (parent.Depth >= SubagentLimits.MaxDepth)
        {
            return new SubagentOutcome.Refused($"The subagent nesting limit of {SubagentLimits.MaxDepth} levels has been reached. Do this step yourself.");
        }

        var agents = _deps.Agents.Registry(parent.WorkspaceId);
        if (agents.Get(request.AgentType) is not { } definition)
        {
            // ★ 绝不静默回落到 general-purpose。把可用清单列全,模型下一次就能选对,或者判断出没有合适的。
            return new SubagentOutcome.Refused(
                $"There is no subagent named \"{request.AgentType}\". Available: {string.Join(", ", agents.Names())}. "
                + "Pick one from that list, or skip the subagent and do this step yourself.");
        }

        // 读取最新设置,让用户在运行期间调整默认模型与并发上限都能影响下一次派发。
        var settings = _deps.Settings.Current();
        var capacity = settings.Capacity;
        if (parent.Options is null && settings.Model is not { IsEmpty: false })
        {
            return new SubagentOutcome.Refused("The parent run has no model to inherit and no default subagent model is configured. Do this step yourself.");
        }

        var parentOptions = parent.Options ?? new SendOptions(settings.Model!.ToRef());
        var selection = ChooseModel(definition, settings, parentOptions, request.Model);
        var thinking = _deps.Models.NormalizeThinking(SubagentThinkingSelection.Select(definition.Thinking, settings.Thinking, parentOptions.Thinking), selection);

        // ★ 取 min,不是取子代理声明的那个:否则一个被投毒的 MCP 工具描述可以诱导主 Agent 派一个子 Agent 去做它自己不被允许做的事——这是真实的提权路径。
        // ★ 取父 Run 此刻的活值(权限状态上的实时档位),不取开始时的快照:用户在派子代理之前把档位调宽,子代理理应看见那次调整。
        var parentMode = _deps.PermissionRuns?.Get(parent.RunId)?.Mode
            ?? (PermissionModes.TryParse(parent.LivePermissionMode, out var live) ? live : PermissionMode.Ask);
        var definedMode = definition.PermissionMode ?? PermissionMode.Full;
        var mode = PermissionModes.Min(parentMode, definedMode);

        string childSessionId;
        var childRunId = SubagentIds.ChildRun(parent.RunId, Interlocked.Increment(ref _sequence));
        if (request.Resume is { Length: > 0 } resumeRaw)
        {
            var resumeId = resumeRaw.Trim();
            var existing = _deps.Sessions.Get(resumeId);
            if (existing is null || existing.ParentSessionId != parent.SessionId || existing.WorkspaceId != parent.WorkspaceId)
            {
                return new SubagentOutcome.Refused(
                    $"Cannot resume subagent session \"{resumeId}\": no matching child session belongs to this conversation. Omit `resume` to start a fresh subagent.");
            }

            if (registry.ActiveRuns().Runs.Any(r => r.SessionId == existing.Id))
            {
                return new SubagentOutcome.Refused(
                    $"Subagent session \"{existing.Id}\" is still running; wait for it to finish before resuming it.");
            }

            childSessionId = existing.Id;
        }
        else
        {
            childSessionId = SubagentIds.ChildSession(parent.SessionId, childRunId);
        }

        var isolation = request.Isolation == AgentIsolation.Worktree || definition.Isolation == AgentIsolation.Worktree
            ? AgentIsolation.Worktree
            : AgentIsolation.None;
        SubagentWorktree? worktree = null;
        if (isolation == AgentIsolation.Worktree)
        {
            var project = _deps.ResolveProject?.Invoke(parent.WorkspaceId);
            if (project is null)
            {
                return new SubagentOutcome.Refused("Cannot isolate subagent in a git worktree: this workspace has no project root.");
            }

            var (created, wtError) = await SubagentWorktree.TryCreateAsync(project.Value.Environment, project.Value.Root, childRunId.Value, ct).ConfigureAwait(false);
            if (created is null)
            {
                return new SubagentOutcome.Refused(wtError ?? "Failed to create git worktree for isolated subagent.");
            }

            worktree = created;
        }

        var background = request.RunInBackground ?? (request.Background || definition.DefaultBackground);
        var child = new ChildRun(childRunId, parent.RunId, parent.SessionId, parent.WorkspaceId, request.CallId, childSessionId, definition, request.Description, definedMode)
        {
            Background = background,
            Worktree = worktree,
        };
        _children[childRunId] = child;
        var options = new SendOptions(selection.ToRef(), thinking, parentOptions.MaxContext, "code", mode.ToWire());

        var queued = false;
        var signal = new ParentSignal(parent, this);
        Task<SlotResult<ChildStart>> slot;
        try
        {
            slot = _queue.Acquire(new SlotRequest<ChildStart>(
                signal,
                Blocking: !background, // 后台派发不阻塞父 Run,所以它不算死锁判据里的「卡住了」
                Start: () => StartChild(child, parent, request, options, mode),
                OnQueued: ahead =>
                {
                    queued = true;
                    registry.Post(parent.RunId, new ToolProgressEvent(request.CallId, new ToolProgress(
                        ahead == 0 ? "Queued, waiting for a free subagent slot." : $"Queued, {ahead.ToString(CultureInfo.InvariantCulture)} ahead.",
                        Details: Json($"{{\"kind\":\"queued\",\"ahead\":{ahead.ToString(CultureInfo.InvariantCulture)}}}"))));
                }));
        }
        catch
        {
            _children.TryRemove(childRunId, out _);
            if (worktree is not null)
            {
                await SafeRemoveWorktreeAsync(worktree).ConfigureAwait(false);
            }

            throw;
        }

        // ★ 这里依赖 Acquire 的同步不变式:它在返回之前就决定好了「直接启动」还是「入队」,所以 queued 此刻已经是准的。
        if (background)
        {
            _ = ObserveBackgroundAsync(slot, child, parent.SessionId, request.CallId);
            return new SubagentOutcome.Background(childRunId, queued, Color: definition.Color, Worktree: worktree?.Info);
        }

        SlotResult<ChildStart> acquired;
        try
        {
            acquired = await slot.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _children.TryRemove(childRunId, out _);
            if (worktree is not null)
            {
                await SafeRemoveWorktreeAsync(worktree).ConfigureAwait(false);
            }

            _deps.Log.Warn($"[subagent] could not start {childRunId}.", e);
            return new SubagentOutcome.Refused($"The subagent could not be started: {e.Message}. Do this step yourself.");
        }

        if (acquired is SlotResult<ChildStart>.Refused refused)
        {
            _children.TryRemove(childRunId, out _);
            if (worktree is not null)
            {
                await SafeRemoveWorktreeAsync(worktree).ConfigureAwait(false);
            }

            // 死锁拒绝要留痕:它基本等价于「用户把并发上限配得太小,而代理在套娃」——生产里这是唯一能发现那件事的途径。
            if (refused.Reason == SlotRefusal.Deadlock)
            {
                _deps.Log.Warn($"[subagent] slot deadlock ({capacity.PerSessionLimit}/{capacity.GlobalLimit}): {childRunId}");
            }

            // 父 Run 被中止:原样抛中止(工具层把它当中止处理),不包成「没排上」的失败结果。
            if (refused.Reason == SlotRefusal.Cancelled)
            {
                ct.ThrowIfCancellationRequested();
            }

            return new SubagentOutcome.Refused(SlotRefusals.Reason(refused.Reason, capacity.GlobalLimit));
        }

        var started = ((SlotResult<ChildStart>.Granted)acquired).Value;

        // 前台在等结果——但用户随时可能在卡片上把它转去后台(DetachForeground)。两件事谁先到听谁的:结果先到,照常交回;
        // 转后台先到,这次调用当场以「后台」收场,子 Run 原样跑下去,结束时按后台收尾(读的是 child.Background)。
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _foreground[childRunId] = () =>
        {
            // ★ 只认「还在跑」的:run_end 已经发出、收尾还没做完的那一小段里转后台,收尾可能已经读过 Background 了——这时改它,卡片会被标成后台,结论却按前台交回。
            if (child.Background || child.Handle is not { State: RunState.Running } handle)
            {
                return false;
            }

            child.Background = true;
            handle.Background = new RunBackground(definition.Name, request.Description);
            detached.TrySetResult();
            return true;
        };
        Task winner;
        try
        {
            winner = await Task.WhenAny(started.Result, detached.Task).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _foreground.TryRemove(childRunId, out _);
        }

        if (winner == detached.Task)
        {
            _deps.Log.Warn($"[subagent] moved to background by user: {childRunId}");
            return new SubagentOutcome.Background(childRunId, Detached: true, Color: definition.Color);
        }

        var result = await started.Result.ConfigureAwait(false);
        return new SubagentOutcome.Finished(childRunId, result.Status, result.Text, definition.Color, result.Error, result.Usage, result.Worktree);
    }

    /// <summary>
    /// 把一个前台子代理转去后台。返回「转成了」。false 不是错误:它已经跑完 / 本来就是后台 / 根本不是这个进程里的 Run——这些情况下用户要的「别再等它」要么已经成立,要么无从谈起。
    /// </summary>
    public bool DetachForeground(RunId childRunId) => _foreground.TryGetValue(childRunId, out var detach) && detach();

    /// <summary>用户在卡片上点「处理」(见 <see cref="SubagentReports.ReportManually"/>)。</summary>
    public SubagentReportStatus ReportManually(string sessionId, string callId, SendOptions? fallback = null) => _reports.ReportManually(sessionId, callId, fallback);

    /// <summary>
    /// 子 Run 用哪个「别名 + 供应商」。三档来源(<see cref="ModelSelections.ForSubagent"/>):定义里声明的 &gt; 设置里的「默认子代理」&gt; 父 Run,别名和供应商必须成对决定。
    /// 定义文件里那一对 <c>(model, modelProviderId)</c>,把<b>已经指不到的那个锁摘掉</b>(摘锁而不是整对作废:别名多半还在,带着一个查不到的供应商去路由拿到的是空候选集,整条失败);
    /// 反过来别名本身查不到时<b>不动它</b>——那是用户写错了模型名,静默换一个模型跑完再交回结果,比失败难查得多。
    /// 设置里那个「默认子代理」悬空时落到下一档(父 Run):这一栏是偏好,不是硬约束。
    /// </summary>
    private ModelSelection ChooseModel(AgentDefinition definition, SubagentSettings settings, SendOptions parentOptions, string? overrideModel = null)
    {
        ModelSelection? declared = null;
        if (!string.IsNullOrWhiteSpace(overrideModel))
        {
            declared = new ModelSelection(overrideModel.Trim());
        }
        else if (definition.Model is { Length: > 0 } alias)
        {
            declared = definition.ModelProviderId is { Length: > 0 } providerId && !_deps.Models.Exists(new ModelSelection(alias, providerId))
                ? new ModelSelection(alias)
                : new ModelSelection(alias, definition.ModelProviderId is { Length: > 0 } p ? p : null);
            if (declared.ProviderId is null && definition.ModelProviderId is { Length: > 0 })
            {
                _deps.Log.Warn($"[subagent] {definition.Name} pins provider {definition.ModelProviderId}, which cannot serve {alias}; choosing by alias instead.");
            }
        }

        var configured = settings.Model ?? new ModelSelection("");
        if (!configured.IsEmpty && !_deps.Models.Exists(configured))
        {
            _deps.Log.Warn($"[subagent] the configured default subagent model is unavailable: {configured.Alias}");
            configured = new ModelSelection("");
        }

        return ModelSelections.ForSubagent(declared, configured, new ModelSelection(parentOptions.Model.Alias, parentOptions.Model.ProviderId));
    }

    /// <summary>名额拿到之后要同步做完的那一段(由队列在锁内内联执行,见 <see cref="SlotRequest{T}.Start"/>)。</summary>
    private ChildStart StartChild(ChildRun child, RunHandle parent, SubagentRequest request, SendOptions options, PermissionMode mode)
    {
        var registry = Registry;
        var definition = child.Definition;

        // 子会话:parent_session_id 是真实父关系(列表、搜索、导出、清理都带 parent_session_id IS NULL;删父会话时跟着回收)。
        var parentSession = _deps.Sessions.Get(parent.SessionId);
        _deps.Sessions.Ensure(new SessionCreate(
            parent.WorkspaceId,
            Id: child.ChildSessionId,
            ParentSessionId: parent.SessionId,
            Title: request.Description,
            Model: options.Model.Alias,
            ModelProviderId: options.Model.ProviderId,
            Mode: "code",
            Thinking: options.Thinking.ToString().ToLowerInvariant(),
            RootPathAtCreation: parentSession?.RootPathAtCreation));

        var message = new Message(new MessageId($"{child.Id.Value}:prompt"), Role.User, [new TextPart(request.Prompt)], _deps.Clock.GetUtcNow());
        var template = _deps.Requests.Create(new RunTemplate(child.Id, child.ChildSessionId, parent.WorkspaceId, [message], options, RunOrigin.Subagent));
        var maxTurns = (definition.MaxTurns, request.MaxTurns) switch
        {
            (int a, int b) => Math.Min(a, b),
            (int a, null) => a,
            (null, int b) => b,
            _ => (int?)null,
        };
        // 子代理拿到的是一个全新的上下文窗(派生 id 或恢复的子会话);角色提示词追加在基础提示词之后,不替换。
        var runRequest = template with
        {
            Depth = parent.Depth + 1,
            AgentPrompt = definition.Prompt,
            AgentType = definition.Name,
            MaxTurns = maxTurns,
            WorkspaceRoot = child.Worktree?.Path,
        };

        var monitor = new ChildMonitor(registry, parent.RunId, child.Id, request.CallId, _deps.Clock, _deps.Log);
        var launched = registry.Launch(new RunLaunch(child.ChildSessionId, parent.WorkspaceId, runRequest)
        {
            Origin = RunOrigin.Subagent,
            ParentRunId = parent.RunId,
            ParentSessionId = parent.SessionId,
            Options = options,
            PermissionMode = mode.ToWire(),
            WorkspaceRoot = child.Worktree?.Path,
            Subscriptions = [monitor.Subscription],
        });
        if (launched is not LaunchResult.Started started)
        {
            monitor.Subscription.Dispose();
            _ = monitor.DrainAsync();
            _children.TryRemove(child.Id, out _);
            throw new InvalidOperationException(((LaunchResult.Refused)launched).Detail);
        }

        var handle = started.Handle;
        child.Handle = handle;

        // ★ 父 Run 被中止时,子 Run 必须一起停:级联中止遍历的是它当时看到的子 Run 集合,而这个子 Run 可能恰好是在那之后才登记的(排队等到了名额的那一刻父 Run 被中止了)。
        // 挂在父 Run 的取消令牌上:父 Run 的 Abort 无论如何最后都会触发它,已经中止的话立即触发。
        child.AbortLink = parent.Token.Register(static state =>
        {
            var (service, id) = ((SubagentService, RunId))state!;
            service.Registry.Abort(id, cascade: true);
        }, (this, child.Id));
        if (child.Background)
        {
            handle.Background = new RunBackground(definition.Name, request.Description);
        }

        registry.Post(parent.RunId, new SubagentStartEvent(
            request.CallId, child.Id, child.ChildSessionId, definition.Name, request.Description, definition.Color, options.Model.Alias, child.Background, _deps.Clock.GetUtcNow()));
        return new ChildStart(child, AwaitChildAsync(child, handle, monitor));
    }

    /// <summary>排着队但一秒都没跑过的后台派发(被拒 / 被取消):必须把落盘的那张回执改成 aborted——否则卡片会永远停在「运行中」,而它其实从来没有启动过。</summary>
    private async Task ObserveBackgroundAsync(Task<SlotResult<ChildStart>> slot, ChildRun child, string parentSessionId, string callId)
    {
        try
        {
            var acquired = await slot.ConfigureAwait(false);
            if (acquired is SlotResult<ChildStart>.Granted granted)
            {
                await granted.Value.Result.ConfigureAwait(false);
                return;
            }

            if (child.Worktree is { } wt)
            {
                await SafeRemoveWorktreeAsync(wt).ConfigureAwait(false);
            }

            var reason = ((SlotResult<ChildStart>.Refused)acquired).Reason;
            _deps.Log.Warn($"[subagent] queued background child never started ({reason}): {child.Id}");
            _children.TryRemove(child.Id, out _);
            Receipts.Apply(parentSessionId, callId, new ReceiptPatch(child.Id.Value) { Status = SubagentStatus.Aborted, Background = true, Report = SubagentReportStatus.Pending });
            _reports.ChildFinished(new BackgroundReport(parentSessionId, callId, child.Id, child.Definition.Name, "", null), RunStatus.Aborted);
        }
        catch (Exception e)
        {
            _children.TryRemove(child.Id, out _);
            _deps.Log.Warn($"[subagent] background child failed to start: {child.Id}", e);
        }
    }

    // ───────────────────────── 收尾 ─────────────────────────

    /// <summary>
    /// 等子 Run 真正结束:★ 光等 <c>run_end</c> 是不够的——历史在它之后才落库,只等 <c>run_end</c> 的话读到的会是空,每一次子代理都会被报成「结束时没有产出任何文字」,而它明明说了话。
    /// 所以等 <c>RunHandle.Completion</c>(驱动 + settle)。然后:读最后一条助手消息 → 更新父会话里的持久回执 → 发 <c>subagent_end</c> → 后台的向父会话汇报 → 通知 <see cref="Stopped"/>。永不抛。
    /// </summary>
    private async Task<ChildResult> AwaitChildAsync(ChildRun child, RunHandle handle, ChildMonitor monitor)
    {
        try
        {
            try
            {
                if (handle.Completion is { } completion)
                {
                    await completion.ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _deps.Log.Warn($"[subagent] the driver of {child.Id} faulted.", e);
            }

            // 名额刚刚腾出来了——立刻重扫一遍队列,把排队中的下一个派出去。这只是快路径:队列自己那个 1 秒的兜底重扫才是正确性所在。
            _queue.Notify();
            await monitor.DrainAsync().ConfigureAwait(false);

            var status = handle.State.ToStatus();
            var error = handle.Error;
            var last = _deps.Sessions.LastAssistant(child.ChildSessionId);
            var text = last is null ? "" : ReceiptStore.VisibleText(MessageCodec.FromRecord(last));
            var summary = text.Trim().Length == 0 ? null : TaskTool.Preview(text);
            var background = child.Background;

            Receipts.Apply(child.ParentSessionId, child.CallId, new ReceiptPatch(child.Id.Value)
            {
                Status = ToSubagentStatus(status),
                Summary = summary,
                Error = error,
                Background = background ? true : null,
                Report = background ? SubagentReportStatus.Pending : null,
            });

            PostEnd(child, status, summary, error, handle);

            WorktreeInfo? preservedWorktree = null;
            if (child.Worktree is { } wt)
            {
                preservedWorktree = await SafeFinalizeWorktreeAsync(wt).ConfigureAwait(false);
            }

            if (background)
            {
                var bgText = preservedWorktree is null
                    ? text
                    : $"{text}\n\n[worktree: {preservedWorktree.Path} (branch {preservedWorktree.Branch})]";
                _reports.ChildFinished(new BackgroundReport(child.ParentSessionId, child.CallId, child.Id, child.Definition.Name, bgText, summary), status);
            }

            RaiseEach(Stopped, new SubagentStopped(child.ParentSessionId, child.ParentRunId, child.CallId, child.Id, child.ChildSessionId, child.Definition.Name, status, background));
            return new ChildResult(status, text, error, handle.EndedAt, monitor.Usage, preservedWorktree);
        }
        catch (Exception e)
        {
            _deps.Log.Warn($"[subagent] finishing {child.Id} failed.", e);
            return new ChildResult(RunStatus.Error, "", AgentError.Create(AgentErrorCode.Unknown, $"Subagent bookkeeping failed: {e.Message}", false), null, null);
        }
        finally
        {
            child.AbortLink.Dispose();
            _children.TryRemove(child.Id, out _);
        }
    }

    private void PostEnd(ChildRun child, RunStatus status, string? summary, AgentError? error, RunHandle handle)
    {
        // 父 Run 已经结束(后台子代理比父 Run 活得久)时 Post 返回 0:原始子 Run 事件仍经继承的订阅推给控制端,这里只记一笔。
        var seq = Registry.Post(child.ParentRunId, new SubagentEndEvent(child.CallId, child.Id, status)
        {
            Summary = summary,
            Error = error,
            ChildSeq = handle.Seq,
            At = handle.EndedAt,
        });
        if (seq == 0 && child.Background)
        {
            _deps.Log.Warn($"[subagent] background child finished after parent: {child.Id}");
        }
    }

    private async Task<WorktreeInfo?> SafeFinalizeWorktreeAsync(SubagentWorktree worktree)
    {
        try
        {
            return await worktree.FinalizeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _deps.Log.Warn($"[subagent] could not finalize worktree {worktree.Path.Value}.", e);
            return worktree.Info;
        }
    }

    private async Task SafeRemoveWorktreeAsync(SubagentWorktree worktree)
    {
        try
        {
            await worktree.RemoveAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _deps.Log.Warn($"[subagent] could not remove worktree {worktree.Path.Value}.", e);
        }
    }

    private static SubagentStatus ToSubagentStatus(RunStatus status) => status switch
    {
        RunStatus.Done => SubagentStatus.Done,
        RunStatus.Aborted => SubagentStatus.Aborted,
        _ => SubagentStatus.Error,
    };

    // ───────────────────────── IRunExtension ─────────────────────────

    /// <summary>
    /// 子 Run 登记:权限状态 = 父 Run 的状态 <see cref="PermissionRunState.ForSubagent"/>(档位 <c>min(父实时值, 定义值)</c>,父 Run 中途调低子代理立刻跟着降),交互作用域 <c>depth &gt; 0</c>(不能提问:
    /// 发起任何交互当场抛 <see cref="InteractionRefusedException"/>,权限扩展对子代理直接拒绝并给出可添加的规则)。拿不到父状态就不登记——权限扩展对没登记的 Run 是拒绝(失败即关闭)。
    /// 已经有登记(Host 的扩展按父 Run 的静态档位登记过)就不重复:安全,只是档位不再跟着父 Run 实时变。
    /// </summary>
    public void OnCreated(RunHandle run)
    {
        if (run.Origin != RunOrigin.Subagent || !_children.TryGetValue(run.RunId, out var child) || _deps.PermissionRuns is not { } runs || _deps.Gate is not { } gate)
        {
            return;
        }

        if (runs.Get(run.RunId) is not null)
        {
            return;
        }

        if (runs.Get(child.ParentRunId) is not { } parentState)
        {
            _deps.Log.Warn($"[subagent] the parent {child.ParentRunId} has no permission state; {run.RunId} gets none either, so every tool call it makes is refused.");
            return;
        }

        runs.Register(parentState.ForSubagent(run.RunId, gate.OpenRun(run.RunId, run.Depth), child.DefinedMode, child.Worktree?.Path));
    }

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
        if (_endWatchers.TryRemove(run.RunId, out var watchers))
        {
            Action[] callbacks;
            lock (watchers)
            {
                callbacks = [.. watchers];
            }

            foreach (var callback in callbacks)
            {
                callback();
            }
        }

        if (_children.ContainsKey(run.RunId) && _deps.PermissionRuns is { } runs)
        {
            // 兜底:权限扩展在 run_end 扩展点里也会清,这里保证「没走到那一步的结局」(看门狗强收、驱动崩溃)同样不留挂起的交互。
            runs.Get(run.RunId)?.Interactions.End(status);
            runs.Remove(run.RunId);
        }

        _queue.Notify();
    }

    /// <summary>落库、释放互斥、<c>run_settled</c> 之后:这个会话的所有消息都已在库里,补写那些「回执所在的消息当时还没落库」的更新。</summary>
    public void OnSettled(RunHandle run)
    {
        Receipts.Flush(run.SessionId);
        _queue.Notify();
    }

    // ───────────────────────── 队列的两个接缝 ─────────────────────────

    IReadOnlyList<string> ISlotOccupancy.ChildRunIds(string parentRunId) => [.. Registry.ActiveChildrenOf(new RunId(parentRunId)).Select(h => h.RunId.Value)];

    IReadOnlyList<string> ISlotOccupancy.SubagentRunIds() => [.. Registry.ActiveSubagentRunIds().Select(id => id.Value)];

    private IDisposable WatchEnd(RunHandle run, Action callback)
    {
        var list = _endWatchers.GetOrAdd(run.RunId, static _ => []);
        lock (list)
        {
            list.Add(callback);
        }

        // 先登记再检查:登记和结束之间的空窗里 run_end 已经发出的话,没有人会再调这个回调。
        if (run.State != RunState.Running)
        {
            callback();
        }

        return new Watch(list, callback);
    }

    private sealed class Watch(List<Action> list, Action callback) : IDisposable
    {
        public void Dispose()
        {
            lock (list)
            {
                list.Remove(callback);
            }
        }
    }

    private sealed class ParentSignal(RunHandle run, SubagentService owner) : IParentSignal
    {
        public string RunId => run.RunId.Value;

        public bool IsActive => run.State == RunState.Running && !run.AbortRequested;

        public CancellationToken Token => run.Token;

        public IDisposable OnEnded(Action callback) => owner.WatchEnd(run, callback);
    }

    private void RaiseEach<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(arg);
            }
            catch (Exception e)
            {
                _deps.Log.Warn($"A {typeof(T).Name} subscriber threw.", e);
            }
        }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public void Dispose() => _queue.Dispose();
}

/// <summary>排不进队列时给模型的那句话。★ 每一种都要说清「不是你错了,是满了」以及「那你现在该做什么」:只说「达到上限」的话,模型会换一个 <c>subagent_type</c> 再试一次——它会以为是那个子代理的问题。</summary>
public static class SlotRefusals
{
    public static string Reason(SlotRefusal refusal, int globalLimit) => refusal switch
    {
        SlotRefusal.Disabled =>
            "Subagents are disabled: the global concurrent subagent limit is set to 0. Do this step yourself — launching another subagent will not work.",
        SlotRefusal.Deadlock =>
            $"All {globalLimit.ToString(CultureInfo.InvariantCulture)} concurrent subagent slots are held by subagents that are themselves waiting for a slot, so none will free up. Do this step yourself.",
        SlotRefusal.Overflow =>
            "Too many subagents are already queued. Wait for the queued ones to finish before launching another, or carry on yourself.",
        _ => "The conversation stopped before a subagent slot became free.",
    };
}
