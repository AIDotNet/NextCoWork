namespace Ncw.Core.Domain;

/// <summary>
/// Skill 的「在某个工作区里启用 / 取消启用」规则(2.x 的 <c>skill/activation.ts</c> + <c>SkillRegistry.resolve</c>):纯函数,Agent 和客户端共用(AGENTS.md §3 规则 3)。
/// <para>
/// ★ <b>空清单 = 全都要</b>(<c>explicit</c> 为假时)。出厂的 <c>activeSkillIds</c> 是 <c>[]</c>,按字面理解就是「新工作区里所有 Skill 都不生效」,用户装完 Skill 却发现模型没反应。
/// 渐进披露之后每条 Skill 在提示词里只占一行,没有成本上的理由默认关掉:<b>列了就是白名单,没列就是全都要</b>。<c>explicit</c> = 用户手动选装过,此时空清单就是「一条都不要」。
/// </para>
/// </summary>
public static class SkillSelection
{
    /// <summary>
    /// 启用 / 取消启用一条之后 <c>activeSkillIds</c> 应该变成什么。隐式的「全都要」先物化成显式清单,否则「关掉一条」是个空操作;
    /// 设置页的开关和提炼会话写完 Skill 后的自动启用走<b>同一份</b>规则。
    /// </summary>
    public static IReadOnlyList<string> NextActiveIds(IReadOnlyList<string> current, bool explicitSelection, IReadOnlyList<string> allIds, string skillId, bool active)
    {
        var baseIds = current.Count == 0 && !explicitSelection ? allIds : current;
        return active
            ? [.. baseIds.Concat([skillId]).Distinct(StringComparer.Ordinal)]
            : [.. baseIds.Where(id => id != skillId)];
    }

    /// <summary>一次设置更新是否真的改变了(顺序敏感的)id 清单。判据和 <see cref="NextActiveIds"/> 放在一起,免得两处漂移。</summary>
    public static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);

    /// <summary>
    /// 这条 Skill 在这个工作区里是否被选装(不含全局开关):空清单且非显式 = 全都要;<paramref name="ids"/> 为 null = 没指定工作区(全局设置页),不谈「在这个工作区激活」,恒为真。
    /// 本机资源不可用的(<paramref name="unavailable"/>)永远不激活。
    /// </summary>
    public static bool IsActiveInWorkspace(string skillId, bool unavailable, IReadOnlyList<string>? ids, bool explicitSelection) =>
        !unavailable && (ids is null || (!explicitSelection && ids.Count == 0) || ids.Contains(skillId, StringComparer.Ordinal));

    /// <summary>
    /// frontmatter 的 <c>name</c> 可能和目录名不一致(加载器以 name 为准)。自动启用要按<b>实际写入的目录</b>找到扫描结果:路径统一分隔符后匹配尾部。
    /// </summary>
    public static bool MatchesProjectSkillPath(string skillPath, string projectSkillsRelative, string directoryName)
    {
        var normalized = skillPath.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var expected = $"{projectSkillsRelative}/{directoryName}/SKILL.md";
        return normalized == expected || normalized.EndsWith($"/{expected}", StringComparison.Ordinal);
    }
}
