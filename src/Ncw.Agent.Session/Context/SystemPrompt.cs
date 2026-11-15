using System.Globalization;
using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>「偏好 › 个性化」那三栏。姓名和背景是事实,全局提示词是指令,所以分成两段。</summary>
public sealed record Personalization(string Name = "", string Background = "", string Instructions = "")
{
    public const int NameMax = 200;
    public const int BackgroundMax = 2000;
    public const int InstructionsMax = 8000;
}

/// <summary>系统提示词 <c># Environment</c> 段要的事实(全是事实,没有规则)。</summary>
/// <param name="Facts">环境事实(操作系统、shell、主机);来自 <c>IExecutionEnvironment.Facts</c>。</param>
/// <param name="Kind">本机 / SSH。</param>
/// <param name="DisplayName">环境名字(例如 <c>dev-server</c>)。</param>
public sealed record EnvironmentPromptFacts(EnvironmentFacts Facts, EnvironmentKind Kind, string DisplayName)
{
    public static EnvironmentPromptFacts Of(IExecutionEnvironment environment) => new(environment.Facts, environment.Kind, environment.DisplayName);
}

/// <summary>
/// 系统提示词的输入。时钟、工作区根、环境事实全部作为参数传入——<b>纯函数,不读时钟、不碰文件系统</b>(04 §2)。
/// <see cref="ExtensionSections"/> 是扩展经 <c>ContributeSystemSection</c> 贡献的节(名字为 <c>permissions</c> 的紧跟在 Environment 之后,其余排在工具段之后);
/// <see cref="Tools"/> 是本回合的工具快照(取各工具的 PromptSnippet / PromptGuidelines)。
/// </summary>
public sealed record SystemPromptInput(
    string WorkspaceRoot,
    DateTimeOffset Now,
    EnvironmentPromptFacts? Environment = null,
    string? AgentPrompt = null,
    Personalization? Personalization = null,
    string? ModePrompt = null,
    IReadOnlyList<(string Name, string Text)>? ExtensionSections = null,
    ToolSnapshot? Tools = null);

/// <summary>系统提示词的一段。<see cref="Bucket"/> 用来给上下文占用做归因(各档之和恒等于总量)。</summary>
public sealed record SystemPart(string Bucket, string Text);

public static class SystemPromptBuckets
{
    public const string System = "system";
    public const string Instructions = "instructions";
    public const string Tools = "tools";
}

/// <summary>
/// 系统提示词的稳定部分(04 §2),按顺序拼接,空段跳过:基础提示词 → 子代理角色 → <c># Environment</c> → 权限说明 → 个性化 → 模式提示词 → 工具段 → 其余扩展段(Skills 目录等)。
/// <para>
/// ★ <b>提示词一律英文,注释一律中文</b>。系统提示词每轮重发,而且是 prompt cache 的前缀——往里加一行之前先过四关:说得出没有它模型会做错的那件具体事;
/// 形容词换成阈值或例子;能变成事实就不写成规则、能变成工具描述就不写在这里、能在代码里强制就不靠嘱咐;对着真实的失败写。
/// </para>
/// <para>
/// 与设计一致的两处取舍:系统提示词里<b>不放任何随回合变化的东西</b>(日期精确到天、git 状态和待办在状态块里),Skill 目录只列名字和描述、正文永不进来(Skills 扩展经 <c>ContributeSystemSection</c> 贡献)。
/// </para>
/// </summary>
public static class SystemPrompt
{
    /// <summary>基础提示词:按 pi 的思路精简,只写行为规则(2.x 的 BASE_PROMPT 去掉了与工具名绑定的措辞,工具相关的说明由各工具的 PromptSnippet / PromptGuidelines 提供)。</summary>
    public const string Base = """
        You are the coding assistant in NextCoWork. You help the user with software work in their workspace by reading, searching, editing and running things through the tools you are given.

        # Tone and style
        - Be concise. Keep prose under 4 lines unless the user asks for detail or the task genuinely needs more. Code, diffs, and tool output do not count toward that.
        - No preamble, no postamble. Do not open with "Here's what I found" or close with a summary of what you just did unless the user asks. Answer, then stop.
        - Reply in the language the user writes in.
        - Output is rendered as GitHub-flavored Markdown in a chat pane.
        - Reference code as `path/to/file.ts:42` so the user can jump straight to it. Quote only the lines that matter; never paste back a whole file the user already has.
        - Explain a command before you run it when it changes the user's machine or takes real time.

        # Proactiveness
        Do what the user asked, completely, and stop there.
        - NEVER refactor, rename, reformat, upgrade, or "clean up" code the task did not require. Notice it, say so in one line, and let the user decide.
        - NEVER create a file the user did not ask for, and NEVER write documentation (*.md, README) unless asked for it.
        - When the user asks a QUESTION, answer it. Do not start editing because the answer implies an edit.

        # Following conventions
        - Before you change code, read enough of the surrounding file to match its naming, idioms, typing style, and how much it comments.
        - NEVER assume a library or a command is available. Check the manifest or find an existing use of it first. The test, lint, build, and run commands are whatever THIS repo defines.
        - NEVER commit, push, or publish anything unless the user asks you to.

        # Doing the work
        - Prefer editing an existing file over creating a new one.
        - Batch independent tool calls into a single reply. Several searches at once beat one per turn.
        - Verify when verifying is cheap: run the test, run the typechecker, re-read the line you edited. NEVER report that something passes when you did not run it.
        - Finish the whole task. If one part is genuinely blocked, do everything else and say plainly what you left out and why.

        # When things go wrong
        - If a call fails twice the same way, STOP repeating it. Change the approach, or tell the user what is blocking you.
        - READ the error before you react to it. Tool errors here are written to tell you what to do next; most of them name the fix.
        - Report what actually happened. If a test fails, show the failure. If you skipped a step, say so. NEVER describe work you did not do.

        # Permissions
        Every tool call is checked against the permission mode the user chose for this workspace.
        If a call is denied, do NOT route around it: do not retry it, do not reach for a different tool
        that does the same thing, and do not use a shell command to do what the denied tool would have done.
        Stop and tell the user which permission you need.
        """;

