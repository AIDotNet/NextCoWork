using Ncw.Agent.Hooks;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Mcp;
using Ncw.Agent.Plugins;
using Ncw.Agent.Skills;
using Ncw.Agent.Skills.Commands;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// 扩展域(M4):Skills / 命令 / Hooks / MCP 四个一方扩展在 Host 里的装配,以及 <c>extensions</c> 主题的修订号。
/// 由 <c>ConversationService</c> 在设备数据就绪之后造一份;各领域服务只知道注入的小接口,这里是唯一把它们接到设备事实(<see cref="ExtensionHostContext"/>)上的地方。
/// <list type="bullet">
/// <item><b>Hooks</b>:<c>PreToolSource</c> 交给权限扩展(<c>PermissionExtension(hooks: …)</c>),<c>HooksExtension</c>(Order 100)是每个工作区的会话扩展;<c>Changed</c> → <c>extensions</c> 主题的 <c>HooksRev</c>。</item>
/// <item><b>Skills / 命令</b>:<c>SkillsExtension</c>(Order 300)每个工作区一份;<c>Changed</c> → <c>SkillsRev</c> / <c>CommandsRev</c>。</item>
/// <item><b>MCP</b>:工具经 <c>ToolRegistry</c>(来源 <c>mcp:&lt;id&gt;</c>)进快照;状态是 <c>mcp</c> 主题(<see cref="McpHost"/> 自己发布)。</item>
/// <item><b>插件</b>(<see cref="Plugins"/>,Jaunt 宿主):工具经 <c>ToolRegistry</c>(来源 <c>plugin:&lt;id&gt;</c>)、Skill 经 <c>ISkillPluginSource</c>(接给 Skills 服务)、拦截器经 <c>PermissionExtension(interceptors: …)</c>、
/// 上下文经会话扩展(<c>plugins</c>,Order 700);状态是 <c>plugins</c> 主题(全量 + 单调 Rev,<c>AgentHost</c> 发布)。</item>
/// </list>
/// </summary>
internal sealed class ExtensionDomain : IAsyncDisposable
{
    private ExtensionDomain(SkillsService skills, CommandsService commands, HooksService hooks, McpService mcp, PluginManager plugins, ExtensionRevisions revisions, ExtensionHostContext context)
    {
        Skills = skills;
        Commands = commands;
        Hooks = hooks;
        Mcp = mcp;
        Plugins = plugins;
        Revisions = revisions;
        Context = context;
    }

    public SkillsService Skills { get; }

    public CommandsService Commands { get; }

    public HooksService Hooks { get; }

    public McpService Mcp { get; }

    /// <summary>插件系统(安装 / 启停 / 授权 / 配置 / 市场,以及每个插件一个 Jaunt 运行时)。</summary>
    public PluginManager Plugins { get; }

    public ExtensionRevisions Revisions { get; }

    public ExtensionHostContext Context { get; }

    /// <param name="registeredCommands">一方扩展经 <c>RegisterCommand</c> 贡献的命令(从探测用的扩展宿主收集;延迟取,避免循环依赖)。</param>
    public static ExtensionDomain Create(
        ExtensionHostContext context, ToolRegistry tools, Func<IReadOnlyList<CommandDefinition>> registeredCommands, IHookEvaluator? hookEvaluator = null)
    {
        var services = context.Services;
        var revisions = new ExtensionRevisions(context.Topics);
        // 插件先于 Skills 造:Skills 服务的「插件层」就是插件管理器(ISkillPluginSource,每次扫描都重新问)
        var sessions = new Ncw.Agent.Storage.Sessions.SessionRepository(context.Database, context.Clock);
        var plugins = Ncw.Agent.Host.Plugins.PluginsHost.Create(context, tools, sessionId => sessions.Get(sessionId)?.WorkspaceId);
        var skills = SkillsHost.CreateSkills(context, services.SkillMarket, plugins.SkillSource);
        var commands = SkillsHost.CreateCommands(context, registeredCommands);
        var hooks = HooksHost.Create(context, hookEvaluator ?? services.HookEvaluator);
        var mcp = McpHost.Create(context, tools, new McpHostOptions { Http = services.McpHttp, Options = services.Mcp, Delayer = services.McpDelayer });
        skills.Changed += revisions.BumpSkills;
        commands.Changed += revisions.BumpCommands;
        hooks.Changed += revisions.BumpHooks;
        return new ExtensionDomain(skills, commands, hooks, mcp, plugins, revisions, context);
    }

    /// <summary>一个工作区的会话扩展(Hooks Order 100、Skills Order 300、插件 Order 700;会话层按 Order 排序)。</summary>
    public IReadOnlyList<IAgentExtension> SessionExtensions(string workspaceId) =>
        [Hooks.CreateExtension(workspaceId), Skills.CreateExtension(workspaceId), Plugins.CreateExtension()];

    /// <summary>数据就绪之后启动:内置 Skill 铺到全局目录(失败只记日志),已启用的 MCP 服务器在后台连接(不阻塞)。</summary>
    public void Start()
    {
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            if (Context.Services.BundledSkillsRoot is { } root)
            {
                await SkillsHost.InstallBundledAsync(Context, root).ConfigureAwait(false);
            }

            await Mcp.StartAsync().ConfigureAwait(false);
            // 已启用的插件在后台并行激活(每个有自己的超时,失败是状态不是异常):不阻塞设备就绪
            await Plugins.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Context.Log.Warn("Starting the extension domain failed.", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Plugins.DisposeAsync().ConfigureAwait(false);
        await Mcp.DisposeAsync().ConfigureAwait(false);
        await Hooks.DisposeAsync().ConfigureAwait(false);
    }
}
