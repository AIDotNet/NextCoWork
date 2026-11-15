using System.Text;
using Jaunt;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 一个插件的 Jaunt 运行时:<b>独立的引擎和 realm</b>(插件之间互相看不到全局对象)、<c>JsProfile.Hardened</c>(冻结内建对象、禁用 <c>eval</c> / <c>Function</c>)、<see cref="JsLimits"/> 预算。
/// <list type="bullet">
/// <item><b>一个插件一把锁</b>:引擎同一时刻只有一次根执行,所以调用在插件内串行(跨插件并行)。拦截器 / 上下文提供者拿不到锁就按超时弃权。</item>
/// <item><b>崩溃隔离</b>:超预算(时间 / 指令数 / 内存 / 栈深 / 输出)或引擎自身故障 = 引擎丢弃 + <see cref="Faulted"/> 事件,<b>不抛进宿主</b>;插件自己 <c>throw</c> = 引擎仍可用,调用方拿到失败原因。</item>
/// <item><b>取消</b>:调用方取消(Run 被中止)会让引擎进入故障态(Jaunt 的语义),但这<b>不是插件的错</b>:下一次调用前自动重建(重新激活);累计分配超过预算 75% 时同样在调用之间重建。</item>
/// <item><b>退役</b>(<see cref="Retire"/>,禁用 / 卸载):立刻拒绝迟到的宿主调用、中断正在跑的执行、丢弃引擎。</item>
/// </list>
/// 只经 <see cref="IExecutionEnvironment"/> 读插件源码(AGENTS.md §5);只有这个项目允许 <c>new JsEngine</c>(AGENTS.md §6)。
/// </summary>
internal sealed class PluginRuntime : IDisposable
{
    private readonly PluginHost _host;
    private readonly IExecutionEnvironment _env;
    private readonly EnvPath _packageRoot;
    private readonly PluginRuntimeLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private JsEngine? _engine;
    private JsFunction? _dispatch;
    private bool _needsRebuild;
    private int _retired;

    public PluginRuntime(PluginHost host, IExecutionEnvironment env, EnvPath packageRoot, PluginRuntimeLimits limits)
    {
        _host = host;
        _env = env;
        _packageRoot = packageRoot;
        _limits = limits;
    }

    public PluginHost Host => _host;

    /// <summary>超出预算 / 引擎故障:插件进入 faulted(<c>reason</c> 见 <see cref="PluginRuntimeException.Reason"/>)。manager 订阅它标记状态、撤下贡献。</summary>
    public event Action<PluginRuntimeException>? Faulted;

    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    // ───────────────────────── 激活 ─────────────────────────

