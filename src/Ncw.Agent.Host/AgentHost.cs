using Ncw.Agent.Storage.Takeover;
using System.Reflection;
using System.Text.Json;
using Ncw.Agent.Cloud;
using Ncw.Agent.Host.Cloud;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Environments;
using Ncw.Agent.Host.Extensions;
using Ncw.Agent.Host.Plugins;
using Ncw.Agent.Host.Providers;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Host.Terminals;
using Ncw.Agent.Host.Workflows;
using Ncw.Agent.Host.Workspace;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Storage.Sessions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host;

/// <summary>
/// Agent 服务进程:装配方法表与主题,在本机承载上接受连接。一个进程可以同时服务多个控制端。
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentOptions _options;
    private readonly MethodRouter _router = new();
    private readonly TopicHub _topics = new();
    private readonly DeviceIdentity _identity;
    private readonly DataGate _data;
    private readonly RemoteService _remote;
    private ProjectRegistry? _projects;
    private ConversationService? _conversation;
    private ProviderRuntime? _providerRuntime;
    private WorkspaceToolsRuntime? _workspaceTools;
    private DeviceServices? _deviceServices;
    private WorkflowService? _workflows;

    /// <summary>测试缝:开放网关的运行时(数据就绪之前为 null)。</summary>
    internal Ncw.Agent.Host.Services.GatewayRuntime? GatewayRuntime => _deviceServices?.Gateway;
    private PushRegistrations? _push;
    private GitCredentialVault? _gitCredentials;
    private readonly CloudHostOptions? _cloudOptions;
    private readonly CloudRuntime? _cloud;
    private readonly IEgressPolicy _egress;
    private readonly ILoopDiagnostics _log;
    private int _conversationDisposed;

    /// <param name="services">
    /// 可注入的服务(模型解析、项目执行环境、退避、注册表参数……,见 <see cref="AgentServices"/>)。缺省:没有供应商(对话以 <c>llm.noEnabledProvider</c> 错误结束)、本机环境。
    /// </param>
    public AgentHost(
        AgentOptions options,
        TimeProvider? clock = null,
        Ncw.Agent.Storage.Takeover.TakeoverOptions? takeover = null,
        Func<Ncw.Agent.Storage.Credentials.ILegacySecretDecryptor>? legacySecrets = null,
        TimeSpan? pairingFailureDelay = null,
        AgentServices? services = null,
        Ncw.Protocol.Transport.RelayClientOptions? relayOptions = null,
        CloudHostOptions? cloud = null)
    {
        _options = options;
        services ??= new AgentServices();
        _log = services.Diagnostics ?? new HostDiagnostics();
        var time = clock ?? TimeProvider.System;
        var account = services.ResolveAccount(time); // 委托令牌:只在内存里,技能 / 插件市场按它是否在位决定 authRequired
        if ((options.Mode == AgentMode.Cloud) != (cloud is not null))
        {
            throw new ArgumentException("--mode cloud needs the cloud startup options, and only --mode cloud takes them.", nameof(cloud));
        }

        _cloudOptions = cloud;
        _egress = cloud?.Egress ?? AllowAllEgress.Instance;
        Directory.CreateDirectory(options.DataRoot);
        if (cloud is not null)
        {
            // 云端:NCK1 主密钥由 KMS 包裹,明文只在内存里。必须先于第一次读主密钥(设备身份的私钥就用它加密)。
            CredentialCrypto.UseSource(options.DataRoot, cloud.KeySource);
        }

        try
        {
            _identity = DeviceIdentity.LoadOrCreate(options.DataRoot, time, cloud?.Config.DeviceName);
            if (cloud?.Config.ExpectedDeviceId is { } expected && !string.Equals(expected, _identity.DeviceId, StringComparison.Ordinal))
            {
                // 挂错了卷(数据根里的设备身份和编排服务期望的不一致):不能继续,否则会用别人的身份上线
                throw new CloudStartupException(CloudStartupCodes.IdentityMismatch, "expectedDeviceId", CloudExitCodes.Identity, "The data root belongs to a different device.");
            }
        }
        catch
        {
            if (cloud is not null)
            {
                CredentialCrypto.ReleaseSource(options.DataRoot, cloud.KeySource);
            }

            throw;
        }

        _data = new DataGate(options.DataRoot, _identity.DeviceId, time, takeover, legacySecrets);

        // 权威广播:首帧和后续用同一个生成函数(AGENTS.md §3 规则 5)。主题先注册,数据就绪时才能发布。
        _topics.Register(DeviceMethods.DeviceTopic, DeviceState);
        _topics.Register(DeviceMethods.DataTopic, () => _data.Status);
        // 对话链路:interactions(全量权威)、session/<id>(前缀主题)、run/<id>(来源主题,序号和游标就是 Run 事件日志的)
        _topics.Register(ConversationMethods.InteractionsTopic, () => Volatile.Read(ref _conversation)?.InteractionsState() ?? new InteractionsDto(0, []));
        _topics.RegisterPrefix(ConversationMethods.SessionTopicPrefix, PayloadShapes.GetShape<SessionTopicState>(),
            name => Conversation().SessionState(name[ConversationMethods.SessionTopicPrefix.Length..]));
        _topics.RegisterSource(new RunTopicSource(Conversation));
        // 目标:全量权威(快照与 goal.changed 同一个语义,带单调版本号);数据没就绪时是空状态
        _topics.Register(OrchestrationMethods.GoalTopic, () => Volatile.Read(ref _conversation)?.GoalsState() ?? new GoalsTopicState(0, []));
        // 模型目录与供应商:数据没就绪时是空状态(版本 0),就绪后发一次 changed,已经订阅的控制端据此补上。
        _topics.Register(CatalogMethods.Topic, () => Volatile.Read(ref _providerRuntime)?.CatalogState() ?? EmptyCatalog);
        _topics.Register(ProvidersMethods.Topic, () => Volatile.Read(ref _providerRuntime)?.ProvidersState() ?? new ProvidersTopicState(0, [], [], [], []));
        _topics.Register(ProvidersMethods.OAuthTopic, () => Volatile.Read(ref _providerRuntime)?.OAuthState() ?? new OAuthTopicState(null));
        // 供应商调试的进度(前缀主题,owner):没开始 / 已回收的 runId 给空快照,所以控制端可以先订阅再发起。
        _topics.RegisterPrefix(ProviderDebugMethods.TopicPrefix, PayloadShapes.GetShape<ProviderDebugTopicState>(),
            name => Volatile.Read(ref _providerRuntime)?.DebugRuns.State(name[ProviderDebugMethods.TopicPrefix.Length..])
                    ?? new ProviderDebugTopicState(name[ProviderDebugMethods.TopicPrefix.Length..], false, []),
            Role.Owner);
        // 设备设置 / 定时任务:全量权威(首帧与事件同一个类型,带单调版本号);数据没就绪时是出厂 / 空状态(版本 0),就绪后发一次 changed。
        _topics.Register(SettingsMethods.Topic, () => Volatile.Read(ref _deviceServices)?.SettingsState() ?? new DeviceSettingsState(0, ServiceMapping.ToDto(Ncw.Agent.Services.Settings.DeviceSettings.Default, false)));
        _topics.Register(SchedulesMethods.Topic, () => Volatile.Read(ref _deviceServices)?.SchedulesState() ?? new SchedulesTopicState(0, [], []));
        // 扩展域:extensions(Skills / 命令 / Hooks 的修订号,控制端据此重拉列表)、mcp(服务器连接状态,全量 + 单调 Rev);数据没就绪时是空状态,就绪后发一次 changed。
        _topics.Register(ExtensionsMethods.Topic, () => Volatile.Read(ref _conversation)?.Domain.Revisions.State() ?? new ExtensionsTopicState(0, 0, 0));
        _topics.Register(McpMethods.Topic, () => McpMapping.ToDto(Volatile.Read(ref _conversation)?.Domain.Mcp.Snapshot() ?? Ncw.Agent.Mcp.McpSnapshot.Empty));
        // 插件:全量权威(首帧与 plugins.changed 同一个类型,带单调 Rev);数据没就绪时是空状态(Rev 0),就绪后发一次 changed。
        _topics.Register(PluginsMethods.Topic, () => Volatile.Read(ref _conversation) is { } c ? PluginsMapping.ToDto(c.Domain.Plugins.Snapshot(), c.Domain.Plugins.ApiVersion) : new PluginsTopicState(0, Ncw.Agent.Plugins.PluginApiVersion.Current, [], []));
        // 执行环境(SSH 多设备执行):全量权威(首帧与 environments.changed 同一个类型,带单调 Rev);数据没就绪时是只有 local 的空状态(Rev 0),就绪后发一次 changed。
        _topics.Register(EnvironmentsMethods.Topic, () => Volatile.Read(ref _conversation)?.Environments.State() ?? new EnvironmentsTopicState(0, "local", []));
        // 搜索提供方:全量权威(首帧与 websearch.changed 同一个类型,带单调 Version);数据没就绪时是空状态(Version 0),就绪后发一次 changed。
        _topics.Register(WebSearchMethods.Topic, () => Volatile.Read(ref _conversation)?.Web.State() ?? new WebSearchTopicState(0, []));
        // 工作流运行(前缀主题 workflow/<runId>):支持先订阅再启动。
        _topics.RegisterPrefix(WorkflowMethods.RunTopicPrefix, PayloadShapes.GetShape<WorkflowRunSnapshotDto>(),
            name => Volatile.Read(ref _workflows)?.RunSnapshot(name[WorkflowMethods.RunTopicPrefix.Length..]));
        _remote = new RemoteService(_identity, _router, _topics, Version, time, pairingFailureDelay, relayOptions);
        if (cloud is not null)
        {
            // 云端:只经中继(地址来自启动配置)、没有配对码、信任记录由编排服务下发;休眠 / 升级前的收尾见 QuiesceAsync
            _remote.UseCloudProfile(cloud.Config.RelayUrl);
            _cloud = new CloudRuntime(cloud, _identity, _remote, account, time, _log, QuiesceAsync);
        }

        _data.Ready += database =>
        {
            var projects = new ProjectRegistry(new WorkspaceRepository(database, time));
            projects.Changed += list =>
                _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, list, PayloadShapes.GetShape<ProjectsListResult>());
            Volatile.Write(ref _projects, projects);
            // 模型目录与供应商先于对话链路装配:会话层经 IModelResolver 拿路由和绑定(测试可以注入假的)。
            // 设备设置先于一切装配:路由(空闲超时 / 故障转移 / 账号轮换 / 代理)、会话层(设置快照)、子代理、目标都读同一份,协议方法写的也是它。
            var deviceSettings = new Ncw.Agent.Services.Settings.SettingsService(new SettingsRepository(database));
            var proxyPassword = new ProxyPasswordStore(new Ncw.Agent.Storage.Credentials.CredentialStore(database, _options.DataRoot, Ncw.Agent.Storage.Credentials.NoLegacySecrets.Instance));
            var proxy = new SettingsProxy(deviceSettings, proxyPassword);
            // 附件与转录共用写入观察器，出站图片解析也经同一个受管附件服务。
            var attachmentRepository = new AttachmentRepository(database, time);
            var sessions = new SessionRepository(database, time, attachmentRepository);
            var attachments = new Ncw.Agent.Services.Attachments.AttachmentService(_options.DataRoot, time, attachmentRepository, sessions);
            var providerRuntime = new ProviderRuntime(database, _options.DataRoot, time, services, _topics, _log, deviceSettings, proxy, new SessionImageResolver(attachments));
            Volatile.Write(ref _providerRuntime, providerRuntime);
            Volatile.Write(ref _conversation, new ConversationService(
                database, _identity.DeviceId, _options.DataRoot, time, services, _topics, () => Volatile.Read(ref _projects)?.Revision ?? 0, services.Models ?? providerRuntime, _log, deviceSettings, providerRuntime, sessions, attachments));
            var deviceServices = new DeviceServices(database, _options.DataRoot, Version, time, _topics, deviceSettings, proxyPassword, proxy, Conversation(), _log, _router, providerRuntime,
                onDataReplaced: () => _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, projects.List(), PayloadShapes.GetShape<ProjectsListResult>()));
            Volatile.Write(ref _deviceServices, deviceServices);
            Volatile.Write(ref _workflows, new WorkflowService(database, Conversation(), _topics, time));
            // 推送令牌登记(只存不发,发送由后端推送中继负责)
            Volatile.Write(ref _push, new PushRegistrations(database));
            // 工作区工具(files / git / review / terminals)和对话链路同生命周期:项目解析、环境、模型(提交信息)都取自它们。
            Volatile.Write(ref _workspaceTools, new WorkspaceToolsRuntime(database, Conversation(), providerRuntime, _topics, time, services, _log));
            providerRuntime.Start();
            providerRuntime.PublishCatalog();
            providerRuntime.PublishProviders();
            // 定时任务:对账错过的、排好定时器;已经订阅 settings / schedules 的控制端从这里拿到真实状态。
            deviceServices.Start();
            _topics.Publish(SettingsMethods.Topic.Name, SettingsEvents.Changed, deviceServices.SettingsState(), PayloadShapes.GetShape<DeviceSettingsState>());
            _topics.Publish(SchedulesMethods.Topic.Name, SchedulesEvents.Changed, deviceServices.SchedulesState(), PayloadShapes.GetShape<SchedulesTopicState>());
            // 插件的状态变化(安装 / 启停 / 激活结果 / 授权 / 提问)→ plugins 主题。先订阅再启动:启动时后台激活插件产生的变化一个都不丢。事件在插件管理器的发布锁里按 Rev 序触发,所以推送序 = 版本序。
            var pluginManager = Conversation().Domain.Plugins;
            pluginManager.Changed += snapshot => _topics.Publish(PluginsMethods.Topic.Name, PluginsEvents.Changed, PluginsMapping.ToDto(snapshot, pluginManager.ApiVersion), PayloadShapes.GetShape<PluginsTopicState>());
            // 扩展域:内置 Skill 铺到全局目录、已启用的 MCP 服务器在后台连接、已启用的插件在后台激活;已经订阅 extensions / mcp / plugins 的控制端从这里拿到真实状态。
            Conversation().Domain.Start();
            _topics.Publish(ExtensionsMethods.Topic.Name, ExtensionsEvents.Changed, Conversation().Domain.Revisions.State(), PayloadShapes.GetShape<ExtensionsTopicState>());
            _topics.Publish(McpMethods.Topic.Name, McpEvents.Changed, McpMapping.ToDto(Conversation().Domain.Mcp.Snapshot()), PayloadShapes.GetShape<McpTopicState>());
            _topics.Publish(PluginsMethods.Topic.Name, PluginsEvents.Changed, PluginsMapping.ToDto(pluginManager.Snapshot(), pluginManager.ApiVersion), PayloadShapes.GetShape<PluginsTopicState>());
            // 执行环境:已经订阅 environments 的控制端从这里拿到真实清单(数据就绪之前是空状态)。
            Conversation().Environments.Publish();
            // 联网工具:搜索提供方的状态变化 → websearch 主题;就绪后发一次,已经订阅的控制端从这里拿到真实状态。
            Conversation().Web.Changed += state => _topics.Publish(WebSearchMethods.Topic.Name, WebSearchEvents.Changed, state, PayloadShapes.GetShape<WebSearchTopicState>());
            Conversation().Web.Publish();
            // 数据晚于连接就绪(接管完成)时,已经连着的控制端从这里拿到项目列表。
            _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, projects.List(), PayloadShapes.GetShape<ProjectsListResult>());
            // Git 主机凭据(NCK1;云端主密钥由 KMS 包裹):pull / push / 克隆用
            var gitCredentials = new GitCredentialVault(new Ncw.Agent.Storage.Credentials.CredentialStore(database, _options.DataRoot, Ncw.Agent.Storage.Credentials.NoLegacySecrets.Instance), new KvStore(database));
            Volatile.Write(ref _gitCredentials, gitCredentials);
            if (Volatile.Read(ref _workspaceTools) is { } workspaceTools)
            {
                workspaceTools.GitCredentials = gitCredentials;
            }

            _remote.OnDataReady(database);
            _cloud?.OnDataReady(database, Conversation(), deviceServices);
        };
        _data.Changed += status =>
            _topics.Publish(DeviceMethods.DataTopic.Name, DataEvents.Changed, status, PayloadShapes.GetShape<DataStatus>());
        _data.TryPrepareFast();
        Ncw.Agent.Host.Accounts.AccountHandlers.Register(_router, account, time);
        ConversationHandlers.Register(_router, Conversation);
        OrchestrationHandlers.Register(_router, Conversation);
        WorkflowHandlers.Register(_router, Workflows);
        ProviderHandlers.Register(_router, Providers);
        ServiceHandlers.Register(_router, Services);
        StorageHandlers.Register(_router, Services);
        SupportHandlers.Register(_router, Services, Conversation);
        GatewayHandlers.Register(_router, Services);
        FilesHandlers.Register(_router, WorkspaceTools);
        GitHandlers.Register(_router, WorkspaceTools);
        ReviewHandlers.Register(_router, WorkspaceTools);
        TerminalHandlers.Register(_router, _topics, WorkspaceTools);
        SkillsHandlers.Register(_router, () => Conversation().Domain.Skills);
        CommandsHandlers.Register(_router, () => Conversation().Domain.Commands);
        HooksHandlers.Register(_router, () => Conversation().Domain.Hooks);
        McpHandlers.Register(_router, () => Conversation().Domain.Mcp);
        PluginsHandlers.Register(_router, () => Conversation().Domain.Plugins);
        EnvironmentsHandlers.Register(_router, () => Conversation().Environments);
        Ncw.Agent.Host.Services.WebSearchHandlers.Register(_router, () => Conversation().Web);
        PushHandlers.Register(_router, () => Volatile.Read(ref _push) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true), time);
        GitCredentialHandlers.Register(_router, () => Volatile.Read(ref _gitCredentials) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true));
        if (_cloud is not null)
        {
            CloudHandlers.Register(_router, _cloud);
        }
        // 路由前置闸门:参数里指向 SSH 项目的方法(文件 / git / 终端 / Skills / 命令 / Hooks ……),先确保项目的执行环境已连接
        ProjectEnvironmentGate.Install(_router, () => Volatile.Read(ref _conversation));

        _router.Handle(DeviceMethods.Info, (_, _, _) => ValueTask.FromResult(new DeviceInfoResult(
            _identity.DeviceId, _identity.Name, options.Mode == AgentMode.Cloud ? "cloud" : "local",
            Version, Environment.OSVersion.VersionString, options.DataRoot, _remote.RelayUrl)));
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(Projects().List()));
        _router.Handle(DeviceMethods.ProjectsAdd, async (p, ctx, ct) =>
        {
            if (p.CloneUrl is { Length: > 0 })
            {
                // 克隆远程仓库(云端「工作区来源」):目标目录是新建的本机目录,经本机环境的 git(凭据经 NCK1);SSH 环境不支持克隆
                if (p.EnvironmentId is { Length: > 0 } cloneEnvironment && cloneEnvironment != "local")
                {
                    throw Ncw.Agent.Host.Services.ServiceMapping.Invalid("projects.clone.environment", "Cloning is only available for local projects");
                }

                var target = await ProjectClone.CloneAsync(Conversation().LocalEnvironment, Volatile.Read(ref _gitCredentials), _egress, _cloudOptions?.Config.WorkspacesRoot, p, ct).ConfigureAwait(false);
                return Projects().Add(target, p.Name);
            }

            if (p.EnvironmentId is not { Length: > 0 } environmentId || environmentId == "local")
            {
                return Projects().Add(p.Path, p.Name);
            }

            // SSH 项目:先确保环境连上(首次指纹 / 凭据以交互出现,等用户回答),核实远程目录存在,再登记。viewer 不能发起连接(方法本身是 member 起,这里是纵深防御)。
            var projects = Projects();
            var environments = Conversation().Environments;
            var (environmentName, root) = await environments.ResolveRemoteDirectoryAsync(environmentId, p.Path, ctx.Role >= Role.Member ? ConnectMode.Interactive : ConnectMode.None, ct).ConfigureAwait(false);
            return projects.AddRemote(environmentId, environmentName, root, p.Name);
        });
        _router.Handle(DeviceMethods.ProjectsRemove, async (p, _, _) =>
        {
            Projects().Remove(p.WorkspaceId);
            Volatile.Read(ref _conversation)?.Agents.Drop(p.WorkspaceId); // 这个工作区的子代理注册表跟着丢
            if (Volatile.Read(ref _workspaceTools) is { } tools)
            {
                await tools.OnProjectRemovedAsync(p.WorkspaceId).ConfigureAwait(false); // 终端关掉、文件索引缓存丢弃
            }

            return Unit.Value;
        });
        _router.Handle(DeviceMethods.DataStatus, (_, _, _) => ValueTask.FromResult(_data.Status));
        // data.retry 在「已回退到 2.x」的状态下就是用户明确要求的「重新迁移」(DataGate.RetryAsync)
        _router.Handle(DeviceMethods.DataRetry, async (_, _, ct) => await _data.RetryAsync(ct).ConfigureAwait(false));
        // 设置 · 数据 · 回退到 2.x(04 §5):只登记请求,Agent 关库之后执行(DataGate.Dispose)
        _router.Handle(DeviceMethods.DataRollbackInfo, (_, _, _) => ValueTask.FromResult(ToDto(_data.RollbackInfo())));
        _router.Handle(DeviceMethods.DataRequestRollback, (p, _, _) =>
        {
            if (!p.AcknowledgeDataLoss)
            {
                throw Ncw.Agent.Host.Services.ServiceMapping.Invalid("data.rollback.notAcknowledged", "Confirm that data written in 3.0 after the move will not go back to 2.x.");
            }

            try
            {
                return ValueTask.FromResult(ToDto(_data.RequestRollback()));
            }
            catch (TakeoverException e) when (e.Failure == TakeoverFailure.NothingToRollBack)
            {
                throw Ncw.Agent.Host.Services.ServiceMapping.Invalid("data.rollback.unavailable", e.Message);
            }
        });
        _router.Handle(DeviceMethods.DataCancelRollback, (_, _, _) => ValueTask.FromResult(ToDto(_data.CancelRollback())));

    }

    private static DataRollbackInfo ToDto(RollbackAvailability a) => new(a.Available, a.TakenOverAt, a.Pending);

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string DeviceId => _identity.DeviceId;

    /// <summary>当前的数据状态(测试和启动日志用)。</summary>
    public DataStatus Data => _data.Status;

    /// <summary>远程连接(测试和启动日志用)。</summary>
    public RemoteService Remote => _remote;

    /// <summary>设备公钥指纹(控制端钉扎用)。</summary>
    public byte[] Fingerprint => _identity.Keys.Fingerprint;

    public void Dispose()
    {
        _cloud?.Dispose();
        _remote.Dispose();
        DisposeConversationAsync().AsTask().GetAwaiter().GetResult();
        _data.Dispose();
        ReleaseCloudKey();
    }

    /// <summary>云端:不再用 KMS 主密钥来源,并把内存里的明文主密钥清零。</summary>
    private void ReleaseCloudKey()
    {
        if (_cloudOptions is { } cloud)
        {
            CredentialCrypto.ReleaseSource(_options.DataRoot, cloud.KeySource);
            cloud.KeySource.Dispose();
        }
    }

    /// <summary>云端:退出的原因(<c>sleep</c> / <c>upgrade</c>);进程还在正常运行或不是云端模式为 null。<see cref="RunAsync"/> 因为休眠 / 升级返回之后读它。</summary>
    public CloudExit? CloudExit => _cloud is { Exited.IsCompletedSuccessfully: true } c ? c.Exited.Result : null;

    /// <summary>云端运行时(测试驱动空闲检测、读状态用)。</summary>
    internal CloudRuntime? CloudRuntime => _cloud;

    /// <summary>
    /// 云端休眠 / 升级前的收尾(02 A6):<b>停止对外服务并注销中继</b>(控制端立刻得到 device_offline)→ 中止所有 Run、杀后台 Shell、关终端和浏览器、停调度器
    /// (<see cref="DisposeConversationAsync"/>,Run 的消息 / 状态在这一步落库)→ <b>数据库检查点</b>(WAL 全部写回主库,卷快照里只有一个自洽的数据库文件)。
    /// 之后进程只剩「写休眠快照、通知编排服务、退出」。
    /// </summary>
    private async Task QuiesceAsync()
    {
        await _remote.StopAsync().ConfigureAwait(false);
        await DisposeConversationAsync().ConfigureAwait(false);
        _data.Database?.Checkpoint();
    }

    /// <summary>
    /// 停掉所有 Run(等它们收尾落库)、中止挂起的交互、杀后台 Shell,然后停目录同步和供应商文件监听(Run 用着路由,所以在它之后)。
    /// 必须在关库之前;只做一次。
    /// </summary>
    private async ValueTask DisposeConversationAsync()
    {
        if (Interlocked.Exchange(ref _conversationDisposed, 1) != 0)
        {
            return;
        }

        // 终端先于对话链路停:本机终端的环境实例归对话链路的项目环境管,它们先释放终端才不会留孤儿 PTY。
        if (Volatile.Read(ref _workspaceTools) is { } tools)
        {
            await tools.DisposeAsync().ConfigureAwait(false);
        }

        // 调度器先于对话链路停:它正在跑的定时 Run 要经注册表中止、等收尾落库。
        if (Volatile.Read(ref _deviceServices) is { } deviceServices)
        {
            await deviceServices.DisposeAsync().ConfigureAwait(false);
        }

        if (Volatile.Read(ref _conversation) is { } conversation)
        {
            await conversation.DisposeAsync().ConfigureAwait(false);
        }

        if (Volatile.Read(ref _providerRuntime) is { } providers)
        {
            await providers.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>运行直到取消或父进程退出。<paramref name="onListening"/> 在端点真正可以连接之后调用(用来报告 ready)。</summary>
    public async Task RunAsync(CancellationToken ct, Action? onListening = null)
    {
        if (_cloud is not null)
        {
            await RunCloudAsync(_cloud, ct, onListening).ConfigureAwait(false);
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.ParentPid is { } parent)
        {
            _ = WatchParentAsync(parent, lifetime);
        }

        var server = new ServerOptions(_identity.DeviceId, Version, Transports.Local, Authenticate);
        var sessions = new List<Task>();
        using var listener = LocalTransport.Listen(_options.Endpoint);
        onListening?.Invoke();
        if (_data.Status.State != DataStates.Ready)
        {
            // 接管 2.x 数据可能要几十秒:在后台进行,控制端通过 data 主题看进度。
            _ = _data.PrepareAsync(lifetime.Token);
        }

        try
        {
            await foreach (var stream in listener.AcceptAsync(lifetime.Token).ConfigureAwait(false))
            {
                sessions.Add(new ServerSession(new FrameStream(stream), _router, _topics, server).RunAsync(lifetime.Token));
                sessions.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            // 停机时在途会话以取消收场(ServerSession 在 lifetime 取消后以 OperationCanceledException 结束);它们的结果没有人要,
            // 但绝不能让它们的失败跳过下面的收尾——否则 Run 不中止、终端 / 后台 Shell 不杀、库不关(并发跑测试时的孤儿 shell 就是这么来的)
            foreach (var session in sessions)
            {
                try
                {
                    await session.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 会话自己的失败不影响停机
                }
            }

            _remote.Dispose();
            await DisposeConversationAsync().ConfigureAwait(false);
            _data.Dispose();
        }
    }

    /// <summary>
    /// 云端模式(<c>--mode cloud</c>):没有本机通道(没有 socket、没有本机令牌),远程连接只经中继。启动 → 就绪回调 → 等取消或休眠 / 升级退出 → 收尾。
    /// 数据没能就绪(被挡住 / 失败)是启动失败(<see cref="CloudStartupException"/>,退出码 69)。
    /// </summary>
    private async Task RunCloudAsync(CloudRuntime cloud, CancellationToken ct, Action? onListening)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (_data.Status.State != DataStates.Ready)
            {
                var status = await _data.PrepareAsync(lifetime.Token).ConfigureAwait(false);
                if (status.State != DataStates.Ready)
                {
                    throw new CloudStartupException("cloud.data.notReady", "dataRoot", CloudExitCodes.Dependency, $"The data root is not ready ({status.State}/{status.Reason}).");
                }
            }

            await cloud.StartAsync(lifetime.Token).ConfigureAwait(false);
            onListening?.Invoke();
            await Task.WhenAny(cloud.Exited, Task.Delay(Timeout.Infinite, lifetime.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 外部取消 = 正常停机
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            cloud.Dispose();
            _remote.Dispose();
            await DisposeConversationAsync().ConfigureAwait(false);
            _data.Dispose();
            ReleaseCloudKey();
        }
    }

    /// <summary>本机通道:核对一次性本机令牌(常数时间比较),通过即 owner。</summary>
    private Role? Authenticate(HelloFrame hello, SessionPeer? peer) =>
        peer is null && hello.LocalToken is { } token
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(_options.LocalToken))
            ? Role.Owner
            : null;

    /// <summary>
    /// <c>device</c> 主题的状态 = 权威广播(首帧)。活动 Run 索引由 Core 的唯一生成函数产出,首帧和 <c>activeRuns.changed</c> 是同一个类型。
    /// 数据没就绪(对话链路还没装配)时是空索引。
    /// </summary>
    private DeviceTopicState DeviceState() =>
        Volatile.Read(ref _conversation)?.DeviceState() ?? new DeviceTopicState([], Volatile.Read(ref _projects)?.Revision ?? 0);

    private static readonly CatalogTopicState EmptyCatalog =
        new(new CatalogStatusDto(0, "seed", null, null, null, 0, 0, null), []);

    /// <summary>设置 / 用量 / 定时任务等杂项服务:数据没就绪时是可重试的 <c>unavailable</c>。</summary>
    private DeviceServices Services() =>
        Volatile.Read(ref _deviceServices) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    /// <summary>工作流服务:数据没就绪时是可重试的 <c>unavailable</c>。</summary>
    private WorkflowService Workflows() =>
        Volatile.Read(ref _workflows) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    /// <summary>目录与供应商:数据没就绪时是可重试的 <c>unavailable</c>。</summary>
    private ProviderRuntime Providers() =>
        Volatile.Read(ref _providerRuntime) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    /// <summary>工作区工具:数据没就绪时是可重试的 <c>unavailable</c>(和对话链路同一个规则)。</summary>
    private WorkspaceToolsRuntime WorkspaceTools() =>
        Volatile.Read(ref _workspaceTools) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    /// <summary>对话链路:数据没就绪时是可重试的 <c>unavailable</c>(和项目方法同一个规则)。</summary>
    private ConversationService Conversation() =>
        Volatile.Read(ref _conversation) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    /// <summary>依赖数据的方法:数据没就绪(正在接管或被挡住)时返回可重试的 unavailable。</summary>
    private ProjectRegistry Projects() =>
        Volatile.Read(ref _projects) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    private static async Task WatchParentAsync(int pid, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(pid);
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // 父进程已经不在了
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
    }
}
