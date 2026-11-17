using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Hooks;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Mcp;
using Ncw.Agent.Parity.Tests.Trace;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>
/// 整机回放(和 2.x 一侧 <c>lib-rt.mts</c> 对应):真 <c>RunRegistry</c> / 会话层 / 循环 / SQLite + 真实的权限扩展、交互闸门、<c>SubagentService</c>(<c>Task</c> 工具)、<c>PlanningKit</c>(计划 / 待办 / 目标)+ 真实的
/// <c>LlmRouter</c>(解码 SSE 原文);唯一假的东西是上游 HTTP(<see cref="RoutedUpstream"/>)和测试内的脚本工具。装配方的那一半(造 <c>ToolRuntime</c>、登记权限状态、打开交互作用域)照 Host 的
/// <c>ConversationRunExtension</c> 写(和 Subagents / Planning 的端到端测试同一份替身)。产出按 Run / 会话 / 路由键分段,形状与录制里的 <c>expected</c> 一致。
/// </summary>
public sealed class OrchestrationRig : IDisposable
{
    private const string WorkspaceId = "ws-1";
    private static readonly Regex PlanIdPattern = new("[0-9A-HJKMNP-TV-Z]{26}", RegexOptions.Compiled);

    private readonly Recording _recording;
    private readonly JsonObject _input;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-parity-orch-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly SessionRepository _repo;
    private readonly LocalEnvironment _env = new("dev");
    private readonly string _workspace;
    private readonly ProviderConfigSource _config;
    private readonly RoutedUpstream _upstream;
    private readonly InteractionGate _gate;
    private readonly PermissionRuns _permissionRuns = new();
    private readonly JournalSink _journal = new();
    private readonly PlanningKit _kit;
    private readonly SubagentService _subagents;
    private readonly HooksService _hooks;
    private readonly RunRegistry _registry;
    private readonly BackgroundShells _shells = new();
    private readonly ToolRegistry _toolRegistry;
    private readonly McpService? _mcp;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RunId, string> _modes = new();
    private readonly List<RunHandle> _handles = [];
    private readonly Dictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stepRuns;
    private readonly Dictionary<string, int> _internalCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<RunId, string> _labels = [];
    private readonly HashSet<int> _fired = [];
    private readonly ConcurrentDictionary<string, bool> _toolEnded = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _answers;
    private readonly object _gate2 = new();
    private readonly Diagnostics _log = new();
    private readonly string _alias;
    private readonly int _maxOutput;
    private readonly JsonArray _actions;

