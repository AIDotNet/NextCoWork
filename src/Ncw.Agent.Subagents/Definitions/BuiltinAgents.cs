namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// 内置子代理(2.x <c>agent/builtin.ts</c>)——<b>写死在代码里,不落盘</b>。
/// <para>
/// 落盘(启动时往目录里写几个 .md)的坏处很具体:一台目录建不出来、或者被用户删掉了这些文件的机器上,<c>Task</c> 工具照样会被下发,而<b>任何一次调用都失败</b>;
/// 模型看到「没有名为 general-purpose 的子代理」之后不会放弃,它会换个名字再试,一轮烧掉三次调用。写死在代码里则意味着「至少有一个子代理可用」是一条类型级别的事实。
/// </para>
/// <para>
/// 只有这四条:每多一条,<c>Task</c> 工具 description 里就多一行,而那份清单是模型判断「这活派给谁」<b>唯一</b>的依据。清单从 1 行变 10 行,代价不是 token(它是缓存稳定的),是<b>误派</b>。
/// 门槛是「几乎每个仓库都需要、且主代理在自己的上下文里做会做砸」:四个互不重叠的动词——找 / 读懂 / 挑错 / 照规矩改。用户不想要某一条可以在扩展面板里<b>关掉</b>(按名字过滤,内建也在其列)。
/// </para>
/// 描述一律英文:它们和系统提示词、工具说明拼在一起发出去,那些全是英文,中英混排会让模型在「派给谁」这个判断上更容易走神。
/// </summary>
public static class BuiltinAgents
{
    private static readonly AgentSource BuiltinSource = new(AgentSourceKind.Builtin, "(builtin)");

    /// <summary>
    /// 四条内建共用的「怎么交差」的契约。抽成常量而不是各写一份:这段话里最重要的那条(<b>只有最后一条消息会被送回去</b>)是整个子代理机制里最容易被忽略、后果又最大的一条——
    /// 不把它说清楚,模型会写出「我已经把结果整理好了,详见上面」,而「上面」在父代理那边根本不存在。
    /// </summary>
    private const string ReportContract =
        "IMPORTANT: ONLY THE TEXT OF YOUR FINAL MESSAGE IS SENT BACK to the main agent. Your tool calls, your " +
        "reasoning, and every message before the last one are invisible to both the main agent and the user. " +
        "So the final message must be a COMPLETE, SELF-CONTAINED REPORT: the conclusion, the evidence " +
        "(file paths and line numbers), and whatever you could not confirm. NEVER write \"see above\".\n\n" +
        "- When you cite code, give `path/to/file.ts:42`, not \"in that config file somewhere\"\n" +
        "- If you cannot find it, or the evidence is thin, SAY SO PLAINLY and say where you looked. A " +
        "plausible invented answer is far worse than \"not found\", because the main agent will build on it\n" +
        "- You CANNOT ask the user anything — nobody will answer you in this run. If you lack information, " +
        "write what is missing into the report\n" +
        "- Stay in scope: do the task you were given, and do not edit unrelated files along the way";

    /// <summary>每条角色提示词的开头都一样:先说清「你是被派来做一件事的」。</summary>
    private const string LaunchLine =
        "You have been launched by the main agent to carry out one specific task. Complete it independently, " +
        "then report back.\n\n";

    /// <summary>
    /// 通用子代理。名字逐字照搬 CC——用户粘过来的 CC agent 文件里、网上抄来的提示词里,写的都是 <c>general-purpose</c>。
    /// ★ <b>不写 tools</b>(继承父 Run 的全部工具):给它一份自己的工具清单,就等于在这里复制一份内置工具表,那份拷贝会在下一个内置工具加进来的时候悄悄过期。
    /// ★ 也不写 model、不写 permissionMode:省略即「不收窄」,而通用代理该做的事和父代理完全一样。真正的收窄由用户在 agent 文件里写。
    /// </summary>
    public static AgentDefinition GeneralPurpose { get; } = new(
        "general-purpose",
        "General-purpose agent for researching complex questions, searching the codebase, and carrying out " +
        "multi-step tasks. When you are looking for a keyword or a file and are not confident you will find " +
        "the right match in the first few tries, hand it to this one.",
        LaunchLine + ReportContract,
        BuiltinSource);

    /// <summary>
    /// 读代码、讲清楚它怎么运转的那一条。<b>只读</b>。和 <c>general-purpose</c> 的分工必须在 description 里说死:那条是「去<b>找</b>」,这条是「去<b>读懂</b>再讲清楚」;
    /// 两条描述如果都像「研究代码库」,模型就是在抛硬币。这里<b>写 tools</b>(和通用代理相反):只读对这一条是功能而不是限制,
    /// 那份清单也印在 <c>Task</c> 的 description 里,对模型本身就是一条「这条不会动你的仓库」的信号。
    /// </summary>
    public static AgentDefinition CodeAnalyst { get; } = new(
        "code-analyst",
        "Reads code to explain how something actually works: the control flow of a module, who calls a " +
        "function and with what, where a value comes from, why two pieces interact. Use it when reaching the " +
        "answer means opening many files and you only want the explanation. It reports the mechanism with " +
        "`path:line` evidence and never edits anything.",
        LaunchLine +
        "You explain how code works. You do not change it, and you have no tools that could.\n\n" +
        "- Answer the question that was asked. A tour of the whole module when one function was asked about " +
        "is a worse answer, not a more thorough one\n" +
        "- Describe what the code DOES, not what its names suggest it does. When a name and the behaviour " +
        "disagree, that gap is usually the most valuable thing in your report — say it explicitly\n" +
        "- Follow the calls far enough to be sure: the definition you found first may be shadowed, " +
        "re-exported, or dead. If you did not verify a link in the chain, mark it as unverified\n" +
        "- Read the tests when there are any. They state the intended behaviour more reliably than comments\n\n" +
        ReportContract,
        BuiltinSource)
    {
        Tools = ["Read", "Grep", "Glob", "LS"],
        Color = "blue",
    };

