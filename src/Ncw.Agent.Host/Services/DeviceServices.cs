using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Providers;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Services.Storage;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 杂项 Agent 服务(M4)在 Host 里的装配(设备数据就绪后创建一份):设置(设备 / 项目)、用量查询、定时任务(服务 + 调度器 + 经注册表的 Run 发起)。
/// 事件 → 主题:<c>settings.changed</c>、<c>schedules.changed</c>,都是全量状态(首帧与事件同一个类型,AGENTS.md §3 规则 5),带单调版本号。
/// 设置的真源只有 <see cref="SettingsService"/> 一份:Run / 派发 / 路由读它,协议方法写它,所以「设置写入后下一个 Run 生效」不需要任何通知。
/// </summary>
internal sealed class DeviceServices : IAsyncDisposable
{
    /// <summary>主题里带的最近已结束运行条数(活动的运行总是带全)。</summary>
    public const int RecentRunsInTopic = 50;

    private readonly TopicHub _topics;
    private readonly ProxyPasswordStore _proxyPassword;
    private readonly SettingsProxy? _proxy;
    private readonly Action? _onDataReplaced;
    private readonly Lock _scheduleGate = new();
    private long _scheduleRev;

    public DeviceServices(
        AgentDatabase database, string dataRoot, string appVersion, TimeProvider clock, TopicHub topics, SettingsService settings, ProxyPasswordStore proxyPassword, SettingsProxy? proxy,
        ConversationService conversation, ILoopDiagnostics log, MethodRouter router, ProviderRuntime? providers, Action? onDataReplaced = null, TimeZoneInfo? zone = null)
    {
        _onDataReplaced = onDataReplaced;
        _topics = topics;
        _proxyPassword = proxyPassword;
        _proxy = proxy;
        Settings = settings;
        Projects = new ProjectSettingsService(conversation.Workspaces);
        Usage = new UsageRepository(database);
        Rollup = new UsageRollup(database, zone);
        Clock = clock;

        ScheduleRepository = new ScheduledRepository(database);
        Schedules = new ScheduledTaskService(ScheduleRepository, clock, id => conversation.Workspaces.Get(id) is not null);
        Scheduler = new Scheduler(Schedules, ScheduleRepository, new ScheduledRunHost(conversation, Projects, settings, clock), clock,
            (message, error) => log.Warn(message, error));

        // 存储维护:清理 / 恢复 / 导入前不能有运行中的 Agent;备份目录取自设置页(每次现读)。
        Attachments = new AttachmentRepository(database, clock);
        var environment = new StorageEnvironment(database, dataRoot, clock, () => conversation.Registry.ActiveRunIds().Count > 0, () => settings.Current().Data.BackupDirectory, appVersion);
        Backups = new BackupService(environment);
        StorageRepo = new StorageRepository(database, conversation.Sessions, Attachments, clock);
        Storage = new StorageMaintenance(environment, StorageRepo, Attachments, Backups.Tokens, Backups);
        Transfer = new DataTransfer(environment, conversation.Sessions, conversation.Workspaces, Backups.Tokens);

        Gateway = new GatewayRuntime(settings, new Ncw.Agent.Storage.Credentials.CredentialStore(database, dataRoot, Ncw.Agent.Storage.Credentials.NoLegacySecrets.Instance), router, topics, conversation, providers, clock, appVersion);
        Composer = new Ncw.Agent.Services.Composer.ComposerService(new HostComposerSource(conversation), new HostComposerModel(conversation, clock), clock, message => log.Warn(message));
        Widgets = new Ncw.Agent.Services.Widgets.WidgetStateStore(new KvStore(database));

        Settings.Changed += (rev, current) =>
        {
            _proxy?.Invalidate();
            _topics.Publish(SettingsMethods.Topic.Name, SettingsEvents.Changed, new DeviceSettingsState(rev, ServiceMapping.ToDto(current, _proxyPassword.Has)), PayloadShapes.GetShape<DeviceSettingsState>());
        };
        Schedules.Changed += _ => PublishSchedules();
        Scheduler.RunChanged += _ => PublishSchedules();
    }

    public SettingsService Settings { get; }

    public ProjectSettingsService Projects { get; }

    public UsageRepository Usage { get; }

    public UsageRollup Rollup { get; }

    public TimeProvider Clock { get; }

    public ScheduledRepository ScheduleRepository { get; }

