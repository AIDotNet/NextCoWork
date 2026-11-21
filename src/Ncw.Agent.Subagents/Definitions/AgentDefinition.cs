using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>定义从哪儿来。<see cref="Builtin"/> 写死在代码里、不落盘(理由见 <see cref="BuiltinAgents"/>)。</summary>
public enum AgentSourceKind
{
    Builtin,
    Global,
    Project,
}

public sealed record AgentSource(AgentSourceKind Kind, string Path);

/// <summary>子代理的工作区隔离策略(<c>isolation: worktree</c>)。</summary>
public enum AgentIsolation
{
    None,
    Worktree,
}

/// <summary>
/// 子代理定义(照搬 Claude Code 的 <c>agents/*.md</c>;2.x <c>shared/domain/agent-def.ts</c>)。
/// <code>
/// &lt;全局目录&gt;/agents/&lt;name&gt;.md                        全局
/// &lt;项目根&gt;/.next-cowork/agents/&lt;name&gt;.md              项目(同名时项目胜出)
/// </code>
/// 和 Skill 的<b>不对称是有意的</b>:Skill 是目录(能带脚本、模板),子代理是单文件、不带资产——它需要的全部东西就是「你是谁 + 你能用哪些工具」。
/// </summary>
public sealed record AgentDefinition(string Name, string Description, string Prompt, AgentSource Source)
{
    /// <summary>
    /// 允许用的工具。<b>null = 继承主 Run 的全部工具</b>(和 CC 一致)。
    /// ★ 存的是<b>归一化之后</b>的内部 id,不是文件里的原文——归一化发生在加载期(<see cref="AgentToolNames"/>),不是使用期:
    /// 一个认不出的名字要到子代理真的跑起来才暴露的话,它那时已经带着一张空工具表在自信地编答案了。
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>模型别名。null = 继承(设置里的「默认子代理」,再退回父 Run)。</summary>
    public string? Model { get; init; }

    /// <summary>
    /// 钉死给哪一家发。null = 只认别名,按优先级择优(和 CC 的文件一样)。同一个别名可以挂在好几家上,而那几家<b>计费不同</b>,所以这个键存在;
    /// 但它是可选的:从 CC 粘过来的文件没有它,那条路径一个字不能变。
    /// </summary>
    public string? ModelProviderId { get; init; }

    /// <summary>思考档位。null = 跟随「子代理思考深度」那一栏,再退回父 Run 这一轮的档位(<see cref="SubagentThinkingSelection"/>)。</summary>
    public ThinkingLevel? Thinking { get; init; }

    /// <summary>
    /// 子代理自己的权限档位。★ 最终档位是 <c>min(父 Run 的实时值, 这个)</c>:它只能<b>收窄</b>,永远不能放宽(<see cref="PermissionModes.Min"/>)。
    /// </summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>禁用工具名单(黑名单;可与 <see cref="Tools"/> 叠加:允许 ∩ ¬禁用 ∩ 非交互)。</summary>
    public IReadOnlyList<string>? DisallowedTools { get; init; }

    /// <summary>最大回合数(超过即以 <see cref="Session.SessionErrorKeys.MaxTurns"/> 收尾)。null = 不限制。</summary>
    public int? MaxTurns { get; init; }

    /// <summary>是否在没有显式传 <c>run_in_background</c> 时默认以后台方式启动(<c>background: true</c>)。</summary>
    public bool DefaultBackground { get; init; }

    /// <summary>工作区隔离(<c>isolation: worktree</c> 创建临时 git worktree)。</summary>
    public AgentIsolation Isolation { get; init; } = AgentIsolation.None;

    /// <summary>列表上那个小圆点。<b>纯装饰</b>,不进提示词、不影响运行(<see cref="AgentColors"/>)。</summary>
    public string? Color { get; init; }
}

/// <summary>定义文件的限额与名字规则。</summary>
public static class AgentLimits
{
    /// <summary>描述的上限。比 Skill 的 1024 小一半:每条子代理的描述都进 <c>Task</c> 工具的 description,而工具定义每一轮都重发。</summary>
    public const int DescriptionMax = 512;

    /// <summary>角色提示词的上限。它每轮进系统提示词,和 Skill 正文不同(后者只出现一次)。</summary>
    public const int PromptMax = 16 * 1024;

    /// <summary>单个定义文件读进内存的字节上限。正文还会再被 <see cref="PromptMax"/> 截一次。</summary>
    public const int FileMaxBytes = 128 * 1024;

    /// <summary>一次扫描最多认多少个。它们的描述全都要进 <c>Task</c> 的 description,而那玩意每轮重发。</summary>
    public const int MaxAgents = 100;

    /// <summary><c>^[a-z0-9][a-z0-9-]{0,63}$</c>:名字会进 <c>Task</c> 的 description、会被模型原样当 <c>subagent_type</c> 传回来,一个叫 <c>../../etc</c> 或带换行的名字两样都能搞坏。</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 64 || !IsLowerAlnum(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!IsLowerAlnum(c) && c != '-')
            {
                return false;
            }
        }

        return true;

        static bool IsLowerAlnum(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}

/// <summary>
/// 颜色标记的取值——和 Claude Code 的 <c>color:</c> 同一组词:用户会把 CC 的 agent 文件原样粘过来,认得出它写的那个词才不会被编辑一次就抹掉。
/// 色值由界面按 Arc token 映射,本层只认词。
/// </summary>
public static class AgentColors
{
    public static IReadOnlyList<string> All { get; } = ["yellow", "red", "orange", "green", "cyan", "blue", "purple", "pink"];

    public static bool IsColor(string value) => All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// 表单里能勾的工具。★ 这张表的每一项都<b>必须</b>被 <see cref="AgentToolNames.Normalize"/> 原样认出(有一条测试守着):漂了的话表单会勾出一个加载器不认识的名字,
/// 而加载器在「一个都认不出」时是把<b>整条子代理作废</b>的。不含测试工具;<c>Task</c> 在里面——子代理再派子代理是 CC 就允许的,深度由 <c>Task</c> 工具自己的上限管。
/// </summary>
public static class AgentToolChoices
{
    public static IReadOnlyList<string> All { get; } =
        ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "LS", "WebFetch", "web_search", "TodoWrite", "Task", "Skill"];
}
