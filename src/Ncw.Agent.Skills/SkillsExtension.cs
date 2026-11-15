using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Skills;

/// <summary>
/// Skills 一方扩展(06 §3,Id <c>skills</c>,<see cref="Order"/> 300,<b>每个会话对象一个实例</b>):
/// <list type="bullet">
/// <item><c>before_run</c>:扫描 → 合并全局开关 / 工作区选装 / 本机资源可用 → 产出这个 Run 的 <see cref="SkillSnapshot"/>。<b>技能目录是运行级常量</b>,Run 开始时定死(保住提示词缓存前缀);
/// 缓存失效规则 = 每个 Run 开始重扫(用户新装的 Skill 下一个 Run 就生效,不要求重启)。扫描失败(环境连不上)= 这个 Run 没有 Skill 目录,记诊断,Run 照常开始。</item>
/// <item><c>system_sections</c>:<c>skills</c> 一节(<see cref="SkillSection"/>),回调只读快照,不做 IO(回调是同步的)。</item>
/// <item>工具 <c>Skill</c>(<see cref="SkillTool"/>)。</item>
/// <item><c>compact_attachments</c>:压缩后附件带回本会话调用过的 Skill 正文(每个 5K token、总 25K),<b>从被压缩的那段历史里取</b>(正文就在 tool_result 里),不重读磁盘。</item>
/// </list>
/// 没有任何 Skill 启用时,目录节不出现,<c>Skill</c> 工具仍然注册(模型认错名字时它要能列出「一条都没有」)。
/// </summary>
public sealed class SkillsExtension(SkillsService service, string workspaceId) : IAgentExtension
{
    public const string ExtensionId = "skills";

    /// <summary>每个 Skill 重附的 token 上限(CC 的 <c>POST_COMPACT_MAX_TOKENS_PER_SKILL</c>)。</summary>
    public const int PostCompactMaxTokensPerSkill = 5_000;

    /// <summary>重附的 token 总预算(CC 的 <c>POST_COMPACT_SKILLS_TOKEN_BUDGET</c>)。</summary>
    public const int PostCompactSkillBudget = 25_000;

    private SkillSnapshot? _snapshot;

    public string Id => ExtensionId;

    public int Order => 300;

    /// <summary>这个会话当前的快照(没跑过 Run 时为 null)。</summary>
    public SkillSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public void Register(IExtensionRegistry registry)
    {
        registry.RegisterTool(new SkillTool(() => Snapshot, service.Usage, _ => workspaceId));
        registry.Mutate(SessionExtensionPoints.BeforeRun, OnBeforeRunAsync);
        registry.ContributeSystemSection(SkillSection.Name, _ => SkillSection.Build(Snapshot?.Active ?? []));
        registry.Mutate(SessionExtensionPoints.CompactAttachments, (draft, _, _) =>
        {
            foreach (var text in Attachments.AfterCompaction(draft.History))
            {
                draft.Texts.Add(text);
            }

            return ValueTask.CompletedTask;
        });
    }

    private async ValueTask OnBeforeRunAsync(RunDraft draft, ExtensionContext ctx, CancellationToken ct)
    {
        try
        {
            Volatile.Write(ref _snapshot, await service.SnapshotAsync(workspaceId, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is EnvironmentException or SkillException)
        {
            ctx.Log.Warn("The Skill catalog could not be built for this run.", e);
            Volatile.Write(ref _snapshot, SkillSnapshot.Empty);
        }
    }

    /// <summary>压缩后附件(纯函数,可单测)。</summary>
    public static class Attachments
    {
        public static IReadOnlyList<string> AfterCompaction(IReadOnlyList<Message> history)
        {
            var texts = new List<string>();
            var budget = PostCompactSkillBudget;
            foreach (var (name, body) in InvokedSkills(history))
            {
                if (budget <= 0)
                {
                    break;
                }

                var clipped = Compactor.TruncateToTokens(body, Math.Min(PostCompactMaxTokensPerSkill, budget));
                budget -= TokenEstimator.Text(clipped);
                texts.Add($"<system-reminder>\nThe Skill \"{name}\" was loaded earlier in this conversation. Its instructions still apply:\n{clipped}\n</system-reminder>");
            }

            return texts;
        }

        /// <summary>最后一次成功调用每个 Skill 时拿到的正文,新的在前。</summary>
        public static IReadOnlyList<(string Name, string Body)> InvokedSkills(IReadOnlyList<Message> messages)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                foreach (var part in message.Parts)
                {
                    if (part is ToolCallPart { Name: SkillTool.ToolName } call
                        && call.Input.ValueKind == JsonValueKind.Object
                        && call.Input.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } skill)
                    {
                        names[call.CallId] = skill;
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<(string, string)>();
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                foreach (var part in messages[i].Parts)
                {
                    if (part is ToolResultPart { IsError: false } r && names.TryGetValue(r.CallId, out var name) && seen.Add(name))
                    {
                        result.Add((name, r.Output.Content));
                    }
                }
            }

            return result;
        }
    }
}
