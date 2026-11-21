using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Ids;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>发送一条消息的结局。界面按它决定撤掉乐观消息(<see cref="Queued"/>)还是报错;Ncw.Client 不产出界面文字。</summary>
public enum SendOutcome
{
    /// <summary>设备开了一个新 Run(已开始跟踪它)。</summary>
    Started,

    /// <summary>会话里已经有一个 Run 在跑:这句话进了队列(不是失败;队列经会话主题到达 Store)。</summary>
    Queued,

    /// <summary>会话正在收尾,稍后重试。</summary>
    Busy,

    /// <summary>设备没连上 / 数据没就绪(可重试)。</summary>
    Unavailable,

    /// <summary>没有权限(例如 viewer,或 member 想用完全访问档位)。</summary>
    Forbidden,

    /// <summary>设备拒绝了请求(没有模型、会话不存在……)。</summary>
    Rejected,
}

public readonly record struct SendResult(SendOutcome Outcome, RunRef? Run = null, string? MessageId = null);

/// <summary>手动压缩(<c>/compact</c>)的结局。成功时边界消息经会话主题(历史重置)回到 Store,这里不改 Store。</summary>
public enum CompactOutcome
{
    /// <summary>压缩完成,边界已落库。</summary>
    Done,

    /// <summary>会话正在运行(或正在压缩):只能在空闲时手动压缩。</summary>
    Busy,

    /// <summary>上一个边界之后还没有新的对话。</summary>
    NothingToCompact,

    /// <summary>压缩本身失败(模型出错、没有可用的压缩模型……)。</summary>
    Failed,

    /// <summary>设备没连上。</summary>
    Unavailable,
}

public enum InteractionOutcome
{
    /// <summary>回答被接受。</summary>
    Answered,

    /// <summary>已被别人处理(或已被中止):<c>Resolution</c> 说明是谁、什么时候;界面显示「已在另一台设备上处理」。</summary>
    AlreadyHandled,

    /// <summary>仍在挂起但版本对不上(控制端手里的是旧的一份);界面重新取最新的。</summary>
    Stale,

    /// <summary>回答不合法(不消耗这次交互,可以改了再答)。</summary>
    Invalid,

    NotFound,

    Forbidden,

    Unavailable,
}

public readonly record struct InteractionResult(InteractionOutcome Outcome, ResolutionDto? Resolution = null, long? CurrentVersion = null);

/// <summary>
/// 输入框和交互卡片用到的对话意图(<see cref="ConversationActions"/> 的子集)。ViewModel 依赖这个接口,单测里换成假实现,
/// 不用起一个真的设备连接就能测发送 / 排队 / 中止 / 审批的状态机。
/// </summary>
public interface IConversationCommands
{
    Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default);

    Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default);

    Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default);

    Task<bool> AbortAsync(RunRef run, CancellationToken ct = default);

    Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default);

    Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default);

    Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default);

    Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default);

    /// <summary>
    /// 输入框里选了一个模型:记在会话上(<c>sessions.setModel</c>,重开应用 / 换回这个会话还是它);<paramref name="defaultForWorkspace"/> 非空时
    /// 同时写成这个项目新会话的默认模型(<c>settings.project.update</c>,owner 才能写,2.x「药丸写回工作区默认值」)。返回会话上是否记住了。
    /// 没有覆盖它的实现(单测里的假实现)什么也不做。
    /// </summary>
    Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>
    /// 记住会话的权限档位(<c>sessions.setPermissionMode</c>);若当前有正在跑的 Run(<paramref name="activeRunId"/> 非空),
    /// 同时切这个实时 Run 的权限档位(<c>runs.setPermissionMode</c>,owner 才能调);<paramref name="defaultForWorkspace"/> 非空时
    /// 顺带写成这个项目新会话的默认权限档位(<c>settings.project.update</c>)。没有覆盖它的实现(单测里的假实现)什么也不做。
    /// </summary>
    Task<bool> SetPermissionModeAsync(SessionRef session, string mode, string? activeRunId = null, string? defaultForWorkspace = null, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>切换会话模式(<c>code</c> / <c>plan</c>,<c>sessions.setMode</c>)。没有覆盖它的实现(单测里的假实现)什么也不做。</summary>
    Task<bool> SetModeAsync(SessionRef session, string mode, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>批准计划之后以 <c>code</c> 模式发起执行 Run(<c>runs.executePlan</c>)。</summary>
    Task<SendResult> ExecutePlanAsync(SessionRef session, PlanApprovalInteractionDto approval, string text, SendOptionsDto options, CancellationToken ct = default) =>
        Task.FromResult(new SendResult(SendOutcome.Unavailable));

    /// <summary>
    /// 立刻压缩会话的上下文(<c>/compact [说明]</c>,<c>context.compact</c>)。<paramref name="instructions"/> 接在摘要提示词的 <c>Additional focus:</c> 后面;
    /// <paramref name="options"/> 是当前的发送档位(会话还没记模型时设备用它选压缩模型)。没有覆盖它的实现(单测里的假实现)返回 <see cref="CompactOutcome.Unavailable"/>。
    /// </summary>
    Task<CompactOutcome> CompactAsync(SessionRef session, string? instructions, SendOptionsDto options, CancellationToken ct = default) =>
        Task.FromResult(CompactOutcome.Unavailable);

    // 带文件引用(输入框里 @ 选的文件)的重载。没有覆盖它们的实现(单测里的假实现)退化成把引用写成 `@路径` 追加在正文后面。

    Task<SendResult> SendAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default) =>
        SendAsync(session, InputFile.WithMentions(text, files), options, ct);

    Task<bool> EnqueueAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default) =>
        EnqueueAsync(session, InputFile.WithMentions(text, files), options, ct);

    Task<bool> SteerAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default) =>
        SteerAsync(session, InputFile.WithMentions(text, files), options, ct);
}

