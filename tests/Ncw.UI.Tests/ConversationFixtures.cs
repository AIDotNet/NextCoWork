using System.Collections.Immutable;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.UI.Tests;

/// <summary>
/// 对话界面截图测试用的 Store 数据:在 <see cref="MockWorkspace"/> 的项目 / 设备之上,按场景写入会话镜像、Run、挂起的交互、子代理、目标。
/// 数据形状和设备推来的完全一致(<see cref="SessionTopicState"/>、<see cref="MessageDto"/> 的 JSON 内容块、<see cref="InteractionsDto"/>),只是不经过连接。
/// </summary>
public static class ConversationFixtures
{
    /// <summary>
    /// 把转录里的分组(「工作区」、连续工具分组,03 §6)一层层全部展开:工具行默认收在表头后面,要检查单个工具行的测试先展开。
    /// 展开只改纯 UI 状态,调用方随后照常跑一轮布局。
    /// </summary>
    public static void ExpandGroups(Ncw.Client.ViewModels.ConversationViewModel conversation)
    {
        while (conversation.Rows.OfType<Ncw.Client.ViewModels.IGroupHeaderRowViewModel>().FirstOrDefault(g => !g.IsExpanded) is { } group)
        {
            group.IsExpanded = true;
        }
    }

    public static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));
    public static readonly ProjectRef Project = new("local", "nextcowork");
    private const string Device = "local";

    public static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    public static EnvRefDto LocalEnv => new(Device, "local", "local", "local");

    public static EnvRefDto SshEnv => new(Device, "dev-server", "dev-server", "ssh");

    // ───────────────────────── 消息 ─────────────────────────

    public static MessageDto User(string id, string text, int minutesAgo = 10) =>
        new(id, "user", Ms(Now.AddMinutes(-minutesAgo)), false, $"[{{\"type\":\"text\",\"text\":{Json(text)}}}]");

    public static MessageDto Assistant(string id, int minutesAgo, params string[] parts) =>
        new(id, "assistant", Ms(Now.AddMinutes(-minutesAgo)), false, "[" + string.Join(",", parts) + "]");

    public static MessageDto ToolResults(string id, int minutesAgo, params string[] parts) =>
        new(id, "user", Ms(Now.AddMinutes(-minutesAgo)), true, "[" + string.Join(",", parts) + "]");

    public static string Text(string text) => $"{{\"type\":\"text\",\"text\":{Json(text)}}}";

    public static string Thinking(string text, int durationMs = 4200) => $"{{\"type\":\"thinking\",\"text\":{Json(text)},\"durationMs\":{durationMs}}}";

    public static string Call(string callId, string name, string inputJson) =>
        $"{{\"type\":\"tool_call\",\"callId\":\"{callId}\",\"name\":\"{name}\",\"input\":{inputJson}}}";

    public static string Result(string callId, string content, bool isError = false, string? subagent = null) =>
        $"{{\"type\":\"tool_result\",\"callId\":\"{callId}\",\"output\":{{\"content\":{Json(content)}}},\"isError\":{(isError ? "true" : "false")}{(subagent is null ? "" : ",\"subagent\":" + subagent)}}}";

    private static string Json(string text) => "\"" + System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text) + "\"";

    // ───────────────────────── Store ─────────────────────────

    /// <summary>基础数据:Mock 的项目和设备,本机是 owner,供应商镜像里有三个模型,选中 <paramref name="sessionId"/>。</summary>
    public static ClientStores Base(string sessionId)
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem(Device, w.Devices[Device] with { Role = "owner" }),
            Sessions = w.Sessions.Add(new SessionSummary("s-ncw-fav", Project, "整理发布说明", Now.AddDays(-3), Favorited: true)),
        });
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add(Device, Providers())));
        stores.Selection.Update(_ => new SelectionState(Project, sessionId));
        return stores;
    }

    public static ModelOptionDto[] ModelOptions() =>
    [
        new("anthropic", "Anthropic", "claude-opus-5-5", "Claude Opus 5.5", "claude-opus-5-5", 200_000, 32_000, true, true, true, true, "effort", ["low", "medium", "high"], "anthropic"),
        new("anthropic", "Anthropic", "claude-haiku-5", "Claude Haiku 5", "claude-haiku-5", 200_000, 16_000, true, true, true, true, null, null, "anthropic"),
        new("gateway", "My Gateway", "gpt-5-mini", "GPT-5 mini", "gpt-5-mini", 1_000_000, 32_000, false, true, false, true, "effort", ["low", "medium"], "openai-responses"),
    ];

    public static DeviceProviders Providers(OAuthProgressDto? oauth = null) => new(
        true, 7,
        [
            new ProviderDto("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", 10, true, "5m", null, true, null),
            new ProviderDto("gateway", "My Gateway", "openai-responses", "https://gateway.example.com/v1", 50, false, "5m", null, false, null),
            new ProviderDto("codex", "ChatGPT", "openai-responses", "https://chatgpt.com/backend-api", 60, true, "5m", null, true, "https://auth.openai.com"),
        ],
        [
            new ModelAliasDto("claude-opus-5-5", "anthropic", "claude-opus-5-5", 200_000, 32_000, "Claude Opus 5.5", null, null, null, true, true, [], "{}", "[\"low\",\"medium\",\"high\"]"),
            new ModelAliasDto("claude-haiku-5", "anthropic", "claude-haiku-5", 200_000, 16_000, "Claude Haiku 5", null, null, null, true, true, [], null, null),
            new ModelAliasDto("gpt-5-mini", "gateway", "gpt-5-mini", 1_000_000, 32_000, null, null, null, null, false, false, [], "{}", null),
        ],
        [],
        ImmutableDictionary<string, ImmutableArray<ProviderAccountDto>>.Empty.Add("codex",
            [new ProviderAccountDto("a1", "codex", "https://auth.openai.com", "work", 0, true, true, false, null, null, 0, 0, new CredentialAuthDto("https://auth.openai.com", "acc", "dev@example.com", "plus", null, false, false))]),
        [.. ModelOptions()], (7, 1), oauth);

    /// <summary>写入一个会话的镜像(设备推来的 <c>session/&lt;id&gt;</c> 快照)。</summary>
    public static void Session(ClientStores stores, string sessionId, IEnumerable<MessageDto> messages, string? activeRunId = null, QueueDto? queue = null)
    {
        var meta = new SessionDto(sessionId, Project.WorkspaceId, null, "user", "设计 Agent Runtime 并实现骨架", "auto", "claude-opus-5-5", null, "code", "auto", false, false, Ms(Now.AddHours(-1)), Ms(Now));
        var page = new SessionPageDto(meta, [.. messages], false, [], [], []);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, Device, new SessionTopicState(page, queue ?? new QueueDto(sessionId, 1, []), activeRunId)));
    }

    /// <summary>写入一个正在跑的 Run 的镜像。</summary>
    public static void Run(ClientStores stores, string runId, string sessionId, Func<RunView, RunView> shape)
    {
        var run = new RunRef(Device, runId);
        var view = shape(RunView.New(run, sessionId, 1) with { Phase = RunPhase.Running });
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(run, view) });
    }

    public static void Interactions(ClientStores stores, params InteractionDto[] pending) =>
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, Device, new InteractionsDto(9, pending)));

    public static ToolPermissionInteractionDto Approval(string id, string runId, string callId, string tool, string inputJson, EnvRefDto env, bool dangerous = false) => new()
    {
        Id = id,
        RunId = runId,
        CallId = callId,
        ToolName = tool,
        InternalId = tool.ToLowerInvariant(),
        InputJson = inputJson,
        Env = env,
        AllowedScopes = ["session", "project", "global"],
        SuggestedRule = $"{tool}(*)",
        Risk = dangerous ? "dangerous" : "unknown",
        CreatedAt = Ms(Now.AddSeconds(-30)),
        Version = 12,
    };

    public static void Subagent(ClientStores stores, string sessionId, string callId, string type, string description, SubagentStatus status, int tools, string? currentTool, string? summary = null)
    {
        var started = Now.AddMinutes(-3);
        var record = new SubagentRecord(sessionId, callId, type, description, status, started, SubagentView.IsTerminal(status) ? Now.AddMinutes(-1) : null, tools, currentTool)
        {
            ChildRunId = "child-" + callId,
            ChildSessionId = "child-s-" + callId,
        };
        var entry = new SubagentEntry(new SubagentRef(Device, sessionId, callId), record, new SubagentTelemetry { Summary = summary }, 1);
        stores.Conversations.Update(c => c with { Subagents = c.Subagents.SetItem(entry.Ref, entry) });
    }

    public static void Goal(ClientStores stores, string sessionId) =>
        stores.Conversations.Update(c => c with
        {
            Goals = c.Goals.SetItem(new SessionRef(Device, sessionId), new GoalEntry(
                new ActiveGoal("g1", "所有单元测试通过,并且 dotnet build 零警告", GoalOrigin.User, Now.AddMinutes(-20), 0) { Iterations = 2, LastReason = "还有 3 个测试失败" }, 4)),
        });
}
