using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Plugins;

/// <summary>宿主 API 调用失败的业务原因:<see cref="Code"/> 是插件里 <c>Error.code</c> 看得到的稳定值(<c>invalid_argument</c> / <c>rejected</c> / <c>permission_denied</c> / <c>not_found</c> / <c>unavailable</c> / <c>internal_error</c>)。</summary>
internal sealed class HostCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static HostCallException Invalid(string message) => new("invalid_argument", message);

    public static HostCallException Rejected(string message) => new("rejected", message);
}

/// <summary>一次调用(命令 / 工具 / 钩子)在宿主侧的作用域:它在哪个项目、哪个执行环境里发生。每次调用在持有该插件的引擎锁时设置,所以同一时刻一个插件只有一个作用域。</summary>
internal sealed class InvocationScope
{
    public string? WorkspaceId { get; init; }

    public string? WorkspaceName { get; init; }

    public IExecutionEnvironment? Environment { get; init; }

    public EnvPath? Root { get; init; }

    public ToolRuntime? Tools { get; init; }

    public RunId? Run { get; init; }

    public FileMutationQueue? FileMutations { get; init; }

    public string? CallId { get; init; }

    public PluginEnvironmentRef? EnvironmentRef => Environment is null
        ? null
        : new PluginEnvironmentRef(Environment.Id.DeviceId, Environment.Id.Environment, Environment.DisplayName, Environment.Kind == EnvironmentKind.Local ? "local" : "ssh");
}


/// <summary>插件宿主需要的外部能力的聚合(由 <see cref="PluginManager"/> 造,每个插件一个 <see cref="PluginHost"/> 共用)。</summary>
internal sealed class PluginServices
{
    public required IPluginHttp Http { get; init; }

    public required IPluginSecrets Secrets { get; init; }

    public required IPluginWorkspaces Workspaces { get; init; }

    public required TimeProvider Clock { get; init; }

    public required ILoopDiagnostics Log { get; init; }

    public required PluginRuntimeLimits Limits { get; init; }

    public required PluginAppInfo AppInfo { get; init; }

    /// <summary>读写某个插件的私有 kv(整张 map;调用方按插件锁)。</summary>
    public required Func<string, PluginKvBucket> Kv { get; init; }

    public required Func<string, IReadOnlyDictionary<string, PluginConfigValue>> Configuration { get; init; }

    public required Func<PluginInteraction, CancellationToken, Task<PluginInteractionReply?>> Ask { get; init; }

    public required Action<string, IReadOnlyList<string>> Grant { get; init; }

    public required Action<string, IReadOnlyList<string>> Revoke { get; init; }
}

/// <summary>
/// 宿主调用的计数(超时分类用:超时那一刻插件在等宿主 = 不是它在空转,不算故障)。★ 计数<b>属于一次调用</b>(<c>InvokeAsync</c>),不属于宿主:
/// 上一次调用留下的、被「执行结束 / 超时」掐断的宿主调用(比如还在睡的 <c>setTimeout</c>)在取消回调里稍后才收尾——CPU 吃紧时这个「稍后」可能落在下一次调用开始之后。
/// 以前计数挂在宿主上,迟到的收尾把下一次调用里真正的死循环也记成「在等宿主」:超时被判成不算故障,死循环的插件没有被标成 faulted
/// (Plugins 的「慢 / 坏拦截器」用例整程序集并发时失败的根因)。现在每次调用一份新计数,宿主调用进入时抓住当时的那一份、收尾只动自己那一份。
/// </summary>
internal sealed class CallAccounting
{
    internal sealed class Counters
    {
        internal int InFlight;

        internal int Abandoned;
    }

    private Counters _current = new();

    public int InFlight => Volatile.Read(ref Volatile.Read(ref _current).InFlight);

    public int Abandoned => Volatile.Read(ref Volatile.Read(ref _current).Abandoned);

    /// <summary>宿主调用进入:记在当前这次调用的计数上,返回那一份(收尾时原样交回)。</summary>
    public Counters Enter()
    {
        var counters = Volatile.Read(ref _current);
        Interlocked.Increment(ref counters.InFlight);
        return counters;
    }

