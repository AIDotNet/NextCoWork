using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.TodoLists;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;
using Ncw.Agent.Parity.Tests.Trace;
using Ncw.Agent.Storage;
using Ncw.Agent.Tools;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>回放出来的结果:和录制里 <c>expected</c> 同形的三份规范化数据。</summary>
public sealed record Replayed(JsonArray Events, JsonArray Rows, JsonArray Requests, JsonArray Runs, JsonArray Interactions, JsonArray Files, IReadOnlyList<string> Diagnostics);

/// <summary>
/// 把一个录制回放到 C# 内核上:假上游(<see cref="ScriptedUpstream"/>)→ 真 <see cref="LlmRouter"/>(解码 SSE 原文)→ 真 <see cref="AgentSession"/> /
/// <c>AgentLoop</c> → 真 SQLite;工具是测试内的假工具。用户操作(审批决定、插话、中止)挂在和录制侧相同的确定触发点上。
/// </summary>
public sealed class ReplayRig : IDisposable
{
    private const string SessionId = "sess-1";
    private const string WorkspaceId = "ws-1";

    private readonly Recording _recording;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-parity-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly SessionRepository _repo;
    private readonly ScriptedUpstream _upstream;
    private readonly JsonArray _actions;
    private readonly List<Message> _steerQueue = [];
    private readonly object _steerGate = new();
    private readonly List<string> _diagnostics = [];
    private readonly AgentSession _session;
    private CancellationTokenSource _cts = new();
    private readonly bool _permission;
    private readonly InteractionGate? _gate;
    private readonly PermissionRuns _permissionRuns = new();
    private readonly JournalSink _journal = new();
    private readonly LocalEnvironment _env = new("dev");
    private readonly string _workspace;
    private readonly JsonObject _answers;
    private readonly ToolRuntime? _runtime;

    public ReplayRig(Recording recording)
    {
        _recording = recording;
        var input = recording.Input;
        Directory.CreateDirectory(_root);
        _db = AgentDatabase.Open(Path.Combine(_root, "db"));
        _repo = new SessionRepository(_db);
        _repo.Create(new SessionCreate(WorkspaceId, SessionId));
        foreach (var message in input["history"]!.AsArray())
        {
            var m = message!.AsObject();
            _repo.Commit(SessionId, new MessageRecord(
                m["id"]!.GetValue<string>(),
                m["role"]!.GetValue<string>(),
                m["parts"]!.ToJsonString(),
                m["createdAt"]!.GetValue<long>(),
                1,
                m["internal"]?.GetValue<bool>() == true));
        }

        _actions = input["actions"]!.AsArray();
        _upstream = new ScriptedUpstream([.. input["upstream"]!.AsArray().Select(i => i!.AsObject())], (request, chunk) => Fire(new JsonObject
        {
            ["upstream"] = request,
            ["afterChunk"] = chunk,
        }));

        var config = new ProviderConfigSource(input);
        var credentials = new CredentialResolver(new KeyStore(), new OAuthRegistry([]), _upstream, TimeProvider.System);
        var router = new LlmRouter(
            config, _upstream, credentials, TimeProvider.System, new CountingIds(), new NoDelay(),
            new LlmRouterOptions { BaseDelay = TimeSpan.Zero, RateLimitFloor = TimeSpan.Zero, ProviderErrorFloor = TimeSpan.Zero });

        var behaviors = input["toolBehaviors"]!.AsObject();
        var tools = input["tools"]!.AsArray().Select(t => (IAgentTool)new ScriptedTool(t!.AsObject(), behaviors, Fire)).ToList();
        var features = input["features"]!.AsArray().Select(f => f!.GetValue<string>()).ToHashSet();
        _permission = features.Contains("permission");
        _answers = input["interactions"]!.AsObject();
        _workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(_workspace);
        _workspace = _env.Fs.RealPathAsync(EnvPath.Parse(_workspace, OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix)).AsTask().GetAwaiter().GetResult().Value;
        var extensionList = new List<IAgentExtension>();
        if (_permission)
        {
            // 真实的权限策略:权限扩展 + 交互闸门(2.x 一侧是 createApprovalFn + InteractionGate)。
            _gate = new InteractionGate(new CountingIds(), journal: _journal);
            extensionList.Add(new PermissionExtension(_permissionRuns, PermissionRuleBook.CreateDefault(null), reviewer: null));
        }
        else
        {
            extensionList.Add(new ScriptedApproval(input["approvals"]!.AsObject(), Fire));
        }

        if (features.Contains("todos"))
        {
            extensionList.Add(new TodoExtension(new TodoOptions { Transcript = new SessionTranscriptReader(_repo) }));
        }

        IReadOnlyList<IAgentExtension> extensions = extensionList;
        if (features.Contains("fence"))
        {
            // 真实的 Read / Write(跑在真临时目录上),计划模式的写入围栏由工具运行时给出。
            tools.AddRange([new ReadTool(), new WriteTool()]);
            var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;
            _runtime = new ToolRuntime
            {
                Environment = _env,
                Root = EnvPath.Parse(_workspace, style),
                WriteFence = input["fence"] is JsonValue f ? EnvPath.Parse(Path.Combine(_workspace, f.GetValue<string>()), style) : null,
            };
        }

        var resumeDelays = input["resumeDelaysMs"]!.AsArray().Select(d => TimeSpan.FromMilliseconds(d!.GetValue<int>())).ToArray();

        _session = new AgentSession(SessionId, WorkspaceId, new AgentSessionDeps
        {
            Sessions = _repo,
            Llm = router,
            Ids = new CountingIds(),
            Clock = TimeProvider.System,
            Delayer = new NoDelay(),
            Environment = _env,
            WorkspaceRoot = EnvPath.Parse(_workspace, OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix),
            Extensions = extensions,
            Tools = new StaticToolSource(tools),
            ResolveModel = model => config.Bindings.FirstOrDefault(b => b.Alias == model.Alias && (model.ProviderId is null || b.ProviderId == model.ProviderId)),
            LoopOptions = new AgentLoopOptions { ResumeDelays = resumeDelays },
            Diagnostics = new Diagnostics(_diagnostics),
        });
    }

