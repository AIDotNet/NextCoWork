using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>读子代理全文汇报的结局。界面按它决定显示全文、「完整结果读取失败」还是「旧版本的转录没有子会话」。</summary>
public enum ReportTextOutcome
{
    /// <summary>读到了子会话最后一条助手消息的全文。</summary>
    Full,

    /// <summary>这张卡片没有子会话 id(只靠回执重建的旧卡片):不发那个注定失败的请求。</summary>
    NoChildSession,

    /// <summary>读不到(设备没连上、会话已删除、子会话里没有助手消息)。</summary>
    Unavailable,
}

public readonly record struct ReportText(ReportTextOutcome Outcome, string? Text = null);

/// <summary>
/// 子代理相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。子代理的真源在设备上;卡片的状态经父 Run 的 <c>subagent_*</c> 事件、父会话主题和权威广播进入
/// <see cref="ClientStores.Conversations"/>(<c>ConversationState.Subagents</c>),这里不直接改 Store 里设备拥有的事实。
/// </summary>
public sealed class SubagentActions(DeviceRegistry devices)
{
    /// <summary>这个工作区磁盘上有哪些子代理定义(含被关掉的)。viewer 可用。</summary>
    public Task<ActionResult<AgentInfoDto[]>> ListAsync(string deviceId, string workspaceId, CancellationToken ct = default) =>
        Run(deviceId, async link => (await link.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(workspaceId), ct).ConfigureAwait(false)).Agents);

    /// <summary>加载诊断(哪个文件为什么被跳过)。</summary>
    public Task<ActionResult<AgentDiagnosticDto[]>> DiagnosticsAsync(string deviceId, string workspaceId, CancellationToken ct = default) =>
        Run(deviceId, async link => (await link.CallAsync(OrchestrationMethods.SubagentsDiagnostics, new SubagentsListParams(workspaceId), ct).ConfigureAwait(false)).Diagnostics);

    /// <summary>启用 / 关闭一个子代理(设备级偏好,只有 owner)。</summary>
    public Task<ActionResult<Unit>> SetEnabledAsync(string deviceId, string name, bool enabled, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams(name, enabled), ct, idem: Idem("setEnabled")));

    /// <summary>
    /// 停掉一个子代理(只停它,不连父 Run 一起停:<c>cascade</c> 只往下)。幂等:已经结束了也不报错。卡片上的停止按钮(不展开就能按)走这里。
    /// 没有子 Run id(从回执重建、没见过 <c>subagent_start</c>)的卡片停不了。
    /// </summary>
    public Task<ActionResult<Unit>> StopAsync(SubagentEntry card, CancellationToken ct = default) =>
        card.Record.ChildRunId is not { } child
            ? Task.FromResult(new ActionResult<Unit>(ActionOutcome.NotFound))
            : Run(card.Ref.DeviceId, link => link.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams(child, Cascade: true), ct, idem: Idem("stop")));

    /// <summary>
    /// 把前台子代理挪到后台(<c>runs.background</c>)。转成了、已经跑完、本来就是后台都不是错误;结果经父会话里那次 <c>Task</c> 调用的回执(<c>background: true</c>)到达卡片。
    /// 排队中(还没有子 Run)的前台子代理挪不了:<see cref="ActionOutcome.NotFound"/>。
    /// </summary>
    public Task<ActionResult<Unit>> MoveToBackgroundAsync(SubagentEntry card, CancellationToken ct = default) =>
        card.Record.ChildRunId is not { } child
            ? Task.FromResult(new ActionResult<Unit>(ActionOutcome.NotFound))
            : Run(card.Ref.DeviceId, link => link.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams(child), ct, idem: Idem("background")));

    /// <summary>
    /// 点「处理」:把后台子代理的结果汇报给主代理(重启之后、或自动那一趟被挡住之后)。结果是汇报进度;进度的后续变化经父会话主题到达卡片。
    /// </summary>
    public Task<ActionResult<SubagentReportStatus>> ReportAsync(SubagentRef card, CancellationToken ct = default) =>
        Run(card.DeviceId, async link =>
            SubagentView.ParseReportStatus((await link.CallAsync(OrchestrationMethods.SubagentsReport, new SubagentsReportParams(card.SessionId, card.CallId), ct, idem: Idem("report")).ConfigureAwait(false)).Status)
            ?? SubagentReportStatus.None);

    /// <summary>
    /// 点开子代理的完整记录(右侧只读面板):订阅子会话主题(快照进 Store),子 Run 还在跑(卡片没结束)时再订阅它自己的 <c>run/&lt;childRunId&gt;</c> 主题——
    /// 面板里的逐字转录就是这一路事件,和主智能体同一个归约函数。<b>只在用户点开时才订阅</b>:一次编排能同时派出十几个后台子代理,绝大多数永远不会被点开。
    /// 早就跑完的子代理库里那份就是全部,不 attach 一个已经回收的 Run。返回子会话的引用;没有子会话 id(旧卡片)返回 null。
    /// </summary>
    public async Task<SessionRef?> OpenAsync(SubagentEntry card, CancellationToken ct = default)
    {
        if (card.Record.ChildSessionId is not { } childSession || devices.Find(card.Ref.DeviceId) is not { IsConnected: true } link)
        {
            return null;
        }

        try
        {
            await link.WatchSessionAsync(childSession, ct).ConfigureAwait(false);
            if (!card.IsTerminal && !card.Unknown && card.Record.ChildRunId is { } child)
            {
                await link.WatchRunAsync(child, ct).ConfigureAwait(false);
            }

            return new SessionRef(link.DeviceId, childSession);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>合上子代理面板:这台设备上不再跟踪它的子会话(当前连接上的订阅随连接结束;重连后不再订阅)。</summary>
    public void Close(SessionRef childSession)
    {
        if (devices.Find(childSession.DeviceId) is { } link)
        {
            link.UnwatchSession(childSession.SessionId);
        }
    }

    /// <summary>
    /// 汇报行展开时<b>现取全文</b>:消息里存下来的摘要只有前 240 字(卡片上的预览),全文是子会话里最后一条助手消息。
    /// 只取那一条(读最近一页,不读整段子转录);收着的时候不该调。
    /// </summary>
    public async Task<ReportText> ReadReportAsync(SubagentEntry card, CancellationToken ct = default)
    {
        if (card.Record.ChildSessionId is not { } childSession)
        {
            return new ReportText(ReportTextOutcome.NoChildSession);
        }

        var page = await Run(card.Ref.DeviceId, link => link.CallAsync(ConversationMethods.SessionsGetPage, new SessionsGetPageParams(childSession, 20), ct)).ConfigureAwait(false);
        if (!page.IsOk || page.Value is null)
        {
            return new ReportText(ReportTextOutcome.Unavailable);
        }

        var last = page.Value.Messages.LastOrDefault(m => m.Role == "assistant" && SubagentReceipts.TextOf(m).Trim().Length > 0);
        return last is null ? new ReportText(ReportTextOutcome.Unavailable) : new ReportText(ReportTextOutcome.Full, SubagentReceipts.TextOf(last));
    }

    private static string Idem(string what) => $"subagent.{what}:{Guid.NewGuid():N}";

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
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