    public OrchestrationRig(Recording recording)
    {
        _recording = recording;
        _input = recording.Input;
        Directory.CreateDirectory(_root);
        _db = AgentDatabase.Open(Path.Combine(_root, "db"));
        _repo = new SessionRepository(_db);
        _workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(_workspace);
        _workspace = _env.Fs.RealPathAsync(EnvPath.Parse(_workspace, Style)).AsTask().GetAwaiter().GetResult().Value;
        foreach (var (rel, content) in _input["files"]!.AsObject())
        {
            var path = Path.Combine(_workspace, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content!.GetValue<string>());
        }

        _alias = _input["model"]!["alias"]!.GetValue<string>();
        _maxOutput = 32000;
        _actions = _input["actions"]!.AsArray();
        _answers = [.. _input["interactions"]!.AsArray().Select(a => a!.AsObject())];
        // 目标步骤(用户手动设 / 清目标)的 Run 是 kickoff 起的内部 Run:按内部 Run 编号(<会话键>#internalN),不算「步骤 Run」。
        _stepRuns = [.. _input["steps"]!.AsArray().Where(s => s!["goal"] is null).Select(s => s!["run"]!.GetValue<string>())];
        foreach (var step in _input["steps"]!.AsArray())
        {
            var key = step!["session"]!.GetValue<string>();
            if (_sessionIds.TryAdd(key, $"sess-{key}"))
            {
                _repo.Create(new SessionCreate(WorkspaceId, _sessionIds[key]));
            }
        }

        _upstream = new RoutedUpstream(_input["routes"]!.AsArray(), _input["upstream"]!.AsObject(), (key, request, chunk) => Fire(new JsonObject
        {
            ["upstream"] = key,
            ["request"] = request,
            ["afterChunk"] = chunk,
        }), name => Gate(name).Task, ToolResultsObservedAsync);

        _config = new ProviderConfigSource(_input);
        var credentials = new CredentialResolver(new KeyStore(), new OAuthRegistry([]), _upstream, TimeProvider.System);
        var router = new LlmRouter(
            _config, _upstream, credentials, TimeProvider.System, new CountingIds(), new NoDelay(),
            new LlmRouterOptions { BaseDelay = TimeSpan.Zero, RateLimitFloor = TimeSpan.Zero, ProviderErrorFloor = TimeSpan.Zero });

        var settings = _input["settings"]!.AsObject();
        ModelRef? evaluator = settings["goalEvaluatorModel"]?.GetValue<string>() is { Length: > 0 } judge ? ModelRef.Unpinned(judge) : null;
        var policy = settings["modelProposedGoals"]?.GetValue<string>() switch
        {
            "disabled" => GoalProposalPolicy.Disabled,
            "alwaysAsk" => GoalProposalPolicy.AlwaysAsk,
            _ => GoalProposalPolicy.Auto,
        };
        _gate = new InteractionGate(new CountingIds(), journal: _journal);
        _kit = PlanningKit.Create(new PlanningKitOptions
        {
            Llm = router,
            Sessions = _repo,
            Gate = _gate,
            PermissionRuns = _permissionRuns,
            Ids = new UlidIdGenerator(),
            ResolveModel = Resolve,
            EvaluatorModel = () => evaluator,
            PlanWorkflow = ctx => _modes.TryGetValue(ctx.RunId, out var mode) && mode == "plan",
            ProposalPolicy = () => policy,
            Log = _log,
        });

        _hooks = new HooksService(new HooksServiceOptions
        {
            GlobalEnvironment = _env,
            GlobalSettingsPath = EnvPath.Parse(Path.Combine(_root, "global-settings.json"), Style),
            ResolveProject = (_, _) => ValueTask.FromResult<HookProjectTarget?>(new HookProjectTarget(_env, EnvPath.Parse(_workspace, Style))),
            Log = _log,
        });
        // Notification 钩子订阅交互闸门的 tool_permission(Host 在 ConversationService 里同样接)。
        _hooks.AttachNotifications(_gate, _permissionRuns);
        var sub = settings["subagent"]?.AsObject();
        var catalog = new AgentCatalog(new ScanSource(this), new MemoryAgentSwitchStore(), _log);
        var factory = new Factory(this);
        _subagents = new SubagentService(new SubagentServiceDeps
        {
            Sessions = _repo,
            Requests = factory,
            Agents = catalog,
            Settings = new FixedSubagentSettings(new SubagentSettings
            {
                PerSessionLimit = sub?["perSessionLimit"]?.GetValue<int>() ?? SlotCapacity.DefaultLimit,
                GlobalLimit = sub?["globalLimit"]?.GetValue<int>() ?? SlotCapacity.DefaultLimit,
            }),
            PermissionRuns = _permissionRuns,
            Gate = _gate,
            Log = _log,
        });

        var behaviors = _input["toolBehaviors"]!.AsObject();
        var scripted = _input["tools"]!.AsArray().Select(t => (IAgentTool)new ScriptedTool(t!.AsObject(), behaviors, Fire, name => Gate(name).Task)).ToList();
        // 工具表是统一注册表(内置 + 脚本工具 + MCP 服务器接入的工具),和 Host 一样每回合取快照。
        _toolRegistry = new ToolRegistry([.. BuiltinTools.Create(), .. scripted]);
        if (_input["mcpServers"] is JsonArray { Count: > 0 })
        {
            _mcp = new McpService(new McpServiceDeps
            {
                Configs = new MemoryMcpConfigs(),
                Secrets = new MemoryMcpSecrets(),
                Environments = new LocalMcpEnvironments(_env),
                Tools = _toolRegistry,
                Delayer = new NoDelay(),
                Log = _log,
            });
        }

        var messageIds = new CountingIds();
        var sessions = new SessionDirectory((sessionId, workspaceId) => new AgentSession(sessionId, workspaceId, new AgentSessionDeps
        {
            Sessions = _repo,
            Llm = router,
            Ids = messageIds,
            Clock = TimeProvider.System,
            Delayer = new NoDelay(),
            Environment = _env,
            WorkspaceRoot = EnvPath.Parse(_workspace, Style),
            Extensions =
            [
                new PermissionExtension(_permissionRuns, PermissionRuleBook.CreateDefault(null), reviewer: new LlmPermissionReviewer(router, _log), hooks: [_hooks.PreToolSource]),
                _hooks.CreateExtension(workspaceId),
                new SubagentExtension(_subagents, workspaceId),
                .. _kit.Extensions,
            ],
            Tools = _toolRegistry,
            ResolveModel = model => _config.Bindings.FirstOrDefault(b => b.Alias == model.Alias && (model.ProviderId is null || b.ProviderId == model.ProviderId)),
            LoopOptions = new AgentLoopOptions { ResumeDelays = [] },
            Diagnostics = _log,
        }));
        _registry = new RunRegistry(new RunRegistryDeps
        {
            DeviceId = "dev-1",
            Sessions = _repo,
            Driver = new TapDriver(new SessionRunDriver(sessions), this),
            Requests = factory,
            Queue = new SessionInputQueueStore(new SessionInputStore(_db)),
            SessionDirectory = sessions,
            Ids = new CountingIds("q"),
            Log = _log,
            Extensions = [_subagents, new HostLikeRunExtension(this), .. _kit.RunExtensions],
            Interactions = new GateView(_gate),
            Shells = _shells,
        });
        _subagents.Bind(_registry);
        _kit.Bind(_registry);
    }

