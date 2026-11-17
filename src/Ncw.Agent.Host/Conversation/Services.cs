using Ncw.Agent.Catalog;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 模型解析的接缝(定义在 Host,由供应商 / 模型目录服务实现并注入)。对话链路只经它拿「调模型的客户端」和「别名 → 绑定(窗口、输出上限)」,
/// 不关心供应商配置、凭据、models.dev 怎么管——那是 <c>providers.*</c> / <c>catalog.*</c> 的事。
/// 上游 HTTP 在实现里经 <c>IUpstreamHttp</c>(Llm 层)注入,所以测试里换成假上游即可,不碰网络。
/// </summary>
public interface IModelResolver
{
    /// <summary>流式调用模型的客户端(通常是 <see cref="LlmRouter"/>)。<b>流返回后永不抛异常</b>:错误变成 <see cref="StreamError"/> 事件。</summary>
    ILlmClient Client { get; }

    /// <summary>别名 → 绑定(窗口、最大输出)。查不到返回 null:会话层按兜底窗口照常组装,由 <see cref="Client"/> 去报权威错误。</summary>
    ModelBinding? Resolve(ModelRef model);
}

/// <summary>
/// 默认实现:没有任何供应商可用。每次调用都以 <see cref="AgentErrorCode.NoHealthyProvider"/> 结束(Run 以错误终态收尾,控制端按 <see cref="UnavailableModelResolver.MessageKey"/> 翻译),
/// 而不是抛异常或挂起。现在只剩常量 <see cref="MessageKey"/> 和测试替身的用途:真实的模型解析是 <c>ProviderRuntime</c>(没有供应商时路由给出同一个错误 key)。
/// </summary>
public sealed class UnavailableModelResolver : IModelResolver
{
    public const string MessageKey = "llm.noEnabledProvider";

    public ILlmClient Client { get; } = new UnavailableClient();

    public ModelBinding? Resolve(ModelRef model) => null;

    private sealed class UnavailableClient : ILlmClient
    {
        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            ModelRef model, LlmRequest request, RequestContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new StreamError(AgentError.Create(
                AgentErrorCode.NoHealthyProvider, "No model provider is configured on this device.", retryable: false, messageKey: MessageKey));
        }
    }
}

/// <summary>
/// 项目的执行环境(AGENTS.md §5:工具只经 <see cref="IExecutionEnvironment"/> 碰设备本地)。本机项目用本机环境;
/// <b>SSH 环境的接线留在这里</b>:注册表(<c>EnvironmentRegistry</c>)按项目登记的连接 id 给出连接好的 <c>SshEnvironment</c>,实现这个接口即可,对话链路不用改。
/// </summary>
public interface IProjectEnvironments
{
    /// <summary>项目在哪个环境里。环境暂时不可用时抛 <see cref="Ncw.Protocol.Wire.ProtocolException"/>(<c>unavailable</c>,可重试)。</summary>
    IExecutionEnvironment Resolve(WorkspaceRecord project);

    /// <summary>
    /// 确保项目的环境已连接再返回它(SSH 环境惰性连接:首次指纹确认、凭据提问以交互出现,等用户回答)。默认实现 = <see cref="Resolve"/>(没有「连接」这回事的实现,例如只有本机)。
    /// 真实实现在 <c>EnvironmentService</c>;需要异步等连接的入口(<c>runs.start</c>、文件 / git / 终端的处理器、扩展域)走它,同步的 <see cref="Resolve"/> 只给已经确保过的路径用。
    /// </summary>
    ValueTask<IExecutionEnvironment> ResolveReadyAsync(WorkspaceRecord project, CancellationToken ct) => ValueTask.FromResult(Resolve(project));
}