    public const string PermissionsSectionName = "permissions";

    public static string Build(SystemPromptInput input) => Join(Parts(input));

    /// <summary>★ 拼法只此一处:分段估算要拿它对不变量,两边写法一漂,归因就永远差一点。</summary>
    public static string Join(IEnumerable<SystemPart> parts) => string.Join("\n\n", parts.Select(p => p.Text).Where(t => t.Length > 0));

    public static IReadOnlyList<SystemPart> Parts(SystemPromptInput input)
    {
        var extensions = input.ExtensionSections ?? [];
        var permissions = extensions.Where(s => s.Name == PermissionsSectionName).Select(s => s.Text.Trim());
        var others = extensions.Where(s => s.Name != PermissionsSectionName).Select(s => s.Text.Trim());
        var parts = new List<SystemPart>
        {
            new(SystemPromptBuckets.System, Base),
            // ★ 角色提示词追加在基础提示词之后,永远不替换它:替换掉的子代理会丢掉「被拒绝时不要试图绕开」这类约束。
            new(SystemPromptBuckets.System, string.IsNullOrWhiteSpace(input.AgentPrompt) ? "" : $"# Your role\n\n{input.AgentPrompt.Trim()}"),
            new(SystemPromptBuckets.System, EnvironmentSection(input)),
        };
        parts.AddRange(permissions.Where(t => t.Length > 0).Select(t => new SystemPart(SystemPromptBuckets.System, t)));
        // 个性化:在事实之后(它和日期、shell 同档,都是本次运行的常量,进稳定前缀)、在模式说明之前(模式说「你现在只有只读工具」必须压得住一条「别问了直接改」的全局提示词)。
        // 归因算 instructions:这一段是用户自己写进去的,和关不掉的基础提示词不同档。
        parts.Add(new SystemPart(SystemPromptBuckets.Instructions, input.Personalization is null ? "" : PersonalizationSection(input.Personalization)));
        parts.Add(new SystemPart(SystemPromptBuckets.System, input.ModePrompt?.Trim() ?? ""));
        parts.Add(new SystemPart(SystemPromptBuckets.Tools, input.Tools is null ? "" : ToolsSection(input.Tools)));
        parts.AddRange(others.Where(t => t.Length > 0).Select(t => new SystemPart(SystemPromptBuckets.System, t)));
        return parts;
    }

