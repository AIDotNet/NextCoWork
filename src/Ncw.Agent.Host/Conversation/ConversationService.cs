using Ncw.Agent.Catalog;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Extensions;
using Ncw.Agent.Hooks;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 对话链路的装配(设备数据就绪后创建一份):会话仓库、Run 注册表、权限扩展、交互闸门、内置工具、排队消息。
/// 这是 Host 里唯一把它们和协议接起来的地方:
/// <list type="bullet">
/// <item><c>InteractionGate.RunEvent</c> → <see cref="RunRegistry.Post"/>(交互请求 / 结果并进 Run 的事件流);<c>Changed</c> → <c>NotifyInteractionsChanged</c> + <c>interactions</c> 主题。</item>
/// <item><see cref="ConversationRunExtension"/>(<c>IRunExtension</c>):Run 创建时 <c>OpenRun</c>、登记 <c>PermissionRunState</c>、造 <c>ToolRuntime</c>;Run 结束时 <c>End</c>。</item>
/// <item><c>ActiveRunsChanged</c> → <c>device</c> 主题的 <c>activeRuns.changed</c>(首帧与广播同一个类型,同一个生成函数);<c>QueueChanged</c> → <c>session/&lt;id&gt;</c> 的 <c>queue.updated</c>。</item>
/// <item><see cref="HostRunRequestFactory"/>(<c>IRunRequestFactory</c>)、<see cref="TappingDriver"/>(把 <c>message_end</c> 变成 <c>message.committed</c>)、会话删除时 <c>SessionDirectory.Remove</c>。</item>
/// <item>子代理(<see cref="SubagentService"/>,先于注册表造、造完 <c>Bind</c>;每个工作区的会话扩展里一个 <c>SubagentExtension</c>):<c>subagent_*</c> 事件是父 Run 日志里的普通事件(<see cref="Mapping"/>),
/// <c>ReportChanged</c> → <c>session/&lt;id&gt;</c> 的 <c>subagent.report</c>。</item>
/// <item>计划 / 待办 / 目标(<see cref="PlanningKit"/>):<c>kit.Extensions</c> 进会话扩展、<c>kit.RunExtensions</c> 排在 Host 的 Run 扩展之后、造完注册表 <c>Bind</c>;<c>Goals.Changed</c> → <c>goal</c> 主题。</item>
/// </list>
/// </summary>
internal sealed class ConversationService : IAsyncDisposable
{
    /// <summary><c>session/&lt;id&gt;</c> 快照里带的最近消息条数。</summary>
    public const int SnapshotPageSize = 50;

    private readonly TopicHub _topics;
    private readonly Func<long> _projectsRevision;
    private readonly IProjectEnvironments _environments;

    private readonly IModelResolver _models;

    private readonly Lock _goalGate = new();
    private long _goalRev;
    private readonly LocalEnvironment _localEnvironment;

