using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>消息级的用户意图(<see cref="MessageActions"/> 的接口):单测里用假实现,不用起真的设备连接。</summary>
public interface IMessageCommands
{
    Task<ActionResult<Unit>> EditAsync(SessionRef session, string messageId, string text, CancellationToken ct = default);

    Task<ActionResult<Unit>> EditAndResendAsync(SessionRef session, string messageId, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default);

    Task<ActionResult<Unit>> DeleteTurnAsync(SessionRef session, string userMessageId, CancellationToken ct = default);

    Task<ActionResult<Unit>> DeleteReplyAsync(SessionRef session, string fromAssistantId, string toAssistantId, CancellationToken ct = default);

    Task<ActionResult<string>> BranchAsync(ProjectRef project, string sessionId, string userMessageId, CancellationToken ct = default);
}

/// <summary>
/// 消息级的用户意图(AGENTS.md §3 规则 4:一个意图一个方法):编辑一条提问、编辑后从这里重新运行、删除一整轮、从某一轮分支出新会话。
/// 会话历史的真源在设备上:这里只经协议下命令(<c>sessions.editMessage</c> / <c>deleteTurn</c> / <c>branch</c>),
/// 改写之后设备广播 <c>history.reset</c>,转录跟着 Store 重新派生,这里不改 Store。
/// 改写历史只能在会话空闲时做:设备上有 Run 在跑会回 <c>busy</c>,这里结构化成 <see cref="ActionOutcome.Conflict"/> + <see cref="BusyKey"/>。
/// </summary>
public sealed class MessageActions(DeviceRegistry devices, SessionActions sessions, IConversationCommands conversations) : IMessageCommands
{
    /// <summary>会话有 Run 在跑(改写历史被拒):稳定 key,界面翻译。</summary>
    public const string BusyKey = "messages.busy";

    /// <summary>历史已经截断、但重新发送没有成功:界面把文字放回输入框,别让用户丢掉它。</summary>
    public const string ResendFailedKey = "messages.resendFailed";

    /// <summary>只改写这条提问的文字(不重跑,后面的回复保留)。</summary>
    public Task<ActionResult<Unit>> EditAsync(SessionRef session, string messageId, string text, CancellationToken ct = default) =>
        Run(session.DeviceId, link => link.CallAsync(ConversationMethods.SessionsEditMessage,
            new SessionsEditMessageParams(session.SessionId, messageId, text, Truncate: false), ct, idem: Idem("edit")));

    /// <summary>
    /// 编辑并从这里重新运行:先把这条和它之后的全部移除(<c>truncate</c>),再用新文字发起一次新的 Run(和发送是同一个入口,档位取 <paramref name="options"/>)。
    /// <paramref name="files"/> 是原消息的附件(图片 / 项目文件引用),随新消息一起发,不会因为重跑而丢。截断成功而发送失败时返回 <see cref="ResendFailedKey"/>。
    /// </summary>
    public async Task<ActionResult<Unit>> EditAndResendAsync(SessionRef session, string messageId, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
    {
        var edit = await Run(session.DeviceId, link => link.CallAsync(ConversationMethods.SessionsEditMessage,
            new SessionsEditMessageParams(session.SessionId, messageId, text, Truncate: true), ct, idem: Idem("rerun"))).ConfigureAwait(false);
        if (!edit.IsOk)
        {
            return edit;
        }

        var send = await conversations.SendAsync(session, text, files, options, ct).ConfigureAwait(false);
        return send.Outcome is SendOutcome.Started or SendOutcome.Queued
            ? edit
            : new ActionResult<Unit>(ActionOutcome.Failed, ErrorKey: ResendFailedKey);
    }

    /// <summary>删除一整轮(这条提问 + 它引出的全部回复与工具回执)。</summary>
    public Task<ActionResult<Unit>> DeleteTurnAsync(SessionRef session, string userMessageId, CancellationToken ct = default) =>
        Run(session.DeviceId, link => link.CallAsync(ConversationMethods.SessionsDeleteTurn,
            new SessionsDeleteTurnParams(session.SessionId, userMessageId), ct, idem: Idem("deleteTurn")));

    /// <summary>
    /// 只删 AI 的回复(<c>fromAssistantId..toAssistantId</c> 两条助手消息之间的全部,设备端再吃掉紧随其后的工具回执),提问留着。
    /// 范围用 <see cref="Ncw.Client.Transcript.MessageTurns.ReplyOf"/> 取这一轮 AI 的全部回复。
    /// </summary>
    public Task<ActionResult<Unit>> DeleteReplyAsync(SessionRef session, string fromAssistantId, string toAssistantId, CancellationToken ct = default) =>
        Run(session.DeviceId, link => link.CallAsync(ConversationMethods.SessionsDeleteReply,
            new SessionsDeleteReplyParams(session.SessionId, fromAssistantId, toAssistantId), ct, idem: Idem("deleteReply")));

    /// <summary>
    /// 从某一轮分支出一个新会话(到这一轮为止的转录,独立的顶层会话),然后刷新这个项目的会话列表并打开它。返回新会话的 id。
    /// </summary>
    public async Task<ActionResult<string>> BranchAsync(ProjectRef project, string sessionId, string userMessageId, CancellationToken ct = default)
    {
        var result = await Run(project.DeviceId, link => link.CallAsync(ConversationMethods.SessionsBranch,
            new SessionsBranchParams(sessionId, userMessageId), ct, idem: Idem("branch"))).ConfigureAwait(true);
        if (!result.IsOk || result.Value is not { } branch)
        {
            return result.IsOk ? new ActionResult<string>(ActionOutcome.Failed) : new ActionResult<string>(result.Outcome, default, result.ErrorKey, result.ErrorParams, result.Detail);
        }

        // 注意不用 ConfigureAwait(false):Open 会写 Store,Store 只能在 UI 线程上读写
        await sessions.RefreshAsync(project, ct).ConfigureAwait(true);
        sessions.Open(project, branch.Id);
        return ActionResult<string>.Success(branch.Id);
    }

    private static string Idem(string what) => $"message.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.Busy)
        {
            return new ActionResult<T>(ActionOutcome.Conflict, default, BusyKey, null, e.Message);
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