/// <summary>只有设备本机环境。SSH 项目返回 <c>unsupported</c>(接线前的明确失败,不悄悄当成本机目录用)。</summary>
public sealed class LocalProjectEnvironments(string deviceId) : IProjectEnvironments, IAsyncDisposable
{
    // 本机 PTY 由 Ncw.Agent.Terminal 注入(06 §9.4 第 1 点):终端、工具、审查共用这一个环境实例。
    private readonly LocalEnvironment _local = new(deviceId, new LocalEnvironmentOptions { TerminalFactory = Ncw.Agent.Terminal.LocalTerminals.CreateFactory() });

    public IExecutionEnvironment Resolve(WorkspaceRecord project) =>
        project.EnvironmentKind == "connection"
            ? throw new Ncw.Protocol.Wire.ProtocolException(Ncw.Protocol.Wire.ErrorCodes.Unsupported, "SSH project environments are not connected on this device yet.")
            : _local;

    public ValueTask DisposeAsync() => _local.DisposeAsync();
}

/// <summary>
/// 装配 <see cref="AgentHost"/> 时可注入的服务(都有默认值;测试和供应商服务按需替换)。
/// </summary>
public sealed class AgentServices
{
    /// <summary>
    /// 模型解析。<b>默认(null)= 真实实现</b>:数据就绪后由模型目录与供应商服务装配(<c>providers.json</c>、凭据、账号池、<c>LlmRouter</c>,见 <c>ProviderRuntime</c>);
    /// 没配过供应商时 Run 以 <see cref="UnavailableModelResolver.MessageKey"/> 的错误结束,而不是挂起。测试可以注入假的。
    /// </summary>
    public IModelResolver? Models { get; init; }

    /// <summary>上游 HTTP(目录同步、模型列表、OAuth 刷新、模型请求共用)。默认:不跟随重定向、自动解压的 <c>HttpClient</c>。测试注入脚本化的假上游。</summary>
    public IUpstreamHttp? Http { get; init; }

    /// <summary>目录同步的参数(启动后第一次拉取的等待、间隔、超时、体积上限……)。默认见 <see cref="ModelCatalogOptions"/>。</summary>
    public ModelCatalogOptions? Catalog { get; init; }

    /// <summary>目录同步定时器的睡眠。默认按时钟睡真的时间;和 <see cref="Delayer"/>(Run 重试退避)分开:后者在测试里常常是「不睡」,共用会让同步循环空转。</summary>
    public IDelayer? CatalogDelayer { get; init; }

    /// <summary>目录缓存的存放处。默认:数据根下的 <c>catalog/</c>。</summary>
    public ICatalogStore? CatalogStore { get; init; }

    /// <summary>供应商服务的参数(拉模型列表的超时等)。</summary>
    public ProviderServiceOptions? ProviderOptions { get; init; }

    /// <summary>路由的参数(退避基数、空闲超时……)。</summary>
    public LlmRouterOptions? Router { get; init; }

    /// <summary>「这家走不走账号登录」的预设表。默认:内置的几家。</summary>
    public IProviderPresets? Presets { get; init; }

    /// <summary>各家 OAuth 的刷新规格(凭据刷新器用)。默认:空(各家 issuer 随 <c>oauth/issuers/*</c> 移植)。</summary>
    public IOAuthRegistry? OAuth { get; init; }

    /// <summary>每家供应商的登录实现(按 issuer 找)。默认:没有(<c>providers.oauth.*</c> 返回 <c>unsupported</c>)。</summary>
    public Func<string, IOAuthLogin?>? OAuthLogins { get; init; }

    /// <summary>供应商相关的传输装饰(额外的头、签名……)。默认:无。</summary>
    public ITransportDecorator? Transport { get; init; }

    /// <summary>项目的执行环境。默认(null)= 真实实现 <c>EnvironmentService</c>(本机 + 设备上配置的 SSH 环境);测试可以注入假的(例如「声称自己是 SSH」的包装环境)。</summary>
    public IProjectEnvironments? Environments { get; init; }

