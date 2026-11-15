using System.Collections.Concurrent;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

/// <summary>确保环境就绪时怎么对待「还没连接」。</summary>
internal enum ConnectMode
{
    /// <summary>不发起连接:环境没连着就是可重试的 <c>unavailable</c>(viewer:只读访客不能让设备去连内网主机,也不能替用户在别的控制端上弹确认框)。</summary>
    None,

    /// <summary>惰性连接,首次指纹 / 凭据以交互出现,等任一控制端回答。</summary>
    Interactive,

    /// <summary>惰性连接,但绝不问人(定时任务):已钉扎的指纹 + 已保存的凭据能连上就连,要问人就失败。</summary>
    NonInteractive,
}

/// <summary>
/// 一台设备上的执行环境服务(06):环境清单与配置(<c>EnvironmentRegistry</c> + 2.x 的 <c>connection_profiles</c> 表)、SSH 连接的生命周期、
/// 交互(指纹 / 凭据,经交互闸门)、<c>environments</c> 主题,以及项目 → 执行环境的解析(<see cref="IProjectEnvironments"/> 的真实实现:本机 + SSH)。
/// <list type="bullet">
/// <item><b>惰性连接</b>:SSH 环境第一次被用到(或用户点「连接」)才连;断线重连由 <see cref="ReconnectPolicy"/> 驱动(环境实例自己做)。</item>
/// <item><b>空闲回收</b>(2.x <c>EnvironmentManager</c> 的行为):没有 Run / 终端 / 后台 Shell 在用(<see cref="Hold"/> + <see cref="AddBusyProbe"/>)、
/// 也没被访问过(<see cref="Touch"/>)超过 <see cref="EnvironmentHostOptions.IdleTimeout"/>(默认 60 秒)就断开回收;配置和已保存的凭据保留,下次使用惰性重连。</item>
/// <item><b>状态</b>:环境状态变化 / 配置变化都推 <c>environments</c> 主题(全量 + 单调 Rev,首帧与事件同一个类型)。状态只由各环境实例回答,这里不另存「连没连上」。</item>
/// <item><b>凭据</b>:密码、口令存 NCK1,明文只在这里;DTO / 事件 / 错误 / 日志里永远没有。</item>
/// </list>
/// </summary>
internal sealed partial class EnvironmentService : IProjectEnvironments, IAsyncDisposable
{
    private static readonly AsyncLocal<bool> NonInteractiveFlag = new();

    private readonly string _deviceId;
    private readonly TimeProvider _clock;
    private readonly TopicHub _topics;
    private readonly ILoopDiagnostics _log;
    private readonly ConnectionProfileRepository _repository;
    private readonly WorkspaceRepository _workspaces;
    private readonly KvStore _kv;
    private readonly ConnectionSecrets _secrets;
    private readonly StoredHostKeyStore _hostKeys;
    private readonly SshInteractionBroker _broker;
    private readonly EnvironmentHostOptions _options;
    private readonly IIdGenerator _ids;
    private readonly EnvironmentRegistry _registry;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ConnectionView> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _holds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EnvironmentErrorCode> _lastErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HostKeyMismatchDto> _mismatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _latencies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _primaryHosts = new(StringComparer.Ordinal);
    private readonly List<Func<string, bool>> _busyProbes = [];
    private readonly Lock _publishLock = new();
    private readonly ITimer? _sweeper;
    private readonly SshEnvironmentOptions _sshOptions;
    private long _rev;
    private int _disposed;

