using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// 父会话里 <c>Task</c> 回执的持久化与汇报状态(2.x 的 <c>persistSubagentCompletion</c> / <c>persistSubagentReportStatus</c> / <c>mergeLatestSubagentReceipts</c>)。
/// 状态单调(AGENTS.md §3 规则 6)是这一组的主题:终态压过运行中,重放的旧更新不能把已完成的子代理改回运行中。
/// </summary>
public sealed class ReceiptStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwrcpt-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly SessionRepository _repo;
    private readonly ReceiptStore _store;

    public ReceiptStoreTests()
    {
        Directory.CreateDirectory(_root);
        _db = AgentDatabase.Open(Path.Combine(_root, "db"));
        _repo = new SessionRepository(_db);
        _store = new ReceiptStore(_repo);
        _repo.Create(new SessionCreate("ws", "s1"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private void CommitTaskResult(SubagentReceipt? receipt, bool isError = false, string callId = "c1", string messageId = "m-result")
    {
        _repo.Commit("s1", MessageCodec.ToRecord(new Message(new MessageId("m-call"), Role.Assistant,
            [new ToolCallPart(callId, "Task", Jsn.Parse("""{"description":"d","prompt":"p","subagent_type":"code-analyst"}"""))], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000))));
        _repo.Commit("s1", MessageCodec.ToRecord(new Message(new MessageId(messageId), Role.User,
            [new ToolResultPart(callId, new ToolOutput("started"), isError, receipt)], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_001))));
    }

    private SubagentReceipt? Stored(string callId = "c1") =>
        _repo.History("s1").SelectMany(r => MessageCodec.PartsFromJson(r.PartsJson)).OfType<ToolResultPart>().Single(p => p.CallId == callId).Subagent;

    private static ReceiptPatch Done(string child = "r:sub:1") => new(child) { Status = SubagentStatus.Done, Summary = "summary", Background = true, Report = SubagentReportStatus.Pending };

    [Fact]
    public void A_completion_updates_the_status_summary_and_report_progress_of_the_stored_receipt()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "running", Background: true, Color: "blue"));

        Assert.True(_store.Apply("s1", "c1", Done()));

        var receipt = Stored()!;
        Assert.Equal(("done", "summary", true, "pending", "blue"), (receipt.Status, receipt.Summary, receipt.Background, receipt.ReportStatus, receipt.Color));
    }

    [Fact]
    public void Invariant_a_replayed_running_update_never_downgrades_a_finished_receipt()
    {
        // 2.x 里父 Run 在 finally 里整段写回自己的内存转录,旧快照会把已完成的子代理降级回 running;3.0 里最后写的人是谁都一样:终态压过运行中。
        CommitTaskResult(new SubagentReceipt("r:sub:1", "running", Background: true));
        _store.Apply("s1", "c1", Done());

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Running });
        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Queued });

        Assert.Equal("done", Stored()!.Status);
    }

    [Fact]
    public void The_first_terminal_state_wins_a_later_different_terminal_state_does_not_overwrite_it()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "running", Background: true));
        _store.Apply("s1", "c1", Done());

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Aborted });

        Assert.Equal("done", Stored()!.Status);
    }

    [Fact]
    public void Invariant_reported_is_the_end_of_report_progress_it_is_not_pulled_back_to_pending()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "done", Background: true, ReportStatus: "reported"));

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Report = SubagentReportStatus.Pending });
        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Report = SubagentReportStatus.Blocked });

        Assert.Equal("reported", Stored()!.ReportStatus);
    }

    [Fact]
    public void Blocked_then_a_retry_moves_it_on_until_reported()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "done", Background: true, ReportStatus: "pending"));

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Report = SubagentReportStatus.Blocked });
        Assert.Equal("blocked", Stored()!.ReportStatus);
        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Report = SubagentReportStatus.Pending });
        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Report = SubagentReportStatus.Reported });

        Assert.Equal("reported", Stored()!.ReportStatus);
    }

    [Fact]
    public void Invariant_an_update_that_arrives_before_the_receipts_message_is_stored_not_lost_and_is_applied_when_the_session_settles()
    {
        // 后台子代理跑得比父 Run 提交那条工具结果还快:回执所在的消息还不在库里。
        Assert.False(_store.Apply("s1", "c1", Done()));
        Assert.Equal(1, _store.PendingCount);

        CommitTaskResult(new SubagentReceipt("r:sub:1", "running", Background: true));
        Assert.Equal("running", Stored()!.Status); // 还没补做
        _store.Flush("other-session"); // 别的会话的 flush 不动它
        Assert.Equal(1, _store.PendingCount);
        _store.Flush("s1");

        Assert.Equal(0, _store.PendingCount);
        Assert.Equal(("done", "pending"), (Stored()!.Status, Stored()!.ReportStatus));
    }

    [Fact]
    public void Two_pending_updates_combine_monotonically_before_they_are_written()
    {
        _store.Apply("s1", "c1", Done());
        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Running, Report = SubagentReportStatus.Injecting });

        CommitTaskResult(new SubagentReceipt("r:sub:1", "running", Background: true));
        _store.Flush("s1");

        Assert.Equal(("done", "injecting"), (Stored()!.Status, Stored()!.ReportStatus));
    }

    [Fact]
    public void A_tool_result_that_has_no_receipt_yet_gets_one_for_example_an_aborted_run_filled_in_the_orphan_result()
    {
        CommitTaskResult(null, isError: true);

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Aborted, Background = true, Report = SubagentReportStatus.Pending });

        var receipt = Stored()!;
        Assert.Equal(("r:sub:1", "aborted", true, "pending"), (receipt.ChildRunId, receipt.Status, receipt.Background, receipt.ReportStatus));
    }

    [Fact]
    public void The_receipt_error_is_kept_with_its_localisation_key()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "running"));
        var error = AgentError.Create(AgentErrorCode.Network, "reset", messageKey: "net.reset");

        _store.Apply("s1", "c1", new ReceiptPatch("r:sub:1") { Status = SubagentStatus.Error, Error = error });

        Assert.Equal(("error", "net.reset"), (Stored()!.Status, Stored()!.Error!.MessageKey));
    }

    [Fact]
    public void An_update_for_a_call_that_does_not_exist_stays_pending_and_never_throws() =>
        Assert.False(_store.Apply("s1", "no-such-call", Done()));

    [Fact]
    public void Reading_a_report_recovers_the_full_text_from_the_child_sessions_last_assistant_message_and_the_agent_type_from_the_call()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "done", "short", true, "pending"));
        _repo.Ensure(new SessionCreate("ws", Id: "s1:sub:r:sub:1", ParentSessionId: "s1"));
        _repo.Commit("s1:sub:r:sub:1", MessageCodec.ToRecord(new Message(new MessageId("a1"), Role.Assistant, [new TextPart("early")], DateTimeOffset.FromUnixTimeMilliseconds(1))));
        _repo.Commit("s1:sub:r:sub:1", MessageCodec.ToRecord(new Message(new MessageId("a2"), Role.Assistant, [new TextPart("the "), new TextPart("FULL final report")], DateTimeOffset.FromUnixTimeMilliseconds(2))));

        var report = _store.ReadReport("s1", "c1")!;

        Assert.Equal(("the FULL final report", "short", "code-analyst", SubagentReportStatus.Pending), (report.Text, report.Summary, report.AgentType, report.ReportStatus));
        Assert.Equal("r:sub:1", report.ChildRunId.Value);
    }

    [Fact]
    public void A_foreground_receipt_is_not_a_report_only_background_ones_have_a_delivery_step()
    {
        CommitTaskResult(new SubagentReceipt("r:sub:1", "done", "short"));

        Assert.Null(_store.ReadReport("s1", "c1"));
        Assert.Null(_store.ReadReport("s1", "nope"));
    }

    [Fact]
    public void Child_ids_are_conventions_derived_in_one_place()
    {
        Assert.Equal("p:sub:3", SubagentIds.ChildRun(new RunId("p"), 3).Value);
        Assert.Equal("s:sub:p:sub:3", SubagentIds.ChildSession("s", new RunId("p:sub:3")));
    }
}
