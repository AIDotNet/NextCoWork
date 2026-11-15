using System.Text.Json;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>对父会话里一张子代理回执的一次更新。字段为 null = 不动。</summary>
public sealed record ReceiptPatch(string ChildRunId)
{
    public SubagentStatus? Status { get; init; }

    public string? Summary { get; init; }

    public AgentError? Error { get; init; }

    public bool? Background { get; init; }

    public SubagentReportStatus? Report { get; init; }
}

/// <summary>从父会话里读出来的、一个后台子代理要汇报的东西(2.x 的 <c>readBackgroundReport</c>)。</summary>
public sealed record StoredReport(string SessionId, string CallId, RunId ChildRunId, string? AgentType, string Text, string? Summary, SubagentReportStatus? ReportStatus);

/// <summary>
/// 父会话里 <c>Task</c> 回执(<c>tool_result.subagent</c>)的持久化(2.x 的 <c>persistSubagentCompletion</c> / <c>persistSubagentReportStatus</c> / <c>mergeLatestSubagentReceipts</c>)。
/// <para>
/// 2.x 里父 Run 在 <c>finally</c> 里整段写回自己那份内存转录,而一个脱离的子代理可能同时在改库里的回执,所以要把库里最新的回执合并进那次最终写入,免得旧快照把已完成的子代理降级回 running。
/// 3.0 里消息只在完成时写一次、没有整段回写,那个竞态不存在了;剩下的是它的镜像问题:<b>子代理跑得比父 Run 提交那条工具结果还快</b>(后台子代理几乎立刻失败 / 完成)——
/// 这时回执所在的消息还不在库里。所以更新做不上就<b>记下来</b>,父会话 <c>run_settled</c>(所有消息都已落库)之后再补做(<see cref="Flush"/>)。
/// </para>
/// 更新是对单条消息的比较并替换(<see cref="SessionRepository.ReplacePartsIfUnchanged"/>):状态单调(终态压过运行中,<see cref="SubagentView.MergeStatus"/>,
/// 和客户端同一个规则);汇报进度里 <c>reported</c> 是终点,不会被退回。
/// </summary>
public sealed class ReceiptStore(SessionRepository sessions, ILoopDiagnostics? log = null)
{
    private const int MaxAttempts = 4;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Session, string Call), ReceiptPatch> _pending = [];
    private readonly ILoopDiagnostics _log = log ?? NullLoopDiagnostics.Instance;

    /// <summary>还没做上的更新数(测试用)。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>应用一次更新;回执所在的消息还不在库里就记下来等 <see cref="Flush"/>。返回是否已经写进库。</summary>
    public bool Apply(string sessionId, string callId, ReceiptPatch patch)
    {
        var key = (sessionId, callId);
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var earlier))
            {
                patch = Combine(earlier, patch);
            }
        }

        bool done;
        try
        {
            done = TryWrite(sessionId, callId, patch);
        }
        catch (Exception e)
        {
            _log.Warn($"[subagent] could not persist the receipt of {callId} in {sessionId}.", e);
            done = false;
        }

        lock (_gate)
        {
            if (done)
            {
                _pending.Remove(key);
            }
            else
            {
                _pending[key] = patch;
            }
        }

        return done;
    }

    /// <summary>补做某个会话名下还挂着的更新(会话里所有消息都已落库之后调,例如它的 Run settle 了)。</summary>
    public void Flush(string sessionId)
    {
        List<((string Session, string Call) Key, ReceiptPatch Patch)> mine;
        lock (_gate)
        {
            mine = [.. _pending.Where(p => p.Key.Session == sessionId).Select(p => (p.Key, p.Value))];
        }

        foreach (var (key, patch) in mine)
        {
            bool done;
            try
            {
                done = TryWrite(key.Session, key.Call, patch);
            }
            catch (Exception e)
            {
                _log.Warn($"[subagent] could not persist the receipt of {key.Call} in {key.Session}.", e);
                continue;
            }

            if (done)
            {
                lock (_gate)
                {
                    _pending.Remove(key);
                }
            }
        }
    }

    /// <summary>两次还没做上的更新合并成一次:状态取单调合并,其余以后到的为准,<c>reported</c> 不被退回。</summary>
    private static ReceiptPatch Combine(ReceiptPatch earlier, ReceiptPatch later) => later with
    {
        Status = (earlier.Status, later.Status) switch
        {
            ({ } a, { } b) => SubagentView.MergeStatus(a, b),
            (var a, var b) => b ?? a,
        },
        Summary = later.Summary ?? earlier.Summary,
        Error = later.Error ?? earlier.Error,
        Background = later.Background ?? earlier.Background,
        Report = MergeReport(earlier.Report, later.Report),
    };

    private static SubagentReportStatus? MergeReport(SubagentReportStatus? current, SubagentReportStatus? incoming) =>
        current == SubagentReportStatus.Reported && incoming is not null ? SubagentReportStatus.Reported : incoming ?? current;

    /// <summary>把更新合进一张回执。状态单调;<c>reported</c> 不被退回;回执上没写的字段由更新补上。</summary>
    internal static SubagentReceipt Merge(SubagentReceipt? current, ReceiptPatch patch, bool resultIsError)
    {
        var currentStatus = SubagentView.ParseStatus(current?.Status) ?? (current is null ? (SubagentStatus?)null : resultIsError ? SubagentStatus.Error : SubagentStatus.Done);
        var status = (currentStatus, patch.Status) switch
        {
            ({ } a, { } b) => SubagentView.MergeStatus(a, b),
            (var a, var b) => b ?? a,
        };
        var currentReport = SubagentView.ParseReportStatus(current?.ReportStatus);
        var report = MergeReport(currentReport, patch.Report);
        return new SubagentReceipt(
            patch.ChildRunId,
            status is { } s ? SubagentView.ToWire(s) : current?.Status,
            patch.Summary ?? current?.Summary,
            patch.Background ?? current?.Background,
            report is { } r ? SubagentView.ToWire(r) : current?.ReportStatus,
            current?.Color,
            patch.Error ?? current?.Error);
    }

    private bool TryWrite(string sessionId, string callId, ReceiptPatch patch)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var history = sessions.History(sessionId);
            var found = false;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                // 2.x 的工具结果消息 role 是 tool,3.0 的是 user:不按 role 筛,只认 callId。
                var record = history[i];
                if (!record.PartsJson.Contains(callId, StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = MessageCodec.PartsFromJson(record.PartsJson);
                var index = IndexOfResult(parts, callId);
                if (index < 0)
                {
                    continue;
                }

                found = true;
                var result = (ToolResultPart)parts[index];
                var merged = Merge(result.Subagent, patch, result.IsError);
                if (merged == result.Subagent)
                {
                    return true; // 没有变化
                }

                var updated = parts.ToList();
                updated[index] = result with { Subagent = merged };
                if (sessions.ReplacePartsIfUnchanged(sessionId, record.Id, record.PartsJson, MessageCodec.PartsToJson(updated)))
                {
                    return true;
                }

                break; // 这条消息在我们读到之后被别人改过:重读再来
            }

            if (!found)
            {
                return false; // 回执所在的消息还没落库
            }
        }

        return false;
    }

    private static int IndexOfResult(IReadOnlyList<Part> parts, string callId)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is ToolResultPart result && result.CallId == callId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 从库里的回执恢复一个后台子代理的交付物——重启之后用户手动点「处理」走这条。★ 全文读子会话的<b>最后一条助手消息</b>,不解析整段子转录。
    /// 不是后台子代理(没有回执 / 回执不是后台)返回 null。
    /// </summary>
    public StoredReport? ReadReport(string sessionId, string callId)
    {
        SubagentReceipt? receipt = null;
        string? agentType = null;
        foreach (var record in sessions.History(sessionId))
        {
            if (!record.PartsJson.Contains(callId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var part in MessageCodec.PartsFromJson(record.PartsJson))
            {
                switch (part)
                {
                    case ToolResultPart { Subagent: { } found } result when result.CallId == callId:
                        receipt = found;
                        break;
                    case ToolCallPart call when call.CallId == callId && call.Input.ValueKind == JsonValueKind.Object:
                        if (call.Input.TryGetProperty("subagent_type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is { } name && name.Trim().Length > 0)
                        {
                            agentType = name;
                        }

                        break;
                }
            }
        }

        if (receipt is not { Background: true })
        {
            return null;
        }

        var text = sessions.LastAssistant(SubagentIds.ChildSession(sessionId, new RunId(receipt.ChildRunId))) is { } last ? VisibleText(MessageCodec.FromRecord(last)) : "";
        return new StoredReport(sessionId, callId, new RunId(receipt.ChildRunId), agentType, text, receipt.Summary, SubagentView.ParseReportStatus(receipt.ReportStatus));
    }

    /// <summary>消息里模型可见的文字(文字块拼起来)。</summary>
    internal static string VisibleText(Message message) => string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>子 Run / 子会话 id 的派生(05 §5)。id 是<b>惯例</b>,<c>sessions.parent_session_id</c> 才是事实:永远不从 id 里反推父亲。</summary>
public static class SubagentIds
{
    /// <summary>子 RunId = <c>&lt;父 RunId&gt;:sub:&lt;N&gt;</c>。</summary>
    public static RunId ChildRun(RunId parent, long sequence) => new($"{parent.Value}:sub:{sequence}");

    /// <summary>子会话 id = <c>&lt;父会话&gt;:sub:&lt;子 RunId&gt;</c>。</summary>
    public static string ChildSession(string parentSessionId, RunId childRun) => $"{parentSessionId}:sub:{childRun.Value}";
}
