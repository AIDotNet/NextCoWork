using Ncw.Agent.Extensions.Interactions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 搜索 / 附件 / 输入框补全 / 小组件的方法实现(方法表在 <see cref="SearchMethods"/> / <see cref="AttachmentsMethods"/> / <see cref="ComposerMethods"/> / <see cref="WidgetsMethods"/>)。
/// 角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调服务层 → 映射成 DTO;业务失败带稳定 key。
/// </summary>
internal static class SupportHandlers
{
    public static void Register(MethodRouter router, Func<DeviceServices> services, Func<Conversation.ConversationService> conversation)
    {
        // search
        router.Handle(SearchMethods.Conversations, (p, _, _) => ServiceMapping.Guard(() =>
            new SearchConversationsResult([.. conversation().Sessions.Search(p.Query, p.WorkspaceId, p.Limit)
                .Select(static h => new SearchHitDto(h.SessionId, h.WorkspaceId, h.MessageId, h.Title, h.Snippet, h.CreatedAt))])));

        // attachments
        router.Handle(AttachmentsMethods.UploadBegin, (p, _, _) => ServiceMapping.Guard(() =>
            new AttachmentsUploadBeginResult(conversation().Attachments.BeginUpload(p.SessionId, p.DisplayName, p.Mime, p.TotalBytes), Ncw.Agent.Services.Attachments.AttachmentService.MaxChunkBytes)));
        router.Handle(AttachmentsMethods.UploadChunk, (p, _, _) => ServiceMapping.Guard(() =>
            new AttachmentsUploadProgress(conversation().Attachments.WriteChunk(p.UploadId, p.Offset, p.Data))));
        router.Handle(AttachmentsMethods.UploadCommit, (p, _, _) => ServiceMapping.Guard(() => ToDto(conversation().Attachments.Commit(p.UploadId))));
        router.Handle(AttachmentsMethods.UploadAbort, (p, _, _) => ServiceMapping.Guard(() =>
        {
            conversation().Attachments.AbortUpload(p.UploadId);
            return Unit.Value;
        }));
        router.Handle(AttachmentsMethods.Remove, (p, _, _) => ServiceMapping.Guard(() =>
        {
            conversation().Attachments.Remove(p.Id);
            return Unit.Value;
        }));
        router.Handle(AttachmentsMethods.ListBySession, (p, _, _) => ServiceMapping.Guard(() =>
            new AttachmentsListResult([.. conversation().Attachments.ListBySession(p.SessionId).Select(ToDto)])));
        router.Handle(AttachmentsMethods.Read, (p, _, _) => ServiceMapping.Guard(() =>
        {
            var chunk = conversation().Attachments.Read(p.Url, p.Offset, p.Length);
            return new AttachmentsReadResult(chunk.Data, chunk.Total, chunk.Mime, chunk.Eof);
        }));

        // composer:一切失败都是空串(不抛)
        router.Handle(ComposerMethods.SuggestNext, async (p, ctx, _) =>
        {
            WatchClose(ctx, services());
            var reply = await services().Composer.SuggestNextAsync(ctx.ClientId,
                new Ncw.Agent.Services.Composer.SuggestRequest(p.RequestId, p.WorkspaceId, p.SessionId, p.TriggerKey, p.Model, p.ModelProviderId)).ConfigureAwait(false);
            return new ComposerReplyDto(reply.RequestId, reply.Text);
        });
        router.Handle(ComposerMethods.History, (p, _, _) => ServiceMapping.Guard(() => new ComposerHistoryResult([.. services().Composer.History(p.WorkspaceId)])));
        router.Handle(ComposerMethods.Complete, async (p, ctx, _) =>
        {
            WatchClose(ctx, services());
            var reply = await services().Composer.CompleteAsync(ctx.ClientId, new Ncw.Agent.Services.Composer.InlineRequest(
                p.RequestId, p.WorkspaceId, p.SessionId, p.Draft, p.Context.Project, p.Context.Branch, p.Context.Files, p.Context.Commands)).ConfigureAwait(false);
            return new ComposerReplyDto(reply.RequestId, reply.Text);
        });
        router.Handle(ComposerMethods.Cancel, (p, ctx, _) =>
        {
            services().Composer.Cancel(ctx.ClientId, p.RequestId);
            return ValueTask.FromResult(Unit.Value);
        });

        // widgets
        router.Handle(WidgetsMethods.GetState, (p, _, _) => ServiceMapping.Guard(() => new WidgetStateResult(services().Widgets.Get(p.SessionId, p.CallId))));
        router.Handle(WidgetsMethods.SetState, (p, _, _) => ServiceMapping.Guard(() =>
        {
            services().Widgets.Set(p.SessionId, p.CallId, p.State);
            return Unit.Value;
        }));
        router.Handle(WidgetsMethods.Submit, (p, ctx, _) => ServiceMapping.Guard(() => Submit(conversation(), p, ctx)));
        router.Handle(WidgetsMethods.RunAction, async (p, _, ct) => await conversation().WidgetActions.RunAsync(p, ct).ConfigureAwait(false));
    }

    /// <summary>每条连接只登记一次关闭回调(连接断开时取消它在途的建议 / 续写)。</summary>
    private static void WatchClose(CallContext ctx, DeviceServices s)
    {
        if (ctx.Items.TryAdd("composer.watch", true))
        {
            ctx.OnClosed(() => s.Composer.DropClient(ctx.ClientId));
        }
    }

    private static AttachmentDto ToDto(Ncw.Agent.Services.Attachments.AttachmentInfo a) =>
        new(a.Id, a.Scope, a.OwnerId, a.DisplayName, a.Mime, a.Size, a.Checksum, a.CreatedAt, a.Url);

    /// <summary>交互模式的提交:按 callId 找挂着的 <c>widget_submit</c> 交互(控制端给不出、也不该给待决项 id),和 <c>interactions.respond</c> 同一个闸门、先到先得。</summary>
    private static WidgetSubmitResult Submit(Conversation.ConversationService s, WidgetSubmitParams p, CallContext context)
    {
        if (p.Value is { Length: > WidgetSubmit.MaxValueLength })
        {
            throw ServiceMapping.Error(ErrorCodes.TooLarge, "widgets.error.valueTooLarge", "The submitted value is too large.");
        }

        var pending = s.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault(w => w.CallId == p.CallId)
            ?? throw ServiceMapping.Error(ErrorCodes.NotFound, "widgets.error.notWaiting", "This widget is no longer waiting for a submission.");
        var responder = string.IsNullOrEmpty(context.ClientId) ? "unknown" : context.ClientId;
        return s.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse(p.Value), responder) switch
        {
            RespondResult.Accepted => new WidgetSubmitResult(true),
            RespondResult.Conflict => throw ServiceMapping.Error(ErrorCodes.Conflict, "widgets.error.alreadyHandled", "The widget submission was already handled."),
            RespondResult.Invalid invalid => throw ServiceMapping.Invalid("widgets.invalid.value", invalid.Reason),
            _ => throw ServiceMapping.Error(ErrorCodes.NotFound, "widgets.error.notWaiting", "This widget is no longer waiting for a submission."),
        };
    }
}
