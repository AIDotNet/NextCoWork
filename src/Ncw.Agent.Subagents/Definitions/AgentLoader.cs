using Ncw.Core.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>加载诊断的稳定 key(控制端按它翻译成界面文字;<see cref="AgentDiagnostic.Message"/> 是英文,给日志和排错)。</summary>
public static class AgentDiagnosticKeys
{
    public const string DirectoryUnreadable = "agents.directoryUnreadable";
    public const string FileUnreadable = "agents.fileUnreadable";
    public const string FileNameInvalid = "agents.fileNameInvalid";
    public const string TooMany = "agents.tooMany";
    public const string OutsideRoot = "agents.outsideRoot";
    public const string Frontmatter = "agents.frontmatter";
    public const string NameMismatch = "agents.nameMismatch";
    public const string NameInvalid = "agents.nameInvalid";
    public const string DescriptionMissing = "agents.descriptionMissing";
    public const string PromptEmpty = "agents.promptEmpty";
    public const string ToolsUnknown = "agents.toolsUnknown";
    public const string ToolsNone = "agents.toolsNone";
    public const string PermissionModeInvalid = "agents.permissionModeInvalid";
    public const string ThinkingInvalid = "agents.thinkingInvalid";
}

/// <summary>一条加载诊断:哪个文件 / 目录、什么原因。<see cref="Params"/> 是结构化参数(名字、值)。</summary>
public sealed record AgentDiagnostic(string Path, string Key, string Message, IReadOnlyDictionary<string, string>? Params = null);

public sealed record AgentScanResult(IReadOnlyList<AgentDefinition> Agents, IReadOnlyList<AgentDiagnostic> Diagnostics);

/// <summary>
/// 扫描的输入。全局层和项目层各有自己的执行环境:全局目录在<b>设备本机</b>,项目目录在项目的执行环境里(可能是 SSH)。
/// 根为 null = 跳过那一层。只经 <see cref="IExecutionEnvironment"/> 读文件(AGENTS.md §5)。
/// </summary>
public sealed record AgentScanInput(
    IExecutionEnvironment? GlobalEnvironment,
    EnvPath? GlobalRoot,
    IExecutionEnvironment? ProjectEnvironment,
    EnvPath? ProjectRoot)
{
    /// <summary>目录名。项目级 = <c>&lt;项目根&gt;/.next-cowork/agents</c>(<see cref="ProjectRuleFile.DirectoryName"/> 那一层)。</summary>
    public const string AgentsDirectory = "agents";

    public static EnvPath ProjectAgentsRoot(EnvPath projectRoot) => projectRoot.Join(ProjectRuleFile.DirectoryName, AgentsDirectory);

    public static EnvPath GlobalAgentsRoot(EnvPath dataRoot) => dataRoot.Join(AgentsDirectory);
}

/// <summary>
/// 子代理定义的扫描(2.x <c>agent/load.ts</c>)。结构和 Skill 加载器是刻意平行的(两层目录、同名时项目胜出、永不抛、一切失败变诊断),差别有两处:
/// <b>单文件,不是目录</b>;<b>内建的那条先进表</b>——「至少有一个子代理可用」是一条无条件成立的事实,而不是「取决于用户机器上有没有那个目录」。
/// </summary>
public static class AgentLoader
{
    /// <summary>
    /// 扫两层目录,产出一张子代理表(内建的永远在里面)。★ <b>永不抛</b>:一个坏文件让整次扫描失败的话,用户看到的是「所有子代理都不见了」。
    /// 唯一的例外是执行环境连不上(<see cref="EnvironmentErrorCode.Disconnected"/> 等):那不是文件的问题,不能把它判成「没有子代理」——原样抛给调用方。
    /// </summary>
    public static async Task<AgentScanResult> ScanAsync(AgentScanInput input, CancellationToken ct = default)
    {
        var diagnostics = new List<AgentDiagnostic>();
        var byName = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        var order = new List<string>();

        // 内建的先进。用户可以用同名文件覆盖它;覆盖失败(文件是坏的)时留下的仍然是这条能用的,而不是一个空表。
        foreach (var builtin in BuiltinAgents.All)
        {
            byName[builtin.Name] = builtin;
            order.Add(builtin.Name);
        }

        // 顺序即优先级:全局先进,项目后进覆盖同名。
        if (input.GlobalEnvironment is { } globalEnv && input.GlobalRoot is { } globalRoot)
        {
            await ScanRootAsync(globalEnv, globalRoot, AgentSourceKind.Global, byName, order, diagnostics, ct).ConfigureAwait(false);
        }

        if (input.ProjectEnvironment is { } projectEnv && input.ProjectRoot is { } projectRoot)
        {
            await ScanRootAsync(projectEnv, projectRoot, AgentSourceKind.Project, byName, order, diagnostics, ct).ConfigureAwait(false);
        }

        return new AgentScanResult([.. order.Select(n => byName[n])], diagnostics);
    }