    public EnvironmentService(
        AgentDatabase database, string deviceId, string dataRoot, TimeProvider clock, AgentServices services, TopicHub topics,
        InteractionGate gate, IIdGenerator ids, ILoopDiagnostics log, WorkspaceRepository workspaces)
    {
        _deviceId = deviceId;
        _clock = clock;
        _topics = topics;
        _log = log;
        _ids = ids;
        _options = services.SshEnvironments ?? new EnvironmentHostOptions();
        _repository = new ConnectionProfileRepository(database);
        _workspaces = workspaces;
        _kv = new KvStore(database);
        _secrets = new ConnectionSecrets(new CredentialStore(database, dataRoot, NoLegacySecrets.Instance));
        _hostKeys = new StoredHostKeyStore(_kv);
        _broker = new SshInteractionBroker(gate, ids);
        _sshOptions = BuildSshOptions(_options);

        _registry = new EnvironmentRegistry(deviceId, new EnvironmentRegistryOptions
        {
            // 本机 PTY 由 Ncw.Agent.Terminal 注入(06 §9.4):终端、工具、审查共用这一个本机环境实例
            Local = new LocalEnvironmentOptions { TerminalFactory = Ncw.Agent.Terminal.LocalTerminals.CreateFactory() },
            SshFactory = CreateSsh,
        });
        var descriptors = new List<EnvironmentDescriptor>();
        foreach (var record in _repository.List())
        {
            if (ConnectionView.From(record) is { } view)
            {
                _views[view.Id] = view;
                descriptors.Add(Describe(view));
            }
        }

        _registry.Load(descriptors, SafeKv(DefaultKey));
        _registry.Changed += (_, e) =>
        {
            if (e.Change == EnvironmentRegistryChange.DefaultChanged)
            {
                PersistDefault();
            }

            Publish();
        };
        _registry.EnvironmentStatusChanged += OnEnvironmentStatus;
        if (_options.IdleTimeout > TimeSpan.Zero)
        {
            var period = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(50).Ticks, _options.IdleTimeout.Ticks / 4));
            _sweeper = _clock.CreateTimer(_ => _ = SweepAsync(), null, period, period);
        }
    }

    public const string DefaultKey = "v3.environments.default";

    /// <summary>设备上的 <c>local</c> 环境(永远在)。</summary>
    public IExecutionEnvironment Local => _registry.Get(EnvironmentId.LocalName);

    /// <summary>当前是否在非交互模式(定时任务这类没人在场的调用):提问一律取消,首次指纹一律拒绝。</summary>
    public static bool IsNonInteractive => NonInteractiveFlag.Value;

    /// <summary>进入非交互模式直到释放(沿异步调用链生效)。</summary>
    public static IDisposable NonInteractiveScope()
    {
        var previous = NonInteractiveFlag.Value;
        NonInteractiveFlag.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => NonInteractiveFlag.Value = previous;
    }

    // ───────────────────────── 构造辅助 ─────────────────────────

    private static SshEnvironmentOptions BuildSshOptions(EnvironmentHostOptions options)
    {
        var baseOptions = options.Ssh ?? new SshEnvironmentOptions();
        var agent = baseOptions.Agent ?? (options.DiscoverSshAgent ? SshAgentClient.Discover() : null);
        return ReferenceEquals(agent, baseOptions.Agent)
            ? baseOptions
            : new SshEnvironmentOptions
            {
                Reconnect = baseOptions.Reconnect,
                ReadyWait = baseOptions.ReadyWait,
                KillGrace = baseOptions.KillGrace,
                LocalUser = baseOptions.LocalUser,
                Agent = agent,
                Delay = baseOptions.Delay,
            };
    }

    private EnvironmentDescriptor Describe(ConnectionView view)
    {
        string? proxyPassword = null;
        if (view.Proxy is not null)
        {
            try
            {
                proxyPassword = _secrets.Get(ConnectionSecrets.ProxyPasswordRef(view.Id));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // 主密钥读不出来:没有代理密码,连接时会因为认证失败而失败,不会因此崩溃
            }
        }

        return new EnvironmentDescriptor(view.Id, view.Name, EnvironmentKind.Ssh, EnvironmentProfiles.ToProfile(view, proxyPassword));
    }

    private IExecutionEnvironment CreateSsh(EnvironmentId id, EnvironmentDescriptor descriptor)
    {
        var callbacks = new SshCallbacks(
            _broker, _secrets, _deviceId, id.Environment, descriptor.Name, () => PrimaryHostName(id.Environment, descriptor.Ssh!),
            mismatch => _mismatches[id.Environment] = mismatch, () => NonInteractiveFlag.Value);
        return new SshEnvironment(id, descriptor.Ssh! with { Name = descriptor.Name }, ServicesFor(callbacks), _sshOptions);
    }

    private SshEnvironmentServices ServicesFor(SshCallbacks callbacks) => new()
    {
        HostKeys = _hostKeys,
        HostKeyCallback = callbacks,
        Credentials = callbacks,
        ConfigFiles = _options.ConfigFiles ?? new PhysicalSshConfigFiles(),
    };

    /// <summary>目标主机(最后一跳)的钉扎名:已保存的密码只对它有效,跳板机的密码单独存。解析不出来(别名配置读不了)时为 null。</summary>
    private string? PrimaryHostName(string environmentId, SshEnvironmentProfile profile) =>
        _primaryHosts.GetOrAdd(environmentId, _ => ResolvePrimary(profile));

    private string? ResolvePrimary(SshEnvironmentProfile profile)
    {
        try
        {
            var files = _options.ConfigFiles ?? new PhysicalSshConfigFiles();
            var config = profile.Target is SshTarget.ConfigAlias { ConfigFile: { Length: > 0 } file } ? SshConfig.Load(files, file) : SshConfig.Load(files);
            return SshRouteResolver.Resolve(profile.Target, config, _sshOptions.LocalUser ?? Environment.UserName)[^1].HostKeyName;
        }
        catch (EnvironmentException)
        {
            return null;
        }
    }

    private string? SafeKv(string key)
    {
        try
        {
            return _kv.Get(key);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or StorageException)
        {
            return null;
        }
    }

    // ───────────────────────── 租约 / 空闲回收 ─────────────────────────

    /// <summary>
    /// 租约(2.x 的 <c>acquire</c> / <c>retain</c>):持有期间环境不会被空闲回收。Run 创建时取、结束时放。本机环境(和不认识的 id)返回一个什么都不做的句柄。
    /// </summary>
    public IDisposable Hold(string? environmentId)
    {
        if (environmentId is null or EnvironmentId.LocalName)
        {
            return NoHold.Instance;
        }

        lock (_lock)
        {
            _holds[environmentId] = _holds.GetValueOrDefault(environmentId) + 1;
        }

        Touch(environmentId);
        return new Lease(this, environmentId);
    }

    private void Release(string environmentId)
    {
        lock (_lock)
        {
            if (_holds.TryGetValue(environmentId, out var count))
            {
                if (count <= 1)
                {
                    _holds.Remove(environmentId);
                }
                else
                {
                    _holds[environmentId] = count - 1;
                }
            }
        }

        Touch(environmentId);
    }

    private sealed class Lease(EnvironmentService owner, string id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(id);
            }
        }
    }

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>登记一个「这个环境有没有活」的探针(终端、后台 Shell 这些不是租约持有者):任何一个返回真,环境就不会被空闲回收。</summary>
    public void AddBusyProbe(Func<string, bool> probe)
    {
        lock (_lock)
        {
            _busyProbes.Add(probe);
        }
    }

    /// <summary>记一次访问:空闲计时从现在重新开始。</summary>
    public void Touch(string environmentId)
    {
        lock (_lock)
        {
            _lastUsed[environmentId] = _clock.GetTimestamp();
        }
    }

    private bool IsBusy(string environmentId)
    {
        Func<string, bool>[] probes;
        lock (_lock)
        {
            if (_holds.GetValueOrDefault(environmentId) > 0)
            {
                return true;
            }

            probes = [.. _busyProbes];
        }

        return probes.Any(probe =>
        {
            try
            {
                return probe(environmentId);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return true; // 探针坏了:宁可不回收
            }
        });
    }

    private async Task SweepAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            foreach (var descriptor in _registry.List().Where(static d => d.Kind == EnvironmentKind.Ssh))
            {
                if (_registry.TryGetInstance(descriptor.Id) is not { State: EnvironmentState.Ready })
                {
                    continue;
                }

                long last;
                lock (_lock)
                {
                    last = _lastUsed.GetValueOrDefault(descriptor.Id);
                }

                if (_clock.GetElapsedTime(last) < _options.IdleTimeout || IsBusy(descriptor.Id))
                {
                    continue;
                }

                if (await _registry.DisconnectAsync(descriptor.Id).ConfigureAwait(false))
                {
                    ClearRuntime(descriptor.Id);
                    Publish();
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.Warn("Recycling idle SSH environments failed.", e);
        }
    }

    // ───────────────────────── 状态与发布 ─────────────────────────

    private void OnEnvironmentStatus(object? sender, EnvironmentStatusChangedEventArgs e)
    {
        var id = e.Id.Environment;
        switch (e.State)
        {
            case EnvironmentState.Failed when e.Error is { } code:
                _lastErrors[id] = code;
                break;
            case EnvironmentState.Failed:
                _lastErrors[id] = EnvironmentErrorCode.Disconnected;
                break;
            case EnvironmentState.Ready:
                _lastErrors.TryRemove(id, out _);
                _mismatches.TryRemove(id, out _);
                break;
            case EnvironmentState.Connecting:
                _lastErrors.TryRemove(id, out _);
                _mismatches.TryRemove(id, out _);
                break;
        }

        Publish();
        if (e.State == EnvironmentState.Ready && sender is IExecutionEnvironment environment)
        {
            _ = MeasureLatencyAsync(environment);
        }
    }

    /// <summary>连接成功后测一次往返(SFTP stat 一个确定存在的目录)。失败不影响连接,只是没有延迟数据。</summary>
    private async Task MeasureLatencyAsync(IExecutionEnvironment environment)
    {
        try
        {
            var home = EnvPath.Parse(environment.Facts.Home, environment.Facts.PathStyle);
            var start = _clock.GetTimestamp();
            await environment.Fs.StatAsync(home, CancellationToken.None).ConfigureAwait(false);
            _latencies[environment.Id.Environment] = (long)_clock.GetElapsedTime(start).TotalMilliseconds;
            Publish();
        }
        catch (Exception e) when (e is EnvironmentException or ObjectDisposedException)
        {
        }
    }

    private void ClearRuntime(string id)
    {
        _lastErrors.TryRemove(id, out _);
        _mismatches.TryRemove(id, out _);
        _latencies.TryRemove(id, out _);
    }

    /// <summary>当前全量状态(首帧、<c>environments.list</c>)。<b>先读 Rev 再读内容</b>:内容只会比 Rev 新,不会旧;不取发布锁(主题表持锁调快照函数,发布路径是先发布锁后主题锁)。</summary>
    public EnvironmentsTopicState State()
    {
        var rev = Volatile.Read(ref _rev);
        return BuildState(rev);
    }

    /// <summary>发布当前全量状态(Rev 加一)。数据就绪后补发首帧、配置 / 状态变化时调用。</summary>
    public void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_publishLock)
        {
            var state = BuildState(++_rev);
            _topics.Publish(EnvironmentsMethods.Topic.Name, EnvironmentsEvents.Changed, state, PayloadShapes.GetShape<EnvironmentsTopicState>());
        }
    }

    private EnvironmentsTopicState BuildState(long rev)
    {
        var defaultId = _registry.DefaultId;
        var list = _registry.List();
        return new EnvironmentsTopicState(rev, defaultId, [.. list.Select(d => ToDto(d, defaultId))]);
    }

    public EnvironmentDto Dto(string id)
    {
        var descriptor = _registry.Find(id) ?? throw EnvironmentErrors.NotFound(id);
        return ToDto(descriptor, _registry.DefaultId);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_sweeper is not null)
        {
            await _sweeper.DisposeAsync().ConfigureAwait(false);
        }

        await _registry.DisposeAsync().ConfigureAwait(false);
    }
}
