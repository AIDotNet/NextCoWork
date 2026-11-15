using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>
/// 内联可视化的控制端链路(M8):真实 Agent(进程内,真工具 / 权限链 / 交互闸门 / SQLite)+ <see cref="DeviceLink"/> + <see cref="WidgetActions"/> + <see cref="WidgetBridge"/> + Store。
/// 模型画一张交互卡片 → 卡片经 <c>tool_progress</c> 进 Store、转录里派生出可提交的卡片 → 页面经桥提交 → 设备接受、Run 拿到值、卡片变成「已提交」(终态);
/// 状态往返并进镜像;未声明的动作被拒绝;非只读动作第一次要确认(取消 / 确认后执行)。页面一侧用桥的原始消息模拟(WebView 里的真实行为见 NcwWidgetSmokeTests)。
/// </summary>
public sealed class WidgetLinkTests : IAsyncLifetime
{
    private const string Token = "widget-link-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncww-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-widget-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _agentCts = new();
    private readonly ClientStores _stores = new();
    private readonly LockedDispatcher _ui = new();
    private readonly FakeLlm _llm = new();
    private Task _agent = Task.CompletedTask;
    private DeviceLink _link = null!;
    private WidgetActions _widgets = null!;
    private ConversationActions _conversation = null!;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    private sealed class LockedDispatcher : IUiDispatcher
    {
        public readonly object Gate = new();

        public void Post(Action action)
        {
            lock (Gate)
            {
                action();
            }
        }
    }

    private sealed class NoDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeModels(ILlmClient client) : IModelResolver
    {
        public ILlmClient Client { get; } = client;

        public ModelBinding? Resolve(ModelRef model) => model.Alias == "sonnet" ? new ModelBinding("sonnet", "p1", "up", 200_000, 8192) : null;
    }

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<IReadOnlyList<LlmEvent>> _scripts = new();

