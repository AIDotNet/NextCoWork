using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Tools;

/// <summary>Grep 可以使用的原生后端(按 rg → git grep → grep 的顺序尝试,都不行就用托管实现)。</summary>
[Flags]
public enum GrepBackends
{
    None = 0,
    Rg = 1,
    Git = 2,
    Grep = 4,
    All = Rg | Git | Grep,
}

/// <summary>
/// 一个 Run 里工具能用到的一切(03 §1 的「会话层派生的 ToolContext 能力」):项目的执行环境、根目录、读取跟踪、变更记录、后台 Shell 注册表。
/// 由会话层构造并通过 <see cref="ExecutionToolContext.Enricher"/> 接到 <c>LoopHooks.EnrichToolContext</c> 上。
/// 能力不可用时直接不提供(<see cref="Shells"/> 为 <c>null</c>),不给一个一调就抛异常的桩(2.x ★)。
/// </summary>
public sealed class ToolRuntime
{
    private readonly object _probeGate = new();
    private Task<EnvironmentFacts>? _probe;

    public required IExecutionEnvironment Environment { get; init; }

    /// <summary>项目(工作区)id。需要「按工作区分区」的工具(浏览器的 profile 目录)用它;<c>null</c> = 没有登记的项目。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>项目根目录(环境里的绝对路径)。<c>null</c> = 没有绑定工作区:相对路径无处可算,Bash 无处可跑,绝对路径照样能用。</summary>
    public EnvPath? Root { get; init; }

    /// <summary>「本 Run 里读过哪些文件」。Write / Edit 的前置条件(2.x 的读取跟踪)。</summary>
    public ReadTracker Reads { get; init; } = new();

    /// <summary>改动记录(回复底部的审查卡与撤销的采集侧)。</summary>
    public ChangeRecorder Changes { get; init; } = new();

    /// <summary>后台 Shell 注册表。<c>null</c> = 没有这项能力:<c>run_in_background</c> 当场失败,BashOutput / KillShell 不下发。</summary>
    public IShellRegistry? Shells { get; init; }

    private readonly object _fenceGate = new();
    private EnvPath? _writeFence;

    /// <summary>
    /// 写入围栏(计划模式):非空时 Write / Edit 只允许改这一个文件。
    /// 一个计划跨多个 Run、而围栏在 Run 中间开关(<c>EnterPlanMode</c> / <c>ExitPlanMode</c>),所以它是可写的:Run 开始时由装配方设成会话当前计划的文件,
    /// 计划扩展在进入 / 退出时改它(同一个值要同步给 <c>PermissionRunState.PlanFence</c>)。写工具每次调用都现读。
    /// </summary>
    public EnvPath? WriteFence
    {
        get
        {
            lock (_fenceGate)
            {
                return _writeFence;
            }
        }
        set
        {
            lock (_fenceGate)
            {
                _writeFence = value;
            }
        }
    }

    /// <summary>Bash 完整输出的临时文件目录。<c>null</c> = 按环境默认(POSIX 是 <c>/tmp</c>,Windows 是 <c>%USERPROFILE%\AppData\Local\Temp</c>)。</summary>
    public EnvPath? TempDirectory { get; init; }

    /// <summary>允许使用的 Grep 原生后端。生产恒为 <see cref="GrepBackends.All"/>;测试用它固定后端。</summary>
    public GrepBackends GrepBackends { get; init; } = GrepBackends.All;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public EnvironmentId EnvironmentId => Environment.Id;

    public PathStyle PathStyle => Environment.Facts.PathStyle;

    /// <summary>大小写不敏感的文件系统(macOS / Windows):glob 匹配跟着文件系统走。</summary>
    public bool CaseInsensitiveFileSystem => Environment.Facts.Os != EnvironmentOs.Linux;

    /// <summary>探测环境里有哪些工具(rg / git / grep),每个运行时只探测一次。探测不挂调用方的取消令牌:结果要缓存,一次中止不该把「没装」写进缓存。</summary>
    internal Task<EnvironmentFacts> ProbeAsync()
    {
        lock (_probeGate)
        {
            return _probe ??= Environment.ProbeToolsAsync(CancellationToken.None);
        }
    }

    private EnvPath? _realRoot;

    /// <summary>
    /// 项目根的真实路径(macOS 的 <c>/var</c> → <c>/private/var</c>,或用户把 <c>~/code</c> 链到外置卷)。路径守卫返回的绝对路径都是 realpath 之后的形式,
    /// 所以「相对根显示」必须拿同样 realpath 过的根比,拿用户登记的原样根比会把根内的文件判到根外去。根暂时读不到(被删 / 断线)时返回登记的原样根,不缓存。
    /// </summary>
    internal async ValueTask<EnvPath?> RealRootAsync(CancellationToken ct)
    {
        if (Root is not { } root)
        {
            return null;
        }

        if (_realRoot is { } cached)
        {
            return cached;
        }

        try
        {
            var real = await Environment.Fs.RealPathAsync(root, ct).ConfigureAwait(false);
            _realRoot = real;
            return real;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return root;
        }
    }

    internal EnvPath ResolveTempDirectory()
    {
        if (TempDirectory is { } configured)
        {
            return configured;
        }

        var facts = Environment.Facts;
        return facts.Os == EnvironmentOs.Windows
            ? EnvPath.Parse(facts.Home, PathStyle.Windows).Join("AppData", "Local", "Temp")
            : EnvPath.Posix("/tmp");
    }
}

/// <summary>
/// 带执行环境的工具上下文:在循环层造的基础 <see cref="ToolContext"/> 之上加 <see cref="ToolRuntime"/>。
/// 会话层把 <see cref="Enricher"/> 交给 <c>LoopHooks.EnrichToolContext</c>。
/// </summary>
public sealed record ExecutionToolContext : ToolContext
{
    public ExecutionToolContext(ToolContext inner, ToolRuntime runtime)
        : base(inner)
    {
        Runtime = runtime;
    }

    public ToolRuntime Runtime { get; }

    public static Func<ToolContext, ToolContext> Enricher(ToolRuntime runtime) => ctx => new ExecutionToolContext(ctx, runtime);
}