    private static async Task ScanRootAsync(
        IExecutionEnvironment env, EnvPath root, AgentSourceKind scope, Dictionary<string, AgentDefinition> output, List<string> order, List<AgentDiagnostic> diagnostics, CancellationToken ct)
    {
        IReadOnlyList<FsDirEntry> entries;
        try
        {
            if (!await env.Fs.ExistsAsync(root, ct).ConfigureAwait(false))
            {
                return; // 没有 agents 目录是常态,不是错误
            }

            entries = await env.Fs.ListAsync(root, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (!IsConnection(e))
        {
            diagnostics.Add(Diagnostic(root.Value, AgentDiagnosticKeys.DirectoryUnreadable, $"Could not read this directory: {e.Message}"));
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Info.IsDirectory || !entry.Name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue; // README.txt 之类的不算错,静静跳过
            }

            var display = root.Join(entry.Name).Value;
            var stem = entry.Name[..^".md".Length];
            // 文件名先过名字规则:名字会进 Task 的 description、会被模型原样当 subagent_type 传回来。
            if (!AgentLimits.IsValidName(stem))
            {
                diagnostics.Add(Diagnostic(display, AgentDiagnosticKeys.FileNameInvalid, "The file name is not a valid agent name; skipped.", ("file", entry.Name)));
                continue;
            }

            if (output.Count >= AgentLimits.MaxAgents && !output.ContainsKey(stem))
            {
                diagnostics.Add(Diagnostic(root.Value, AgentDiagnosticKeys.TooMany, $"More than {AgentLimits.MaxAgents} agents; the rest were not loaded.", ("max", AgentLimits.MaxAgents.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                return;
            }

            EnvPath file;
            try
            {
                // 挡软链逃逸:<agents>/evil.md -> /etc/passwd 之后,「加载子代理」就变成了「把任意文件的内容拼进系统提示词」。
                file = await env.Paths.ResolveWithinAsync(root, entry.Name, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PathEscape)
            {
                diagnostics.Add(Diagnostic(display, AgentDiagnosticKeys.OutsideRoot, "This file resolves to somewhere outside the agents directory; skipped."));
                continue;
            }
            catch (EnvironmentException e) when (!IsConnection(e))
            {
                diagnostics.Add(Diagnostic(display, AgentDiagnosticKeys.FileUnreadable, $"Could not read this file: {e.Message}"));
                continue;
            }

            var loaded = await LoadOneAsync(env, file, stem, scope, diagnostics, ct).ConfigureAwait(false);
            if (loaded is not null)
            {
                if (output.TryAdd(loaded.Name, loaded))
                {
                    order.Add(loaded.Name);
                }
                else
                {
                    output[loaded.Name] = loaded; // 同名:后进的(项目)覆盖先进的(全局 / 内建),位置不变
                }
            }
        }
    }

    private static async Task<AgentDefinition?> LoadOneAsync(
        IExecutionEnvironment env, EnvPath file, string fileName, AgentSourceKind scope, List<AgentDiagnostic> diagnostics, CancellationToken ct)
    {
        string raw;
        try
        {
            var bytes = await env.Fs.ReadHeadAsync(file, AgentLimits.FileMaxBytes, ct).ConfigureAwait(false);
            raw = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (EnvironmentException e) when (!IsConnection(e))
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.FileUnreadable, $"Could not read this file: {e.Message}"));
            return null;
        }

        var fm = FrontmatterParser.Parse(raw);
        foreach (var skipped in fm.Skipped)
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.Frontmatter, skipped));
        }

        // frontmatter 里的 name 胜过文件名,但不一致时要说一声——否则用户改完文件名会发现旧名字还在,而文件里明明写着新的。
        var declared = FrontmatterParser.String(fm, "name");
        var name = declared ?? fileName;
        if (declared is not null && declared != fileName)
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.NameMismatch, $"The name \"{declared}\" in the frontmatter does not match the file name \"{fileName}\"; the frontmatter name wins.", ("declared", declared), ("file", fileName)));
        }

        if (!AgentLimits.IsValidName(name))
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.NameInvalid, $"The name \"{name}\" is not valid; this agent was dropped.", ("name", name)));
            return null;
        }

        // 缺 description 就整条作废,而不是拿名字凑一个:描述逐字进 Task 工具的 description,是模型判断「这活该不该派给它」唯一的依据——
        // 没有描述,模型要么永远不派给它,要么见什么都派给它。
        var description = FrontmatterParser.String(fm, "description");
        if (description is null)
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.DescriptionMissing, "The frontmatter has no description, so the model cannot tell when to use this agent; it was dropped."));
            return null;
        }

        var prompt = AgentText.Clamp(AgentText.StripControlChars(fm.Body).Trim(), AgentLimits.PromptMax);
        if (prompt.Length == 0)
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.PromptEmpty, "The body (the role prompt) is empty; this agent was dropped."));
            return null;
        }

        var tools = ResolveTools(fm, file, diagnostics, out var toolsInvalid);
        if (toolsInvalid)
        {
            return null;
        }

        var mode = ResolvePermissionMode(fm, file, diagnostics, out var modeInvalid);
        if (modeInvalid)
        {
            return null;
        }

        var model = FrontmatterParser.String(fm, "model");
        // 只在写了 model 时才认 modelProviderId:单独一个供应商没有意义(别名都没定,钉住一家也选不出模型)。
        // 也不校验这家是否还在:加载器够不着路由器,校验在运行时那一侧,查不到就退回只按别名择优。
        var modelProviderId = model is null ? null : FrontmatterParser.String(fm, "modelProviderId");

        // thinking: 读不懂就当没写,不作废(它只影响花多少钱和想多深)。诊断里必须说清「照哪一档跑」。
        // inherit 不是合法的文件取值:文件里「跟随」表达为没有这一行。
        ThinkingLevel? thinking = null;
        if (FrontmatterParser.String(fm, "thinking")?.Trim().ToLowerInvariant() is { } rawThinking)
        {
            if (SubagentThinkingSelection.TryParseLevel(rawThinking, out var level))
            {
                thinking = level;
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    file.Value,
                    AgentDiagnosticKeys.ThinkingInvalid,
                    $"thinking \"{rawThinking}\" is not one of {string.Join(" / ", SubagentThinkingSelection.LevelNames)}; the line was ignored and the agent follows the default subagent thinking setting (or the parent's level).",
                    ("value", rawThinking)));
            }
        }

        // 颜色读不懂就当没写,不作废:纯装饰,为一条坏颜色让用户的子代理消失,代价完全不成比例。
        var rawColor = FrontmatterParser.String(fm, "color")?.Trim().ToLowerInvariant();
        var color = rawColor is not null && AgentColors.IsColor(rawColor) ? rawColor : null;

        return new AgentDefinition(
            name,
            AgentText.Clamp(AgentText.StripControlChars(description), AgentLimits.DescriptionMax),
            prompt,
            new AgentSource(scope, file.Value))
        {
            Tools = tools,
            Model = model is null ? null : AgentText.StripControlChars(model),
            ModelProviderId = modelProviderId is null ? null : AgentText.StripControlChars(modelProviderId),
            Thinking = thinking,
            PermissionMode = mode,
            Color = color,
        };
    }

    /// <summary>
    /// <c>tools:</c> → 归一化后的内部 id。三种结局,各自的理由都不一样:没写 → null(继承全部工具);<b>写了、但一个都认不出 → 整条作废</b>
    /// (放行的话,子代理会带着一张空工具表跑起来,然后自信地编一个答案交回去——宁可让它「不存在」,也不要让它「存在但是瞎的」);认出一部分 → 用那部分,认不出的记进诊断。
    /// </summary>
    private static IReadOnlyList<string>? ResolveTools(Frontmatter fm, EnvPath file, List<AgentDiagnostic> diagnostics, out bool invalid)
    {
        invalid = false;
        var raw = FrontmatterParser.List(fm, "tools");
        if (raw is null)
        {
            return null;
        }

        var (tools, unknown) = AgentToolNames.NormalizeList(raw);
        if (unknown.Count > 0)
        {
            diagnostics.Add(Diagnostic(file.Value, AgentDiagnosticKeys.ToolsUnknown, $"Unrecognised tool names in tools (ignored): {string.Join(", ", unknown)}.", ("names", string.Join(", ", unknown))));
        }

        if (tools.Count == 0)
        {
            diagnostics.Add(Diagnostic(
                file.Value,
                AgentDiagnosticKeys.ToolsNone,
                "None of the names in tools is recognised, so this agent was dropped: an agent with an empty tool list does not fail, it makes an answer up. Delete the tools line to let it inherit every tool."));
            invalid = true;
            return null;
        }

        return tools;
    }

    /// <summary>
    /// <c>permissionMode:</c> → 档位。★ 认不出的值<b>整条作废</b>,不是「忽略这个字段」。这是三个可选字段里唯一这么处理的,因为它是唯一一个<b>安全相关</b>的:
    /// 忽略掉一个写错的 <c>permissionMode: readonly</c>,子代理就按父代理的档位跑——那和作者的意图正好相反,而他不会知道。
    /// </summary>
    private static PermissionMode? ResolvePermissionMode(Frontmatter fm, EnvPath file, List<AgentDiagnostic> diagnostics, out bool invalid)
    {
        invalid = false;
        var raw = FrontmatterParser.String(fm, "permissionMode") ?? FrontmatterParser.String(fm, "permission-mode");
        if (raw is null)
        {
            return null;
        }

        if (PermissionModes.TryParse(raw.Trim().ToLowerInvariant(), out var mode))
        {
            return mode;
        }

        diagnostics.Add(Diagnostic(
            file.Value,
            AgentDiagnosticKeys.PermissionModeInvalid,
            $"permissionMode \"{raw}\" is not one of ask / auto / full; this agent was dropped. The field exists to narrow permissions, and ignoring an unreadable value would run the agent with the parent's mode, the opposite of what the author meant.",
            ("value", raw)));
        invalid = true;
        return null;
    }

    private static bool IsConnection(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration;

    private static AgentDiagnostic Diagnostic(string path, string key, string message, params (string Key, string Value)[] parameters) =>
        new(path, key, message, parameters.Length == 0 ? null : parameters.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal));
}
