using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>一个后台子代理交回来的东西。<see cref="Text"/> 是子代理最后一条助手消息的<b>全文</b>;取不到时为空串。<see cref="Summary"/> 是卡片上那截预览。</summary>
public sealed record BackgroundReport(string SessionId, string CallId, RunId ChildRunId, string? AgentType, string Text, string? Summary);

/// <summary>汇报进度变了(<c>session/&lt;id&gt;</c> 主题的子代理汇报通知:控制端据此更新卡片上的「待汇报 / 汇报中 / 已汇报 / 被挡住」)。</summary>
public sealed record ReportChange(string SessionId, string CallId, SubagentReportStatus Status);

/// <summary>
/// 后台子代理完成后的报告(2.x <c>session-runtime.ts</c> 的 <c>childFinished</c> / <c>reportManually</c> / <c>report</c>):把子代理的结果以<b>内部引导消息</b>交回父会话
/// (<see cref="RunRegistry.DeliverInternal"/>:Run 在跑就进它的引导队列,会话在收尾就排到之后,空闲就起一个新的内部 Run),报告状态 <c>pending → injecting → reported / blocked</c>。
/// <list type="bullet">
/// <item><b>发的是全文</b>,不是卡片上那截 240 字的摘要——拿摘要当正文的话,一份「改了八个文件、逐条说明」的报告到主代理手上只剩开头一句,不报错,只是做错。取不到全文才退回摘要:残缺的汇报也好过没有汇报。</item>
/// <item><b>中止的子代理不自动汇报</b>:那基本是用户按了停止(级联到子代理)或者应用在退出——此时自动开一轮新的对话,正好违背了「停下」这个意图。回执停在 pending,由用户自己点「处理」。</item>
/// <item><b>只汇报一次</b>:持久化的回执管跨重启,进程内的 <c>_reporting</c> 管并发。<c>injecting</c> 是运行期状态:未确认消费前落盘仍为 pending,崩溃重启后才能手动重试。</item>
/// <item><b>连档位都没有时置 blocked</b>,而不是静默返回:界面上那颗「处理」按钮还在,一个按下去什么都不发生的按钮不是诚实的界面。</item>
/// </list>
/// 汇报状态的持久化经 <see cref="ReceiptStore"/>(状态单调,<c>reported</c> 是终点)。
/// </summary>
internal sealed class SubagentReports(Func<RunRegistry> registry, ReceiptStore receipts, ILoopDiagnostics log)
{
    internal const string NoSummary = "The background subagent finished without a summary.";

    private readonly Lock _gate = new();
    private readonly HashSet<(string Session, string Call)> _reporting = [];

    public event Action<ReportChange>? Changed;

    /// <summary>后台子代理跑完。<see cref="RunStatus.Aborted"/> 不自动汇报。</summary>
    public void ChildFinished(BackgroundReport report, RunStatus status)
    {
        if (status == RunStatus.Aborted)
        {
            Raise(report.SessionId, report.CallId, SubagentReportStatus.Pending);
            return;
        }

        Report(report, null);
    }

    /// <summary>
    /// 用户在卡片上点「处理」——重启之后、或者自动那一趟被挡住(blocked)之后。<paramref name="fallback"/> 是装配方按工作区默认值拼的档位:这个进程没见过这条会话发消息时,汇报靠它才发得出去。
    /// </summary>
    public SubagentReportStatus ReportManually(string sessionId, string callId, SendOptions? fallback)
    {
        if (receipts.ReadReport(sessionId, callId) is not { } stored)
        {
            return SubagentReportStatus.None;
        }

        var status = stored.ReportStatus ?? SubagentReportStatus.Pending;
        // 旧版本留下的 injecting 可以重试;本进程真正投递中的报告仍由 _reporting 去重。
        if (status is not (SubagentReportStatus.Pending or SubagentReportStatus.Blocked or SubagentReportStatus.Injecting))
        {
            return status;
        }

        return Report(new BackgroundReport(stored.SessionId, stored.CallId, stored.ChildRunId, stored.AgentType, stored.Text, stored.Summary), fallback);
    }

    private SubagentReportStatus Report(BackgroundReport report, SendOptions? fallback)
    {
        var key = (report.SessionId, report.CallId);
        lock (_gate)
        {
            if (!_reporting.Add(key))
            {
                return SubagentReportStatus.Injecting;
            }
        }

        SetStatus(report.SessionId, report.CallId, report.ChildRunId.Value, SubagentReportStatus.Injecting);
        var body = report.Text.Trim().Length > 0 ? report.Text.Trim() : report.Summary ?? NoSummary;
        IReadOnlyList<Part> parts =
        [
            new TextPart($"Background subagent result ({report.AgentType ?? "subagent"}, {report.ChildRunId}):\n\n{body}\n\nReview this result and continue the conversation if action is needed."),
            // 第二个块只给界面看(编码器丢弃):让这条内部消息被认成「某个后台子代理的结果回传」,画一行可展开的汇报行。
            // 挂短摘要,不挂全文——全文已经在文字块里落盘了,再存一份就是同一段话出现两次。
            new SubagentReportPart(report.CallId, report.ChildRunId.Value, report.Summary),
        ];

        InternalDelivery delivery;
        try
        {
            delivery = registry().DeliverInternal(report.SessionId, new InternalInput(parts, fallback, null, ok =>
            {
                // 持久化的回执管跨重启的「只汇报一次」;这里只挡本进程里的并发。
                lock (_gate)
                {
                    _reporting.Remove(key);
                }

                SetStatus(report.SessionId, report.CallId, report.ChildRunId.Value, ok ? SubagentReportStatus.Reported : SubagentReportStatus.Pending);
            }));
        }
        catch (Exception e)
        {
            log.Warn($"[subagent] could not deliver the report of {report.CallId} to {report.SessionId}.", e);
            lock (_gate)
            {
                _reporting.Remove(key);
            }

            SetStatus(report.SessionId, report.CallId, report.ChildRunId.Value, SubagentReportStatus.Pending);
            return SubagentReportStatus.Pending;
        }

        if (delivery == InternalDelivery.Failed && fallback is null)
        {
            // 没有任何档位可用:失败而不是静默 return。
            SetStatus(report.SessionId, report.CallId, report.ChildRunId.Value, SubagentReportStatus.Blocked);
            return SubagentReportStatus.Blocked;
        }

        return delivery == InternalDelivery.Failed ? SubagentReportStatus.Pending : SubagentReportStatus.Injecting;
    }

    private void SetStatus(string sessionId, string callId, string childRunId, SubagentReportStatus status)
    {
        try
        {
            // injecting 是运行期状态:未确认消费前落盘仍为 pending,崩溃重启后才能手动重试。
            var persisted = status == SubagentReportStatus.Injecting ? SubagentReportStatus.Pending : status;
            receipts.Apply(sessionId, callId, new ReceiptPatch(childRunId) { Report = persisted });
        }
        catch (Exception e)
        {
            log.Warn($"[subagent] could not persist the report status of {callId} in {sessionId}.", e);
        }

        Raise(sessionId, callId, status);
    }

    private void Raise(string sessionId, string callId, SubagentReportStatus status)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var change = new ReportChange(sessionId, callId, status);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ReportChange>)handler)(change);
            }
            catch (Exception e)
            {
                log.Warn("A report-status subscriber threw.", e);
            }
        }
    }
}
