using System.Text.Json;
using Ncw.Agent.Catalog;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Conversation;

/// <summary>子代理设置(<c>settings.subagent</c>:默认模型 + 供应商、思考深度、每父 Run 上限、全局上限)。每次派发现读(改完设置立刻对下一次派发生效);读不了或不认识的值按出厂值,绝不抛。</summary>
internal sealed class StoredSubagentSettings(Ncw.Agent.Services.Settings.SettingsService settings) : ISubagentSettingsSource
{
    public SubagentSettings Current()
    {
        var sub = settings.Current().Subagent;
        var thinking = sub.Thinking != Ncw.Agent.Services.Settings.DeviceSettings.Inherit && SubagentThinkingSelection.TryParseLevel(sub.Thinking, out var level)
            ? SubagentThinking.Of(level)
            : SubagentThinking.Inherit;
        return new SubagentSettings
        {
            Model = sub.Model.IsSet ? new ModelSelection(sub.Model.Model, sub.Model.ProviderId) : null,
            Thinking = thinking,
            // 归一化(< 1 → 4;全局 0 保留 = 禁用)由 SlotCapacity.Normalize 做:这里只原样给值
            PerSessionLimit = sub.PerSessionLimit,
            GlobalLimit = sub.GlobalLimit,
        };
    }
}

/// <summary>目标设置:判定模型(<c>goalEvaluatorModel</c> + 供应商,成对)、模型自提目标的策略(<c>modelProposedGoals</c>)和后台推迟时的检查间隔基数(<c>goalCheckinMinutes</c>)。每个 Run / 每次检查现读。</summary>
internal sealed class StoredGoalSettings(Ncw.Agent.Services.Settings.SettingsService settings)
{
    /// <summary>判定模型。null = 用这个 Run 的模型(设置里没配 / 配的是空串)。</summary>
    public ModelRef? EvaluatorModel() =>
        settings.Current().GoalEvaluator is { IsSet: true } pick ? new ModelRef(pick.ProviderId, pick.Model) : null;

    public GoalProposalPolicy ProposalPolicy() => settings.Current().ModelProposedGoals switch
    {
        "alwaysAsk" => GoalProposalPolicy.AlwaysAsk,
        "disabled" => GoalProposalPolicy.Disabled,
        _ => GoalProposalPolicy.Auto,
    };

    /// <summary>空闲检查的基数间隔(2.x 的 <c>CLAUDE_CODE_GOAL_CHECKIN_MINUTES</c>,3.0 改成设置项;出厂 30 分钟)。</summary>
    public TimeSpan? CheckinInterval() => TimeSpan.FromMinutes(settings.Current().GoalCheckinMinutes);
}

/// <summary>「哪些子代理被用户关掉了」:kv 表的 <c>agents.disabled</c>(2.x 的键,JSON 字符串数组,接管后原样可用)。宽容读:坏值当作没有。</summary>
internal sealed class KvAgentSwitchStore(KvStore kv) : IAgentSwitchStore
{
    public const string Key = "agents.disabled";

    private readonly Lock _gate = new();

    public IReadOnlySet<string> Disabled()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            var names = new HashSet<string>(Load(), StringComparer.Ordinal);
            if (enabled ? !names.Remove(name) : !names.Add(name))
            {
                return;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var item in names.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            kv.Set(Key, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private HashSet<string> Load()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (kv.Get(Key) is not { } json)
            {
                return names;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }
}

/// <summary><see cref="ISubagentModels"/> 的适配:路由器(<see cref="IModelResolver"/>)里这条「别名 + 供应商」还指得到绑定吗、思考档位按模型静默降级。</summary>
internal sealed class ResolverSubagentModels(IModelResolver models) : ISubagentModels
{
    public bool Exists(ModelSelection selection) => models.Resolve(new ModelRef(selection.ProviderId, selection.Alias)) is not null;

    public ThinkingLevel NormalizeThinking(ThinkingLevel wanted, ModelSelection selection) =>
        models.Resolve(new ModelRef(selection.ProviderId, selection.Alias)) is { } binding ? CompactBinding.AuxiliaryThinking(wanted, binding) : wanted;
}

/// <summary>子代理定义的扫描输入:全局目录(<c>&lt;数据根&gt;/agents</c>)在设备本机,项目目录(<c>&lt;项目根&gt;/.next-cowork/agents</c>)在项目的执行环境里。</summary>
internal sealed class HostAgentScanSource(ConversationService service, string dataRoot) : IAgentScanSource
{
    public ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct)
    {
        var local = service.LocalEnvironment;
        var global = AgentScanInput.GlobalAgentsRoot(Ncw.Agent.Environments.EnvPath.Parse(dataRoot, local.Facts.PathStyle));
        if (service.Workspaces.Get(workspaceId) is not { } project)
        {
            return ValueTask.FromResult(new AgentScanInput(local, global, null, null));
        }

        // 项目环境暂时不可用(SSH 没连上):ProtocolException 原样抛出——那不是「没有子代理」。
        var environment = service.EnvironmentOf(project);
        var root = Ncw.Agent.Environments.EnvPath.Parse(project.RootPath, environment.Facts.PathStyle);
        return ValueTask.FromResult(new AgentScanInput(local, global, environment, AgentScanInput.ProjectAgentsRoot(root)));
    }
}