    public ConversationService(
        AgentDatabase database, string deviceId, string dataRoot, TimeProvider clock, AgentServices services, TopicHub topics, Func<long> projectsRevision, IModelResolver models,
        ILoopDiagnostics log,
        Ncw.Agent.Services.Settings.SettingsService? deviceSettings = null)
    {
        _models = models;
        _topics = topics;
        _projectsRevision = projectsRevision;
        DeviceId = deviceId;
        Services = services;
        Log = log;
        var ids = services.Ids ?? new UlidIdGenerator(clock);

        // 附件登记挂在会话仓库的写入旁路上:消息落盘的同一个事务里把引用到的草稿附件升为 committed(2.x 的 recordMessageAttachments)。
        var attachmentRepository = new AttachmentRepository(database, clock);
        Sessions = new SessionRepository(database, clock, attachmentRepository);
        Attachments = new Ncw.Agent.Services.Attachments.AttachmentService(dataRoot, clock, attachmentRepository, Sessions);
        // 挂起的交互落库(只记元数据);启动时把上次进程遗留的 pending 行收尾为 cancelled。
        Gate = new InteractionGate(ids, clock, new StoredInteractionJournal(new InteractionJournalStore(database, deviceId), clock), Log);
        Workspaces = new WorkspaceRepository(database, clock);
        // 执行环境(本机 + SSH):SSH 连接的指纹确认 / 凭据提问经上面的交互闸门;测试可以用 services.Environments 换成假的(只换「项目 → 环境」的解析,环境清单 / 配置协议照常)。
        Environments = new Ncw.Agent.Host.Environments.EnvironmentService(database, deviceId, dataRoot, clock, services, topics, Gate, ids, Log, Workspaces);
        _environments = services.Environments ?? Environments;
        var settingsRepository = new SettingsRepository(database);
        // 设备设置(设置页的值):读写只有这一份服务(AgentHost 装配时建好传进来,协议方法和这里读的是同一个)。每个 Run / 每次派发现读。
        DeviceSettings = deviceSettings ?? new Ncw.Agent.Services.Settings.SettingsService(settingsRepository);
        _localEnvironment = new LocalEnvironment(deviceId);
        Settings = new StoredSessionSettings(DeviceSettings, () => new GlobalInstructions(_localEnvironment, EnvPath.Parse(dataRoot, _localEnvironment.Facts.PathStyle)));
        PermissionRuns = new PermissionRuns();
        Rules = PermissionRuleBook.CreateDefault(services.GlobalRules ?? new StoredGlobalRules(new KvStore(database)));
        Tools = BuiltinTools.Create();
        // 统一的工具注册表:内置先注册(任何来源都替换不了它们),MCP / 插件的工具按来源注册 / 注销;每回合由会话层取一次快照。
        ToolRegistry = new ToolRegistry(Tools);
        // 需要运行时依赖的内置工具(来源 builtin、可被禁用):AskUserQuestion(经交互闸门提问,子代理里不提供)、WebFetch / web_search(联网;受「联网搜索」开关管,见 NetworkSwitchExtension)。
        Web = new Ncw.Agent.Host.Services.WebToolsHost(database, dataRoot, services);
        ToolRegistry.Register(new AskUserQuestionTool(PermissionRuns), ToolRegistry.BuiltinSource);
        ToolRegistry.Register(Web.FetchTool, ToolRegistry.BuiltinSource);
        ToolRegistry.Register(Web.SearchTool, ToolRegistry.BuiltinSource);
        // 内联可视化(visualize_*,来源 builtin、可被禁用):载荷服务端消毒;交互模式(widget_submit)与动作在子代理里不提供;edit / update 按转录找卡。
        foreach (var tool in Ncw.Agent.Widgets.VisualizeTools.Create(PermissionRuns, new HostWidgetTranscript(Sessions)))
        {
            ToolRegistry.Register(tool, ToolRegistry.BuiltinSource);
        }

        // 浏览器工具(browser_*,来源 builtin、可被禁用):驱动设备上已有的 Chrome / Edge / Chromium,进程经本机执行环境启动,profile 在 <数据根>/browser/<项目 id>,找不到浏览器给结构化错误(不下载)。
        Browser = new Ncw.Agent.Browser.BrowserService(new Ncw.Agent.Browser.BrowserOptions
        {
            ProfileRoot = EnvPath.Parse(dataRoot, _localEnvironment.Facts.PathStyle).Join("browser"),
            GetEnvironmentVariable = static name => System.Environment.GetEnvironmentVariable(name),
            Clock = clock,
            Launcher = services.BrowserLauncher,
        });
        foreach (var tool in Ncw.Agent.Browser.BrowserTools.Create(Browser))
        {
            ToolRegistry.Register(tool, ToolRegistry.BuiltinSource);
        }

        Shells = new BackgroundShells(clock);
        // 后台 Shell 不是租约持有者(它们比 Run 活得久):还有在跑的,它所在的 SSH 环境就不能被空闲回收
        Environments.AddBusyProbe(id => Shells.List().Any(shell => shell.Status == BackgroundShellStatus.Running && shell.Environment.Environment == id));
        // 扩展域(Skills / 命令 / Hooks / MCP):设备事实一份,PreToolUse Hook 作为权限链里的接入口交给权限扩展(它们不是 before_tool 上的并列处理器)。
        RunRegistry? registryForHooks = null;
        var hookEvaluator = services.HookEvaluator ?? new LlmHookEvaluator(new LlmHookEvaluatorOptions
        {
            Llm = models.Client,
            ResolveModel = models.Resolve,
            ReadMessages = sessionId => Sessions.History(sessionId).Select(MessageCodec.FromRecord).ToList(),
            ActiveModel = (sessionId, runId) =>
                registryForHooks?.Get(new RunId(runId))?.Model
                ?? (Sessions.Get(sessionId) is { Model.Length: > 0 } s ? new ModelSelection(s.Model, s.ModelProviderId).ToRef() : null),
            Clock = clock,
        });
        Domain = ExtensionDomain.Create(new ExtensionHostContext
        {
            Database = database,
            Kv = new KvStore(database),
            Credentials = new Ncw.Agent.Storage.Credentials.CredentialStore(database, dataRoot, Ncw.Agent.Storage.Credentials.NoLegacySecrets.Instance),
            DataRoot = dataRoot,
            DeviceId = deviceId,
            Clock = clock,
            Log = Log,
            Services = services,
            LocalEnvironment = _localEnvironment,
            Topics = topics,
            Project = ProjectTargetOf,
            // 没人在场的后台路径(MCP 服务器启动)连项目的 SSH 环境:非交互,已钉扎 + 已保存凭据能连上就连
            EnsureProjectReady = async (workspaceId, ct) =>
            {
                if (Workspaces.Get(workspaceId) is { EnvironmentKind: "connection" } project)
                {
                    await Environments.EnsureProjectReadyAsync(project, Ncw.Agent.Host.Environments.ConnectMode.NonInteractive, ct).ConfigureAwait(false);
                }
            },
        }, ToolRegistry, () => Probe().RegisteredCommands, hookEvaluator);
        var permissions = new PermissionExtension(
            PermissionRuns, Rules, reviewer: new LlmPermissionReviewer(models.Client, Log), hooks: [Domain.Hooks.PreToolSource], interceptors: [Domain.Plugins.Interceptor], options: services.Permissions);
        Domain.Hooks.AttachNotifications(Gate, PermissionRuns);
        Permissions = permissions;
        Directory = new SessionDirectory((sessionId, workspaceId) => CreateSession(sessionId, workspaceId, permissions, ids, clock));

        var extension = new ConversationRunExtension(this);
        var requests = new HostRunRequestFactory(Settings);

        // 子代理:先于注册表造(注册表构造时就要拿到扩展列表),造完注册表再 Bind。和顶层 Run 用同一个请求工厂:设置快照、模式提示词只有一份来源。
        Agents = new AgentCatalog(new HostAgentScanSource(this, dataRoot), new KvAgentSwitchStore(new KvStore(database)), Log);
        Subagents = new SubagentService(new SubagentServiceDeps
        {
            Sessions = Sessions,
            Requests = requests,
            Agents = Agents,
            Settings = new StoredSubagentSettings(DeviceSettings),
            Models = new ResolverSubagentModels(models),
            PermissionRuns = PermissionRuns,
            Gate = Gate,
            Clock = clock,
            Log = Log,
            ResolveProject = workspaceId =>
            {
                if (Workspaces.Get(workspaceId) is not { } project)
                {
                    return null;
                }

                var env = EnvironmentOf(project);
                return (env, EnvPath.Parse(project.RootPath, env.Facts.PathStyle));
            },
        });

        // 计划 / 待办 / 目标:同样先造、后 Bind。判定模型与「模型自提目标」策略每个 Run 现读;「这个 Run 开着计划工作流」由 Host 按会话模式回答。
        var goalSettings = new StoredGoalSettings(DeviceSettings);
        Planning = PlanningKit.Create(new PlanningKitOptions
        {
            Llm = models.Client,
            Sessions = Sessions,
            Gate = Gate,
            PermissionRuns = PermissionRuns,
            Ids = ids,
            Clock = clock,
            ResolveModel = models.Resolve,
            SessionTokens = sessionId => Sessions.RunUsage(sessionId).Values.Sum(static u => u.InputTokens + u.OutputTokens),
            PlanWorkflow = ctx => IsPlanWorkflow(ctx.RunId, ctx.SessionId),
            EvaluatorModel = goalSettings.EvaluatorModel,
            ProposalPolicy = goalSettings.ProposalPolicy,
            CheckinIntervalSource = goalSettings.CheckinInterval,
            // 定时任务起的会话不是交互式的(没有人在看):目标工具不对它提案、不弹审批(2.x 的 origin = scheduled)。
            Interactive = sessionId => Sessions.Get(sessionId)?.Origin != "scheduled",
            Log = Log,
        });

        Registry = new RunRegistry(new RunRegistryDeps
        {
            DeviceId = deviceId,
            Sessions = Sessions,
            Driver = new TappingDriver(new SessionRunDriver(Directory), this),
            Requests = requests,
            Queue = new SessionInputQueueStore(new SessionInputStore(database)),
            SessionDirectory = Directory,
            Ids = ids,
            Clock = clock,
            Log = Log,
            Options = services.Registry ?? new RunRegistryOptions(),
            // 顺序:子代理(登记子 Run 的权限与交互作用域)→ Host(造 ToolRuntime / 顶层 Run 的权限状态)→ 计划 / 目标的 Run 扩展(它们同步围栏,必须排在造 ToolRuntime 的扩展之后)
            Extensions = [Subagents, extension, .. Planning.RunExtensions],
            Interactions = new GateView(Gate),
            Shells = Shells,
        });
        registryForHooks = Registry;
        // 会话标题:第一条消息发出时起名(预览立即写、模型回答迟到再换)。注入了假模型解析时默认关,见 AgentServices.SessionTitles。
        if (services.SessionTitles ?? (services.Models is null))
        {
            Titles = new SessionTitleGenerator(Sessions, _models, clock, session => PublishSession(session.Id), Log);
        }

        Subagents.Bind(Registry);
        Planning.Bind(Registry);
        Subagents.ReportChanged += PublishSubagentReport;
        Subagents.Stopped += PublishSubagentStopped;
        Planning.Goals.Changed += PublishGoalChange;

        // 交互闸门 → 事件流 / 全局角标 / interactions 主题
        Gate.RunEvent += (runId, ev) => Registry.Post(runId, ev);
        Gate.Changed += snapshot =>
        {
            Registry.NotifyInteractionsChanged();
            _topics.Publish(ConversationMethods.InteractionsTopic.Name, InteractionEvents.Changed, Mapping.ToDto(snapshot), PayloadShapes.GetShape<InteractionsDto>());
        };

        // 权威广播:activeRuns 全量(Core 的唯一生成函数);队列全量
        Registry.ActiveRunsChanged += index =>
            _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ActiveRunsChanged, Mapping.ToDeviceState(index, _projectsRevision()), PayloadShapes.GetShape<DeviceTopicState>());
        Registry.QueueChanged += update =>
        {
            var topic = ConversationMethods.SessionTopicName(update.SessionId);
            if (_topics.HasSubscribers(topic))
            {
                _topics.Publish(topic, SessionEvents.QueueUpdated, Mapping.ToDto(update), PayloadShapes.GetShape<QueueDto>());
            }
        };
    }

    /// <summary>
    /// 一个 Run 收尾时工具层采集到的文件改动(<c>run_end</c> 进日志之后同步取走)。聚合到<b>顶层 Run</b>:子代理 Run 的改动并进父 Run(<see cref="RunChanges.RootRunId"/>),
    /// <see cref="RunChanges.Run"/> 才是真正写它的 Run。改动审查(<c>review.*</c>)订阅它落库;没有人订阅时改动被丢弃(采集器读即清)。
    /// </summary>
    public event Action<RunChanges>? RunChangesCollected;

    internal void RaiseRunChanges(RunChanges changes) => RunChangesCollected?.Invoke(changes);

    public string DeviceId { get; }

    /// <summary>
    /// Run 发起前的闸门(云端:配额 / 收尾中)。本机模式为 null,行为不变。<c>runs.start</c>、定时任务、排队 / 插话都经它;
    /// 子代理的 Run 不算「发起」(它们是父 Run 的一部分)。
    /// </summary>
    internal Ncw.Agent.Host.Cloud.IRunGate? RunGate { get; set; }

    /// <summary>模型解析(别名 → 绑定):定时任务 / 开放网关这类设备内发起的 Run 用它在发起之前验证模型。</summary>
    public IModelResolver Models => _models;

    /// <summary>会话标题生成器;关闭时(<see cref="AgentServices.SessionTitles"/>)为 null。</summary>
    internal SessionTitleGenerator? Titles { get; }

    public AgentServices Services { get; }

    public ILoopDiagnostics Log { get; }

    public SessionRepository Sessions { get; }

    /// <summary>附件(分块上传 / 去重 / 草稿 / 按引用读 / 复制分支时的图片搬家 / 删会话回收文件)。</summary>
    public Ncw.Agent.Services.Attachments.AttachmentService Attachments { get; }

    public WorkspaceRepository Workspaces { get; }

    public InteractionGate Gate { get; }

    /// <summary>设置页的值(Run 开始时现读成 <see cref="SessionSettings"/> 快照)。</summary>
    internal StoredSessionSettings Settings { get; }

    /// <summary>设备设置的读写服务(<c>settings.*</c> 方法与 Run 读的是同一份)。</summary>
    public Ncw.Agent.Services.Settings.SettingsService DeviceSettings { get; }

    public PermissionRuns PermissionRuns { get; }

    /// <summary>权限扩展(Run 里的 <c>before_tool</c> 与 <c>widgets.runAction</c> 的预检是同一个实例、同一张表)。</summary>
    internal PermissionExtension Permissions { get; }

    private WidgetActionRunner? _widgetActions;

    /// <summary><c>widgets.runAction</c>(widget 里预先声明的动作,经权限链代参执行)。</summary>
    internal WidgetActionRunner WidgetActions => _widgetActions ??= new WidgetActionRunner(this);

    public PermissionRuleBook Rules { get; }

    public IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>统一工具注册表(03 §1):内置 + MCP + 插件的工具;一方扩展注册的工具(<c>Task</c> / <c>TodoWrite</c> / 计划工具 / <c>Skill</c>……)随会话扩展进快照,<see cref="AllTools"/> 把它们并进 <c>tools.list</c>。</summary>
    public ToolRegistry ToolRegistry { get; }

    public BackgroundShells Shells { get; }

    /// <summary>联网工具(WebFetch / web_search)的 HTTP 出口、搜索提供方的配置与 Key(<c>websearch.*</c> 方法的实现)。</summary>
    internal Ncw.Agent.Host.Services.WebToolsHost Web { get; }

    /// <summary>浏览器工具的运行时(每个项目至多一个无头浏览器进程;Agent 关闭时杀掉)。</summary>
    internal Ncw.Agent.Browser.BrowserService Browser { get; }

    public SessionDirectory Directory { get; }

    public RunRegistry Registry { get; }

    /// <summary>子代理编排(<c>Task</c> 工具的启动器、子 Run 的权限登记、后台汇报)。</summary>
    public SubagentService Subagents { get; }

    /// <summary>子代理定义目录(每个工作区一份注册表;<c>subagents.list / diagnostics / setEnabled</c>)。</summary>
    public AgentCatalog Agents { get; }

    /// <summary>计划模式 + 待办 + 目标三个一方扩展的装配。</summary>
    public PlanningKit Planning { get; }

    /// <summary>设备本机环境(全局子代理目录在它上面,不经项目的执行环境)。</summary>
    public IExecutionEnvironment LocalEnvironment => _localEnvironment;

    /// <summary>
    /// 设备上的执行环境服务(本机 + SSH 的清单、配置、连接、空闲回收、<c>environments</c> 主题)。项目的环境解析走 <see cref="EnvironmentOf"/> / <see cref="EnvironmentOfAsync"/>
    /// (默认就是它;测试注入的 <see cref="AgentServices.Environments"/> 只替换解析)。
    /// </summary>
    internal Ncw.Agent.Host.Environments.EnvironmentService Environments { get; }

    /// <summary>项目的执行环境(同步版:环境已经就绪才返回;SSH 没连上是可重试的 <c>unavailable</c>)。需要等连接的入口用 <see cref="EnvironmentOfAsync"/>。</summary>
    public IExecutionEnvironment EnvironmentOf(WorkspaceRecord project) => _environments.Resolve(project);

    /// <summary>确保项目的执行环境已连接再返回它:SSH 环境惰性连接,首次指纹确认 / 凭据提问以交互出现(任何控制端可答),这里等用户回答。</summary>
    public ValueTask<IExecutionEnvironment> EnvironmentOfAsync(WorkspaceRecord project, CancellationToken ct = default) => _environments.ResolveReadyAsync(project, ct);

    /// <summary>一个工作区的会话扩展(权限固定在最前,其余按各自的 <c>Order</c> 排;会话层再排序)。</summary>
    private IReadOnlyList<IAgentExtension> ExtensionsFor(string workspaceId, PermissionExtension permissions) =>
        [permissions, new NetworkSwitchExtension(PermissionRuns), new SubagentExtension(Subagents, workspaceId), .. Domain.SessionExtensions(workspaceId), .. Planning.Extensions];

    private ExtensionHost? _probe;

    /// <summary>探测用的扩展宿主(只为收集一方扩展注册的工具和命令;不跑任何会话)。</summary>
    private ExtensionHost Probe()
    {
        if (Volatile.Read(ref _probe) is { } existing)
        {
            return existing;
        }

        var probe = new ExtensionHost(ExtensionsFor("", new PermissionExtension(PermissionRuns, Rules, options: Services.Permissions)));
        return Interlocked.CompareExchange(ref _probe, probe, null) ?? probe;
    }

    /// <summary>扩展域(Skills / 命令 / Hooks / MCP)的服务。</summary>
    internal ExtensionDomain Domain { get; }

    private ProjectTarget ProjectTargetOf(string workspaceId)
    {
        var project = RequireProject(workspaceId);
        var environment = EnvironmentOf(project);
        return new ProjectTarget(project, environment, EnvPath.Parse(project.RootPath, environment.Facts.PathStyle));
    }

    /// <summary>
    /// <c>tools.list</c> 列的全部工具:统一注册表里的(内置 + MCP + 插件,含被禁用的)加上一方扩展注册的(<c>Task</c> / <c>TodoWrite</c> / 计划工具 /
    /// <c>ProposeGoal</c> / <c>Skill</c>……,它们随会话扩展进每回合的快照,不在注册表里;这里用探测用的扩展宿主把它们收出来)。同一个内部 id 只列一次,注册表优先。
    /// </summary>
    public IReadOnlyList<IAgentTool> AllTools()
    {
        var registered = ToolRegistry.List().Select(static e => e.Tool).ToList();
        var seen = new HashSet<string>(registered.Select(static t => t.Info.InternalId), StringComparer.Ordinal);
        registered.AddRange(Probe().RegisteredTools.Where(t => seen.Add(t.Info.InternalId)));
        return registered;
    }

    private AgentSession CreateSession(string sessionId, string workspaceId, PermissionExtension permissions, IIdGenerator ids, TimeProvider clock)
    {
        var project = Workspaces.Get(workspaceId) ?? throw new InvalidOperationException($"Project {workspaceId} does not exist.");
        var environment = EnvironmentOf(project);
        return new AgentSession(sessionId, workspaceId, new AgentSessionDeps
        {
            Sessions = Sessions,
            Llm = _models.Client,
            Ids = ids,
            Clock = clock,
            Delayer = Services.Delayer ?? new TimeProviderDelayer(clock),
            Environment = environment,
            WorkspaceRoot = EnvPath.Parse(project.RootPath, environment.Facts.PathStyle),
            Extensions = ExtensionsFor(workspaceId, permissions),
            Tools = ToolRegistry,
            ResolveModel = _models.Resolve,
            LoopOptions = Services.Loop,
            Diagnostics = Log,
        });
    }

    // ───────────────────────── 主题的状态生成 ─────────────────────────

    /// <summary><c>device</c> 主题的状态(首帧)。和 <c>activeRuns.changed</c> 同一个生成函数、同一个类型。</summary>
    public DeviceTopicState DeviceState() => Mapping.ToDeviceState(Registry.ActiveRuns(), _projectsRevision());

    public InteractionsDto InteractionsState() => Mapping.ToDto(Gate.Snapshot());

    /// <summary><c>session/&lt;id&gt;</c> 的快照;会话不存在返回 null(<c>not_found</c>)。</summary>
    public SessionTopicState? SessionState(string sessionId)
    {
        var page = Sessions.Page(sessionId, SnapshotPageSize);
        return page is null
            ? null
            : new SessionTopicState(Mapping.ToDto(page), Mapping.ToDto(Registry.QueueOf(sessionId)), Registry.ActiveTopLevelRun(sessionId)?.RunId.Value);
    }

    // ───────────────────────── 往 session/<id> 主题发布 ─────────────────────────

    private bool Watched(string sessionId) => _topics.HasSubscribers(ConversationMethods.SessionTopicName(sessionId));

    public void PublishSession(string sessionId)
    {
        if (Watched(sessionId) && Sessions.Get(sessionId) is { } session)
        {
            _topics.Publish(ConversationMethods.SessionTopicName(sessionId), SessionEvents.Updated, Mapping.ToDto(session), PayloadShapes.GetShape<SessionDto>());
        }
    }

    /// <summary>历史被改写:整页替换(编辑、删除回合 / 回复、压缩)。</summary>
    public void PublishHistoryReset(string sessionId)
    {
        if (Watched(sessionId) && Sessions.Page(sessionId, SnapshotPageSize) is { } page)
        {
            _topics.Publish(ConversationMethods.SessionTopicName(sessionId), SessionEvents.HistoryReset, Mapping.ToDto(page), PayloadShapes.GetShape<SessionPageDto>());
        }
    }

    public void PublishMessageCommitted(string sessionId, MessageDto message)
    {
        if (Watched(sessionId))
        {
            _topics.Publish(ConversationMethods.SessionTopicName(sessionId), SessionEvents.MessageCommitted, message, PayloadShapes.GetShape<MessageDto>());
        }
    }

    public void PublishSessionDeleted(string sessionId)
    {
        if (Watched(sessionId))
        {
            _topics.Publish(ConversationMethods.SessionTopicName(sessionId), SessionEvents.Deleted, Unit.Value, PayloadShapes.GetShape<Unit>());
        }
    }

    // ───────────────────────── 子代理 / 目标 ─────────────────────────

    /// <summary>
    /// 计划工作流对应的会话模式 id(2.x:<c>mode.id === 'plan'</c>)。开着时计划工具对模型可见、<c>Task</c> 被摘掉(计划模式下没法借子代理绕开写入围栏)。
    /// </summary>
    public const string PlanModeId = "plan";

    /// <summary>取 Run 发送时冻结的会话模式;Run 已不在注册表里时取会话当前的。</summary>
    private bool IsPlanWorkflow(RunId runId, string sessionId) =>
        (Registry.Get(runId)?.Options?.Mode ?? Sessions.Get(sessionId)?.Mode) == PlanModeId;

    private void PublishSubagentReport(ReportChange change)
    {
        var topic = ConversationMethods.SessionTopicName(change.SessionId);
        if (_topics.HasSubscribers(topic))
        {
            _topics.Publish(topic, SubagentEvents.Report, new SubagentReportDto(change.SessionId, change.CallId, OrchestrationMapping.ReportName(change.Status)), PayloadShapes.GetShape<SubagentReportDto>());
        }
    }

    private void PublishSubagentStopped(SubagentStopped stopped)
    {
        var topic = ConversationMethods.SessionTopicName(stopped.ParentSessionId);
        if (_topics.HasSubscribers(topic))
        {
            _topics.Publish(
                topic, SubagentEvents.Stopped,
                new SubagentStoppedDto(stopped.ParentSessionId, stopped.CallId, stopped.ChildRunId.Value, stopped.ChildSessionId, stopped.AgentType, Mapping.StatusName(stopped.Status), stopped.Background),
                PayloadShapes.GetShape<SubagentStoppedDto>());
        }
    }

    /// <summary>目标变了 → <c>goal</c> 主题的增量(全量、带单调版本号;版本号与推送在同一把锁里,所以版本序就是推送序)。</summary>
    private void PublishGoalChange(GoalChange change)
    {
        lock (_goalGate)
        {
            var rev = ++_goalRev;
            _topics.Publish(
                OrchestrationMethods.GoalTopic.Name, GoalEvents.Changed,
                OrchestrationMapping.ToChange(change, Planning.Goals.Store.Get(change.SessionId), rev), PayloadShapes.GetShape<GoalChangeDto>());
        }
    }

    /// <summary>
    /// <c>goal</c> 主题的快照:内存里所有会话的目标 + 当前版本号(首帧和增量同一个全量语义)。
    /// ★ 不取 <c>_goalGate</c>:主题表在持有它自己的锁时调快照函数,而发布路径是先 <c>_goalGate</c> 后主题表的锁——两把锁反着取就是死锁。
    /// 先读版本号再读目标:目标至少和这个版本一样新;更新的变更随后作为事件到达(版本更大,同样的全量替换),控制端按「不更旧」收,最终收敛。
    /// </summary>
    public GoalsTopicState GoalsState()
    {
        var rev = Interlocked.Read(ref _goalRev);
        return new GoalsTopicState(rev, [.. Planning.Goals.Store.All().Select(static kv => OrchestrationMapping.ToDto(kv.Key, kv.Value))]);
    }

    /// <summary><c>goal.get</c>:先恢复(转录里最后一条标记),再读。版本号取读之后的:只会比这份目标更新,控制端按「不更旧」收,最终收敛。</summary>
    public GoalStateDto GoalOf(string sessionId)
    {
        var goal = Planning.Goals.Get(sessionId);
        var rev = Interlocked.Read(ref _goalRev);
        return new GoalStateDto(sessionId, goal is null ? null : OrchestrationMapping.ToDto(sessionId, goal), rev);
    }

    // ───────────────────────── 校验助手 ─────────────────────────

    public SessionRecord RequireSession(string sessionId) =>
        Sessions.Get(sessionId) ?? throw new ProtocolException(ErrorCodes.NotFound, $"Unknown session '{sessionId}'.");

    public WorkspaceRecord RequireProject(string workspaceId) =>
        Workspaces.Get(workspaceId) ?? throw new ProtocolException(ErrorCodes.NotFound, $"Unknown project '{workspaceId}'.");

    /// <summary>member 不能用完全访问档位(09 §4):<c>full</c> 只有 owner。</summary>
    public static void RequirePermissionModeAllowed(string permissionMode, CallContext context)
    {
        if (permissionMode == "full" && context.Role < Ncw.Protocol.Methods.Role.Owner)
        {
            throw new ProtocolException(ErrorCodes.Forbidden, "Only an owner can run with the 'full' permission mode.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Titles?.Dispose();
        await Registry.ShutdownAsync().ConfigureAwait(false);
        await Domain.DisposeAsync().ConfigureAwait(false);
        Web.Dispose();
        await Browser.DisposeAsync().ConfigureAwait(false);
        Gate.AbortAll();
        Planning.Goals.StopAll();
        Subagents.Dispose();
        Registry.Dispose();
        await _localEnvironment.DisposeAsync().ConfigureAwait(false);
        await Environments.DisposeAsync().ConfigureAwait(false);
    }

    // ───────────────────────── 内部类型 ─────────────────────────

    /// <summary>编排层只用它数待处理交互(广播里的 <c>PendingInteractions</c>、回收时钉住)。交互的真源只有闸门。</summary>
    private sealed class GateView(InteractionGate gate) : IInteractionView
    {
        public int PendingCount(RunId runId) => gate.PendingCount(runId);
    }
}

/// <summary>
/// 装配方(Host)实现的 <see cref="IRunRequestFactory"/>:读设置、拼 <see cref="SessionSettings"/>(自动压缩、最大输出、个性化、压缩模型,见 <see cref="StoredSessionSettings"/>;
/// 每个 Run 现读一次,运行期不变);模式提示词随会话模式移植。
/// </summary>
internal sealed class HostRunRequestFactory(StoredSessionSettings settings) : IRunRequestFactory
{
    public RunRequest Create(RunTemplate template) =>
        new(template.RunId, template.Input, template.Options.Model, template.Options.Thinking)
        {
            MaxContext = template.Options.MaxContext,
            Settings = settings.Read(),
        };
}

/// <summary>
/// <c>IRunExtension</c>:每个 Run 创建时造它的 <see cref="ToolRuntime"/>、为它打开交互作用域、登记 <see cref="PermissionRunState"/>(权限扩展靠它判定:没有登记就是拒绝);
/// 工具上下文里套上 <c>ExecutionToolContext</c>(工具只经它访问文件和进程);Run 结束时清理它挂起的交互。回调里的异常被编排层吞掉并记诊断,
/// 所以这里任何一步失败的结果都是「没有权限状态 → 工具全部拒绝」(失败即关闭),而不是放行。
/// </summary>
internal sealed class ConversationRunExtension(ConversationService service) : IRunExtension
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RunId, ToolRuntime> _runtimes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RunId, IDisposable> _leases = new();

    public void OnCreated(RunHandle run)
    {
        var project = service.RequireProject(run.WorkspaceId);
        var environment = service.EnvironmentOf(project);
        // 租约:Run 活着期间它的 SSH 环境不会被空闲回收(2.x EnvironmentManager 的 acquire / release);本机项目是空句柄
        _leases[run.RunId] = service.Environments.Hold(project.ConnectionId);
        var runtime = new ToolRuntime
        {
            Environment = environment,
            WorkspaceId = run.WorkspaceId,
            Root = run.WorkspaceRoot ?? EnvPath.Parse(project.RootPath, environment.Facts.PathStyle),
            Shells = service.Shells,
        };
        // 子代理的 Run 不在这里登记权限状态 / 开交互作用域:那由 SubagentService.OnCreated 做(档位 = min(父实时值, 定义值),作用域 depth > 0 = 不能提问)。
        // 这里只给它造工具运行时,让它的工具和顶层 Run 一样经 ExecutionToolContext 访问文件和进程。
        if (run.Origin != RunOrigin.Subagent)
        {
            var interactions = service.Gate.OpenRun(run.RunId, run.Depth);
            var mode = PermissionModes.TryParse(run.LivePermissionMode, out var parsed) ? parsed : PermissionMode.Ask;
            // 权限层和工具层对「在哪、围在哪」永远是同一个答案:环境、项目根、写入围栏都取自同一个 ToolRuntime。
            service.PermissionRuns.Register(new PermissionRunState(run.RunId, run.SessionId, runtime.Environment, interactions, mode)
            {
                Root = runtime.Root,
                WorkspaceId = run.WorkspaceId,
                // 「为我批准」档位的专用审核模型(设置 › 模型):没配 = 没有审核模型 = 回退人工审批(Unknown,不是放行)。每个 Run 现读。
                ReviewerModel = service.DeviceSettings.Current().PermissionReviewer is { IsSet: true } reviewer ? new ModelRef(reviewer.ProviderId, reviewer.Model) : null,
                // 审核器要看的「用户的任务」:这次 Run 的输入文字(2.x 的 req.input;子代理的权限状态从父 Run 继承它)。
                UserTask = run.InputText,
                PlanFence = runtime.WriteFence,
                // 「联网搜索」开关(项目设置,Run 开始时现读):关着时 WebFetch / web_search 不进工具快照、调用也被拒(NetworkSwitchTools 那一张名单)。
                WebSearch = new Ncw.Agent.Services.Settings.ProjectSettingsService(service.Workspaces).Get(run.WorkspaceId)?.WebSearch ?? false,
            });
        }

        _runtimes[run.RunId] = runtime;
    }

    public ToolContext EnrichToolContext(RunHandle run, ToolContext context) =>
        _runtimes.TryGetValue(run.RunId, out var runtime) ? ExecutionToolContext.Enricher(runtime)(context) : context;

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
        CollectChanges(run);
        if (run.Origin == RunOrigin.Subagent)
        {
            return; // 子 Run 的权限状态和交互作用域由 SubagentService.OnEnded 结清
        }

        // 兜底:权限扩展在 run_end 扩展点里也会清,这里保证「没走到那一步的结局」(看门狗强收、驱动崩溃)同样不留挂起的交互。
        service.PermissionRuns.Get(run.RunId)?.Interactions.End(status);
        service.PermissionRuns.Remove(run.RunId);
    }

    public void OnSettled(RunHandle run)
    {
        _runtimes.TryRemove(run.RunId, out _);
        if (_leases.TryRemove(run.RunId, out var lease))
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// 封包本轮的文件改动(采集器读即清)。<b>不看 Run 的结局</b>:中断 / 出错时磁盘上已发生的写入就是既成事实,照样封,好让审查卡也能撤销「跑到一半」的改动。
    /// 子代理的改动并进顶层 Run(沿 ParentRunId 往上找);父 Run 已经被回收(后台子代理活得比父久)时落在能找到的最高一级(它的父 id 与父会话)。
    /// </summary>
    private void CollectChanges(RunHandle run)
    {
        if (!_runtimes.TryGetValue(run.RunId, out var runtime))
        {
            return;
        }

        var changes = runtime.Changes.Take(run.RunId);
        if (changes.Count == 0)
        {
            return;
        }

        var rootRunId = run.RunId.Value;
        var rootSessionId = run.SessionId;
        var cursor = run;
        var seen = new HashSet<RunId> { run.RunId };
        while (cursor.ParentRunId is { } parentId && seen.Add(parentId))
        {
            if (service.Registry.Get(parentId) is not { } parent)
            {
                rootRunId = parentId.Value;
                rootSessionId = cursor.ParentSessionId ?? rootSessionId;
                break;
            }

            rootRunId = parent.RunId.Value;
            rootSessionId = parent.SessionId;
            cursor = parent;
        }

        service.RaiseRunChanges(new RunChanges(run, rootRunId, rootSessionId, changes));
    }
}

