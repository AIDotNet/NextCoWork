using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.UI.Tests;

/// <summary>改动审查的脚本化后端(截图用):变更集、文件 diff 固定;撤销 / 重做成功。</summary>
public sealed class ScriptedReview : IReviewCommands
{
    public ReviewChangeSetDto[] Sets { get; set; } = [];

    public Dictionary<string, ReviewFileDiffDto> Diffs { get; } = [];

    public string[] Conflicts { get; set; } = [];

    public Task<ActionResult<ReviewChangeSetDto[]>> ListAsync(string deviceId, string sessionId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<ReviewChangeSetDto[]>(ActionOutcome.Ok, Sets));

    public Task<ActionResult<ReviewFileDiffDto?>> FileDiffAsync(string deviceId, string runId, string path, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<ReviewFileDiffDto?>(ActionOutcome.Ok, Diffs.GetValueOrDefault(path)));

    public Task<ActionResult<string[]>> PrecheckUndoAsync(string deviceId, string runId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<string[]>(ActionOutcome.Ok, Conflicts));

    public Task<ActionResult<ReviewMutationDto>> UndoAsync(string deviceId, string runId, bool force = false, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<ReviewMutationDto>(ActionOutcome.Ok, new ReviewMutationDto(runId, ReviewStates.Reverted, [new("a", ReviewFileStatuses.Ok)])));

    public Task<ActionResult<ReviewMutationDto>> RedoAsync(string deviceId, string runId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<ReviewMutationDto>(ActionOutcome.Ok, new ReviewMutationDto(runId, ReviewStates.Applied, [])));
}

/// <summary>脚本化的终端后端:创建 / 列表走 Store,订阅只是记下接收器,输出由测试直接推给它。</summary>
public sealed class ScriptedTerminals(ClientStores stores) : ITerminalCommands
{
    public Dictionary<string, ITerminalSink> Sinks { get; } = [];

    public TerminalIntentDto? NextIntent { get; set; }

    public Task<ActionResult<TerminalDto[]>> ListAsync(string deviceId, string projectId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<TerminalDto[]>(ActionOutcome.Ok, [.. stores.Terminals.State.InProject(deviceId, projectId).Select(v => v.Info)]));

    public Task<ActionResult<TerminalPrepareResult>> PrepareAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<TerminalPrepareResult>(ActionOutcome.Ok, new TerminalPrepareResult(null, NextIntent)));

    public Task<ActionResult<TerminalApproveResult>> ApproveAsync(string deviceId, string intentId, bool approved, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<TerminalApproveResult>(ActionOutcome.Ok, new TerminalApproveResult(approved ? "cred" : null)));

    public Task<ActionResult<TerminalDto>> CreateAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, string? approval = null, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<TerminalDto>(ActionOutcome.Ok, null));

    public Task<ActionResult<Unit>> AttachAsync(string deviceId, string terminalId, ITerminalSink sink, CancellationToken ct = default)
    {
        Sinks[terminalId] = sink;
        return Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));
    }

    public Task DetachAsync(string deviceId, string terminalId)
    {
        Sinks.Remove(terminalId);
        return Task.CompletedTask;
    }

    public Task<ActionResult<Unit>> WriteAsync(string deviceId, string terminalId, byte[] data, string? idem = null, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));

    public Task<ActionResult<Unit>> ResizeAsync(string deviceId, string terminalId, int columns, int rows, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));

    public Task<ActionResult<Unit>> KillAsync(string deviceId, string terminalId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));

    public Task<ActionResult<Unit>> CloseAsync(string deviceId, string terminalId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));
}

/// <summary>检查器截图测试的一整套数据:对话夹具的基础 Store + 内存里的文件 / Git + 脚本化的审查和终端。</summary>
public sealed record InspectorRig(ClientStores Stores, MockFiles Files, MockGit Git, ScriptedReview Review, ScriptedTerminals Terminals, InspectorServices Services)
{
    public static readonly ProjectRef Project = ConversationFixtures.Project;

    public static InspectorRig Create(string sessionId = "s-ncw-1")
    {
        var stores = ConversationFixtures.Base(sessionId);
        ConversationFixtures.Session(stores, sessionId,
        [
            ConversationFixtures.User("u1", "把检查器的文件页签接到 files.list,目录惰性展开。", 12),
            ConversationFixtures.Assistant("a1", 11,
                ConversationFixtures.Text("好的,我先读一下现有的 FileTreeViewModel。"),
                ConversationFixtures.Call("c1", "Read", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Client/ViewModels/FileTreeViewModel.cs\"}"),
                ConversationFixtures.Call("k1", "Task", "{\"subagent_type\":\"code-analyst\",\"description\":\"阅读 kernel/upstream\"}")),
            ConversationFixtures.ToolResults("r1", 10, ConversationFixtures.Result("c1", "(file contents)")),
        ]);
        var files = new MockFiles(stores);
        var git = new MockGit(stores);
        var review = new ScriptedReview();
        var terminals = new ScriptedTerminals(stores);
        var services = new InspectorServices(files, git, review, terminals, new SubagentActions(new DeviceRegistry()));
        return new InspectorRig(stores, files, git, review, terminals, services);
    }

    public static TerminalDto Terminal(string id, string title, string status = "running", string env = "local", string kind = "local", long created = 1_000, int? exitCode = null, string? reason = null) =>
        new(id, Project.WorkspaceId, new TerminalEnvDto(Project.DeviceId, env, env, kind), title, "/Users/token/code/NextCoWork", "/bin/zsh", 100, 30, status, created, exitCode, null, reason);

    public void AddTerminal(TerminalDto dto) =>
        Stores.Terminals.Update(m => TerminalReducer.ApplyInfo(m, Project.DeviceId, dto, 1));

    public const string SampleOutput =
        "\u001b[1;32mtoken@mac\u001b[0m \u001b[1;34m~/code/NextCoWork\u001b[0m % dotnet test tests/Ncw.Client.Tests\r\n"
        + "  Determining projects to restore...\r\n"
        + "  Ncw.Client -> src/Ncw.Client/bin/Debug/net10.0/Ncw.Client.dll\r\n"
        + "\u001b[33mwarning\u001b[0m CS8602: Dereference of a possibly null reference.\r\n"
        + "\u001b[31mFailed\u001b[0m Ncw.Client.Tests.TerminalPanelTests.Disconnect_shows_unknown\r\n"
        + "\u001b[32mPassed!\u001b[0m  - Failed: 0, Passed: 141, Skipped: 0, Total: 141\r\n"
        + "\u001b[36m中文输出\u001b[0m 与 emoji ✓ 正常\r\n"
        + "\u001b[1;32mtoken@mac\u001b[0m \u001b[1;34m~/code/NextCoWork\u001b[0m % ";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