        public FakeLlm Then(IReadOnlyList<LlmEvent> events)
        {
            lock (_scripts)
            {
                _scripts.Enqueue(events);
            }

            return this;
        }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
        {
            IReadOnlyList<LlmEvent>? script;
            lock (_scripts)
            {
                _scripts.TryDequeue(out script);
            }

            foreach (var ev in script ?? [new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))])
            {
                await Task.Yield();
                yield return ev;
            }
        }
    }

    private sealed class Launcher : IUrlLauncher
    {
        public Task OpenAsync(string url) => Task.CompletedTask;
    }

    private sealed class Confirmations : IWidgetConfirmations
    {
        public bool Answer { get; set; }
        public List<WidgetActionPrompt> Prompts { get; } = [];

        public Task<bool> ConfirmOpenUrlAsync(string url) => Task.FromResult(false);

        public Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Answer);
        }
    }

    private static IReadOnlyList<LlmEvent> Say(string text) =>
        [new MessageStart("m", "p", "anthropic", "up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered)];

    private static IReadOnlyList<LlmEvent> Show(string callId, JsonObject input) =>
    [
        new MessageStart("m", "p", "anthropic", "up"),
        new ToolCallStart(0, callId, "visualize_show_widget"),
        new ToolCallDelta(0, callId, input.ToJsonString()),
        new ToolCallEnd(0, callId),
        new MessageEnd(StopReason.ToolUse, new TokenUsage(10, 5)),
    ];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        var host = new AgentHost(
            new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null),
            services: new AgentServices { Models = new FakeModels(_llm), Delayer = new NoDelay(), Loop = new AgentLoopOptions { ResumeDelays = [] } });
        _agent = host.RunAsync(_agentCts.Token);
        _link = new DeviceLink(_stores, _ui, "local-widget", DeviceKind.Local, "本机",
            async ct => await LocalTransport.ConnectAsync(Endpoint, ct),
            new ClientHello("widget-link", "3.0.0", LocalToken: Token));
        var devices = new DeviceRegistry { Local = _link };
        _widgets = new WidgetActions(devices, _stores.Widgets);
        _conversation = new ConversationActions(devices);
        _link.Start();
        await WaitUntil(() => Read(() => _stores.Workspace.State.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == DataStates.Ready)), "connected");
    }

    public async ValueTask DisposeAsync()
    {
        await _link.DisposeAsync();
        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }

    private T Read<T>(Func<T> read)
    {
        lock (_ui.Gate)
        {
            return read();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    private WidgetRow? RowOf(SessionRef session, string callId) => Read(() => TranscriptBuilder
        .Build(_stores.Conversations.State, session, null, DateTimeOffset.UtcNow, deviceUnknown: false).OfType<WidgetRow>().FirstOrDefault(r => r.CallId == callId));

    /// <summary>为这张卡造一座桥(和桌面视图用同一条路:WidgetHostContext.CreateBridge),设施事实每次现从转录里读。</summary>
    private WidgetBridge BridgeFor(SessionRef session, string callId, Confirmations confirmations, List<string> replies)
    {
        var context = new WidgetHostContext(_stores, _widgets, () => new Launcher(), () => confirmations);
        return context.CreateBridge(new WidgetRef(session.DeviceId, session.SessionId, callId), () =>
        {
            var row = RowOf(session, callId)!;
            var entry = Read(() => _stores.Widgets.State.Of(new WidgetRef(session.DeviceId, session.SessionId, callId)));
            return new WidgetBridgeContext(
                row.Facts with { Submitted = row.Facts.Submitted || entry.Submit == WidgetSubmit.Accepted, AwaitingSubmit = row.Facts.AwaitingSubmit && entry.Submit != WidgetSubmit.Closed },
                row.Card?.Actions ?? []);
        }, reply => { lock (replies) { replies.Add(reply); } });
    }

    private static string Call(int id, string method, string args) => $"{{\"type\":\"ncw:widget:call\",\"id\":{id},\"method\":\"{method}\",\"args\":{args}}}";

    private static JsonElement Last(List<string> replies)
    {
        lock (replies)
        {
            return JsonDocument.Parse(replies[^1]).RootElement.Clone();
        }
    }

    [Fact]
    public async Task An_interactive_widget_is_submitted_through_the_bridge_and_the_card_ends_up_submitted_in_the_transcript()
    {
        var project = await _link.AddProjectAsync(_workspace);
        var input = new JsonObject
        {
            ["title"] = "pick_db",
            ["widget_code"] = "<div id=\"o\" onclick=\"steal()\">pick</div><script>window.ncw.getState()</script>",
            ["loading_messages"] = new JsonArray("drawing"),
            ["interactive"] = true,
        };
        _llm.Then(Show("w1", input)).Then(Say("got it"));
        var session = (await _conversation.CreateSessionAsync(project, "sonnet"))!.Value;
        var started = await _conversation.SendAsync(session, "ask via widget", new SendOptionsDto("sonnet"));
        Assert.Equal(SendOutcome.Started, started.Outcome);
        var run = started.Run!.Value;

        // 卡片先经 tool_progress 进 Store,转录里派生出一张「可提交」的卡片(消毒后的代码,不是工具入参里的原文)
        await WaitUntil(() => RowOf(session, "w1") is { Card: not null } row && WidgetView.CanSubmit(row.Facts), "a submittable live card");
        var row = RowOf(session, "w1")!;
        Assert.Equal(WidgetPhase.Ready, row.Phase);
        Assert.True(row.Card!.Interactive);
        Assert.DoesNotContain("onclick", row.Card.Code);
        Assert.Contains("<script>", row.Card.Code);
        Assert.DoesNotContain("<script", row.Card.Snapshot);
        Assert.Equal(session.DeviceId, row.Env!.DeviceId);

        var replies = new List<string>();
        var confirmations = new Confirmations();
        var bridge = BridgeFor(session, "w1", confirmations, replies);

        // 状态往返:页面 setState → 设备 → 镜像;读回来是同一份
        await bridge.HandleAsync(Call(1, "setState", "{\"state\":{\"slider\":7}}"));
        Assert.True(Last(replies).GetProperty("ok").GetBoolean());
        Assert.Equal("{\"slider\":7}", Read(() => _stores.Widgets.State.Of(new WidgetRef(session.DeviceId, session.SessionId, "w1")).StateJson));
        var fromDevice = await _widgets.GetStateAsync(session.DeviceId, session.SessionId, "w1");
        Assert.Equal("{\"slider\":7}", fromDevice.Value!.State);

        // 页面提交:设备接受(先到先得),镜像标「已接受」;重复提交被桥挡在本地
        await bridge.HandleAsync(Call(2, "submit", "{\"value\":\"\\\"postgres\\\"\"}"));
        Assert.True(Last(replies).GetProperty("ok").GetBoolean(), replies[^1]);
        Assert.Equal(WidgetSubmit.Accepted, Read(() => _stores.Widgets.State.Of(new WidgetRef(session.DeviceId, session.SessionId, "w1")).Submit));
        await bridge.HandleAsync(Call(3, "submit", "{\"value\":\"\\\"sqlite\\\"\"}"));
        Assert.False(Last(replies).GetProperty("ok").GetBoolean());
        Assert.Equal("already_submitted", Last(replies).GetProperty("error").GetString());

        // Run 拿到值收尾;转录里的卡片是终态「已提交」,提交窗口关闭
        await WaitUntil(() => Read(() => _stores.Conversations.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }), "run done");
        await WaitUntil(() => RowOf(session, "w1") is { Card.Submitted: not null }, "the card carries the submitted value");
        var done = RowOf(session, "w1")!;
        Assert.Equal("\"postgres\"", done.Card!.Submitted);
        Assert.Equal(WidgetPhase.Ready, done.Phase);
        Assert.False(WidgetView.CanSubmit(done.Facts));
    }

    [Fact]
    public async Task Declared_actions_are_checked_by_the_device_and_a_non_read_only_one_needs_the_users_confirmation_first()
    {
        var project = await _link.AddProjectAsync(_workspace);
        var input = new JsonObject
        {
            ["title"] = "dashboard",
            ["widget_code"] = "<div>buttons</div>",
            ["loading_messages"] = new JsonArray("drawing"),
            ["actions"] = new JsonArray(new JsonObject
            {
                ["id"] = "say",
                ["label"] = "Say a word",
                ["tool"] = "Bash",
                ["input"] = new JsonObject { ["command"] = "echo {{word}}" },
                ["params"] = new JsonObject { ["word"] = new JsonObject { ["type"] = "enum", ["values"] = new JsonArray("hello", "world") } },
            }),
        };
        _llm.Then(Show("w1", input)).Then(Say("ok"));
        var session = (await _conversation.CreateSessionAsync(project, "sonnet"))!.Value;
        var run = (await _conversation.SendAsync(session, "draw", new SendOptionsDto("sonnet"))).Run!.Value;
        await WaitUntil(() => Read(() => _stores.Conversations.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }), "run done");
        await WaitUntil(() => RowOf(session, "w1") is { Card: not null }, "the card");

        // 动作清单随卡片到控制端(只有 id / 文案 / 参数约束,没有要跑的工具和入参)
        var action = Assert.Single(RowOf(session, "w1")!.Card!.Actions);
        Assert.Equal(("say", "Say a word"), (action.Id, action.Label));
        Assert.Equal(["hello", "world"], action.Params.Single().Value.Values);

        var replies = new List<string>();
        var confirmations = new Confirmations();
        var bridge = BridgeFor(session, "w1", confirmations, replies);

        // 没声明的动作:设备拒绝,页面拿到稳定的错误码
        await bridge.HandleAsync(Call(1, "runAction", "{\"id\":\"rm_rf\"}"));
        Assert.Equal("widgets.action.undeclared", Last(replies).GetProperty("error").GetString());

        // 参数越界(不在枚举里):拒绝
        await bridge.HandleAsync(Call(2, "runAction", "{\"id\":\"say\",\"params\":{\"word\":\"; rm -rf /\"}}"));
        Assert.False(Last(replies).GetProperty("ok").GetBoolean());
        Assert.Empty(confirmations.Prompts); // 参数都没过校验:连确认框都不该出现

        // 第一次要问:确认框里是绑定之后真实的工具与入参 + 执行环境;取消 → 不执行
        confirmations.Answer = false;
        await bridge.HandleAsync(Call(3, "runAction", "{\"id\":\"say\",\"params\":{\"word\":\"hello\"}}"));
        var prompt = Assert.Single(confirmations.Prompts);
        Assert.Contains("echo hello", prompt.InputJson);
        Assert.Equal("local", prompt.Environment!.Environment);
        Assert.Equal("declined", Last(replies).GetProperty("error").GetString());

        // 确认 → 执行,输出回给页面(不进模型上下文)
        confirmations.Answer = true;
        await bridge.HandleAsync(Call(4, "runAction", "{\"id\":\"say\",\"params\":{\"word\":\"world\"}}"));
        var ok = Last(replies);
        Assert.True(ok.GetProperty("ok").GetBoolean(), replies[^1]);
        Assert.Contains("world", ok.GetProperty("value").GetProperty("output").GetString());
        Assert.True(ok.GetProperty("value").GetProperty("ok").GetBoolean());
    }
}
