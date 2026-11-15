namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// <c>tools:</c> 里那些名字 → 内部 id(2.x <c>agent/tool-alias.ts</c>)。
/// <para>
/// 这张表挡的是所有失败模式里最坏的一个:用户会把 Claude Code 的 agent 文件<b>原样</b>粘过来,里面写的是 <c>tools: Read, Grep, Glob</c>。
/// 全对不上时是<b>零个工具</b>,而零工具的子代理不会报错——它会跑起来、发现自己什么也做不了、然后<b>凭想象编一个答案交上来</b>。
/// 所以归一化在<b>加载期</b>:认不出的名字要在加载诊断里当场说出来。
/// </para>
/// 内置工具的 id 逐字照搬 CC,所以这张表大部分是恒等映射;剩下的一小部分是真的:大小写(<c>read, grep</c>)、CC 有而我们没有的(<c>MultiEdit</c> / <c>NotebookEdit</c> 在我们这儿都是 <c>Edit</c>)、
/// 同名不同物(CC 的 <c>WebSearch</c> 是我们的 <c>web_search</c>,不是 <c>WebFetch</c>)。认不出就返回 null,<b>不静默丢弃、也不放行</b>。
/// </summary>
public static class AgentToolNames
{
    // 键必须全小写,查表前先转小写:这张表存在的理由之一正是不挑写法。
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["read"] = "Read",
        ["write"] = "Write",
        ["edit"] = "Edit",
        ["multiedit"] = "Edit",
        ["notebookedit"] = "Edit",
        ["bash"] = "Bash",
        // 和 CC 同名。子代理只写 Bash 时拿不到这两个——而一个能起后台命令、却读不到它输出、也停不掉它的子代理,比根本不让它起更糟。
        ["bashoutput"] = "BashOutput",
        ["killshell"] = "KillShell",
        ["glob"] = "Glob",
        ["grep"] = "Grep",
        ["ls"] = "LS",
        ["webfetch"] = "WebFetch",
        // 不是 WebFetch:两个是不同的工具,搞混了子代理就上不了网只能抓 URL。
        ["websearch"] = "web_search",
        ["web_search"] = "web_search",
        ["todowrite"] = "TodoWrite",
        ["task"] = "Task",
        ["skill"] = "Skill",
        ["echo"] = "echo",
    };

    /// <summary>这张表认得的全部写法(给测试和诊断用)。</summary>
    public static IReadOnlyCollection<string> KnownAliases => Aliases.Keys;

    /// <summary>一个名字 → 内部 id;认不出返回 null。容忍前后空白。</summary>
    public static string? Normalize(string raw) => Aliases.GetValueOrDefault(raw.Trim().ToLowerInvariant());

    /// <summary>
    /// 整份 <c>tools:</c> 的归一化:认出来的按原顺序、已去重(<c>Read, MultiEdit, Edit</c> 会把 <c>Edit</c> 归一化出两次,去重让诊断和界面上的「这个子代理有 3 个工具」不是谎话);
    /// 认不出的原文留着,诊断里要显示给用户看。
    /// </summary>
    public static (IReadOnlyList<string> Tools, IReadOnlyList<string> Unknown) NormalizeList(IEnumerable<string> raw)
    {
        var tools = new List<string>();
        var unknown = new List<string>();
        foreach (var item in raw)
        {
            if (item.Trim().Length == 0)
            {
                continue;
            }

            if (Normalize(item) is not { } hit)
            {
                unknown.Add(item.Trim());
                continue;
            }

            if (!tools.Contains(hit, StringComparer.Ordinal))
            {
                tools.Add(hit);
            }
        }

        return (tools, unknown);
    }
}