    private static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    private ModelBinding? Resolve(ModelRef model) => _config.Bindings.FirstOrDefault(b => b.Alias == model.Alias && (model.ProviderId is null || b.ProviderId == model.ProviderId));

    private TaskCompletionSource Gate(string name) => _gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    // ───────────────────────── 触发点 ─────────────────────────

    /// <summary>触发用户操作:和录制里 <c>actions[].on</c> 逐字相等的触发点才响;每个动作只响一次。</summary>
    private void Fire(JsonObject trigger)
    {
        for (var i = 0; i < _actions.Count; i++)
        {
            var action = _actions[i]!.AsObject();
            if (!JsonNode.DeepEquals(action["on"], trigger))
            {
                continue;
            }

            lock (_gate2)
            {
                if (!_fired.Add(i))
                {
                    continue;
                }
            }

            var what = action["do"]!.AsObject();
            if (what["release"] is { } release)
            {
                var name = release.GetValue<string>();
                if (what["whenGoal"]?.GetValue<string>() is { } goalKey)
                {
                    // 等这个会话有了活动的目标才放行(模型自提的目标要用户批准,设立发生在批准之后)。
                    var sessionId = _sessionIds[goalKey];
                    _ = Task.Run(async () =>
                    {
                        while (_kit.Goals.Store.Get(sessionId) is null)
                        {
                            await Task.Delay(1).ConfigureAwait(false);
                        }

                        // 目标一生效、kickoff 紧接着投进引导队列:2.x 里这两步是同步的,C# 里中间隔着第一次调用的 JIT / 投递(单独跑这个场景时能到几十毫秒),
                        // 所以放行前留一小段让投递先完成(否则下一回合会先于 kickoff 起来,偶发地把消息顺序换掉)。
                        await Task.Delay(150).ConfigureAwait(false);
                        Gate(name).TrySetResult();
                    });
                }
                else if (what["whenIdle"]?.GetValue<string>() is { } idleKey)
                {
                    // 等这个会话没有任何 Run / 操作占着(run_end 之后、收尾落盘之前也算占着)才放行:报告投递的时机不该赌在「父会话刚好收尾完」上。
                    var sessionId = _sessionIds[idleKey];
                    _ = Task.Run(async () =>
                    {
                        while (_registry.IsSessionBusy(sessionId))
                        {
                            await Task.Delay(1).ConfigureAwait(false);
                        }

                        Gate(name).TrySetResult();
                    });
                }
                else
                {
                    Gate(name).TrySetResult();
                }
            }
            else if (what["abort"] is { } abort)
            {
                if (HandleByLabel(abort.GetValue<string>()) is { } handle)
                {
                    _registry.Abort(handle.RunId, cascade: true);
                }
            }
            else if (what["planFile"] is JsonObject planFile)
            {
                // 用户在编辑器里改写 / 清空 / 删掉计划文件。
                PlanFileOp(planFile);
            }
            else if (what["detach"] is { } detach)
            {
                // 用户在子代理卡片上点「转去后台」(2.x:detachForegroundSubagent)。
                if (HandleByLabel(detach.GetValue<string>()) is { } child)
                {
                    _subagents.DetachForeground(child.RunId);
                }
            }
            else if (what["abortSession"] is { } session)
            {
                if (_registry.ActiveTopLevelRun(_sessionIds[session.GetValue<string>()]) is { } active)
                {
                    _registry.Abort(active.RunId, cascade: true);
                }
            }
        }
    }

    private RunHandle? HandleByLabel(string label)
    {
        lock (_gate2)
        {
            return _handles.FirstOrDefault(h => LabelFor(h) == label);
        }
    }