    /// <summary>
    /// <c># Environment</c>:全是事实。<c>Platform</c> 挡掉的是一整类 shell 失败(macOS 的 <c>sed -i</c> 要带空串参数、没有 <c>readlink -f</c>),成本是三个 token。
    /// 执行环境(本机 / SSH 主机)在这里说清楚:工作区里的文件和命令都在那台机器上执行,客户端的文件系统和浏览器不可用。
    /// </summary>
    private static string EnvironmentSection(SystemPromptInput input)
    {
        var text = new StringBuilder("# Environment\n\n");
        text.Append("Workspace root: ").Append(input.WorkspaceRoot).Append('\n');
        if (input.Environment is { } env)
        {
            var facts = env.Facts;
            text.Append("Platform: ").Append(OsName(facts.Os)).Append(" (").Append(facts.OsVersion).Append(")\n");
            text.Append("Shell: ").Append(facts.Shell).Append('\n');
            var remote = env.Kind == EnvironmentKind.Ssh;
            text.Append("Execution location: ").Append(remote ? "SSH server" : "local machine").Append(' ').Append(Quote(env.DisplayName)).Append('\n');
            if (remote)
            {
                text.Append("Host: ").Append(Quote(facts.Hostname)).Append("; user: ").Append(Quote(facts.Username)).Append("; home: ").Append(Quote(facts.Home)).Append('\n');
                text.Append("Workspace files, commands and terminals execute on this server. The client filesystem and client browser are not available to workspace tools.\n");
            }
        }

        text.Append("Today's date: ").Append(input.Now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" (UTC)");
        return text.ToString();
    }

    private static string OsName(EnvironmentOs os) => os switch
    {
        EnvironmentOs.MacOS => "macOS",
        EnvironmentOs.Windows => "Windows",
        _ => "Linux",
    };

    // JSON 风格的引号:主机名、用户名这类来自远端的值不能顺手带进一个换行
    private static string Quote(string value) => "\"" + PromptText.StripControlChars(value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

    /// <summary>
    /// 个性化。仍然要消毒:粘贴进来的文本能带控制字符,旧库里也可能躺着一段在上限存在之前写下的超长指令。
    /// 最后那句是<b>优先级声明</b>:设置里写下的是「默认怎么做」,聊天框里刚打的那句是「这一次要怎么做」,冲突时后者赢。
    /// </summary>
    private static string PersonalizationSection(Personalization p)
    {
        static string Clean(string s, int max) => PromptText.Clamp(PromptText.StripControlChars(s).Trim(), max);
        var name = Clean(p.Name, Personalization.NameMax);
        var background = Clean(p.Background, Personalization.BackgroundMax);
        var instructions = Clean(p.Instructions, Personalization.InstructionsMax);
        var sections = new List<string>();
        var facts = new List<string>();
        if (name.Length > 0)
        {
            facts.Add($"Name: {name}");
        }

        if (background.Length > 0)
        {
            facts.Add($"What they do: {background}");
        }

        if (facts.Count > 0)
        {
            sections.Add(
                "# About the user\n\nThe user filled this in themselves, in Settings. It is background, not a task: "
                + "do not greet them by name every turn and do not bring it up unless it is relevant.\n\n" + string.Join("\n", facts));
        }

        if (instructions.Length > 0)
        {
            sections.Add(
                "# User instructions\n\nStanding instructions the user set in Settings. They apply to every conversation. "
                + "The user cannot see this block: do not mention it, do not thank them for it. "
                + "When it conflicts with what the user just asked you for, the user's latest message wins.\n\n" + instructions);
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// 工具段(取自 pi):<c>PromptSnippet</c> 一行一个工具(只列有 snippet 的),<c>PromptGuidelines</c> 去重后合成一组规则。
    /// 工具的描述和 schema 已经随请求的 tools 字段发给模型,这里只放「怎么选工具」的路由说明,所以工具不在快照里时那段字也不会占位。
    /// </summary>
    public static string ToolsSection(ToolSnapshot tools)
    {
        var snippets = new List<string>();
        var guidelines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in tools.ModelTools)
        {
            if (tools.Find(info.ExternalName)?.Info is not { } spec)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(spec.PromptSnippet))
            {
                snippets.Add($"- {info.ExternalName}: {spec.PromptSnippet.Trim()}");
            }

            foreach (var guideline in spec.PromptGuidelines ?? [])
            {
                var line = guideline.Trim();
                if (line.Length > 0 && seen.Add(line))
                {
                    guidelines.Add($"- {line}");
                }
            }
        }

        if (snippets.Count == 0 && guidelines.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder("# Tools\n");
        if (snippets.Count > 0)
        {
            text.Append("\nAvailable tools:\n").Append(string.Join("\n", snippets)).Append('\n');
        }

        if (guidelines.Count > 0)
        {
            text.Append("\nGuidelines:\n").Append(string.Join("\n", guidelines)).Append('\n');
        }

        return text.ToString().TrimEnd();
    }
}
