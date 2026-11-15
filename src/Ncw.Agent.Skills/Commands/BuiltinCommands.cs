namespace Ncw.Agent.Skills.Commands;

/// <summary>
/// 内置命令——写死在代码里的那几条(2.x <c>kernel/command/builtin.ts</c>)。和内置子代理同一个理由:它们先进表、可以被同名文件覆盖,
/// 于是「至少有一条命令可用」是一条无条件成立的事实,而不是「取决于用户机器上有没有那个目录」。
/// <para>
/// <c>/init</c> 加载内置 Skill <c>repo-init</c>,由它产出 AGENTS.md、<c>.agents/codemap/</c> 和 <c>.agents/memory/</c>。方法论住在 Skill 里(30KB、分阶段派子代理、带一整套页面模板,
/// 还会持续迭代),这里只剩一层薄壳:改了资源重启即生效,用户能用同名的项目 Skill 覆盖它。
/// ★ 找不到 Skill 时<b>停下</b>,不让模型凭印象自己写:被禁用、或者工作区只选装了别的 Skill 时它不在目录里;模型凭项目名和几个文件名脑补出来的 AGENTS.md 看起来完全正常,但每一条命令都是猜的。
/// </para>
/// </summary>
public static class BuiltinCommands
{
    private const string InitPrompt =
        "Load the `repo-init` Skill with the Skill tool and follow it to create or update this repository's AGENTS.md, code map (`.agents/codemap/`) and repository memory (`.agents/memory/`).\n\n"
        + "Arguments passed to /init (empty means a full run): $ARGUMENTS\n\n"
        + "If the Skill tool reports that `repo-init` does not exist or is unavailable, do not attempt the task from memory. Tell the user that the built-in repo-init Skill is disabled or not enabled for this workspace, and that they can enable it again in the Extensions panel under Skills. Then stop.";

    public static IReadOnlyList<SlashCommand> All { get; } =
    [
        // 用户看到的那行副标题由界面按 name 翻译;这条英文是兜底(测试与非 UI 调用)。
        new SlashCommand(
            "init", "Analyze the project and generate AGENTS.md, a code map and repository memory", InitPrompt, CommandScope.Builtin, "", "[update|agents-only] [--no-history]"),
    ];
}
