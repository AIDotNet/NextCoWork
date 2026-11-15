using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Conversation;

/// <summary>「永久」作用域的权限规则文件形状(kv 表 <c>v3.permissions.global</c>)。未知键在读入时丢弃,所以只认这三个桶。</summary>
internal sealed record GlobalRulesFile(string[]? Allow, string[]? Ask, string[]? Deny);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GlobalRulesFile))]
[JsonSerializable(typeof(CatalogSettingsFile))]
internal sealed partial class HostStoresJsonContext : JsonSerializerContext;

/// <summary>目录同步设置(kv 表 <c>v3.catalog.settings</c>)。</summary>
internal sealed record CatalogSettingsFile(bool AutoUpdate, int IntervalMinutes);

/// <summary>
/// 「永久」权限规则的存放处(03 §9 P8,<see cref="AgentServices.GlobalRules"/> 的默认实现):设备上所有会话、所有项目共享,存在数据库的 kv 表里。
/// 读取失败(坏 JSON)抛异常 = 权限扩展按<b>失败即关闭</b>拒绝这次调用;写入遇到读不懂的内容拒绝覆盖(用户点的只是一颗按钮,不是「清空我的规则」)。每桶最多 500 条。
/// </summary>
internal sealed class StoredGlobalRules(KvStore kv) : IPermissionRuleStore
{
    public const string Key = "v3.permissions.global";

    private readonly Lock _lock = new();

    public ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(Load() ?? RuleBuckets.Empty);
        }
    }

    public ValueTask<RuleWriteResult> AddAsync(RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct)
    {
        lock (_lock)
        {
            RuleBuckets current;
            try
            {
                current = Load() ?? RuleBuckets.Empty;
            }
            catch (JsonException)
            {
                return ValueTask.FromResult(RuleWriteResult.Failed(RuleWriteFailure.Unreadable));
            }

            var list = current.Of(bucket);
            if (list.Contains(rule))
            {
                return ValueTask.FromResult(RuleWriteResult.AlreadyPresent);
            }

            if (list.Count >= RuleLimits.MaxRulesPerBucket)
            {
                return ValueTask.FromResult(RuleWriteResult.Failed(RuleWriteFailure.Full));
            }

            string[] Next(RuleBucket b) => b == bucket ? [.. current.Of(b), rule] : [.. current.Of(b)];
            try
            {
                kv.Set(Key, JsonSerializer.Serialize(new GlobalRulesFile(Next(RuleBucket.Allow), Next(RuleBucket.Ask), Next(RuleBucket.Deny)), HostStoresJsonContext.Default.GlobalRulesFile));
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or StorageException)
            {
                return ValueTask.FromResult(RuleWriteResult.Failed(RuleWriteFailure.Io));
            }

            return ValueTask.FromResult(RuleWriteResult.Written);
        }
    }

    private RuleBuckets? Load()
    {
        if (kv.Get(Key) is not { } json)
        {
            return null;
        }

        var file = JsonSerializer.Deserialize(json, HostStoresJsonContext.Default.GlobalRulesFile) ?? throw new JsonException("The stored rules are not an object.");
        return new RuleBuckets(file.Allow ?? [], file.Ask ?? [], file.Deny ?? []);
    }
}

/// <summary>
/// <see cref="IInteractionJournal"/> 的 Storage 实现(<c>device_interactions</c>,见 <see cref="InteractionJournalStore"/>)。
/// 只记元数据(种类、Run、版本、谁回答的);不存工具输入等内容。构造时把上次进程遗留的 pending 行收尾为 cancelled。
/// </summary>
internal sealed class StoredInteractionJournal : IInteractionJournal
{
    private readonly InteractionJournalStore _store;

