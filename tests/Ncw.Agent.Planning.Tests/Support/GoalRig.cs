using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests.Support;

/// <summary>内存里的转录口:一张「会话 → 消息列表(带原样 parts JSON)」的表。</summary>
public sealed class MemoryGoalTranscript : IGoalTranscript
{
    private readonly object _gate = new();

    /// <summary>每条消息的 parts 都以 JSON 文本存着(和库里一样),比较并替换才测得出。</summary>
    public Dictionary<string, List<(Message Message, string PartsJson)>> Sessions { get; } = [];

    public int StaleOnce { get; set; }

    public int LastWithGoalStatusCalls { get; private set; }

    public bool Exists(string sessionId)
    {
        lock (_gate)
        {
            return Sessions.ContainsKey(sessionId);
        }
    }

    public void Seed(string sessionId, params Message[] messages)
    {
        lock (_gate)
        {
            Sessions[sessionId] = [.. messages.Select(m => (m, Ncw.Agent.Kernel.Session.MessageCodec.PartsToJson([.. m.Parts])))];
        }
    }

    public void Drop(string sessionId)
    {
        lock (_gate)
        {
            Sessions.Remove(sessionId);
        }
    }

    public GoalMessage? LastAssistant(string sessionId)
    {
        lock (_gate)
        {
            var found = Sessions.GetValueOrDefault(sessionId)?.LastOrDefault(e => e.Message.Role == Role.Assistant);
            return found is { Message: not null } e ? new GoalMessage(e.Message, e.PartsJson) : null;
        }
    }

    public GoalMessage? LastWithGoalStatus(string sessionId)
    {
        lock (_gate)
        {
            LastWithGoalStatusCalls++;
            var found = Sessions.GetValueOrDefault(sessionId)?.LastOrDefault(e => e.Message.Role == Role.Assistant && e.Message.Parts.Any(p => p is GoalStatusPart));
            return found is { Message: not null } e ? new GoalMessage(e.Message, e.PartsJson) : null;
        }
    }

    public bool TryReplaceParts(string sessionId, string messageId, string expectedPartsJson, string newPartsJson)
    {
        lock (_gate)
        {
            var list = Sessions.GetValueOrDefault(sessionId);
            var at = list?.FindIndex(e => e.Message.Id.Value == messageId) ?? -1;
            if (list is null || at < 0)
            {
                return false;
            }

            if (StaleOnce > 0)
            {
                StaleOnce--;
                return false;
            }

            if (list[at].PartsJson != expectedPartsJson)
            {
                return false;
            }

            var parts = Ncw.Agent.Kernel.Session.MessageCodec.PartsFromJson(newPartsJson);
            list[at] = (list[at].Message with { Parts = [.. parts] }, newPartsJson);
            return true;
        }
    }

    public IReadOnlyList<GoalStatusPart> Statuses(string sessionId)
    {
        lock (_gate)
        {
            return [.. (Sessions.GetValueOrDefault(sessionId) ?? []).SelectMany(e => e.Message.Parts).OfType<GoalStatusPart>()];
        }
    }

    public IReadOnlyList<Message> Messages(string sessionId)
    {
        lock (_gate)
        {
            return [.. (Sessions.GetValueOrDefault(sessionId) ?? []).Select(e => e.Message)];
        }
    }
}

/// <summary>
/// 目标运行时的测试台:假上游(判定器)、手动时钟、内存转录、记录下来的投递。2.x 的 <c>goal-runtime.test.ts</c> 把转录和判定都注入成假的、时间走假定时器;这里同样没有一次真网络请求、没有一次真的等待。
/// </summary>
public sealed class GoalRig : IDisposable
{
    public const string S = "sess-1";
    public const string Condition = "让 bun test 全绿(转录里有这次运行的结果)";