    /// <summary>触发用户操作:和录制里 <c>actions[].on</c> 逐字相等的触发点才响。</summary>
    private void Fire(JsonObject trigger)
    {
        foreach (var action in _actions)
        {
            var a = action!.AsObject();
            if (!JsonNode.DeepEquals(a["on"], trigger))
            {
                continue;
            }

            var what = a["do"]!.AsObject();
            if (what["abort"] is not null)
            {
                _cts.Cancel();
            }
            else
            {
                lock (_steerGate)
                {
                    foreach (var s in what["steer"]!.AsArray())
                    {
                        var item = s!.AsObject();
                        _steerQueue.Add(new Message(
                            new MessageId(item["id"]!.GetValue<string>()), Role.User, [new TextPart(item["text"]!.GetValue<string>())],
                            DateTimeOffset.UtcNow, Internal: item["internal"]?.GetValue<bool>() == true));
                    }
                }
            }
        }
    }

    public async Task<Replayed> RunAsync()
    {
        var input = _recording.Input;
        var steps = input["steps"]!.AsArray();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in input["history"]!.AsArray())
        {
            known.Add(m!["id"]!.GetValue<string>());
        }

        var n = 0;
        foreach (var step in steps)
        {
            known.Add(step!["id"]?.GetValue<string>() ?? $"u{++n}");
        }

        foreach (var action in _actions)
        {
            if (action!["do"]!["steer"] is JsonArray steer)
            {
                foreach (var s in steer)
                {
                    known.Add(s!["id"]!.GetValue<string>());
                }
            }
        }

        var ids = new IdMap(known);
        var events = new JsonArray();
        var runs = new JsonArray();
        n = 0;
        foreach (var step in steps)
        {
            n++;
            var s = step!.AsObject();
            _cts = new CancellationTokenSource();
            var model = ModelRef.Unpinned(s["model"]?.GetValue<string>() ?? input["model"]!["alias"]!.GetValue<string>());
            var inputText = s["input"]!.GetValue<string>();
            var id = s["id"]?.GetValue<string>() ?? $"u{n}";
            Message[] messages = inputText.Length == 0
                ? []
                : [new Message(new MessageId(id), Role.User, [new TextPart(inputText)], DateTimeOffset.UtcNow)];
            var settings = input["settings"]!.AsObject();
            var request = new RunRequest(new RunId($"run-{n}"), messages, model, Thinking(s["thinking"]?.GetValue<string>() ?? "off"))
            {
                Depth = s["depth"]?.GetValue<int>() ?? 0,
                Settings = new SessionSettings
                {
                    AutoCompact = settings["autoCompact"]?.GetValue<bool>() == true,
                    MaxOutputTokens = settings["maxOutputTokens"]?.GetValue<int>() ?? input["model"]!["maxOutputTokens"]!.GetValue<int>(),
                    ProjectInstructions = false,
                },
                TakeSteering = TakeSteering,
                EnrichToolContext = _runtime is null ? null : ExecutionToolContext.Enricher(_runtime),
            };
            var sink = new TriggerSink(Fire);
            RunInteractions? interactions = null;
            Task? responder = null;
            using var responderStop = new CancellationTokenSource();
            if (_permission)
            {
                interactions = _gate!.OpenRun(request.RunId, request.Depth);
                _permissionRuns.Register(new PermissionRunState(request.RunId, SessionId, _env, interactions, PermissionMode.Ask)
                {
                    Root = EnvPath.Parse(_workspace, OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix),
                    WorkspaceId = WorkspaceId,
                    UserTask = inputText,
                });
                responder = Task.Run(() => AnswerAsync(responderStop.Token));
            }

            var outcome = await _session.RunAsync(request, sink, _cts.Token).ConfigureAwait(false);
            if (_permission)
            {
                await responderStop.CancelAsync().ConfigureAwait(false);
                await responder!.ConfigureAwait(false);
                interactions!.End(outcome.Status);
                _permissionRuns.Remove(request.RunId);
            }

            var shaped = TraceBuilder.Events(sink.Events, ids);
            events.Add(new JsonObject { ["e"] = "run", ["n"] = n });
            foreach (var e in shaped)
            {
                events.Add(e!.DeepClone());
            }

            var run = new JsonObject { ["status"] = outcome.Status.ToString().ToLowerInvariant() };
            if (outcome.Error is { } error)
            {
                run["errorCode"] = Normalize.SnakeCase(error.Code.ToString());
            }

            runs.Add(run);
        }