    /// <summary>SSH 环境的参数(重连退避、空闲回收时间、<c>~/.ssh</c> 的位置、是否自动发现系统 ssh-agent)。默认见 <see cref="Ncw.Agent.Host.Environments.EnvironmentHostOptions"/>。</summary>
    public Ncw.Agent.Host.Environments.EnvironmentHostOptions? SshEnvironments { get; init; }

    /// <summary>
    /// 「AI 写提交信息」的模型端口(<c>git.generateMessage</c>)。默认:用设置里的默认模型经模型路由流式调一次(<c>HostCommitMessageModel</c>,用量走路由单独记)。测试注入假的,不碰网络。
    /// 参数是发起请求的项目 id(用量按项目记)。
    /// </summary>
    public Func<string, Ncw.Agent.Workspace.Git.ICommitMessageModel>? CommitMessages { get; init; }

    /// <summary>
    /// 会话标题自动生成(第一条消息发出时起一次旁路请求)。null = 跟着模型来源:用真实的供应商运行时(<see cref="Models"/> 为空)就开;注入了假的模型解析(测试)就关——
    /// 否则每个会话多出的那次旁路请求会吃掉脚本化上游的响应。测试要验标题时显式给 true。
    /// </summary>
    public bool? SessionTitles { get; init; }

    /// <summary>终端管理器的参数(限额、环形缓冲、意图有效期……)。默认见 <see cref="Ncw.Agent.Terminal.TerminalManagerOptions"/>(<c>Time</c> 用 Host 的时钟)。测试用它缩短意图有效期、调小限额。</summary>
    public Ncw.Agent.Terminal.TerminalManagerOptions? Terminals { get; init; }

    public IIdGenerator? Ids { get; init; }

    /// <summary>退避睡眠(测试里不必真的睡)。</summary>
    public IDelayer? Delayer { get; init; }

    public AgentLoopOptions? Loop { get; init; }

    public RunRegistryOptions? Registry { get; init; }

    /// <summary>「永久」作用域的权限规则存放处。默认:数据库 kv 表里的持久实现(<c>StoredGlobalRules</c>)。</summary>
    public IPermissionRuleStore? GlobalRules { get; init; }

    public PermissionOptions? Permissions { get; init; }

    /// <summary>Skill 市场目录的 HTTP 抓取。默认:真实的 <c>HttpSkillMarketClient</c>(测试注入假的)。</summary>
    public Ncw.Agent.Skills.Install.ISkillMarketClient? SkillMarket { get; init; }

    /// <summary>随应用发布的内置 Skill 目录(<c>resources/skills</c>);null = 没有,不安装内置 Skill。</summary>
    public string? BundledSkillsRoot { get; init; }

    /// <summary>prompt 型 Hook 的判定器(辅助模型调用)。默认:没有(prompt 型 Hook 跳过 = 放行)。</summary>
    public Ncw.Agent.Hooks.IHookEvaluator? HookEvaluator { get; init; }

    /// <summary>MCP 的 HTTP 传输工厂(测试注入假的)。默认:真实的 <c>HttpClient</c>。</summary>
    public Ncw.Agent.Mcp.IMcpHttpFactory? McpHttp { get; init; }

    /// <summary>MCP 的连接参数(超时、重连退避、体积上限……)。</summary>
    public Ncw.Agent.Mcp.McpOptions? Mcp { get; init; }

    /// <summary>MCP 重连退避的睡眠。和 <see cref="Delayer"/>(Run 重试退避)分开:后者在测试里常常是「不睡」,共用会让重连循环空转。</summary>
    public IDelayer? McpDelayer { get; init; }

    /// <summary>插件市场目录与下载。默认:真实的 <c>HttpPluginMarketClient</c>(测试注入假的)。</summary>
    public Ncw.Agent.Plugins.IPluginMarketClient? PluginMarket { get; init; }

    /// <summary>插件的 <c>net.fetch</c> 出口(一次一跳,不跟随重定向)。默认:真实的 <c>HttpClient</c>(测试注入假的)。</summary>
    public Ncw.Agent.Plugins.IPluginHttp? PluginHttp { get; init; }