    public static void Exit(Counters counters) => Interlocked.Decrement(ref counters.InFlight);

    public static void NoteAbandoned(Counters counters) => Interlocked.Increment(ref counters.Abandoned);

    /// <summary>新的一次调用开始:换一份全新的计数(上一次调用的迟到收尾落在旧的那份上,碰不到这一次)。</summary>
    public void BeginInvocation() => Volatile.Write(ref _current, new Counters());
}

/// <summary>
/// 一个插件在宿主侧的状态(绑定读写它):清单、<b>实时</b>的授权状态(撤权立刻生效,即使引擎还在跑)、当前调用的作用域、插件注册进来的东西、实时卡片的动作通道。
/// </summary>
internal sealed class PluginHost(PluginManifest manifest, Func<PluginPermissionState> permissions, PluginServices services, PluginActivityLog activity, PluginDiagnostics diagnostics)
{
    private readonly ConcurrentDictionary<string, Channel<string?>> _actions = new(StringComparer.Ordinal);

    public PluginManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;

    public PluginServices Services { get; } = services;

    public PluginActivityLog Activity { get; } = activity;

    public PluginDiagnostics Diagnostics { get; } = diagnostics;

    public PluginPermissionState Permissions => permissions();

    /// <summary>引擎已被丢弃(禁用 / 卸载 / 故障)之后,迟到的宿主调用一律拒绝。</summary>
    public volatile bool Retired;

    /// <summary>宿主调用的计数(按调用分账,见 <see cref="CallAccounting"/>)。</summary>
    internal CallAccounting Calls { get; } = new();

    private CancellationTokenSource _sleeps = new();

    /// <summary>当前调用里正在进行的异步宿主调用数。超时发生时 &gt; 0 = 插件在等 IO / 用户(不是它在空转),不算故障。</summary>
    public int InFlight => Calls.InFlight;

    /// <summary>当前调用里被「执行结束 / 超时」掐断的宿主调用数。超时发生时引擎会先取消还在等的宿主调用,等异常传到分类处 <see cref="InFlight"/> 已经归零,所以另记一笔。</summary>
    public int Abandoned => Calls.Abandoned;

    /// <summary>新的一次调用开始:换一份全新的计数。</summary>
    public void BeginInvocation() => Calls.BeginInvocation();

    /// <summary>定时器(<c>setTimeout</c>)的睡眠令牌:一次调用结束时取消所有没触发的定时器。</summary>
    public CancellationToken SleepToken => _sleeps.Token;

    public void CancelSleeps()
    {
        var old = Interlocked.Exchange(ref _sleeps, new CancellationTokenSource());
        try
        {
            old.Cancel();
        }
        finally
        {
            old.Dispose();
        }
    }

    public InvocationScope? Scope { get; set; }

    // ───── 插件注册进来的(manifest 声明过才收)─────