/// <summary>消息里引用的一个项目文件(<c>InputPartKinds.File</c>:项目相对路径 + 显示名)。设备按路径读,正文里不重复文件内容。</summary>
public readonly record struct InputFile(string Path, string Name)
{
    /// <summary>
    /// 上传到设备的附件(<c>attachments.upload.*</c> 之后的 <c>ncw://</c> 引用,不是本地路径):图片是 <c>image</c> 块(<see cref="InputPartKinds.Image"/>,<c>DataRef</c> = 引用),
    /// 其他类型是 <c>file</c> 块(<c>Path</c> = 引用)。<see cref="Path"/> 里存的是引用,<see cref="Mime"/> 非空就表示这是一个上传的附件。
    /// </summary>
    public string? Mime { get; init; }

    public static InputFile Attachment(string url, string name, string mime) => new(url, name) { Mime = mime };

    internal InputPartDto ToPart() => Mime switch
    {
        null => new InputPartDto(InputPartKinds.File, Path: Path, Name: Name),
        var mime when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => new InputPartDto(InputPartKinds.Image, Mime: mime, DataRef: Path, Name: Name),
        var mime => new InputPartDto(InputPartKinds.File, Mime: mime, Path: Path, Name: Name),
    };

    /// <summary>退化写法:把引用写成 <c>@路径</c> 追加在正文后面(上传的附件只能走真正的输入块,退化时不写)。</summary>
    public static string WithMentions(string text, IReadOnlyList<InputFile> files) =>
        files.Where(static f => f.Mime is null).ToList() is { Count: > 0 } mentions ? text + "\n" + string.Join(' ', mentions.Select(static f => "@" + f.Path)) : text;
}

