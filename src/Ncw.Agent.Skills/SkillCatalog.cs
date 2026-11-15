using Ncw.Core.Domain;

namespace Ncw.Agent.Skills;

/// <summary>
/// 一次扫描的结果,<b>不可变</b>(2.x <c>SkillRegistry</c>,但 3.0 不再有进程内的可变注册表:每个 Run 开始时扫一次、产出一份这个快照,整个 Run 都读它)。
/// 按名字排序:目录里的顺序会直接变成系统提示词里的顺序,而目录遍历的顺序在不同平台上不一样——抖动的提示词前缀会让上游的 prompt cache 整体失效。
/// </summary>
public sealed class SkillCatalog
{
    public static SkillCatalog Empty { get; } = new([], []);

    public SkillCatalog(IEnumerable<Skill> skills, IReadOnlyList<SkillDiagnostic> diagnostics)
    {
        Skills = [.. skills.OrderBy(s => s.Name, StringComparer.Ordinal)];
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Skill> Skills { get; }

    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; }

    public Skill? Get(string name) => Skills.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// 把工作区的选装清单解析成真正要下发的那几条。★ <b>空清单 = 全部可用</b>(<paramref name="explicitSelection"/> 为假时);列了就是白名单,认不出的 id 被忽略。
    /// </summary>
    public IReadOnlyList<Skill> Resolve(IReadOnlyList<string>? ids, bool explicitSelection = false)
    {
        if (ids is null)
        {
            return Skills;
        }

        if (ids.Count == 0)
        {
            return explicitSelection ? [] : Skills;
        }

        var want = new HashSet<string>(ids, StringComparer.Ordinal);
        return [.. Skills.Where(s => want.Contains(s.Id))];
    }

    /// <summary>
    /// 「这一轮下发哪几条」——<b>唯一</b>的合并规则(全局开关 ∩ 工作区选装 ∩ 本机资源可用),设置页的「已启用」显示和每个 Run 的快照都读它。
    /// </summary>
    public static bool IsActive(Skill skill, IReadOnlySet<string> disabledGlobally, WorkspaceSkillSelection? selection) =>
        !disabledGlobally.Contains(skill.Id)
        && SkillSelection.IsActiveInWorkspace(skill.Id, skill.ClientAssetsOnly, selection?.Ids, selection?.Explicit ?? false);
}

/// <summary>一个 Run 的 Skill 快照:<see cref="Active"/> 进目录(按名字排序),<see cref="Unavailable"/> 是被开着却跑不了的(只用于给 <c>Skill</c> 工具一个准确的拒绝)。</summary>
public sealed record SkillSnapshot(IReadOnlyList<Skill> Active, IReadOnlySet<string> Unavailable)
{
    public static SkillSnapshot Empty { get; } = new([], new HashSet<string>());
}