    public StoredInteractionJournal(InteractionJournalStore store, TimeProvider clock)
    {
        _store = store;
        Recovered = store.CancelPending(clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>启动时收尾的上次遗留行数。</summary>
    public int Recovered { get; }

    public void Opened(InteractionRequest request) =>
        _store.Open(request.Id.Value, request.RunId.Value, RowKind(request.Kind), request.Version, request.CreatedAt.ToUnixTimeMilliseconds());

    public void Settled(InteractionResolution resolution) =>
        _store.Settle(resolution.Id.Value, resolution.Status == ResolutionStatus.Answered, resolution.ResolvedBy, resolution.At.ToUnixTimeMilliseconds());

    /// <summary>线上种类名 → 2.x 表里 kind 列允许的值。</summary>
    internal static string RowKind(string kind) => kind switch
    {
        "tool_permission" => "permission",
        "ask_user" => "question",
        "plan_approval" => "plan",
        "goal_proposal" => "goal",
        // ssh_host_key / ssh_credential 等设备级交互:2.x 表里没有对应的种类,落在「其它」(只记元数据:种类 / 版本 / 谁回答的)
        _ => "plugin",
    };
}

/// <summary>
/// 把设置页的值(2.x 的 <c>settings</c> 表第 1 行,接管后原样可用;读写在服务层的 <see cref="Ncw.Agent.Services.Settings.SettingsService"/>)读成 Run 开始时的 <see cref="SessionSettings"/> 快照:
/// 自动压缩总闸、最大输出 Token、个性化三栏、压缩用的模型 / 思考档位、全局指令目录。每次 Run 现读(改完设置立刻对下一次 Run 生效),读不了或不认识的值一律按出厂值。
/// <b>全局指令</b>:设备本地的数据根(04 §2:全局目录在设备本地,SSH 项目的全局指令仍然来自本机设备)。2.x 没有「导入的指令副本」这个设置,<c>ImportedInstructions</c> 保持 null。
/// </summary>
internal sealed class StoredSessionSettings(Ncw.Agent.Services.Settings.SettingsService settings, Func<GlobalInstructions?> globalInstructions)
{
    public SessionSettings Read()
    {
        var s = settings.Current();
        Personalization? personalization = null;
        if (s.Personalization is { } p && (p.Name.Length > 0 || p.Background.Length > 0 || p.Instructions.Length > 0))
        {
            personalization = new Personalization(
                Clip(p.Name, Personalization.NameMax), Clip(p.Background, Personalization.BackgroundMax), Clip(p.Instructions, Personalization.InstructionsMax));
        }

        CompactionSettings? compaction = null;
        if (s.Compact.IsSet)
        {
            var thinking = Enum.TryParse<ThinkingLevel>(s.CompactThinking, ignoreCase: true, out var level) ? CompactThinkingSetting.Of(level) : CompactThinkingSetting.Inherit;
            compaction = new CompactionSettings(s.Compact.Model, s.Compact.ProviderId, thinking);
        }

        return new SessionSettings
        {
            AutoCompact = s.AutoCompact,
            MaxOutputTokens = s.MaxOutputTokens == Ncw.Agent.Services.Settings.DeviceSettings.DefaultMaxOutputTokens ? null : s.MaxOutputTokens,
            Personalization = personalization,
            Compaction = compaction,
            GlobalInstructions = globalInstructions(),
        };
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary><see cref="IUsageSink"/> 的实现:每次 HTTP 尝试一行 <c>usage_records</c>。费用是路由按目录价格算好的(写入即冻结),查不到价格写 NULL。永不抛(路由也会吞)。</summary>
internal sealed class UsageRecordSink(UsageRepository repository) : IUsageSink
{
    public void OnAttempt(UsageAttempt a)
    {
        var known = a.Cost.State == CostState.Known;
        repository.Insert(new UsageRow(
            a.Id, a.At.ToUnixTimeMilliseconds(), a.RunId, a.WorkspaceId, a.SessionId, a.Attempt, a.ProviderId, a.ProviderName, Llm.Wire.WireProtocols.NameOf(a.Protocol),
            a.Endpoint, a.Alias, a.UpstreamModel, a.ResponseModel,
            a.Usage.InputTokens, a.Usage.OutputTokens, a.Usage.CacheReadInputTokens ?? 0, a.Usage.CacheCreationInputTokens ?? 0, a.Usage.CacheCreation1hInputTokens ?? 0, a.Usage.ReasoningTokens,
            a.LatencyMs, a.TimeToFirstTokenMs, a.Ok, a.HttpStatus, a.ErrorKind?.ToString().ToLowerInvariant(), a.ErrorMessage, a.Stop?.ToString().ToLowerInvariant(), a.ToolCalls,
            known ? a.Cost.Micros : null, known ? a.Cost.Currency.ToString().ToUpperInvariant() : null, null, null));
    }
}