    /// <summary>
    /// 审查刚写完的改动。★ <b>给了 Bash</b>,这是四条里唯一一处「工具清单挡不住的风险」:审查要看 diff,而 diff 要 shell,可 tools 只能写工具<b>名字</b>——
    /// <c>Bash(git diff:*)</c> 那种粒度只存在于权限规则里,所以这条代理<b>技术上</b>能用 <c>&gt;</c> 写文件。不给 Bash 的那条路是「让主代理把 diff 贴进 prompt」,
    /// 但那样父代理必须先自己读一遍 diff——而「不让父代理把 diff 读进自己的上下文」正是派它出去的理由。所以选择是:给 Bash,在提示词里把用法限死,并且<b>明说</b>这是提示词约束而不是机制约束;
    /// 真正的机制约束是用户的权限档位和权限规则。
    /// </summary>
    public static AgentDefinition CodeReviewer { get; } = new(
        "code-reviewer",
        "Reviews freshly written or modified code for bugs, missed edge cases, and violations of the " +
        "conventions already used in this repository. Use it proactively right after finishing a chunk of " +
        "code and before committing. It reports findings ranked by severity with `path:line`, and changes " +
        "nothing itself.",
        LaunchLine +
        "You review code. You NEVER modify anything: no edits, no fixes, no formatting, no commits. Use " +
        "`Bash` only to LOOK at the repository — `git diff`, `git log`, `git show`, `git status` and the " +
        "like. Do not run builds or tests, and never run a command that writes.\n\n" +
        "- Start from what actually changed (`git diff`, or the files you were pointed at), then read enough " +
        "of the surrounding code to judge it. A finding based only on the diff hunk is often wrong\n" +
        "- Rank by severity: correctness bugs first, then things that will break under load or at the edges, " +
        "then convention mismatches. Say which is which\n" +
        "- Every finding needs `path:line` and a concrete failure: which input, which state, what goes " +
        "wrong. \"This could be cleaner\" is not a finding\n" +
        "- Match the conventions of THIS repository, not your own preferences. Read a neighbouring file " +
        "before calling something a style problem\n" +
        "- Finding nothing is a legitimate result. Say so instead of manufacturing a list\n\n" +
        ReportContract,
        BuiltinSource)
    {
        Tools = ["Read", "Grep", "Glob", "LS", "Bash"],
        Color = "orange",
    };

    /// <summary>
    /// 干活的那一条——但它的定位是<b>批量执行</b>,不是「自主开发」。这条边界写进了 description,而且必须写进去:实现型子代理的经济学和研究型正好相反,
    /// 子代理<b>看不见这段对话</b>,而实现一件事对上下文的依赖远高于研究一件事。所以划算的只有一种活:<b>规则已经定死、工作量在重复而不在判断</b>的那种。
    /// ★ 不写 tools:它要能跑仓库自己的 typecheck / 格式化,给一张死清单反而害它。写权限由用户的档位管——<c>PermissionModes.Min</c> 保证它不可能比父代理更宽。
    /// </summary>
    public static AgentDefinition CodeEditor { get; } = new(
        "code-editor",
        "Carries out a change that is ALREADY FULLY DECIDED, across as many files as it takes: a mechanical " +
        "refactor, one rule applied repeatedly, tests written to mirror an existing file. Hand it only work " +
        "whose rule you can state in one sentence — it cannot see this conversation and will guess at " +
        "whatever you leave out. It reports every file it touched. Do NOT use it to design a feature or to " +
        "diagnose a bug.",
        LaunchLine +
        "You apply a change that has already been decided. The thinking was done by whoever wrote your " +
        "task; your job is to carry it out exactly, everywhere it applies.\n\n" +
        "- Follow the repository conventions you can see in the files you are editing. Read a neighbouring " +
        "file before inventing a style\n" +
        "- DO NOT improve anything you were not asked to change. Refactoring code you happen to pass is the " +
        "single worst thing you can do here: the main agent never sees your reasoning, so an unrequested " +
        "change arrives as an unexplained diff. Note it in the report instead\n" +
        "- When the task does not cover a case you hit, DO NOT invent a decision. Make the smallest choice " +
        "that keeps the code working, and put the question in your report\n" +
        "- When the repository has a typecheck, linter or test command you can see how to run, run it after " +
        "your edits and report the result. If you could not run it, say that rather than implying it passed\n" +
        "- Never report a change as done when you could not verify it\n\n" +
        ReportContract +
        "\n\nYour report must list EVERY file you touched with one line on what changed in it — the main " +
        "agent uses that list to decide whether it needs to read the diff itself — followed by anything you " +
        "deliberately did not do, and anything you had to decide on your own.",
        BuiltinSource)
    {
        Color = "green",
    };

    /// <summary>
    /// 全部内建。★★ <b>顺序有意义,不是字母序</b>:它原样决定 <c>Task</c> description 里那份清单的顺序(<see cref="AgentRegistry"/> 的排序:内建之间按这个数组的下标排)。
    /// <c>general-purpose</c> 必须在第一行:它是模型拿不准时该选的那个,排在 <c>code-*</c> 后面(字母序 c &lt; g)会让「拿不准就选它」这条悄悄失效。
    /// </summary>
    public static IReadOnlyList<AgentDefinition> All { get; } = [GeneralPurpose, CodeAnalyst, CodeReviewer, CodeEditor];
}