/// <summary>
/// 对话相关的用户意图(AGENTS.md §3 规则 4):一个意图一个方法。结果以结构化的结局返回,界面自己生成文字;
/// 状态变化经设备推送(会话、Run、交互主题)进入 <see cref="ClientStores.Conversations"/>,这里不直接改 Store 里设备拥有的事实
/// (唯一的例外:发起 Run 后立刻建一份「运行中」的镜像,让界面不必等第一个事件)。
/// </summary>
public sealed class ConversationActions(DeviceRegistry devices, InteractionLedger? ledger = null) : IConversationCommands
{
    /// <summary>在项目里新建一个会话,并开始跟踪它。</summary>
    public async Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default)
    {
        if (Link(project.DeviceId) is not { } link)
        {
            return null;
        }

        try
        {
            var session = await link.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: model), ct).ConfigureAwait(false);
            await link.WatchSessionAsync(session.Id, ct).ConfigureAwait(false);
            return new SessionRef(link.DeviceId, session.Id);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>
    /// 记住会话的模型(见 <see cref="IConversationCommands.SetModelAsync"/>)。更新后的会话经会话主题推回 Store,这里不改 Store。
    /// 写项目默认值失败(不是 owner、设备拒绝)不算失败:会话上已经记住了,一个存不下来的默认值不值得打断用户。
    /// </summary>
    public async Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return false;
        }

        try
        {
            await link.CallAsync(ConversationMethods.SessionsSetModel, new SessionsSetModelParams(session.SessionId, alias, providerId), ct,
                idem: $"setModel:{session.SessionId}:{Ulid.New()}").ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            return false;
        }

        if (defaultForWorkspace is not null)
        {
            try
            {
                await link.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(defaultForWorkspace, DefaultModel: new ModelPickDto(alias, providerId)), ct,
                    idem: $"project.defaultModel:{defaultForWorkspace}:{Ulid.New()}").ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                // 见上:只记在会话上
            }
        }

        return true;
    }

    /// <summary>手动压缩(见 <see cref="IConversationCommands.CompactAsync"/>)。设备拒绝的原因按错误码 / 消息 key 归成 <see cref="CompactOutcome"/>。</summary>
    public async Task<CompactOutcome> CompactAsync(SessionRef session, string? instructions, SendOptionsDto options, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return CompactOutcome.Unavailable;
        }

        try
        {
            var result = await link.CallAsync(ConversationMethods.ContextCompact,
                new ContextCompactParams(session.SessionId, string.IsNullOrWhiteSpace(instructions) ? null : instructions, options), ct,
                idem: $"compact:{session.SessionId}:{Ulid.New()}").ConfigureAwait(false);
            return result switch
            {
                { Boundary: not null } => CompactOutcome.Done,
                { Error.MessageKey: "compaction.nothingToCompact" } => CompactOutcome.NothingToCompact,
                _ => CompactOutcome.Failed,
            };
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.Busy)
        {
            return CompactOutcome.Busy;
        }
        catch (ProtocolException)
        {
            return CompactOutcome.Failed;
        }
    }

    /// <summary>
    /// 记住会话的权限档位(见 <see cref="IConversationCommands.SetPermissionModeAsync"/>)。更新后的会话经会话主题推回 Store,这里不改 Store。
    /// 切正在跑的 Run 或写项目默认值失败(Run 已刚结束、不是 owner)不算失败:会话上已经记住了。
    /// </summary>
    public async Task<bool> SetPermissionModeAsync(SessionRef session, string mode, string? activeRunId = null, string? defaultForWorkspace = null, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return false;
        }

        try
        {
            await link.CallAsync(ConversationMethods.SessionsSetPermissionMode, new SessionsSetPermissionModeParams(session.SessionId, mode), ct,
                idem: $"setPermissionMode:{session.SessionId}:{Ulid.New()}").ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            return false;
        }

        if (activeRunId is { Length: > 0 } runId)
        {
            try
            {
                await link.CallAsync(ConversationMethods.RunsSetPermissionMode, new RunsSetPermissionModeParams(runId, mode), ct,
                    idem: $"run.permissionMode:{runId}:{Ulid.New()}").ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                // Run 可能刚好结束或当前不是 owner:会话上已经记住了
            }
        }

        if (defaultForWorkspace is not null)
        {
            try
            {
                await link.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(defaultForWorkspace, PermissionMode: mode), ct,
                    idem: $"project.permissionMode:{defaultForWorkspace}:{Ulid.New()}").ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                // 只记在会话上
            }
        }

        return true;
    }

    /// <summary>切换会话模式(<c>code</c> / <c>plan</c>,<c>sessions.setMode</c>)。更新后的会话经会话主题推回 Store。</summary>
    public async Task<bool> SetModeAsync(SessionRef session, string mode, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return false;
        }

        try
        {
            await link.CallAsync(ConversationMethods.SessionsSetMode, new SessionsSetModeParams(session.SessionId, mode), ct,
                idem: $"setMode:{session.SessionId}:{Ulid.New()}").ConfigureAwait(false);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <summary>打开一个会话:订阅它的主题(快照进 Store,之后的变化跟着更新,断线重连后自动恢复)。</summary>
    public async Task<bool> OpenSessionAsync(SessionRef session, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return false;
        }

        try
        {
            await link.WatchSessionAsync(session.SessionId, ct).ConfigureAwait(false);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <summary>
    /// 发送一条消息。RunId 和消息 id 在这里生成(送达以同 id 的 <c>message_end</c> 为准;幂等键绑定 RunId,所以重发同一次发送不会开两个 Run)。
    /// 会话已有 Run 在跑时设备会把它放进队列(<see cref="SendOutcome.Queued"/>)。
    /// </summary>
    public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) =>
        SendAsync(session, text, [], options, ct);

    /// <summary>同 <see cref="SendAsync(SessionRef, string, SendOptionsDto, CancellationToken)"/>,输入里带 <paramref name="files"/> 里的文件引用(文本块在前,文件块依次在后)。</summary>
    public async Task<SendResult> SendAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return new SendResult(SendOutcome.Unavailable);
        }

        var runId = Ulid.New();
        var messageId = Ulid.New();
        try
        {
            var result = await link.CallAsync(
                ConversationMethods.RunsStart,
                new RunsStartParams(runId, session.SessionId, messageId, Parts(text, files), options),
                ct, idem: $"start:{runId}").ConfigureAwait(false);
            if (result.Outcome == StartOutcomes.Queued)
            {
                return new SendResult(SendOutcome.Queued, null, messageId);
            }

            var run = new RunRef(link.DeviceId, result.RunId);
            link.UpdateConversations(c => ConversationReducer.ApplyRunStarted(c, run, session.SessionId, link.KnownRev()));
            await link.WatchRunAsync(result.RunId, ct).ConfigureAwait(false);
            return new SendResult(SendOutcome.Started, run, messageId);
        }
        catch (ProtocolException e)
        {
            return new SendResult(Classify(e));
        }
    }

    /// <summary>
    /// 批准计划之后以 <c>code</c> 模式发起执行 Run(<c>runs.executePlan</c>):和 <see cref="SendAsync"/> 同一套 RunId / 消息 id / 幂等键 / 开始跟踪,外加那份批准的计划引用
    /// (取自 <c>plan_approval</c> 交互的 <c>PlanId</c> / <c>Path</c>)。会话忙时结局是 <see cref="SendOutcome.Busy"/>(不进队列:计划只属于这个 RunId)。
    /// </summary>
    public async Task<SendResult> ExecutePlanAsync(SessionRef session, PlanApprovalInteractionDto approval, string text, SendOptionsDto options, CancellationToken ct = default)
    {
        if (Link(session.DeviceId) is not { } link)
        {
            return new SendResult(SendOutcome.Unavailable);
        }

        var runId = Ulid.New();
        var messageId = Ulid.New();
        var start = new RunsStartParams(runId, session.SessionId, messageId, [new InputPartDto(InputPartKinds.Text, text)], options with { Mode = "code" });
        try
        {
            var result = await link.CallAsync(
                OrchestrationMethods.RunsExecutePlan, new RunsExecutePlanParams(start, new PlanFileDto(approval.PlanId, approval.Path)), ct, idem: $"executePlan:{runId}").ConfigureAwait(false);
            if (result.Outcome == StartOutcomes.Queued)
            {
                return new SendResult(SendOutcome.Queued, null, messageId);
            }

            var run = new RunRef(link.DeviceId, result.RunId);
            link.UpdateConversations(c => ConversationReducer.ApplyRunStarted(c, run, session.SessionId, link.KnownRev()));
            await link.WatchRunAsync(result.RunId, ct).ConfigureAwait(false);
            return new SendResult(SendOutcome.Started, run, messageId);
        }
        catch (ProtocolException e)
        {
            return new SendResult(Classify(e));
        }
    }

    /// <summary>停止一个 Run(幂等:已经结束了也不报错)。</summary>
    public async Task<bool> AbortAsync(RunRef run, CancellationToken ct = default)
    {
        if (Link(run.DeviceId) is not { } link)
        {
            return false;
        }

        try
        {
            await link.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams(run.RunId), ct).ConfigureAwait(false);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <summary>
    /// 回答一次交互(审批、提问……)。<see cref="InteractionDto.Version"/> 随请求带上:别的控制端抢先处理了得到 <see cref="InteractionOutcome.AlreadyHandled"/>(带谁、什么时候)。
    /// </summary>
    public async Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default)
    {
        if (Link(deviceId) is not { } link)
        {
            return new InteractionResult(InteractionOutcome.Unavailable);
        }

        try
        {
            var result = await link.CallAsync(
                ConversationMethods.InteractionsRespond, new InteractionsRespondParams(interaction.Id, interaction.Version, response), ct, idem: $"respond:{interaction.Id}:{Guid.NewGuid():N}").ConfigureAwait(false);
            ledger?.MarkAnswered(interaction.Id);
            return new InteractionResult(InteractionOutcome.Answered, result.Resolution);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.Conflict)
        {
            var details = e.Details is { } bytes ? FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<InteractionConflictDto>()) : null;
            return details?.Reason == ConflictReasons.StaleVersion
                ? new InteractionResult(InteractionOutcome.Stale, null, details.CurrentVersion)
                : new InteractionResult(InteractionOutcome.AlreadyHandled, details?.Resolution);
        }
        catch (ProtocolException e)
        {
            return new InteractionResult(e.Code switch
            {
                ErrorCodes.InvalidRequest => InteractionOutcome.Invalid,
                ErrorCodes.NotFound => InteractionOutcome.NotFound,
                ErrorCodes.Forbidden => InteractionOutcome.Forbidden,
                _ => InteractionOutcome.Unavailable,
            });
        }
    }

    /// <summary>把一条消息放进会话的队列(不立刻发送;会话空闲时设备会接着发)。</summary>
    public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) =>
        EnqueueAsync(session, text, [], options, ct);

    /// <summary>同 <see cref="EnqueueAsync(SessionRef, string, SendOptionsDto, CancellationToken)"/>,带文件引用。</summary>
    public Task<bool> EnqueueAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default) =>
        Queue(session.DeviceId, link => link.CallAsync(
            ConversationMethods.QueueEnqueue, new QueueEnqueueParams(session.SessionId, Ulid.New(), Parts(text, files), options), ct));

    /// <summary>
    /// 「插话」:把这句话立刻引入正在跑的 Run(回合边界送达);会话空闲时等于普通入队。
    /// 和 <see cref="EnqueueAsync"/> + <see cref="PromoteQueuedAsync"/> 的区别是一步到位,输入框的「立即插话」走这里。
    /// </summary>
    public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) =>
        SteerAsync(session, text, [], options, ct);

    /// <summary>同 <see cref="SteerAsync(SessionRef, string, SendOptionsDto, CancellationToken)"/>,带文件引用。</summary>
    public Task<bool> SteerAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default) =>
        Queue(session.DeviceId, link => link.CallAsync(
            ConversationMethods.RunsSteer, new RunsSteerParams(session.SessionId, Ulid.New(), Parts(text, files), options), ct, idem: $"steer:{Guid.NewGuid():N}"));

    /// <summary>输入块:正文一个文本块,每个引用的文件一个文件块(正文为空时不发空文本块)。</summary>
    private static InputPartDto[] Parts(string text, IReadOnlyList<InputFile> files) =>
        [.. (text.Length > 0 || files.Count == 0 ? [new InputPartDto(InputPartKinds.Text, text)] : Array.Empty<InputPartDto>()), .. files.Select(static f => f.ToPart())];

    /// <summary>「插话」开关:没引入的引入(下一个回合边界送进正在跑的 Run),已引入的退回排队。</summary>
    public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) =>
        Queue(session.DeviceId, link => link.CallAsync(ConversationMethods.QueuePromote, new QueueItemParams(session.SessionId, itemId), ct));

    public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) =>
        Queue(session.DeviceId, link => link.CallAsync(ConversationMethods.QueueRemove, new QueueItemParams(session.SessionId, itemId), ct));

    private async Task<bool> Queue(string deviceId, Func<DeviceLink, Task<QueueResultDto>> call)
    {
        if (Link(deviceId) is not { } link)
        {
            return false;
        }

        try
        {
            return (await call(link).ConfigureAwait(false)).Accepted;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    private DeviceLink? Link(string deviceId) => devices.Find(deviceId) is { IsConnected: true } link ? link : null;

    private static SendOutcome Classify(ProtocolException e) => e.Code switch
    {
        ErrorCodes.Busy => SendOutcome.Busy,
        ErrorCodes.Forbidden => SendOutcome.Forbidden,
        ErrorCodes.Unavailable => SendOutcome.Unavailable,
        _ => SendOutcome.Rejected,
    };
}