    public GoalRig(Action<GoalOptionsBuilder>? configure = null)
    {
        var b = new GoalOptionsBuilder();
        configure?.Invoke(b);
        Clock = new ManualClock();
        Transcript = new MemoryGoalTranscript();
        Llm = new FakeLlm();
        Fx = new Fixture();
        Background = b.Background;
        Telemetry = [];
        Options = new GoalOptions
        {
            Llm = Llm,
            Transcript = Transcript,
            Ids = new SeqIds("g"),
            Clock = Clock,
            ResolveModel = m => new ModelBinding(m.Alias, "p1", "up", 200_000, 8192),
            SessionTokens = _ => Tokens,
            BackgroundWork = _ => Background.Tasks,
            IsIdle = _ => Background.Idle,
            Deliver = (session, input) =>
            {
                lock (Delivered)
                {
                    Delivered.Add((session, input));
                }

                return DeliveryResult;
            },
            SessionExists = Transcript.Exists,
            EvaluatorModel = () => EvaluatorModel,
            ProposalPolicy = () => Policy,
            Planning = (_, _) => Planning,
            Interactive = _ => InteractiveSession,
            CheckinInterval = b.CheckinInterval ?? TimeSpan.FromMinutes(30),
            EvaluationTimeout = TimeSpan.FromSeconds(30),
            Gate = Fx.Gate,
            PermissionRuns = Fx.Permissions,
            RunIsLive = runId => !DeadRuns.Contains(runId),
            RunAborted = runId => AbortedRuns.Contains(runId),
            Log = Log,
            Telemetry = (name, fields) =>
            {
                lock (Telemetry)
                {
                    Telemetry.Add($"{name} {string.Join(' ', fields.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
            },
        };
        Goals = new GoalService(Options);
    }

    public sealed class GoalOptionsBuilder
    {
        public BackgroundState Background { get; } = new();

        public TimeSpan? CheckinInterval { get; set; }
    }

    public sealed class BackgroundState
    {
        public IReadOnlyList<BackgroundTaskLine> Tasks { get; set; } = [];

        public bool Idle { get; set; } = true;
    }

    public ManualClock Clock { get; }

    public MemoryGoalTranscript Transcript { get; }

    public FakeLlm Llm { get; }

    public Fixture Fx { get; }

    public BackgroundState Background { get; }

    public GoalOptions Options { get; }

    public GoalService Goals { get; }

    public CollectingLog Log { get; } = new();

    public List<string> Telemetry { get; }

    public List<(string Session, InternalInput Input)> Delivered { get; } = [];

    public InternalDelivery DeliveryResult { get; set; } = InternalDelivery.Launched;

    public ModelRef? EvaluatorModel { get; set; }

    public GoalProposalPolicy Policy { get; set; } = GoalProposalPolicy.Auto;

    public bool Planning { get; set; }

    public bool InteractiveSession { get; set; } = true;

    public long Tokens { get; set; }

    public HashSet<RunId> DeadRuns { get; } = [];

    public HashSet<RunId> AbortedRuns { get; } = [];

    public GoalRunContext Context(string runId = "run-1", SendOptions? options = null) =>
        new(new RunId(runId), "ws-1", ModelRef.Unpinned("main-model"), EvaluatorModel, options ?? new SendOptions(ModelRef.Unpinned("main-model")));

    public static IReadOnlyList<Message> DefaultTranscript() =>
    [
        Msg.User("u1", "让测试全绿"),
        Msg.Say("a1", "我先跑一下测试。"),
    ];

    public TurnEnd TurnEnd(int streak = 0, IReadOnlyList<Message>? transcript = null, string session = S, string runId = "run-1") =>
        new(new RunId(runId), session, 1, Msg.Say("a1", "好"), StopReason.EndTurn, transcript ?? DefaultTranscript(), 0, streak, false);

    public Task<GoalTurnDecision> TurnEndAsync(int streak = 0, GoalRunContext? context = null, bool subagent = false, CancellationToken ct = default, IReadOnlyList<Message>? transcript = null) =>
        Goals.HandleTurnEndAsync(TurnEnd(streak, transcript), context ?? Context(), subagent, ct);

    public IReadOnlyList<string> DeliveredText() =>
        [.. Delivered.Select(d => string.Concat(d.Input.Parts.OfType<TextPart>().Select(p => p.Text)))];

    public static string TextOf(IReadOnlyList<Part> parts) => string.Concat(parts.OfType<TextPart>().Select(p => p.Text));

    public void Dispose()
    {
        Goals.Dispose();
        Fx.Dispose();
    }
}