    /// <summary>
    /// 建引擎、跑前导、加载 <c>main</c>、调 <c>activate(context)</c>。失败抛 <see cref="PluginRuntimeException"/>(此时引擎已丢弃)。
    /// 拿不到锁 / 已退役也是抛。
    /// </summary>
    public async Task ActivateAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfRetired();
            await BuildAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsRetired && !ct.IsCancellationRequested)
        {
            throw new PluginRuntimeException(PluginFailureKind.NotActive, PluginDiagnosticKeys.ActivateFailed, "The plugin was disabled while it was activating.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfRetired()
    {
        if (IsRetired)
        {
            throw new PluginRuntimeException(PluginFailureKind.NotActive, PluginDiagnosticKeys.ActivateFailed, "The plugin is not active.");
        }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        DisposeEngine();
        _needsRebuild = false;
        // 重新激活之前清空上一轮注册的东西:activate 会再注册一遍
        _host.Commands.Clear();
        _host.Tools.Clear();
        _host.Interceptor = false;
        _host.ContextProvider = false;

        var main = _host.Manifest.Main;
        string source;
        try
        {
            var file = await _env.Paths.ResolveWithinAsync(_packageRoot, main, ct).ConfigureAwait(false);
            source = await _env.Fs.ReadTextAsync(file, _limits.MaxSourceBytes, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e)
        {
            throw new PluginRuntimeException(PluginFailureKind.Load, PluginDiagnosticKeys.MainMissing, $"The entry file \"{main}\" could not be read ({e.Code}).", inner: e);
        }

        var engine = new JsEngine(new JsEngineOptions
        {
            Profile = JsProfile.Hardened,
            Limits = new JsLimits
            {
                MaxInstructions = _limits.MaxInstructionsPerCall,
                MaxTrackedBytes = _limits.MaxTrackedBytes,
                MaxStackDepth = _limits.MaxStackDepth,
                MaxOutputBytes = _limits.MaxOutputBytes,
                MaxPendingJobs = _limits.MaxPendingJobs,
                MaxModules = 8,
                MaxStringLength = 32 * 1024 * 1024,
            },
            Clock = () => _host.Services.Clock.GetUtcNow(),
            CompileOptions = new JsCompileOptions { SourceName = _host.Id, MaxSourceLength = _limits.MaxSourceBytes },
            ModuleResolver = new JsDictionaryModuleResolver().Add("nextcowork", PluginPrelude.ModuleSource),
            UnhandledRejection = rejection => _host.Diagnostics.Add(new PluginDiagnostic(
                "promise", PluginDiagnosticKeys.UnhandledRejection, "A promise was rejected and nobody handled it: " + Clip(rejection.Reason, 500), "warn")),
        });
        _engine = engine;
        try
        {
            engine.SetValue("__ncw_id", _host.Id);
            engine.SetValue("__ncw_api", PluginApiVersion.Current);
            PluginBinder.Bind(engine, _host);
            engine.Evaluate(PluginPrelude.Source, ct);

            var timeout = _limits.ActivateTimeout;
            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loadCts.CancelAfter(timeout);
            JsModuleInstance instance;
            try
            {
                var module = JsCompiler.CompileModule(source, new JsCompileOptions { SourceName = main, MaxSourceLength = _limits.MaxSourceBytes }, loadCts.Token);
                instance = await engine.LoadModuleAsync(module, (IJsModuleResolver?)null, main, loadCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (loadCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw Budget("timeout", "The plugin took longer than its activation budget to load.");
            }

            var dispatch = engine.GetFunction("__ncw_dispatch");
                // 前导留下的内部入口从全局里摘掉:插件代码看不见 dispatch / 上下文对象之外的任何宿主内部
            engine.Evaluate("delete globalThis.__ncw_dispatch; delete globalThis.__ncw_ctx; delete globalThis.__ncw;");
            _dispatch = dispatch;

            _deactivate = instance.ExportNames.Contains("deactivate") ? instance.GetFunction("deactivate") : null;
            if (instance.ExportNames.Contains("activate"))
            {
                // 经 dispatch 进去:没 await 的宿主调用(模板里的 `void ncw.window.setStatusBarItem(...)`)在返回前被收齐,不会让引擎因「未汇合的异步操作」进入故障态
                var activate = instance.GetFunction("activate");
                await engine.InvokeAsync(dispatch, [JsValue.From("activate"), JsValue.From(""), JsValue.From(""), JsValue.From(""), activate.Value], Options(timeout), ct).ConfigureAwait(false);
            }

            _host.NotifyRegistrationsChanged();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = Classify(e, PluginDiagnosticKeys.ActivateFailed, "activating");
            DisposeEngine();
            throw failure;
        }
    }

    private JsFunction? _deactivate;

    // ───────────────────────── 调用 ─────────────────────────

    /// <summary>
    /// 在插件的引擎里跑一次调度(<c>command</c> / <c>tool</c> / <c>interceptor</c> / <c>context</c>),返回插件结果的 JSON 文本(没有返回值 = <c>null</c> 文本)。
    /// 调用方取消 → <see cref="OperationCanceledException"/>(引擎标记为下次重建);插件 throw / 超预算 / 不在线 / 忙 → <see cref="PluginRuntimeException"/>。
    /// </summary>
    public async Task<string> InvokeAsync(string kind, string name, string argsJson, string callId, InvocationScope? scope, TimeSpan timeout, TimeSpan queueTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        bool entered;
        try
        {
            entered = await _gate.WaitAsync(queueTimeout, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsRetired && !ct.IsCancellationRequested)
        {
            throw NotActive();
        }

        if (!entered)
        {
            throw new PluginRuntimeException(PluginFailureKind.Busy, PluginDiagnosticKeys.ActivateFailed, "The plugin is busy with another call.");
        }

        try
        {
            if (IsRetired)
            {
                throw NotActive();
            }

            if (_needsRebuild || _engine is null)
            {
                try
                {
                    await BuildAsync(linked.Token).ConfigureAwait(false);
                }
                catch (PluginRuntimeException e) when (e.Kind is PluginFailureKind.Budget or PluginFailureKind.Engine)
                {
                    Fault(e);
                    throw;
                }
            }

            _host.Scope = scope;
            _host.BeginInvocation();
            var engine = _engine!;
            try
            {
                var result = await engine.InvokeAsync(
                    _dispatch!, [JsValue.From(kind), JsValue.From(name), JsValue.From(argsJson), JsValue.From(callId)], Options(timeout), linked.Token).ConfigureAwait(false);
                var text = result.IsString ? result.AsString() : "null";
                if (engine.GetStatistics().TrackedBytes > _limits.MaxTrackedBytes / 4 * 3)
                {
                    _needsRebuild = true; // 累计分配接近预算:在调用之间重建,而不是等到某一次调用被它掐掉
                }

                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && IsRetired)
            {
                throw NotActive();
            }
            catch (OperationCanceledException)
            {
                // 调用方取消(Run 被停止):引擎已被 Jaunt 标记为故障,不是插件的错——下一次调用前重建
                _needsRebuild = true;
                throw;
            }
            catch (Exception e)
            {
                var failure = Classify(e, PluginDiagnosticKeys.Faulted, $"{kind} {name}");
                if (failure.Kind is PluginFailureKind.Budget or PluginFailureKind.Engine)
                {
                    Fault(failure);
                }
                else if (failure.Kind == PluginFailureKind.Timeout)
                {
                    _needsRebuild = true; // Jaunt 已把引擎标成故障:下一次调用前重建
                }

                throw failure;
            }
        }
        finally
        {
            _host.Scope = null;
            _host.CloseAllActions();
            _gate.Release();
        }
    }

    /// <summary>
    /// 休眠前 / 禁用时的收尾:调 <c>deactivate()</c>、释放 <c>context.subscriptions</c>。尽力而为(拿不到锁、超时都算了),随后退役。
    /// </summary>
    public async Task DeactivateAsync(TimeSpan grace)
    {
        if (!IsRetired && _engine is not null && _dispatch is not null && await _gate.WaitAsync(grace).ConfigureAwait(false))
        {
            try
            {
                if (!IsRetired && _engine is { IsFaulted: false } engine)
                {
                    using var cts = new CancellationTokenSource(grace);
                    try
                    {
                        if (_deactivate is { } deactivate)
                        {
                            await engine.InvokeAsync(_dispatch, [JsValue.From("deactivate"), JsValue.From(""), JsValue.From(""), JsValue.From(""), deactivate.Value], Options(grace), cts.Token).ConfigureAwait(false);
                        }

                        await engine.InvokeAsync(_dispatch, [JsValue.From("dispose"), JsValue.From(""), JsValue.From(""), JsValue.From("")], Options(grace), cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _host.Services.Log.Warn($"[plugin] {_host.Id}: deactivate failed", e);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        Retire();
    }

    /// <summary>退役:迟到的宿主调用一律拒绝,正在跑的执行被中断,引擎丢弃。幂等,可从任意线程调。</summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
        {
            return;
        }

        _host.Retired = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // JsEngine.Dispose 可以从任意线程调:正在跑的执行在下一个检查点以 ObjectDisposedException 结束
        DisposeEngine();
        _host.CloseAllActions();
    }

    private void DisposeEngine()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        _dispatch = null;
        _deactivate = null;
        try
        {
            engine?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _host.Services.Log.Warn($"[plugin] {_host.Id}: disposing the engine failed", e);
        }
    }

    public void Dispose()
    {
        Retire();
        _lifetime.Dispose();
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private JsExecutionOptions Options(TimeSpan timeout) => new() { Mode = JsExecutionMode.Async, Timeout = timeout, MaxInstructions = _limits.MaxInstructionsPerCall };

    private static PluginRuntimeException NotActive() => new(PluginFailureKind.NotActive, PluginDiagnosticKeys.Faulted, "The plugin is not active.");

    private static PluginRuntimeException Budget(string reason, string message) => new(PluginFailureKind.Budget, PluginDiagnosticKeys.Faulted, message, reason);

    private void Fault(PluginRuntimeException failure)
    {
        Retire();
        Faulted?.Invoke(failure);
    }

    /// <summary>引擎异常 → 分类。★ 插件自己 throw 的(<see cref="JsException"/>)不是故障;超预算、引擎故障才是。</summary>
    private PluginRuntimeException Classify(Exception e, string key, string what) => e switch
    {
        PluginRuntimeException p => p,
        // 超时而插件当时在等宿主(IO / 用户 / 定时器):不是它在空转,不算故障
        JsLimitExceededException { Kind: JsLimitKind.Timeout } when _host.InFlight > 0 || _host.Abandoned > 0 => new PluginRuntimeException(
            PluginFailureKind.Timeout, key, $"The plugin did not finish within its time limit while {what} (it was waiting on the host)."),
        JsException js => new PluginRuntimeException(PluginFailureKind.Thrown, key, js.ErrorMessage.Length > 0 ? js.ErrorMessage : js.Name, inner: js),
        JsLimitExceededException limit => Budget(
            limit.Kind switch
            {
                JsLimitKind.Timeout => "timeout",
                JsLimitKind.Instructions => "instructions",
                JsLimitKind.TrackedBytes => "memory",
                JsLimitKind.StackDepth => "stack",
                JsLimitKind.OutputBytes => "output",
                JsLimitKind.PendingJobs => "jobs",
                _ => "modules",
            },
            $"The plugin exceeded its execution budget while {what} ({limit.Kind})."),
        JsEngineFaultedException { Code: "UnjoinedAsyncWork" } unjoined => new PluginRuntimeException(
            PluginFailureKind.Load, PluginDiagnosticKeys.ActivateFailed, "The plugin started asynchronous work at module level and did not wait for it; start work inside activate() instead.", inner: unjoined),
        JsCompileException compile => new PluginRuntimeException(PluginFailureKind.Load, PluginDiagnosticKeys.CompileFailed, $"The plugin code could not be compiled ({compile.Code}) at {compile.Line}:{compile.Column}: {Clip(compile.Description, 300)}", inner: compile),
        ObjectDisposedException => new PluginRuntimeException(PluginFailureKind.NotActive, PluginDiagnosticKeys.Faulted, "The plugin is not active.", inner: e),
        _ => new PluginRuntimeException(PluginFailureKind.Engine, key, $"The plugin engine failed while {what}. [{e.GetType().Name}: {e.Message}]", inner: e),
    };

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
