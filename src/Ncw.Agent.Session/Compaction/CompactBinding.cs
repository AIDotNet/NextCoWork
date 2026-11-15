using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Session;

/// <summary>压缩的思考档位设置:<see cref="Level"/> 为 null = <c>inherit</c>(跟随本轮 Run 的档位)。</summary>
public readonly record struct CompactThinkingSetting(ThinkingLevel? Level)
{
    public static CompactThinkingSetting Inherit => default;

    public static CompactThinkingSetting Of(ThinkingLevel level) => new(level);
}

/// <summary>全局设置(设置 › 通用 › Agent)里压缩的那一对 + 档位,Run 开始时的快照。<see cref="Model"/> 空串 = 跟随会话模型。</summary>
public sealed record CompactionSettings(string Model = "", string? ModelProviderId = null, CompactThinkingSetting Thinking = default);

/// <summary>
/// 工作区对压缩的覆盖(三态,2.x 的 <c>WorkspaceCompactionOverride</c>):
/// <see cref="Model"/> 非空 = 用这个别名(与 <see cref="ModelProviderId"/> 成对);<see cref="FollowSessionModel"/> = <b>显式</b>跟随会话模型(用来反盖全局配的压缩模型,否则全局一配,单个工作区就再也回不去);
/// 都没有 = 跟随全局设置。<see cref="Thinking"/> 为 null = 跟随全局。
/// </summary>
public sealed record WorkspaceCompaction(
    string? Model = null,
    string? ModelProviderId = null,
    bool FollowSessionModel = false,
    CompactThinkingSetting? Thinking = null);

/// <summary>这一次压缩发给谁、用什么参数。三档来源与回落规则只在 <see cref="Resolve"/> 一处,自动压缩与手动 /compact 共用。</summary>
public sealed record CompactBinding(
    ModelRef Model,
    ThinkingLevel Thinking,
    int MaxOutputTokens,
    int ProtocolWindow,
    bool FellBack)
{
    /// <summary>记进边界、给界面看的那一组事实。</summary>
    public SummaryModelInfo Info => new(Model.Alias, Model.ProviderId, Thinking.ToString().ToLowerInvariant(), FellBack);

    /// <summary>
    /// 解析这次压缩的绑定。三档来源:工作区 &gt; 全局设置 &gt; 会话模型;别名和供应商<b>成对</b>取用,绝不拼出「A 家的别名 + B 家的锁」。
    /// ★ 配置的压缩模型<b>当前解析不到</b>(供应商被删、别名被改名)时回落会话模型(连同它的供应商一起换回去)并 <paramref name="warn"/>,不判失败——
    /// 让一条过期配置把压缩打死,表现就是上下文一路涨到上游报超长。回落记进边界(<c>FellBack</c>)。
    /// ★ 档位按压缩模型归一化(<see cref="AuxiliaryThinking"/>):effort 模型关不掉推理,直接下发 off 会被思考适配器拒成不可重试的错,压缩每次必败、三次熔断。
    /// ★ 输出额度和预裁窗口按<b>压缩模型</b>算,不是会话模型。返回 null = 连会话模型都解析不到(调用方走失败路径)。
    /// </summary>
    public static CompactBinding? Resolve(
        Func<ModelRef, ModelBinding?> resolveModel,
        ModelRef session,
        ThinkingLevel sessionThinking,
        CompactionSettings? settings = null,
        WorkspaceCompaction? workspace = null,
        int? maxOutputSetting = null,
        Action<string>? warn = null)
    {
        settings ??= new CompactionSettings();
        var (model, followsSession) = SelectModel(workspace, settings, session);
        var fellBack = false;
        var binding = resolveModel(model);
        if (binding is null && !followsSession)
        {
            warn?.Invoke($"The configured compaction model {model.Alias} is not available; falling back to the session model {session.Alias}.");
            model = session;
            fellBack = true;
            binding = resolveModel(model);
        }

        if (binding is null)
        {
            return null;
        }

        var wanted = (workspace?.Thinking ?? settings.Thinking).Level ?? sessionThinking;
        return new CompactBinding(
            model,
            AuxiliaryThinking(wanted, binding),
            ContextWindows.ResolveMaxOutputTokens(maxOutputSetting, binding.ContextWindow),
            // 协议窗口:摘要请求只受压缩模型自己的真实上限约束,和「最大上下文」计费开关无关
            ContextWindows.Effective(binding.ContextWindow, maxContext: true),
            fellBack);
    }

    /// <summary>三档来源,越具体越优先。<c>FollowSessionModel</c> 和「没配」是两个不同的答案。</summary>
    public static (ModelRef Model, bool FollowsSession) SelectModel(WorkspaceCompaction? workspace, CompactionSettings settings, ModelRef session)
    {
        if (workspace?.FollowSessionModel == true)
        {
            return (session, true);
        }

        if (!string.IsNullOrWhiteSpace(workspace?.Model))
        {
            return (new ModelRef(workspace.ModelProviderId, workspace.Model), false);
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            return (new ModelRef(settings.ModelProviderId, settings.Model), false);
        }

        return (session, true);
    }

    private static readonly ThinkingLevel[] Strength =
        [ThinkingLevel.Off, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max];

    /// <summary>
    /// 一次旁路请求的思考档位:模型接受 <paramref name="wanted"/> 就用它;否则取模型接受的、不比它强的最强一档;
    /// 模型最低的那一档都比它强时取那一档(「off」关不掉的模型走的就是这一支);再没有就 <see cref="ThinkingLevel.Auto"/>。
    /// ★ 绝不抛,绝不返回一个这个模型不支持的档位。与界面上换模型的归一化差别是故意的:后台请求没有界面可看,落回 auto 会让一次「尽量别思考」的请求悄悄按模型默认的高档位去想。
    /// </summary>
    public static ThinkingLevel AuxiliaryThinking(ThinkingLevel wanted, ModelBinding binding)
    {
        var config = binding.Thinking;
        if (config is null)
        {
            return wanted; // 没有声明(旧别名):保留明确的 off,协议边界会按能力处理
        }

        switch (config.Mode)
        {
            case ThinkingMode.Unsupported:
                return ThinkingLevel.Off;
            case ThinkingMode.Always:
                return ThinkingLevel.Auto;
            case ThinkingMode.Effort when binding.ReasoningEfforts is { } efforts:
                var allowed = new HashSet<ThinkingLevel> { ThinkingLevel.Auto };
                foreach (var effort in efforts)
                {
                    allowed.Add(effort switch
                    {
                        ReasoningEffort.None => ThinkingLevel.Off,
                        ReasoningEffort.Minimal => ThinkingLevel.Minimal,
                        ReasoningEffort.Low => ThinkingLevel.Low,
                        ReasoningEffort.Medium => ThinkingLevel.Medium,
                        ReasoningEffort.High => ThinkingLevel.High,
                        ReasoningEffort.XHigh => ThinkingLevel.XHigh,
                        _ => ThinkingLevel.Max,
                    });
                }

                if (allowed.Contains(wanted))
                {
                    return wanted;
                }

                var wantedAt = Array.IndexOf(Strength, wanted);
                if (wantedAt < 0)
                {
                    return ThinkingLevel.Auto;
                }

                var available = Strength.Select((level, index) => (level, index)).Where(e => allowed.Contains(e.level)).ToList();
                var weaker = available.Where(e => e.index <= wantedAt).ToList();
                return weaker.Count > 0 ? weaker[^1].level : available.Count > 0 ? available[0].level : ThinkingLevel.Auto;
            default:
                return wanted;
        }
    }
}