        var rows = TraceBuilder.Rows(_repo.History(SessionId), ids);
        var requests = new JsonArray([.. _upstream.Requests.Select(r => (JsonNode)new JsonObject { ["url"] = r.Url.ToString(), ["body"] = r.Body?.DeepClone() })]);
        var seen = new JsonArray([.. _journal.Requests.OfType<ToolPermission>().Select(t => (JsonNode)new JsonObject
        {
            ["kind"] = "tool_permission",
            ["callId"] = t.CallId,
            ["toolName"] = t.ToolName,
            ["readOnly"] = t.Traits.ReadOnly,
            ["destructive"] = t.Traits.Destructive,
        })]);
        var files = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetRelativePath(_workspace, p).Replace('\\', '/'), StringComparer.Ordinal))
        {
            files.Add((JsonNode)new JsonObject { ["path"] = Path.GetRelativePath(_workspace, path).Replace('\\', '/'), ["content"] = File.ReadAllText(path) });
        }

        return new Replayed(events, rows, requests, runs, seen, files, _diagnostics);
    }

    /// <summary>扮演用户:盯着交互闸门,按录制里 <c>interactions[callId]</c> 回答每一次工具审批弹窗。</summary>
    private async Task AnswerAsync(CancellationToken stop)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        while (!stop.IsCancellationRequested)
        {
            foreach (var pending in _gate!.Snapshot().Pending.OfType<ToolPermission>())
            {
                if (!answered.Add(pending.Id.Value))
                {
                    continue;
                }

                var answer = _answers[pending.CallId]?.AsObject();
                PermissionChoice choice = answer?["kind"]?.GetValue<string>() switch
                {
                    "deny" => PermissionChoice.Deny(answer["reason"]?.GetValue<string>()),
                    _ => PermissionChoice.AllowOnce,
                };
                _gate.Respond(pending.Id, pending.Version, new ToolPermissionResponse(choice), "parity");
            }

            try
            {
                await Task.Delay(1, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private IReadOnlyList<Message> TakeSteering()
    {
        lock (_steerGate)
        {
            var taken = _steerQueue.ToList();
            _steerQueue.Clear();
            return taken;
        }
    }

    private static ThinkingLevel Thinking(string level) => level switch
    {
        "off" => ThinkingLevel.Off,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        _ => ThinkingLevel.Auto,
    };

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>收集事件,并在第 k 个内容增量被发出之后触发 <c>{delta: k}</c> 用户操作(和录制侧在事件流上数的是同一件事)。</summary>
    private sealed class TriggerSink(Action<JsonObject> fire) : IRunSink
    {
        private readonly ListRunSink _inner = new();
        private int _deltas;

        public IReadOnlyList<AgentEvent> Events => _inner.Events;

        public async ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
        {
            await _inner.EmitAsync(ev, ct).ConfigureAwait(false);
            // 工具参数流的第一条增量是只带工具名的空串(让界面提前画卡片),不是内容。
            if (ev is MessageDeltaEvent { Delta.Length: > 0 })
            {
                fire(new JsonObject { ["delta"] = ++_deltas });
            }
        }
    }

    private sealed class JournalSink : IInteractionJournal
    {
        private readonly object _gate = new();
        private readonly List<InteractionRequest> _opened = [];

        public IReadOnlyList<InteractionRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _opened];
                }
            }
        }

        public void Opened(InteractionRequest request)
        {
            lock (_gate)
            {
                _opened.Add(request);
            }
        }

        public void Settled(InteractionResolution resolution)
        {
        }
    }

    private sealed class NoDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingIds : IIdGenerator
    {
        private int _n;

        public string NewId() => $"gen{Interlocked.Increment(ref _n):D4}";
    }

    private sealed class KeyStore : ICredentialStore
    {
        public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) => ValueTask.FromResult<string?>("sk-test");

        public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Diagnostics(List<string> sink) : ILoopDiagnostics
    {
        public void Warn(string message, Exception? error = null)
        {
            lock (sink)
            {
                sink.Add(error is null ? message : $"{message} {error.GetType().Name}: {error.Message}");
            }
        }
    }
}