    private string LabelFor(RunHandle handle)
    {
        lock (_gate2)
        {
            if (_labels.TryGetValue(handle.RunId, out var known))
            {
                return known;
            }

            string label;
            if (_stepRuns.Contains(handle.RunId.Value) || handle.ParentRunId is not null)
            {
                label = handle.RunId.Value;
            }
            else
            {
                var key = _sessionIds.FirstOrDefault(kv => kv.Value == handle.SessionId).Key ?? handle.SessionId;
                var n = _internalCounters.GetValueOrDefault(handle.SessionId) + 1;
                _internalCounters[handle.SessionId] = n;
                label = $"{key}#internal{n}";
            }

            _labels[handle.RunId] = label;
            return label;
        }
    }

    /// <summary>C# 事件 → 录制里用的 2.x 事件名(触发器只认这些名字,所以它们必须是循环 / 会话层自己发出的事件)。</summary>
    private void Observe(RunHandle run, AgentEvent ev)
    {
        var name = ev switch
        {
            ToolStartEvent => "tool_start",
            ToolEndEvent => "tool_end",
            RunEndEvent => "run_end",
            MessageEndEvent => "message_commit",
            ContextUsageEvent => "ctx_usage",
            _ => null,
        };
        if (name is null)
        {
            return;
        }

        var label = LabelFor(run);
        Fire(new JsonObject { ["run"] = label, ["event"] = name });
        var callId = ev switch
        {
            ToolStartEvent s => s.CallId,
            ToolEndEvent e => e.CallId,
            _ => null,
        };
        if (callId is not null)
        {
            Fire(new JsonObject { ["run"] = label, ["event"] = name, ["callId"] = callId });
        }

        if (ev is ToolEndEvent ended)
        {
            // 放在 Fire 之后:等的人要的是「挂在这个 tool_end 上的用户操作已经做完」
            _toolEnded[ended.CallId] = true;
        }
    }