/// <summary>一个 Run 收尾时采集到的文件改动,已经算好要聚合到哪个顶层 Run / 会话。</summary>
internal sealed record RunChanges(RunHandle Run, string RootRunId, string RootSessionId, IReadOnlyList<PendingChange> Changes);

/// <summary>
/// 包住会话层的驱动,把每条已落库的消息(<c>message_end</c> 在落库之后才发)转成 <c>session/&lt;id&gt;</c> 主题的 <c>message.committed</c>,
/// 这样在另一台设备上看着同一个会话的控制端,不用订阅 Run 也能看到消息落库。事件仍原样交给编排层的事件流。
/// </summary>
internal sealed class TappingDriver(IRunDriver inner, ConversationService service) : IRunDriver
{
    public Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct)
    {
        StartTitle(run, request);
        return inner.RunAsync(run, request, new TapSink(sink, run.SessionId, service), ct);
    }

    /// <summary>
    /// 顶层 Run、会话里还没有任何消息(输入要等会话层在 Run 里落库,所以此刻数到 0 才是「第一条消息」)时,给会话起标题。
    /// 旁路请求不挂在 Run 的任务上(<see cref="SessionTitleGenerator"/>):不阻塞对话、不随 Run 结束而取消。永不抛。
    /// </summary>
    private void StartTitle(RunHandle run, RunRequest request)
    {
        if (service.Titles is not { } titles || !run.IsTopLevel || run.Origin == RunOrigin.Subagent || run.State != RunState.Running)
        {
            return;
        }

        try
        {
            if (service.Sessions.Get(run.SessionId) is { } session
                && request.Input.FirstOrDefault(m => m.Role == Ncw.Agent.Llm.Role.User && !m.Internal) is { } first
                && service.Sessions.CountMessages(run.SessionId) == 0)
            {
                titles.Start(session, first, request.Model);
            }
        }
        catch (Exception e)
        {
            service.Log.Warn("Could not start background title generation.", e);
        }
    }

    private sealed class TapSink(IRunSink inner, string sessionId, ConversationService service) : IRunSink
    {
        public async ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
        {
            await inner.EmitAsync(ev, ct).ConfigureAwait(false);
            if (ev is MessageEndEvent end)
            {
                try
                {
                    service.PublishMessageCommitted(sessionId, Mapping.ToDto(end.Message));
                }
                catch (Exception e)
                {
                    service.Log.Warn("Publishing a committed message failed.", e);
                }
            }
        }
    }
}
