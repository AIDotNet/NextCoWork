using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Tools;

/// <summary>
/// 内置工具清单(03 §2)。每次调用返回新实例的列表,调用方改它不会污染下一次。
/// 注册表(Kernel 之上的会话层)先注册它们:内置工具先注册,后来者与已有 id 冲突时丢弃后来者,任何来源都不能替换 <c>Bash</c> 等内置工具。
/// <para>
/// 已实现的是文档里的「基础工具」:Read / Write / Edit / LS / Glob / Grep / Bash / BashOutput / KillShell。
/// 依赖尚未存在的模块的工具(TodoWrite、WebFetch / web_search、Skill、AskUserQuestion、EnterPlanMode / ExitPlanMode、Task、ProposeGoal、定时任务、浏览器、visualize_*、图片 / 视频生成、Workflow)
/// 随各自的模块移植,不在这里。
/// </para>
/// </summary>
public static class BuiltinTools
{
    /// <summary>CC 的那几个名字一个不少,顺序固定(系统提示词的工具段按它排,缓存友好)。</summary>
    public static IReadOnlyList<IAgentTool> Create() =>
    [
        new ReadTool(),
        new WriteTool(),
        new EditTool(),
        new LsTool(),
        new GlobTool(),
        new GrepTool(),
        new BashTool(),
        new BashOutputTool(),
        new KillShellTool(),
    ];
}
