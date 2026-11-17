using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 对话链路的方法实现(方法表在 <see cref="ConversationMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:
/// 校验参数 → 调领域层 → 映射成 DTO。设备数据没就绪时 <paramref name="service"/> 抛可重试的 <c>unavailable</c>。
/// 失败一律抛 <see cref="ProtocolException"/>(其它异常路由收成 <c>internal</c>,不把细节发给控制端)。
/// </summary>
internal static class ConversationHandlers
{
    private const int MaxPage = 200;

    public static void Register(MethodRouter router, Func<ConversationService> service)
    {
        // sessions
        router.Handle(ConversationMethods.SessionsList, (p, _, _) => Sync(() => SessionsList(service(), p)));
        router.Handle(ConversationMethods.SessionsGet, (p, _, _) => Sync(() => SessionsGet(service(), p)));
        router.Handle(ConversationMethods.SessionsGetPage, (p, _, _) => Sync(() => SessionsGetPage(service(), p)));
        router.Handle(ConversationMethods.SessionsCreate, (p, _, _) => Sync(() => SessionsCreate(service(), p)));
        router.Handle(ConversationMethods.SessionsRename, (p, _, _) => Sync(() => Mutate(service(), p.SessionId, s => s.Sessions.Rename(p.SessionId, p.Title))));
        router.Handle(ConversationMethods.SessionsSetMode, (p, _, _) => Sync(() => SetMode(service(), p)));
        router.Handle(ConversationMethods.SessionsSetModel, (p, _, _) => Sync(() => Mutate(service(), p.SessionId, s => s.Sessions.SetModel(p.SessionId, p.Model, p.ModelProviderId))));
        router.Handle(ConversationMethods.SessionsArchive, (p, _, _) => Sync(() => Mutate(service(), p.SessionId, s => s.Sessions.SetArchived(p.SessionId, p.Archived))));
        router.Handle(ConversationMethods.SessionsFavorite, (p, _, _) => Sync(() => Mutate(service(), p.SessionId, s => s.Sessions.SetFavorited(p.SessionId, p.Favorited))));
        router.Handle(ConversationMethods.SessionsDelete, async (p, _, _) => await DeleteAsync(service(), p).ConfigureAwait(false));
        router.Handle(ConversationMethods.SessionsDuplicate, (p, _, _) => Sync(() => Duplicate(service(), p)));
        router.Handle(ConversationMethods.SessionsBranch, (p, _, _) => Sync(() => Branch(service(), p)));
        router.Handle(ConversationMethods.SessionsEditMessage, (p, _, _) => Sync(() => EditHistory(service(), p.SessionId, s => s.Sessions.EditMessage(p.SessionId, p.MessageId, p.Text, p.Truncate))));
        router.Handle(ConversationMethods.SessionsDeleteTurn, (p, _, _) => Sync(() => EditHistory(service(), p.SessionId, s => s.Sessions.DeleteTurn(p.SessionId, p.UserMessageId))));
        router.Handle(ConversationMethods.SessionsDeleteReply, (p, _, _) => Sync(() => EditHistory(service(), p.SessionId, s => s.Sessions.DeleteReply(p.SessionId, p.FromId, p.ToId))));

        // runs
        router.Handle(ConversationMethods.RunsStart, async (p, ctx, ct) => await StartAsync(service(), p, ctx, ct: ct).ConfigureAwait(false));
        router.Handle(ConversationMethods.RunsAbort, (p, _, _) => Sync(() => Abort(service(), p)));
        router.Handle(ConversationMethods.RunsSteer, async (p, ctx, _) => await SteerAsync(service(), p, ctx).ConfigureAwait(false));
        router.Handle(ConversationMethods.RunsBackground, (p, _, _) => Sync(() => Background(service(), p)));
        router.Handle(ConversationMethods.RunsSetPermissionMode, (p, _, _) => Sync(() => SetPermissionMode(service(), p)));

        // interactions
        router.Handle(ConversationMethods.InteractionsList, (_, _, _) => Sync(() => service().InteractionsState()));
        router.Handle(ConversationMethods.InteractionsRespond, (p, ctx, _) => Sync(() => Respond(service(), p, ctx)));

        // queue
        router.Handle(ConversationMethods.QueueList, (p, _, _) => Sync(() => QueueList(service(), p)));
        router.Handle(ConversationMethods.QueueEnqueue, async (p, ctx, ct) => await EnqueueAsync(service(), p, ctx, ct).ConfigureAwait(false));
        router.Handle(ConversationMethods.QueuePromote, (p, _, _) => Sync(() => Queue(service(), p.SessionId, s => s.Registry.TogglePromote(p.SessionId, new MessageId(p.ItemId)))));
        router.Handle(ConversationMethods.QueueRemove, (p, _, _) => Sync(() => Queue(service(), p.SessionId, s => s.Registry.DropQueued(p.SessionId, new MessageId(p.ItemId)))));

        // tools / context
        router.Handle(ConversationMethods.ToolsList, (p, _, _) => Sync(() => ToolsList(service(), p)));
        router.Handle(ConversationMethods.ContextCompact, async (p, _, ct) => await CompactAsync(service(), p, ct).ConfigureAwait(false));
        router.Handle(ConversationMethods.ContextPreview, async (p, _, ct) => await PreviewAsync(service(), p, ct).ConfigureAwait(false));
    }

    private static ValueTask<T> Sync<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (StorageException e)
        {
            // 目标消息已经不在会话里、库拒绝了这次写:对控制端是「找不到 / 做不了」,不是 internal。
            throw new ProtocolException(ErrorCodes.NotFound, e.Message);
        }
        catch (SessionBusyException e)
        {
            throw new ProtocolException(ErrorCodes.Busy, e.Message, retryable: true);
        }
    }

    // ───────────────────────── sessions ─────────────────────────

    private static SessionsListResult SessionsList(ConversationService s, SessionsListParams p) =>
        new([.. s.Sessions.List(p.WorkspaceId, p.Archived).Select(static i => Mapping.ToDto(i))]);

    private static SessionPageDto SessionsGet(ConversationService s, SessionRefParams p) =>
        Mapping.ToDto(s.Sessions.Detail(p.SessionId) ?? throw NotFound(p.SessionId));

    private static SessionPageDto SessionsGetPage(ConversationService s, SessionsGetPageParams p) =>
        Mapping.ToDto(s.Sessions.Page(p.SessionId, Math.Clamp(p.Limit, 1, MaxPage), p.BeforeMessageId) ?? throw NotFound(p.SessionId));

    private static SessionDto SessionsCreate(ConversationService s, SessionsCreateParams p)
    {
        var project = s.RequireProject(p.WorkspaceId);
        // 没指定模型:落到项目的默认模型,再落到设备的默认模型(和定时任务同一条规则);都没配就留空,由控制端在候选里兜底。
        // 别名和供应商整对地取,不各挑各的(否则拼出「新别名 + 旧供应商」这个谁也没配过的组合)。
        var pick = p.Model is { Length: > 0 } model
            ? new ModelPick(model, p.ModelProviderId)
            : new ProjectSettingsService(s.Workspaces).Get(project.Id)?.DefaultModel is { IsSet: true } projectModel ? projectModel : s.DeviceSettings.Current().DefaultModel;
        // 给了 id 用 Ensure:同 id 已存在就返回现有的,不整行覆盖(控制端生成 id 的重试 / 重复创建是安全的)。
        var input = new SessionCreate(
            project.Id, p.Id, Title: p.Title, Model: pick.IsSet ? pick.Model : null, ModelProviderId: pick.IsSet ? pick.ProviderId : null, Mode: p.Mode, Thinking: p.Thinking,
            RootPathAtCreation: project.RootPath);
        var created = p.Id is null ? s.Sessions.Create(input) : s.Sessions.Ensure(input);
        return Mapping.ToDto(created);
    }

    private static SessionDto Mutate(ConversationService s, string sessionId, Action<ConversationService> change)
    {
        _ = s.RequireSession(sessionId);
        change(s);
        s.PublishSession(sessionId);
        return Mapping.ToDto(s.RequireSession(sessionId));
    }

    private static SessionDto SetMode(ConversationService s, SessionsSetModeParams p)
    {
        var session = Mutate(s, p.SessionId, x =>
        {
            if (x.Sessions.SetMode(p.SessionId, p.Mode))
            {
                // 排队里还没被消费的条目跟着换模式(用户切模式要的就是「接下来都按新模式」)。
                x.Registry.RetagQueuedMode(p.SessionId, SessionRepository.NormalizeMode(p.Mode));
            }
        });
        return session;
    }

    private static async ValueTask<Unit> DeleteAsync(ConversationService s, SessionRefParams p)
    {
        _ = s.RequireSession(p.SessionId);
        var ids = s.Sessions.SubtreeIds(p.SessionId);
        // 先停 Run、等收尾、杀属于它们的后台 Shell、丢掉排队状态——必须在删会话行之前(要靠 runs 表找 Run id)。
        await s.Registry.ForgetSessionsAsync(ids).ConfigureAwait(false);
        var attachmentPaths = s.Sessions.AttachmentPaths(p.SessionId); // 级联会带走这些行:先记路径,删完回收没有引用的文件
        var deleted = s.Sessions.Delete(p.SessionId);
        s.Attachments.RemoveUnreferencedFiles(attachmentPaths);
        foreach (var id in deleted)
        {
            s.Directory.Remove(id);
            s.Planning.Goals.EndSession(id); // 目标(不盖标记)、空闲唤醒、排着的标记一起走;关会话不是用户清除
            s.Planning.Plans.Plans.Leave(id);
            s.Domain.Hooks.Runtime.Clear(id); // 运行期钩子表是按会话的内存状态
            s.PublishSessionDeleted(id);
        }

        return Unit.Value;
    }

    private static SessionDto Duplicate(ConversationService s, SessionsDuplicateParams p)
    {
        var source = s.RequireSession(p.SessionId);
        var copy = s.Sessions.Duplicate(p.SessionId, p.Title ?? source.Title);
        s.Attachments.Relocate(p.SessionId, copy.Id); // 图片搬家:新会话有自己的一份,删源会话不会带走它们
        return Mapping.ToDto(copy);
    }

    private static SessionDto Branch(ConversationService s, SessionsBranchParams p)
    {
        var source = s.RequireSession(p.SessionId);
        var branch = s.Sessions.Branch(p.SessionId, p.UptoMessageId, p.Title ?? source.Title);
        s.Attachments.Relocate(p.SessionId, branch.Id);
        return Mapping.ToDto(branch);
    }

    /// <summary>改写历史只能在会话空闲时做:占会话互斥(Run 在跑 → <c>busy</c>,可重试)。</summary>
    private static Unit EditHistory(ConversationService s, string sessionId, Action<ConversationService> edit)
    {
        _ = s.RequireSession(sessionId);
        using (s.Registry.AcquireOperation(sessionId))
        {
            edit(s);
        }

        s.PublishHistoryReset(sessionId);
        return Unit.Value;
    }

    // ───────────────────────── runs ─────────────────────────

    /// <param name="beforeSend">发送之前的最后一步(拿到 RunId):<c>runs.executePlan</c> 在这里登记「这个 Run 是执行一份批准的计划」。</param>
    internal static async ValueTask<RunsStartResult> StartAsync(ConversationService s, RunsStartParams p, CallContext context, Action<RunId>? beforeSend = null, CancellationToken ct = default)
    {
        if (p.RunId.Length == 0 || p.MessageId.Length == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "runs.start needs a run id and a message id generated by the controller.");
        }

        var session = s.RequireSession(p.SessionId);
        var project = s.RequireProject(session.WorkspaceId);
        var parts = Mapping.ToParts(p.Input);
        if (parts.Count == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A message needs at least one part.");
        }

        var options = Mapping.ToOptions(p.Options, session);
        ConversationService.RequirePermissionModeAllowed(options.PermissionMode, context);
        // SSH 项目:惰性连接(首次指纹 / 凭据以交互出现,等用户回答)。连不上就在这里明确失败,而不是起一个必然失败的 Run。
        _ = await s.EnvironmentOfAsync(project, ct).ConfigureAwait(false);

        var send = new UserSend(new RunId(p.RunId), session.Id, session.WorkspaceId, new MessageId(p.MessageId), parts, options);
        // 云端:配额 / 收尾中的闸门(Run 发起之前)。没有真正发起 Run 时(被拒绝 / 抛异常)把这一次从账本里退掉。
        var ticket = s.RunGate?.AdmitRun();
        try
        {
            beforeSend?.Invoke(send.RunId);
            var result = await s.Registry.SendAsync(send).ConfigureAwait(false);
            if (result is SendResult.Refused)
            {
                ticket?.Refund();
            }

            return result switch
            {
                SendResult.StartedRun started => new RunsStartResult(StartOutcomes.Started, started.Handle.RunId.Value),
                SendResult.Queued queued => new RunsStartResult(StartOutcomes.Queued, p.RunId, Mapping.ToDto(queued.Result.Update)),
                SendResult.Refused refused => throw Refused(refused.Reason, refused.Detail),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception) when (ticket is not null)
        {
            ticket.Refund(); // 幂等:已经退过的不会再退
            throw;
        }
    }

    private static ProtocolException Refused(RunRefusal reason, string detail) => reason switch
    {
        RunRefusal.SessionBusy => new ProtocolException(ErrorCodes.Busy, detail, retryable: true),
        RunRefusal.DuplicateRunId => new ProtocolException(ErrorCodes.Conflict, detail),
        RunRefusal.ShuttingDown => new ProtocolException(ErrorCodes.Unavailable, detail, retryable: true),
        _ => new ProtocolException(ErrorCodes.InvalidRequest, detail),
    };

    private static Unit Abort(ConversationService s, RunsAbortParams p)
    {
        // 不存在(已被回收)的 Run:什么都不做——中止是幂等的,重复按停止不该报错。
        s.Registry.Abort(new RunId(p.RunId), p.Cascade);
        return Unit.Value;
    }

    private static async ValueTask<QueueResultDto> SteerAsync(ConversationService s, RunsSteerParams p, CallContext context)
    {
        var session = s.RequireSession(p.SessionId);
        var options = Mapping.ToOptions(p.Options, session);
        ConversationService.RequirePermissionModeAllowed(options.PermissionMode, context);
        var id = p.MessageId.Length > 0 ? new MessageId(p.MessageId) : (MessageId?)null;
        var enqueued = await EnqueueCoreAsync(s, session, id, Mapping.ToParts(p.Parts), options).ConfigureAwait(false);
        if (!enqueued.Accepted)
        {
            return Mapping.ToResult(enqueued);
        }

        // 插话 = 入队再引入:运行中的 Run 在下一个回合边界收到它;会话已经空闲则入队时就已经续跑,引入被拒(不是错误)。
        var item = enqueued.Update.Items.LastOrDefault();
        if (item is null)
        {
            return Mapping.ToResult(enqueued);
        }

        var promoted = s.Registry.TogglePromote(session.Id, item.Id);
        return Mapping.ToResult(promoted.Accepted ? promoted : enqueued);
    }

    /// <summary>
    /// 把前台子代理挪到后台(<c>subagent</c> 层的 <c>DetachForeground</c>):转成了、跑完了、本来就是后台都不是错误(用户要的「别再等它」要么已经成立,要么无从谈起);
    /// 状态变化经父会话里那条 <c>Task</c> 调用的回执(<c>background: true</c>)到达控制端。顶层 Run 不能挪 → <c>invalid_request</c>;不存在(已回收 / 还在排队没有 Run) → <c>not_found</c>。
    /// </summary>
    private static Unit Background(ConversationService s, RunsBackgroundParams p)
    {
        var runId = new RunId(p.RunId);
        var run = s.Registry.Get(runId) ?? throw new ProtocolException(ErrorCodes.NotFound, $"Unknown run '{p.RunId}'.");
        if (run.IsTopLevel)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Only a subagent run can be moved to the background.");
        }

        _ = s.Subagents.DetachForeground(runId);
        return Unit.Value;
    }

    private static Unit SetPermissionMode(ConversationService s, RunsSetPermissionModeParams p)
    {
        if (!PermissionModes.TryParse(p.Mode, out var mode))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown permission mode '{p.Mode}'.");
        }

        var run = s.Registry.Get(new RunId(p.RunId)) ?? throw new ProtocolException(ErrorCodes.NotFound, $"Unknown run '{p.RunId}'.");
        if (run.State != RunState.Running)
        {
            throw new ProtocolException(ErrorCodes.Conflict, "The run has already ended.");
        }

        // 只影响还没做出的判定:已经在等用户点的那个弹窗不会被这次调宽放掉(2.x ★)。
        run.LivePermissionMode = p.Mode;
        if (s.PermissionRuns.Get(run.RunId) is { } state)
        {
            state.Mode = mode;
        }

        s.Registry.RetagQueuedPermission(run.SessionId, p.Mode);
        return Unit.Value;
    }

    // ───────────────────────── interactions ─────────────────────────

    private static InteractionsRespondResult Respond(ConversationService s, InteractionsRespondParams p, CallContext context)
    {
        var response = Mapping.ToResponse(p.Response);
        var responder = string.IsNullOrEmpty(context.ClientId) ? "unknown" : context.ClientId;
        switch (s.Gate.Respond(new InteractionId(p.Id), p.ExpectedVersion, response, responder))
        {
            case RespondResult.Accepted accepted:
                return new InteractionsRespondResult(Mapping.ToDto(accepted.Resolution));
            case RespondResult.Conflict conflict:
                var details = new InteractionConflictDto(
                    conflict.Reason == ConflictReason.AlreadyResolved ? ConflictReasons.AlreadyResolved : ConflictReasons.StaleVersion,
                    conflict.Resolution is { } resolution ? Mapping.ToDto(resolution) : null,
                    conflict.CurrentVersion);
                throw new ProtocolException(
                    ErrorCodes.Conflict,
                    conflict.Reason == ConflictReason.AlreadyResolved ? "The interaction has already been handled." : "The interaction changed since you saw it.",
                    retryable: false,
                    FrameCodec.EncodePayload(details, PayloadShapes.GetShape<InteractionConflictDto>()));
            case RespondResult.Invalid invalid:
                throw new ProtocolException(ErrorCodes.InvalidRequest, invalid.Reason);
            default:
                throw new ProtocolException(ErrorCodes.NotFound, $"Unknown interaction '{p.Id}'.");
        }
    }

    // ───────────────────────── queue ─────────────────────────

    private static QueueDto QueueList(ConversationService s, SessionRefParams p)
    {
        _ = s.RequireSession(p.SessionId);
        return Mapping.ToDto(s.Registry.QueueOf(p.SessionId));
    }

    private static async ValueTask<QueueResultDto> EnqueueAsync(ConversationService s, QueueEnqueueParams p, CallContext context, CancellationToken ct = default)
    {
        var session = s.RequireSession(p.SessionId);
        var options = Mapping.ToOptions(p.Options, session);
        ConversationService.RequirePermissionModeAllowed(options.PermissionMode, context);
        var parts = Mapping.ToParts(p.Parts);
        if (parts.Count == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A message needs at least one part.");
        }

        // 排队的消息之后由注册表在会话空闲时起 Run:SSH 环境得在这里先连上(起 Run 时只会取已就绪的环境)
        _ = await s.EnvironmentOfAsync(s.RequireProject(session.WorkspaceId), ct).ConfigureAwait(false);
        return Mapping.ToResult(await EnqueueCoreAsync(s, session, p.MessageId is { Length: > 0 } id ? new MessageId(id) : null, parts, options).ConfigureAwait(false));
    }

    private static async Task<QueueResult> EnqueueCoreAsync(ConversationService s, SessionRecord session, MessageId? id, IReadOnlyList<Part> parts, SendOptions options)
    {
        s.RunGate?.CheckInput(); // 云端:配额用尽 / 收尾中不再接新输入(排队的消息之后会变成 Run)
        try
        {
            return await s.Registry.EnqueueAsync(session.Id, session.WorkspaceId, id, parts, options).ConfigureAwait(false);
        }
        catch (QueuePersistenceException e)
        {
            // 落盘失败:绝不回 accepted。可重试(同一个幂等键重发不会重复入队:同 id 已存在会被拒绝)。
            throw new ProtocolException(ErrorCodes.Unavailable, e.Message, retryable: true);
        }
    }

    private static QueueResultDto Queue(ConversationService s, string sessionId, Func<ConversationService, QueueResult> operation)
    {
        _ = s.RequireSession(sessionId);
        try
        {
            return Mapping.ToResult(operation(s));
        }
        catch (QueuePersistenceException e)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, e.Message, retryable: true);
        }
    }

    // ───────────────────────── tools / context ─────────────────────────

    private static ToolsListResult ToolsList(ConversationService s, ToolsListParams p)
    {
        if (p.SessionId is { } sessionId)
        {
            _ = s.RequireSession(sessionId);
        }

        return new ToolsListResult([.. s.AllTools().Select(static t => Mapping.ToDto(t))]);
    }

    private static async ValueTask<ContextCompactResult> CompactAsync(ConversationService s, ContextCompactParams p, CancellationToken ct)
    {
        var session = s.RequireSession(p.SessionId);
        var options = Mapping.ToOptions(p.Options ?? new SendOptionsDto(""), session);
        _ = await s.EnvironmentOfAsync(s.RequireProject(session.WorkspaceId), ct).ConfigureAwait(false);
        // 注册表持有互斥(Run 开始会被拒绝);会话层自己的锁再挡一次直接调用方。
        using var lease = AcquireOrBusy(s, session.Id);
        var outcome = await s.Directory.Get(session.Id, session.WorkspaceId)
            .CompactNowAsync(options.Model, options.Thinking, s.Settings.Read(), p.Instructions, ct).ConfigureAwait(false);
        if (outcome.Boundary is not null)
        {
            s.PublishHistoryReset(session.Id);
        }

        return new ContextCompactResult(outcome.Boundary is { } boundary ? Mapping.ToDto(boundary) : null, outcome.Error is { } error ? Mapping.ToDto(error) : null);
    }

    private static IDisposable AcquireOrBusy(ConversationService s, string sessionId)
    {
        try
        {
            return s.Registry.AcquireOperation(sessionId);
        }
        catch (SessionBusyException e)
        {
            throw new ProtocolException(ErrorCodes.Busy, e.Message, retryable: true);
        }
    }

    private static async ValueTask<ContextPreviewResult> PreviewAsync(ConversationService s, ContextPreviewParams p, CancellationToken ct)
    {
        var session = s.RequireSession(p.SessionId);
        var options = Mapping.ToOptions(p.Options ?? new SendOptionsDto(""), session);
        _ = await s.EnvironmentOfAsync(s.RequireProject(session.WorkspaceId), ct).ConfigureAwait(false);
        var usage = await s.Directory.Get(session.Id, session.WorkspaceId)
            .PreviewContextAsync(options.Model, options.Thinking, s.Settings.Read(), ct).ConfigureAwait(false)
            ?? throw NotFound(p.SessionId);
        return new ContextPreviewResult(usage.Used, usage.Window, usage.ShouldCompact, [.. usage.Segments.Select(static x => new ContextSegmentDto(x.Kind, x.Tokens))]);
    }

    private static ProtocolException NotFound(string sessionId) => new(ErrorCodes.NotFound, $"Unknown session '{sessionId}'.");
}
