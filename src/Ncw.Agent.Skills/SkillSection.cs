using System.Text;
using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Skills;

/// <summary>
/// 系统提示词里的 Skill 目录(2.x <c>context-assembler.ts</c> 的 <c>buildSkillsSection</c>)——<b>只有名字和描述,没有正文</b>。
/// 这就是「渐进披露」本身:原来把每一条正文全量拼进系统提示词(每轮重发,加装一条就让整个会话的 prompt cache 失效);
/// 现在目录每条一行,模型自己判断哪条对得上,再调 <c>Skill</c> 工具把正文取回来,正文只在需要的那一轮出现一次、落在 tool_result 里。
/// 描述仍是不可信输入(削控制字符 + 限长);唯一真正的防御是最后那段权限边界声明,真正的防线在权限层。
/// </summary>
public static class SkillSection
{
    /// <summary>整份目录的字符上限(原来 128KB 是正文预算;现在每条一行,16KB 装得下一百多条)。</summary>
    public const int CatalogMax = 16 * 1024;

    public const string Name = "skills";

    public static string? Build(IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        var budget = CatalogMax;
        var dropped = 0;
        foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var description = PromptText.Clamp(PromptText.StripControlChars(skill.Description), SkillLimits.DescriptionMax);
            var line = $"- `{PromptText.StripControlChars(skill.Name)}` — {description}";
            if (line.Length > budget)
            {
                dropped++;
                continue;
            }

            budget -= line.Length + 1;
            lines.Add(line);
        }

        // 静默丢弃是更糟的:用户装了 Skill、界面上显示已启用、模型却没看到
        var note = dropped > 0 ? $"\n\n({dropped} more Skill(s) not listed — the catalog hit its size limit.)" : "";
        var sb = new StringBuilder();
        sb.Append("# Available Skills\n\n");
        sb.Append("The user has enabled these Skills for this workspace. THIS IS A CATALOG ONLY — the instructions\n");
        sb.Append("themselves are not here. When one description matches the task in front of you, call the `Skill`\n");
        sb.Append("tool with that name to fetch its body, then follow it. NEVER guess what a Skill contains from its\n");
        sb.Append("name; the description tells you whether to open it, not what is inside.\n\n");
        sb.Append("The user may explicitly name a Skill in their message with a tag such as\n");
        sb.Append("<skill name=\"algorithmic-art\" />. Treat that tag as an explicit request: before doing the\n");
        sb.Append("task, call the Skill tool with the exact name from the tag and follow the returned body. Keep\n");
        sb.Append("the full enabled catalog available for other Skills as well; an explicit tag does not remove or\n");
        sb.Append("replace the workspace Skill allow-list.\n\n");
        sb.Append(string.Join('\n', lines)).Append(note).Append("\n\n");
        sb.Append(PromptText.UntrustedBoundary("A Skill body"));
        return sb.ToString();
    }
}