    /// <summary>
    /// 上游应答前的对齐点(见 <see cref="RoutedUpstream"/> 的构造参数):等这些调用的 <c>tool_end</c> 都被 <see cref="Observe"/> 处理过。
    /// 有上限:不经事件、由会话层补出来的工具结果(比如恢复时补的「已中断」)永远等不到,到点照旧往下走。
    /// </summary>
    private async Task ToolResultsObservedAsync(IReadOnlyList<string> callIds, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!callIds.All(_toolEnded.ContainsKey) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    // ───────────────────────── 跑 ─────────────────────────

    public async Task<Replayed> RunAsync()
    {
        var steps = _input["steps"]!.AsArray();
        using var responderStop = new CancellationTokenSource();
        var responder = Task.Run(() => AnswerAsync(responderStop.Token));
        PlanFileRef? lastPlan = null;
        foreach (var server in _input["mcpServers"]?.AsArray() ?? [])
        {
            // 真起一台 stdio 服务器(`/bin/sh -c <script>`,和录制同一个脚本);没连上就是台架的问题,不是比较结果。
            var status = await _mcp!.UpsertAsync(new McpServerConfig
            {
                Id = server!["id"]!.GetValue<string>(),
                Name = server["name"]!.GetValue<string>(),
                Description = server["description"]?.GetValue<string>(),
                Transport = McpTransportKind.Stdio,
                Command = "/bin/sh",
                Args = ["-c", server["script"]!.GetValue<string>()],
            }).ConfigureAwait(false);
            Assert.Equal(McpState.Connected, status.State);
        }

        foreach (var node in steps)
        {
            var step = node!.AsObject();
            var runId = step["run"]!.GetValue<string>();
            var sessionId = _sessionIds[step["session"]!.GetValue<string>()];
            var mode = step["mode"]?.GetValue<string>() ?? "code";
            var permission = step["permission"]?.GetValue<string>() ?? "full";
            var thinking = Thinking(step["thinking"]?.GetValue<string>() ?? "off");
            var text = step["input"]!.GetValue<string>();
            var model = ModelRef.Unpinned(_alias);
            if (step["goal"] is JsonObject goal)
            {
                ApplyGoalStep(goal, sessionId, new SendOptions(model, thinking, false, mode, permission));
                await QuiesceAsync().ConfigureAwait(false);
                continue;
            }

            Message[] messages = text.Length == 0 ? [] : [new Message(new MessageId($"u-{runId}"), Role.User, [new TextPart(text)], DateTimeOffset.UtcNow)];
            _modes[new RunId(runId)] = mode;
            var request = MakeRequest(new RunId(runId), messages, model, thinking);
            if (step["planExecution"]?.GetValue<bool>() == true && lastPlan is not null)
            {
                _kit.Plans.ExpectExecution(new RunId(runId), lastPlan);
            }

            var launch = new RunLaunch(sessionId, WorkspaceId, request)
            {
                Origin = RunOrigin.User,
                Options = new SendOptions(model, thinking, false, mode, permission),
                PermissionMode = permission,
            };
            Assert.IsType<LaunchResult.Started>(_registry.Launch(launch));
            await QuiesceAsync().ConfigureAwait(false);
            lastPlan = FindPlan(sessionId) ?? lastPlan;
        }

        await QuiesceAsync().ConfigureAwait(false);
        if (_input["settleMs"]?.GetValue<int>() is > 0 and var settleMs)
        {
            // 给「发了就不管」的钩子(Notification)写文件留出时间:两边同一个数。
            await Task.Delay(settleMs).ConfigureAwait(false);
        }

        await responderStop.CancelAsync().ConfigureAwait(false);
        await responder.ConfigureAwait(false);
        return Collect();
    }

    /// <summary>
    /// 用户对目标的手动操作(2.x:<c>goal:set</c> / <c>goal:clear</c> 两个 IPC,kickoff 由渲染层再当内部消息发出去;3.0:<c>GoalService.SetByUser</c> 自己把 kickoff 当内部输入交付,
    /// 档位由 Host 的 <c>goal.set</c> 处理器先记在会话上——这里照做)。<c>kickoff: false</c> 只设立不起 Run。
    /// </summary>
    private void ApplyGoalStep(JsonObject goal, string sessionId, SendOptions options)
    {
        switch (goal["op"]!.GetValue<string>())
        {
            case "clear":
                _kit.Goals.Clear(sessionId);
                break;
            case "set" when goal["kickoff"]?.GetValue<bool>() == false:
                _kit.Goals.CancelProposals(sessionId);
                _kit.Goals.Activate(sessionId, goal["condition"]!.GetValue<string>(), GoalOrigin.User);
                break;
            case "set":
                _registry.RememberOptions(sessionId, options);
                Assert.IsType<GoalSetResult.Ok>(_kit.Goals.SetByUser(sessionId, goal["condition"]!.GetValue<string>()));
                break;
            default:
                throw new InvalidOperationException($"Unknown goal op '{goal["op"]}'.");
        }
    }

    /// <summary>对工作区里唯一的计划文件(<c>.plan/&lt;ULID&gt;.md</c>)做手脚:<c>delete</c> / <c>truncate</c> / <c>replace</c>(录制脚本同一份语义)。</summary>
    private void PlanFileOp(JsonObject what)
    {
        var files = Directory.GetFiles(Path.Combine(_workspace, ".plan"), "*.md");
        Assert.Single(files);
        switch (what["op"]!.GetValue<string>())
        {
            case "delete":
                File.Delete(files[0]);
                break;
            case "truncate":
                File.WriteAllText(files[0], string.Empty);
                break;
            case "replace":
                File.WriteAllText(files[0], what["content"]!.GetValue<string>());
                break;
            default:
                throw new InvalidOperationException($"Unknown plan file op '{what["op"]}'.");
        }
    }

    /// <summary>这条会话里最近一次 <c>EnterPlanMode</c> 的结果带着的计划引用(下一步「执行计划」用;2.x 一侧同一份做法)。</summary>
    private PlanFileRef? FindPlan(string sessionId)
    {
        PlanFileRef? found = null;
        foreach (var record in _repo.History(sessionId))
        {
            foreach (var part in JsonNode.Parse(record.PartsJson)!.AsArray())
            {
                if (part?["type"]?.GetValue<string>() == "tool_result" && part["output"]?["content"]?.GetValue<string>() is { } content && content.Contains("\"plan_file_entered\"", StringComparison.Ordinal))
                {
                    var json = JsonNode.Parse(content)!;
                    found = new PlanFileRef(json["planId"]!.GetValue<string>(), json["path"]!.GetValue<string>());
                }
            }
        }

        return found;
    }

    /// <summary>整机安静:没有活跃 Run、没有排队的子代理、没有会话被占着,并且连续若干个轮询都是这样(后台汇报是 Run 结束之后才投递的)。</summary>
    private async Task QuiesceAsync()
    {
        var calm = 0;
        for (var i = 0; i < 4000 && calm < 12; i++)
        {
            var busy = _registry.ActiveRunIds().Count > 0 || _subagents.QueuedCount > 0 || _sessionIds.Values.Any(_registry.IsSessionBusy);
            calm = busy ? 0 : calm + 1;
            await Task.Delay(busy ? 5 : 2).ConfigureAwait(false);
        }
    }

    private RunRequest MakeRequest(RunId id, IReadOnlyList<Message> input, ModelRef model, ThinkingLevel thinking) =>
        new(id, input, model, thinking)
        {
            Settings = new SessionSettings { MaxOutputTokens = _maxOutput, ProjectInstructions = false },
        };

    private Replayed Collect()
    {
        // 计划 id 按首次出现编号:录制脚本是「交互先(它们在 Run 里就规范化了),再事件、落库行、请求、文件」,这里同一个顺序。
        var seen = new JsonArray();
        foreach (var request in _journal.Requests)
        {
            switch (request)
            {
                case ToolPermission t:
                    var permission = new JsonObject { ["kind"] = "tool_permission", ["callId"] = t.CallId, ["toolName"] = t.ToolName, ["readOnly"] = t.Traits.ReadOnly, ["destructive"] = t.Traits.Destructive };
                    if (t.SuggestedRule is { } rule)
                    {
                        permission["suggestedRule"] = rule;
                    }

                    seen.Add(PlanIds(permission));
                    break;
                case PlanApproval p:
                    seen.Add(PlanIds(new JsonObject { ["kind"] = "plan_approval", ["path"] = p.Path, ["plan"] = p.Plan }));
                    break;
                case GoalProposal g:
                    seen.Add(new JsonObject { ["kind"] = "goal_proposal", ["condition"] = g.Condition });
                    break;
            }
        }

        var ids = new IdMap(new HashSet<string>(_stepRuns.Select(r => $"u-{r}"), StringComparer.Ordinal));
        var events = new JsonArray();
        var runs = new JsonArray();
        List<RunHandle> handles;
        lock (_gate2)
        {
            handles = [.. _handles];
        }

        var tops = handles.Where(h => h.ParentRunId is null).ToList();
        var kids = handles.Where(h => h.ParentRunId is not null).ToList();
        foreach (var handle in tops.Concat(kids))
        {
            var evs = handle.Since(0).Select(e => e.Event).ToList();
            events.Add(new JsonObject { ["e"] = "run", ["run"] = LabelFor(handle) });
            foreach (var e in TraceBuilder.Events(evs, ids))
            {
                events.Add(PlanIds(e!.DeepClone()));
            }

            var end = evs.OfType<RunEndEvent>().LastOrDefault();
            var run = new JsonObject { ["run"] = LabelFor(handle), ["status"] = (end?.Status.ToString() ?? handle.State.ToString()).ToLowerInvariant() };
            if (end?.Error is { } error)
            {
                run["errorCode"] = Normalize.SnakeCase(error.Code.ToString());
            }

            runs.Add(run);
        }

        var rows = new JsonArray();
        var sessionList = _sessionIds.Select(kv => (Key: kv.Key, Id: kv.Value)).ToList();
        foreach (var kid in kids)
        {
            sessionList.Add((kid.SessionId.StartsWith("sess-", StringComparison.Ordinal) ? kid.SessionId["sess-".Length..] : kid.SessionId, kid.SessionId));
        }

        foreach (var (key, id) in sessionList)
        {
            rows.Add(new JsonObject { ["session"] = key });
            foreach (var row in TraceBuilder.Rows(_repo.History(id), ids))
            {
                rows.Add(PlanIds(row!.DeepClone()));
            }
        }

        var focus = _input["focusTools"]!.AsArray().Select(t => t!.GetValue<string>()).Concat(_input["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())).ToHashSet();
        var requests = new JsonArray();
        foreach (var key in _input["upstream"]!.AsObject().Select(kv => kv.Key))
        {
            foreach (var (url, body) in _upstream.RequestsOf(key))
            {
                var copy = body!.DeepClone();
                if (copy["tools"] is JsonArray tools)
                {
                    copy["tools"] = new JsonArray([.. tools.Select(t => t!["name"]!.GetValue<string>()).Where(focus.Contains).Order(StringComparer.Ordinal).Select(name => (JsonNode)new JsonObject { ["name"] = name })]);
                }

                requests.Add(PlanIds(new JsonObject { ["key"] = key, ["url"] = url.ToString(), ["body"] = copy }));
            }
        }

        var files = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetRelativePath(_workspace, p).Replace('\\', '/'), StringComparer.Ordinal))
        {
            files.Add(PlanIds(new JsonObject
            {
                ["path"] = Path.GetRelativePath(_workspace, path).Replace('\\', '/'),
                ["content"] = File.ReadAllText(path),
            }));
        }

        return new Replayed(events, rows, requests, runs, seen, files, _log.Lines);
    }

    /// <summary>计划 id 在两边都是 ULID(随机):按首次出现的顺序编号成 PLAN1、PLAN2……(录制侧同一份约定)。</summary>
    private readonly Dictionary<string, string> _planNames = [];

    private JsonNode PlanIds(JsonNode node)
    {
        string Rename(string text) => PlanIdPattern.Replace(text, m =>
        {
            if (!_planNames.TryGetValue(m.Value, out var name))
            {
                name = $"PLAN{_planNames.Count + 1}";
                _planNames[m.Value] = name;
            }

            return name;
        });

        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        o[key] = Rename(text);
                    }
                    else if (child is not null)
                    {
                        PlanIds(child);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        a[i] = Rename(text);
                    }
                    else if (a[i] is { } child)
                    {
                        PlanIds(child);
                    }
                }