    public ConcurrentDictionary<string, bool> Commands { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, PluginToolDeclaration> Tools { get; } = new(StringComparer.Ordinal);

    public volatile bool Interceptor;

    /// <summary>进行中的工具调用的进度出口(<c>progress(update)</c> → 界面上的实时进度 / 卡片;易失,不进记录)。</summary>
    public ConcurrentDictionary<string, IProgress<Ncw.Agent.Kernel.Tools.ToolProgress>> Progress { get; } = new(StringComparer.Ordinal);

    public volatile bool ContextProvider;

    /// <summary>注册集合变了(新增 / 注销工具、命令、钩子)。manager 据此重新同步工具注册表和对外状态。</summary>
    public event Action? RegistrationsChanged;

    public void NotifyRegistrationsChanged() => RegistrationsChanged?.Invoke();

    /// <summary>实时卡片上的按钮动作:每个进行中的工具调用一条通道;工具返回 / 中止时关闭,泵里的 <c>nextAction</c> 随之拿到 null。</summary>
    public Channel<string?> OpenActions(string callId)
    {
        var channel = Channel.CreateBounded<string?>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _actions[callId] = channel;
        return channel;
    }

    public void CloseActions(string callId)
    {
        if (_actions.TryRemove(callId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public bool DeliverAction(string callId, string actionJson) =>
        _actions.TryGetValue(callId, out var channel) && channel.Writer.TryWrite(actionJson);

    public ChannelReader<string?>? ActionsOf(string callId) => _actions.TryGetValue(callId, out var channel) ? channel.Reader : null;

    public void CloseAllActions()
    {
        foreach (var callId in _actions.Keys.ToList())
        {
            CloseActions(callId);
        }
    }
}

/// <summary>插件声明的一个工具(<c>registerTool</c> 里的描述)。<see cref="InputSchemaJson"/> 原样下发给上游,宿主不解释。</summary>
internal sealed record PluginToolDeclaration(string Name, string Description, string InputSchemaJson, bool ReadOnly, bool Destructive, bool NeedsNetwork, bool Interactive);

/// <summary>宿主方法的返回:数据(进信封的 <c>data</c>)+ 一句不含参数原文的摘要(进活动日志)。</summary>
internal readonly record struct HostResult(JsonNode? Data, string Summary);

internal delegate ValueTask<HostResult> HostHandler(HostCall call, JsonElement args);

/// <summary>方法表里的一行:名字、所需能力(null = 不需要,但仍然要过白名单)、处理函数。<b>只有这张表里的方法才会被绑定进引擎</b>。</summary>
internal sealed record HostMethod(string Name, string? Permission, HostHandler Handler);

/// <summary>一次宿主调用的上下文。<see cref="Token"/> 在调用被中止 / 超时 / 插件被禁用时触发。</summary>
internal sealed class HostCall(PluginHost host, InvocationScope? scope, CancellationToken token)
{
    public PluginHost Host { get; } = host;

    public InvocationScope? Scope { get; } = scope;

    public CancellationToken Token { get; } = token;

    public PluginServices Services => Host.Services;

    public InvocationScope RequireScope() => Scope ?? throw HostCallException.Rejected("no workspace is open");
}

/// <summary>最近的宿主调用(环形缓冲,按插件分桶;2.x <c>diagnostics.ts</c>)。★ 不记参数原文(<c>writeFile</c> 的参数是整个文件内容,<c>net.fetch</c> 的参数里可能有 token)。</summary>
internal sealed class PluginActivityLog(int capacity = 500)
{
    private readonly object _gate = new();
    private readonly List<PluginActivity> _ring = [];

    public void Record(PluginActivity entry)
    {
        lock (_gate)
        {
            _ring.Add(entry);
            if (_ring.Count > capacity)
            {
                _ring.RemoveRange(0, _ring.Count - capacity);
            }
        }
    }

    /// <summary>最近的在前(面板从上往下读)。</summary>
    public IReadOnlyList<PluginActivity> Recent(string? pluginId)
    {
        lock (_gate)
        {
            return [.. Enumerable.Reverse(_ring).Where(a => pluginId is null || a.PluginId == pluginId)];
        }
    }

    public void Clear(string pluginId)
    {
        lock (_gate)
        {
            _ring.RemoveAll(a => a.PluginId == pluginId);
        }
    }
}

/// <summary>一个插件的诊断(最近 50 条,相邻重复折叠):运行期的权限拒绝、故障、未声明的注册都落在这里,插件详情页展示。</summary>
internal sealed class PluginDiagnostics(Action changed)
{
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly List<PluginDiagnostic> _runtime = [];

    public void Add(PluginDiagnostic diagnostic)
    {
        lock (_gate)
        {
            // 相邻重复折叠:一个在循环里反复调被拒方法的插件不该刷满诊断、也不该每次都推一份全量状态。比较的是内容(Params 是字典,记录的相等比的是引用)
            if (_runtime.Count > 0 && _runtime[^1] is { } last && last.Path == diagnostic.Path && last.Key == diagnostic.Key && last.Message == diagnostic.Message && last.Level == diagnostic.Level)
            {
                return;
            }

            _runtime.Add(diagnostic);
            if (_runtime.Count > Capacity)
            {
                _runtime.RemoveAt(0);
            }
        }

        changed();
    }

    public IReadOnlyList<PluginDiagnostic> Snapshot()
    {
        lock (_gate)
        {
            return [.. _runtime];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _runtime.Clear();
        }
    }
}