    /// <summary>插件运行时的预算与限额(超时、指令数、内存、存储配额……)。默认见 <see cref="Ncw.Agent.Plugins.PluginRuntimeLimits"/>;测试用小值验证「超预算被终止、不影响宿主」。</summary>
    public Ncw.Agent.Plugins.PluginRuntimeLimits? PluginLimits { get; init; }

    /// <summary>
    /// 联网工具(WebFetch / web_search / 搜索服务测试)的 HTTP 出口,一次一跳、不跟随重定向。默认:<c>GuardedHttp</c>(解析 → 校验 → 钉死 IP,拒绝环回 / 内网 / 链路本地 / 元数据地址)。
    /// 测试注入假的(它绕过连接时的地址校验,所以 URL 字面量筛查仍然生效,连接时的 IP 校验由 <c>GuardedHttp</c> 自己的单测覆盖)。
    /// </summary>
    public Ncw.Agent.WebTools.Net.IWebHttp? WebHttp { get; init; }

    /// <summary>联网出口的策略(<c>AllowPrivateAddresses</c> 只给测试用,生产恒为 false)。</summary>
    public Ncw.Agent.WebTools.Net.WebPolicy? WebPolicy { get; init; }

    /// <summary>浏览器工具的启动器。默认:经本机执行环境起设备上已有的 Chrome / Edge / Chromium(<c>ProcessBrowserLauncher</c>);测试注入假的(指向假 CDP 服务 / 模拟「没装浏览器」)。</summary>
    public Ncw.Agent.Browser.Launch.IBrowserLauncher? BrowserLauncher { get; init; }

    /// <summary>WebFetch 的上限(超时 / 重定向跳数 / 字节数 / 字符数)。默认见 <c>WebFetchLimits</c>;测试用来缩小。</summary>
    public Ncw.Agent.WebTools.Fetch.WebFetchLimits? WebFetch { get; init; }

    /// <summary>搜索的超时(每家 / 测试)。默认见 <c>SearchOptions</c>。</summary>
    public Ncw.Agent.WebTools.Search.SearchOptions? WebSearch { get; init; }

    /// <summary>诊断输出。默认:进程内环形缓冲(<see cref="HostDiagnostics"/>)。</summary>
    public ILoopDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// 账户委托令牌的持有者(<c>account.attachDelegatedToken</c> 交付,只在内存里)。默认(null)= Host 自己造一个。模型通道、配置同步、技能 / 插件市场都从它取令牌;
    /// 测试可以注入一个,直接看令牌是否被用到。
    /// </summary>
    public DelegatedAccount? Account { get; init; }

    /// <summary>技能 / 插件市场的 <see cref="HttpClient"/>(目录、授权、下载共用)。默认:进程内一份真实的(跟随少量重定向、自动解压)。测试注入假的 <see cref="HttpMessageHandler"/>,不碰网络,并核对 <c>Authorization</c>。</summary>
    public HttpClient? MarketHttpClient { get; init; }

    private DelegatedAccount? _resolvedAccount;

    /// <summary>实际生效的委托账户(注入的,或 Host 造的那一份;同一个 <see cref="AgentServices"/> 上永远是同一个实例)。</summary>
    internal DelegatedAccount ResolveAccount(TimeProvider clock) => _resolvedAccount ??= Account ?? new DelegatedAccount(clock);
}

/// <summary>进程内的诊断缓冲(最近 256 条)。Agent 的 stdout 用来报告 ready,不能乱写;<c>diagnostics.logs</c> 落地后从这里读。</summary>
public sealed class HostDiagnostics : ILoopDiagnostics
{
    private readonly Queue<string> _lines = new();

    public void Warn(string message, Exception? error = null)
    {
        lock (_lines)
        {
            _lines.Enqueue(error is null ? message : $"{message} ({error.GetType().Name}: {error.Message})");
            while (_lines.Count > 256)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }
}