                break;
        }

        return node;
    }

    // ───────────────────────── 扮演用户 ─────────────────────────

    private async Task AnswerAsync(CancellationToken stop)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        while (!stop.IsCancellationRequested)
        {
            foreach (var pending in _gate.Snapshot().Pending)
            {
                if (!answered.Add(pending.Id.Value))
                {
                    continue;
                }

                InteractionResponse? response = null;
                JsonObject? before = null;
                lock (_gate2)
                {
                    var kind = pending switch { ToolPermission => "tool_permission", PlanApproval => "plan_approval", GoalProposal => "goal_proposal", _ => "other" };
                    var index = _answers.FindIndex(a => a["kind"]!.GetValue<string>() == kind
                        && (a["callId"] is null || (pending is ToolPermission tp && tp.CallId == a["callId"]!.GetValue<string>())));
                    if (index < 0 && kind == "tool_permission")
                    {
                        // 没有为这次审批写剧本:用户点「允许一次」(2.x 一侧同一个默认)。
                        response = new ToolPermissionResponse(PermissionChoice.AllowOnce);
                    }
                    else if (index >= 0)
                    {
                        var answer = _answers[index];
                        before = answer["beforeAnswer"]?["planFile"] as JsonObject;
                        if (kind != "tool_permission" || answer["callId"] is not null)
                        {
                            _answers.RemoveAt(index);
                        }

                        response = kind switch
                        {
                            "plan_approval" => new PlanApprovalResponse(
                                answer["action"]!.GetValue<string>() switch
                                {
                                    "approve_new_session" => PlanApprovalAction.ApproveNewSession,
                                    "request_revision" => PlanApprovalAction.RequestRevision,
                                    "reject" => PlanApprovalAction.Reject,
                                    _ => PlanApprovalAction.ApproveCurrent,
                                },
                                answer["feedback"]?.GetValue<string>()),
                            "goal_proposal" => new GoalProposalResponse(answer["approved"]?.GetValue<bool>() == true),
                            _ => new ToolPermissionResponse(answer["decision"]?["kind"]?.GetValue<string>() switch
                            {
                                "deny" => PermissionChoice.Deny(answer["decision"]!["reason"]?.GetValue<string>()),
                                "allow_always" => new PermissionChoice.AllowAlwaysChoice(RememberScope.Project),
                                _ => PermissionChoice.AllowOnce,
                            }),
                        };
                    }
                }

                if (response is not null)
                {
                    if (before is not null)
                    {
                        PlanFileOp(before);
                    }

                    _gate.Respond(pending.Id, pending.Version, response, "parity");
                    Fire(new JsonObject
                    {
                        ["answered"] = pending switch { ToolPermission => "tool_permission", PlanApproval => "plan_approval", _ => "goal_proposal" },
                    });
                }
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
        _mcp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _kit.Goals.Dispose();
        _subagents.Dispose();
        _registry.Dispose();
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

    // ───────────────────────── 台架里的小零件 ─────────────────────────

    private sealed class Factory(OrchestrationRig rig) : IRunRequestFactory
    {
        public RunRequest Create(RunTemplate template) => rig.MakeRequest(template.RunId, template.Input, template.Options.Model, template.Options.Thinking) with { MaxContext = template.Options.MaxContext };
    }

    private sealed class ScanSource(OrchestrationRig rig) : IAgentScanSource
    {
        public ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct) =>
            ValueTask.FromResult(new AgentScanInput(rig._env, EnvPath.Parse(Path.Combine(rig._root, "global-agents"), Style), null, null));
    }

    /// <summary>每个 Run 先登记进台架,事件经它一份给触发器(driver 层看得见循环 / 会话层发的全部事件)。</summary>
    private sealed class TapDriver(IRunDriver inner, OrchestrationRig rig) : IRunDriver
    {
        public Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct)
        {
            lock (rig._gate2)
            {
                if (!rig._handles.Contains(run))
                {
                    rig._handles.Add(run);
                }
            }

            return inner.RunAsync(run, request, new TapSink(sink, run, rig), ct);
        }
    }

    private sealed class TapSink(IRunSink inner, RunHandle run, OrchestrationRig rig) : IRunSink
    {
        public async ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
        {
            await inner.EmitAsync(ev, ct).ConfigureAwait(false);
            rig.Observe(run, ev);
        }
    }

    /// <summary>照 Host 的 <c>ConversationRunExtension</c>:顶层 Run 开交互作用域、登记权限状态、造工具运行时;子 Run 的权限登记归 <see cref="SubagentService"/>。</summary>
    private sealed class HostLikeRunExtension(OrchestrationRig rig) : IRunExtension
    {
        private readonly ConcurrentDictionary<RunId, ToolRuntime> _runtimes = new();

        public void OnCreated(RunHandle run)
        {
            var runtime = new ToolRuntime { Environment = rig._env, WorkspaceId = run.WorkspaceId, Root = EnvPath.Parse(rig._workspace, Style), Shells = rig._shells };
            _runtimes[run.RunId] = runtime;
            if (run.Origin == RunOrigin.Subagent)
            {
                return;
            }

            var interactions = rig._gate.OpenRun(run.RunId, run.Depth);
            var mode = PermissionModes.TryParse(run.LivePermissionMode, out var parsed) ? parsed : PermissionMode.Ask;
            rig._permissionRuns.Register(new PermissionRunState(run.RunId, run.SessionId, rig._env, interactions, mode)
            {
                Root = runtime.Root,
                WorkspaceId = run.WorkspaceId,
                PlanFence = runtime.WriteFence,
                // 「为我批准」档位的专用审核模型:没配 = 回退人工审批(Host 每个 Run 现读设置,这里读录制里的设置)。
                ReviewerModel = rig._input["settings"]!["permissionReviewerModel"]?.GetValue<string>() is { Length: > 0 } reviewer ? ModelRef.Unpinned(reviewer) : null,
                UserTask = run.InputText,
                PlanWorkflow = rig._modes.TryGetValue(run.RunId, out var m) && m == "plan",
            });
        }

        public ToolContext EnrichToolContext(RunHandle run, ToolContext context) =>
            _runtimes.TryGetValue(run.RunId, out var runtime) ? ExecutionToolContext.Enricher(runtime)(context) : context;

        public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
        {
            if (run.Origin == RunOrigin.Subagent)
            {
                return;
            }

            rig._permissionRuns.Get(run.RunId)?.Interactions.End(status);
            rig._permissionRuns.Remove(run.RunId);
        }

        public void OnSettled(RunHandle run) => _runtimes.TryRemove(run.RunId, out _);
    }

    private sealed class GateView(InteractionGate gate) : IInteractionView
    {
        public int PendingCount(RunId runId) => gate.PendingCount(runId);
    }

    private sealed class JournalSink : IInteractionJournal
    {
        private readonly object _lock = new();
        private readonly List<InteractionRequest> _opened = [];

        public IReadOnlyList<InteractionRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _opened];
                }
            }
        }

        public void Opened(InteractionRequest request)
        {
            lock (_lock)
            {
                _opened.Add(request);
            }
        }

        public void Settled(InteractionResolution resolution)
        {
        }
    }

    private sealed class MemoryMcpConfigs : IMcpConfigStore
    {
        private readonly Dictionary<string, McpServerConfig> _rows = new(StringComparer.Ordinal);

        public IReadOnlyList<McpServerConfig> List()
        {
            lock (_rows)
            {
                return [.. _rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value)];
            }
        }

        public McpServerConfig? Get(string id)
        {
            lock (_rows)
            {
                return _rows.GetValueOrDefault(id);
            }
        }

        public void Put(McpServerConfig config)
        {
            lock (_rows)
            {
                _rows[config.Id] = config;
            }
        }

        public bool Remove(string id)
        {
            lock (_rows)
            {
                return _rows.Remove(id);
            }
        }
    }

    private sealed class MemoryMcpSecrets : IMcpSecretStore
    {
        public string? Get(string reference) => null;

        public void Set(string reference, string value)
        {
        }

        public bool Remove(string reference) => false;
    }

    private sealed class LocalMcpEnvironments(IExecutionEnvironment env) : IMcpEnvironments
    {
        public ValueTask<McpRuntimeTarget> ResolveAsync(McpServerConfig config, CancellationToken ct) => ValueTask.FromResult(new McpRuntimeTarget(env, null));
    }

    private sealed class NoDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingIds(string prefix = "gen") : IIdGenerator
    {
        private int _n;

        public string NewId() => $"{prefix}{Interlocked.Increment(ref _n):D4}";
    }

    private sealed class KeyStore : ICredentialStore
    {
        public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) => ValueTask.FromResult<string?>("sk-test");

        public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Diagnostics : ILoopDiagnostics
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Warn(string message, Exception? error = null)
        {
            lock (_lines)
            {
                _lines.Add(error is null ? message : $"{message} {error.GetType().Name}: {error.Message}");
            }
        }
    }
}