    public ScheduledTaskService Schedules { get; }

    public Scheduler Scheduler { get; }

    public AttachmentRepository Attachments { get; }

    public StorageRepository StorageRepo { get; }

    public StorageMaintenance Storage { get; }

    public BackupService Backups { get; }

    public DataTransfer Transfer { get; }

    public Ncw.Agent.Services.Composer.ComposerService Composer { get; }

    /// <summary>开放网关(默认关闭;开关是设置 <c>gateway.enabled</c>)。</summary>
    internal GatewayRuntime Gateway { get; }

    public Ncw.Agent.Services.Widgets.WidgetStateStore Widgets { get; }

    /// <summary>恢复备份 / 导入数据之后:库的内容整个换了。把设置、定时任务重新广播一份全量(控制端按版本收),项目列表由 <c>onDataReplaced</c> 回调去发。</summary>
    public void OnDataReplaced()
    {
        _topics.Publish(SettingsMethods.Topic.Name, SettingsEvents.Changed, SettingsState(), PayloadShapes.GetShape<DeviceSettingsState>());
        PublishSchedules();
        _onDataReplaced?.Invoke();
        Scheduler.Reschedule();
    }

    /// <summary>数据就绪、对话链路装好之后启动调度器(对账错过的、排好定时器)。</summary>
    public void Start()
    {
        Scheduler.Start();
        _ = Gateway.ReconcileAsync(); // 设置里开着就起(默认关闭)
    }

    // ───────────────────────── 设置 ─────────────────────────

    public DeviceSettingsState SettingsState()
    {
        var (rev, current) = Settings.Snapshot();
        return new DeviceSettingsState(rev, ServiceMapping.ToDto(current, _proxyPassword.Has));
    }

    /// <summary>代理密码刚改:设置的内容(<c>HasPassword</c>)变了而版本没变,所以重发一份同版本的全量(控制端按「不更旧」收)。</summary>
    public DeviceSettingsState SetProxyPassword(string password)
    {
        _proxyPassword.Set(password);
        _proxy?.Invalidate();
        var state = SettingsState();
        _topics.Publish(SettingsMethods.Topic.Name, SettingsEvents.Changed, state, PayloadShapes.GetShape<DeviceSettingsState>());
        return state;
    }

    // ───────────────────────── 定时任务 ─────────────────────────

    /// <summary>任务或运行变了 → 全量广播。版本号与推送在同一把锁里,所以版本序就是推送序;推的内容取此刻的真源,最后一条一定是终局。</summary>
    private void PublishSchedules()
    {
        lock (_scheduleGate)
        {
            var rev = ++_scheduleRev;
            _topics.Publish(SchedulesMethods.Topic.Name, SchedulesEvents.Changed, SchedulesStateAt(rev), PayloadShapes.GetShape<SchedulesTopicState>());
        }
    }

    /// <summary>
    /// <c>schedules</c> 主题的快照。★ 不取 <c>_scheduleGate</c>:主题表在持有它自己的锁时调快照函数,而发布路径是先 gate 后主题表的锁——两把锁反着取就是死锁。
    /// 先读版本号再读内容:内容至少和这个版本一样新,更新的变更随后作为事件到达(版本更大,同样的全量替换)。
    /// </summary>
    public SchedulesTopicState SchedulesState() => SchedulesStateAt(Interlocked.Read(ref _scheduleRev));

    private SchedulesTopicState SchedulesStateAt(long rev)
    {
        var runs = new Dictionary<string, ScheduledRunRow>(StringComparer.Ordinal);
        foreach (var run in ScheduleRepository.ActiveRuns())
        {
            runs[run.Id] = run;
        }

        foreach (var run in ScheduleRepository.ListRuns(limit: RecentRunsInTopic))
        {
            runs.TryAdd(run.Id, run);
        }

        return new SchedulesTopicState(
            rev,
            [.. Schedules.List().Select(ServiceMapping.ToDto)],
            [.. runs.Values.OrderByDescending(static r => r.ScheduledAt).ThenByDescending(static r => r.Id, StringComparer.Ordinal).Select(ServiceMapping.ToDto)]);
    }

    public async ValueTask DisposeAsync()
    {
        await Gateway.DisposeAsync().ConfigureAwait(false);
        await Scheduler.DisposeAsync().ConfigureAwait(false);
    }
}
