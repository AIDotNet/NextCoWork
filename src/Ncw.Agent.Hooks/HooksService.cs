using System.Collections.Concurrent;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Hooks;

/// <summary>项目的落点:执行环境 + 根目录(执行环境里的绝对路径)。服务层不认识 Host 的项目登记,经注入的解析函数拿。</summary>
public sealed record HookProjectTarget(IExecutionEnvironment Environment, EnvPath Root);

/// <summary>构造参数。环境解析、配置存放、判定器、时钟、ID 都是注入的小接口 / 函数,服务层不引用 Host 类型。</summary>
public sealed class HooksServiceOptions
{
    /// <summary>设备本机环境:全局设置文件在它上面(全局钩子的<b>定义</b>在设备本机;但钩子<b>执行</b>在项目的执行环境里)。</summary>
    public required IExecutionEnvironment GlobalEnvironment { get; init; }

    /// <summary>全局设置文件(2.x 的 <c>&lt;profile 根&gt;/settings.json</c>)。</summary>
    public required EnvPath GlobalSettingsPath { get; init; }

    /// <summary>
    /// 工作区 id → 项目落点;null = 没有这个项目。环境暂时不可用(SSH 没连上)时抛异常:列表 / 保存原样向上抛,
    /// 运行期的各个事件处理(Run 里)把它当成「没有项目钩子」并记诊断(失败放行)。
    /// </summary>
    public required Func<string, CancellationToken, ValueTask<HookProjectTarget?>> ResolveProject { get; init; }

    public IHookEvaluator? Evaluator { get; init; }

    public IIdGenerator? Ids { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;
}

/// <summary>保存时的环境变量一行:<see cref="Value"/> 为 null = 沿用这条钩子里同名变量已有的值(值不上线,所以编辑时控制端手里没有它)。</summary>
public sealed record HookEnvEntry(string Name, string? Value);

/// <param name="Hook">要保存的定义;<c>Id</c> 为空 = 新建(服务铸一个)。它的 <c>Env</c> 被忽略,环境变量只看 <paramref name="Env"/>。</param>
/// <param name="Env">null = 不动(新建 = 没有;更新 = 保持原样);非 null = 恰好这些变量。</param>
public sealed record HookSaveRequest(HookScope Scope, string? WorkspaceId, HookDefinition Hook, IReadOnlyList<HookEnvEntry>? Env = null);

/// <summary>试运行:跑的是此刻的<b>草稿</b>,不读磁盘上那一条(失败模式——脚本路径、shell 语法、超时——只有真跑一次才暴露,用户不该先保存一条自己还不确定的钩子)。</summary>
public sealed record HookTestRequest(HookScope Scope, string? WorkspaceId, HookDefinition Hook, IReadOnlyList<HookEnvEntry>? Env = null, string? ExistingId = null);

/// <summary>一次事件触发需要的上下文(Run 里的各个扩展点 / 权限链填)。</summary>
public sealed record HookEventRequest(
    string Event, IExecutionEnvironment Environment, EnvPath? Root, string SessionId, string RunId, string WorkspaceId)
{
    public string? ToolInternalId { get; init; }

    public string? ToolName { get; init; }

    public JsonElement? ToolInput { get; init; }

    /// <summary>按事件补充载荷的字段(<c>toolOutput</c>、<c>prompt</c>、<c>status</c>……)。</summary>
    public Func<HookPayload, HookPayload>? Customize { get; init; }

    /// <summary>只跑命令型(收尾阶段的通知:不能再发起一次判定)。</summary>
    public bool CommandOnly { get; init; }
}

/// <summary>
/// Hooks 的服务入口(06 §3 的 <c>Hooks</c> 一方扩展的状态与配置面):
/// 配置(列表 / 诊断 / 保存 / 删除 / 启停 / 试运行,<c>hooks.*</c> 协议方法的实现)、事件触发(<see cref="RunEventAsync"/>)、
/// 与权限链(<see cref="PreToolSource"/>)和会话扩展(<see cref="CreateExtension"/>)的接入。
/// <para>
/// <b>执行顺序</b>全局 → 项目 → 运行期,<b>不去重</b>(几个来源表达的是几个人的意图:一条团队级的格式化钩子和我自己的通知钩子命令相同,也该各响一次),顺序执行,首个阻断即短路,
/// 每个事件合并后最多 <see cref="HookLimits.MaxPerEvent"/> 条。<b>钩子失败不阻断运行</b>(见 <see cref="CommandHookRunner"/>),代价是它会静悄悄地什么也不做——
/// 所以失败记进最近 20 条的环形缓冲(<see cref="DiagnosticsAsync"/>),那是用户唯一能发现「我那条钩子其实一直在报错」的地方。
/// </para>
/// </summary>
public sealed partial class HooksService : IAsyncDisposable
{
    private const int MaxFailures = 20;

    private readonly HooksServiceOptions _options;
    private readonly HookConfigStore _store = new();
    private readonly IIdGenerator _ids;
    private readonly Lock _failuresGate = new();
    private readonly List<HookDiagnostic> _failures = [];
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();

    public HooksService(HooksServiceOptions options)
    {
        _options = options;
        _ids = options.Ids ?? new UlidIdGenerator(options.Clock);
    }

    /// <summary>配置或诊断变了(保存 / 删除 / 启停 / 新的失败)。Host 据此推 <c>extensions</c> 主题的版本号。</summary>
    public event Action? Changed;

    public RuntimeHookTable Runtime { get; } = new();

    private ILoopDiagnostics Log => _options.Log;

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn("A hooks change listener failed.", e);
        }
    }

    private static EnvPath SettingsPathOf(EnvPath root) => ProjectRuleFile.PathOf(root);

    // ───────────────────────── 配置:读 ─────────────────────────

    private async ValueTask<HookProjectTarget?> ProjectAsync(string? workspaceId, CancellationToken ct) =>
        workspaceId is null ? null : await _options.ResolveProject(workspaceId, ct).ConfigureAwait(false)
            ?? throw new HookException(HookErrorKeys.NoWorkspace, $"Unknown project '{workspaceId}'.", new Dictionary<string, string> { ["workspaceId"] = workspaceId });

    /// <summary>
    /// 全局层 + 项目层(没有 <paramref name="workspaceId"/> 时只有全局)。<b>每次都重读</b>(用户刚往文件里加了一条就来看列表,不能返回上次的缓存);
    /// 顺便把手写条目缺的 id 补上并回写。
    /// </summary>
    public async Task<IReadOnlyList<HookItem>> ListAsync(string? workspaceId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(workspaceId, ct).ConfigureAwait(false);
        var global = await _store.ReadAssigningIdsAsync(_options.GlobalEnvironment, _options.GlobalSettingsPath, _ids.NewId, Log, ct).ConfigureAwait(false);
        var items = new List<HookItem>(global.Hooks.Select(h => new HookItem(h, HookScope.Global, _options.GlobalSettingsPath.Value)));
        if (project is not null)
        {
            var path = SettingsPathOf(project.Root);
            var layer = await _store.ReadAssigningIdsAsync(project.Environment, path, _ids.NewId, Log, ct).ConfigureAwait(false);
            items.AddRange(layer.Hooks.Select(h => new HookItem(h, HookScope.Project, path.Value)));
        }

        return items;
    }

    /// <summary>
    /// 配置诊断 + 运行期失败。归一化会把读不懂的条目<b>静默丢掉</b>(刻意的:一条坏钩子不该让整个文件作废),所以「文件里有几条 vs 认出来几条」的差值就是诊断的来源。
    /// </summary>
    public async Task<IReadOnlyList<HookDiagnostic>> DiagnosticsAsync(string? workspaceId, CancellationToken ct = default)
    {
        var result = new List<HookDiagnostic>();
        var project = await ProjectAsync(workspaceId, ct).ConfigureAwait(false);
        var layers = new List<(IExecutionEnvironment Env, EnvPath Path)> { (_options.GlobalEnvironment, _options.GlobalSettingsPath) };
        if (project is not null)
        {
            layers.Add((project.Environment, SettingsPathOf(project.Root)));
        }

        foreach (var (env, path) in layers)
        {
            try
            {
                var contents = await _store.ReadAsync(env, path, ct).ConfigureAwait(false);
                if (contents.Unreadable)
                {
                    result.Add(new HookDiagnostic(path.Value, HookDiagnosticKeys.Unreadable, "The settings file cannot be parsed."));
                }
                else if (contents.RawCount > contents.Hooks.Count)
                {
                    var dropped = contents.RawCount - contents.Hooks.Count;
                    result.Add(new HookDiagnostic(
                        path.Value, HookDiagnosticKeys.Invalid, $"{dropped} hook entries could not be read and were ignored.",
                        new Dictionary<string, string> { ["count"] = dropped.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                }
            }
            catch (EnvironmentException)
            {
                result.Add(new HookDiagnostic(path.Value, HookDiagnosticKeys.Unreadable, "The settings file cannot be read."));
            }
        }

        lock (_failuresGate)
        {
            result.AddRange(_failures);
        }

        return result;
    }

    // ───────────────────────── 配置:写 ─────────────────────────

    private async ValueTask<(IExecutionEnvironment Env, EnvPath Path)> TargetAsync(HookScope scope, string? workspaceId, CancellationToken ct)
    {
        if (scope == HookScope.Global)
        {
            return (_options.GlobalEnvironment, _options.GlobalSettingsPath);
        }

        var project = await ProjectAsync(workspaceId, ct).ConfigureAwait(false)
            ?? throw new HookException(HookErrorKeys.NoWorkspace, "Project hooks need a project.");
        return (project.Environment, SettingsPathOf(project.Root));
    }

    /// <summary>
    /// 新建或更新(按 id upsert)。校验失败 / 文件读不懂 / 写不进去都是 <see cref="HookException"/>(带稳定 key);
    /// 环境变量的值只在这里单向写入文件:读出来的列表里没有它们(见 <see cref="HookEnvEntry"/>)。
    /// </summary>
    public async Task<HookItem> SaveAsync(HookSaveRequest request, CancellationToken ct = default)
    {
        var (env, path) = await TargetAsync(request.Scope, request.WorkspaceId, ct).ConfigureAwait(false);
        var id = request.Hook.Id.Length > 0 ? request.Hook.Id : _ids.NewId();
        // 按判别分支重建:挡住「prompt 型带着一个 command 字段」那种半成品条目。
        var draft = request.Hook with
        {
            Id = id,
            Matcher = string.IsNullOrWhiteSpace(request.Hook.Matcher) ? null : request.Hook.Matcher.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Hook.Description) ? null : request.Hook.Description.Trim(),
            Command = request.Hook.Kind == HookKind.Command ? request.Hook.Command?.Trim() : null,
            Prompt = request.Hook.Kind == HookKind.Prompt ? request.Hook.Prompt?.Trim() : null,
            Model = request.Hook.Kind == HookKind.Prompt && !string.IsNullOrWhiteSpace(request.Hook.Model) ? request.Hook.Model.Trim() : null,
            Env = HookDefinition.EmptyEnv,
        };
        draft = draft with { ModelProviderId = draft.Model is null || string.IsNullOrWhiteSpace(request.Hook.ModelProviderId) ? null : request.Hook.ModelProviderId!.Trim() };

        HookDefinition? saved = null;
        await _store.MutateAsync(env, path, _ids.NewId, hooks =>
        {
            var existing = hooks.FirstOrDefault(h => h.Id == id);
            var withEnv = draft with { Env = request.Hook.Kind == HookKind.Command ? ResolveEnv(request.Env, existing?.Env) : HookDefinition.EmptyEnv };
            if (HookValidation.Validate(withEnv) is { } key)
            {
                throw new HookException(HookErrorKeys.Invalid, $"The hook is not valid: {key}.", new Dictionary<string, string> { ["reason"] = key });
            }

            if (existing is null && hooks.Count(h => h.Event == withEnv.Event) >= HookLimits.MaxPerEvent)
            {
                throw new HookException(HookErrorKeys.Full, $"At most {HookLimits.MaxPerEvent} hooks per event.", new Dictionary<string, string> { ["max"] = HookLimits.MaxPerEvent.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            saved = withEnv;
            return HookSettingsFile.Upsert(hooks, withEnv);
        }, ct).ConfigureAwait(false);
        RaiseChanged();
        return new HookItem(saved!, request.Scope, path.Value);
    }

    public async Task DeleteAsync(HookScope scope, string? workspaceId, string id, CancellationToken ct = default)
    {
        var (env, path) = await TargetAsync(scope, workspaceId, ct).ConfigureAwait(false);
        await _store.MutateAsync(env, path, _ids.NewId, hooks =>
            hooks.Any(h => h.Id == id) ? HookSettingsFile.Remove(hooks, id) : throw NotFound(id), ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetEnabledAsync(HookScope scope, string? workspaceId, string id, bool enabled, CancellationToken ct = default)
    {
        var (env, path) = await TargetAsync(scope, workspaceId, ct).ConfigureAwait(false);
        await _store.MutateAsync(env, path, _ids.NewId, hooks =>
            hooks.Any(h => h.Id == id) ? HookSettingsFile.SetEnabled(hooks, id, enabled) : throw NotFound(id), ct).ConfigureAwait(false);
        RaiseChanged();
    }

    private static HookException NotFound(string id) =>
        new(HookErrorKeys.NotFound, "Hook does not exist.", new Dictionary<string, string> { ["id"] = id });

    /// <summary>环境变量的合并:<c>null</c> = 保持原样;否则恰好这些名字,值为 null 的沿用已有值(没有已有值 = 非法)。</summary>
    internal static IReadOnlyDictionary<string, string> ResolveEnv(IReadOnlyList<HookEnvEntry>? entries, IReadOnlyDictionary<string, string>? existing)
    {
        if (entries is null)
        {
            return existing ?? HookDefinition.EmptyEnv;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Value is { } value)
            {
                result[entry.Name] = value;
            }
            else if (existing is not null && existing.TryGetValue(entry.Name, out var kept))
            {
                result[entry.Name] = kept;
            }
            else
            {
                throw new HookException(HookErrorKeys.BadEnv, "An environment variable without a value has no stored value to keep.", new Dictionary<string, string> { ["name"] = entry.Name });
            }
        }

        return result;
    }

    // ───────────────────────── 试运行 ─────────────────────────

    /// <summary>
    /// 命令型:在项目的执行环境里真跑一次(没给项目就在设备本机),喂示例载荷,返回 退出码 / 输出 / 耗时 / 决定(输出截断,环境变量的值已抹掉)。
    /// prompt 型:<b>只预览</b>展开结果,不真调模型(见 <see cref="PromptHookRunner.Preview"/>)。
    /// </summary>
    public async Task<HookRunReport> TestAsync(HookTestRequest request, CancellationToken ct = default)
    {
        var project = await ProjectAsync(request.WorkspaceId, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string>? existing = null;
        if (request.ExistingId is { } existingId)
        {
            existing = (await ListAsync(request.WorkspaceId, ct).ConfigureAwait(false)).FirstOrDefault(i => i.Hook.Id == existingId && i.Scope == request.Scope)?.Hook.Env;
        }

        var draft = request.Hook with
        {
            Id = "test",
            Enabled = true,
            Env = request.Hook.Kind == HookKind.Command ? ResolveEnv(request.Env, existing) : HookDefinition.EmptyEnv,
        };
        if (HookValidation.Validate(draft) is { } key)
        {
            throw new HookException(HookErrorKeys.Invalid, $"The hook is not valid: {key}.", new Dictionary<string, string> { ["reason"] = key });
        }

        var root = project?.Root.Value ?? "";
        var payload = HookPayload.Sample(draft.Event, request.Scope, root);
        if (draft.Kind == HookKind.Prompt)
        {
            return new HookRunReport("test", request.Scope, null, "", "", 0, HookOutcome.Ok, Preview: PromptHookRunner.Preview(draft.Prompt ?? "", payload.ToJson()));
        }

        var env = project?.Environment ?? _options.GlobalEnvironment;
        var report = await CommandHookRunner.RunAsync(env, draft, request.Scope, payload, project?.Root, _options.Clock, ct).ConfigureAwait(false);
        return Scrub(report, draft);
    }

    // ───────────────────────── 事件触发 ─────────────────────────

    /// <summary>
    /// 读两层钩子 + 运行期那一批,按事件 + matcher 筛出这一次要跑的,顺序执行,首个阻断即短路。
    /// 调用方拿到报告自己决定怎么用:PreToolUse 看 decision,PostToolUse 拼 additionalContext,Stop 看有没有人阻断,通知类的直接丢掉。
    /// 读配置失败(环境断开 / 文件读不懂)按「没有这一层」处理并记诊断:钩子的失败不该变成 Run 的失败。
    /// </summary>
    public async Task<IReadOnlyList<HookRunReport>> RunEventAsync(HookEventRequest request, CancellationToken ct)
    {
        var selected = await SelectAsync(request, ct).ConfigureAwait(false);
        if (selected.Count == 0 || ct.IsCancellationRequested)
        {
            return [];
        }

        var reports = new List<HookRunReport>();
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            var hook = item.Hook;
            var payload = new HookPayload
            {
                Event = request.Event,
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceRoot = request.Root?.Value ?? "",
                Scope = item.Scope,
                ToolName = request.ToolName,
                ToolInternalId = request.ToolInternalId,
                ToolInput = request.ToolInput,
            };
            if (request.Customize is { } customize)
            {
                payload = customize(payload);
            }

            HookRunReport report;
            try
            {
                report = hook.Kind == HookKind.Prompt
                    ? await PromptHookRunner.RunAsync(_options.Evaluator, hook, item.Scope, payload, request.WorkspaceId, ct).ConfigureAwait(false)
                    : await CommandHookRunner.RunAsync(request.Environment, hook, item.Scope, payload, request.Root, _options.Clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            // ★ 环境变量的值(密钥)不能经钩子的输出流到模型上下文 / 界面 / 诊断:脚本可能把它回显到 stderr 当作阻断原因。出站前统一抹掉。
            report = Scrub(report, hook);
            if (report.Outcome is HookOutcome.Timeout or HookOutcome.SpawnFailed || (report.ExitCode is { } code && code != 0 && code != 2))
            {
                RecordFailure(hook, report, item.SourcePath);
            }

            reports.Add(report);
            if (report.Blocks)
            {
                break;
            }
        }

        return reports;
    }

    private async Task<IReadOnlyList<HookItem>> SelectAsync(HookEventRequest request, CancellationToken ct)
    {
        var all = new List<HookItem>();
        await AddLayerAsync(all, _options.GlobalEnvironment, _options.GlobalSettingsPath, HookScope.Global, request, ct).ConfigureAwait(false);
        if (request.Root is { } root)
        {
            await AddLayerAsync(all, request.Environment, SettingsPathOf(root), HookScope.Project, request, ct).ConfigureAwait(false);
        }

        foreach (var hook in Runtime.For(request.SessionId))
        {
            all.Add(new HookItem(hook, HookScope.Global, ""));
        }

        return [.. all
            .Where(i => i.Hook.Event == request.Event && i.Hook.Enabled && (!request.CommandOnly || i.Hook.Kind == HookKind.Command) && Matches(i.Hook, request))
            .Take(HookLimits.MaxPerEvent)];
    }

    private async Task AddLayerAsync(List<HookItem> all, IExecutionEnvironment env, EnvPath path, HookScope scope, HookEventRequest request, CancellationToken ct)
    {
        try
        {
            var contents = await _store.ReadAsync(env, path, ct).ConfigureAwait(false);
            all.AddRange(contents.Hooks.Select(h => new HookItem(h, scope, path.Value)));
        }
        catch (EnvironmentException e) when (e.Code != EnvironmentErrorCode.Cancelled)
        {
            Log.Warn($"[hook] could not read the {scope} hooks ({e.Code}); this layer is skipped for {request.Event}.");
        }
    }

    private static bool Matches(HookDefinition hook, HookEventRequest request)
    {
        if (string.IsNullOrEmpty(hook.Matcher))
        {
            return true;
        }

        // 非工具类事件手里没有「工具 + 入参」可比,写了 matcher 也无从匹配——忽略它,而不是把这条钩子整个跳过(用户多写了一个字段,不该让钩子静默失效)。
        if (!HookEvents.ToolScoped.Contains(hook.Event, StringComparer.Ordinal) || request.ToolInternalId is null)
        {
            return true;
        }

        return PermissionRules.Matches(hook.Matcher, request.ToolInternalId, request.ToolInput ?? default);
    }

    // ───────────────────────── 失败记录(诊断) ─────────────────────────

    private static string Label(HookDefinition hook) =>
        hook.Kind == HookKind.Prompt ? "prompt: " + Truncate(hook.Prompt ?? "", 80) : Truncate(hook.Command ?? "", 200);

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    /// <summary>抹掉这条钩子环境变量里的值(脚本可能把它回显到 stderr)。太短的值(&lt; 4 字符)不抹:会把普通文字抹成一团。</summary>
    internal static string Scrub(string text, HookDefinition hook)
    {
        foreach (var value in hook.Env.Values.Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }

        return text;
    }

    internal static HookRunReport Scrub(HookRunReport report, HookDefinition hook) => hook.Env.Count == 0
        ? report
        : report with
        {
            Stdout = Scrub(report.Stdout, hook),
            Stderr = Scrub(report.Stderr, hook),
            Reason = report.Reason is null ? null : Scrub(report.Reason, hook),
            AdditionalContext = report.AdditionalContext is null ? null : Scrub(report.AdditionalContext, hook),
        };

    private void RecordFailure(HookDefinition hook, HookRunReport report, string sourcePath)
    {
        var seconds = (hook.TimeoutMs / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var stderr = Truncate(Scrub(report.Stderr, hook), 200);
        var (key, detail) = report.Outcome switch
        {
            HookOutcome.Timeout => (hook.Kind == HookKind.Prompt ? HookDiagnosticKeys.Evaluator : HookDiagnosticKeys.Timeout, $"timed out ({seconds}s)"),
            HookOutcome.SpawnFailed => (HookDiagnosticKeys.SpawnFailed, $"could not start: {stderr}"),
            _ => (hook.Kind == HookKind.Prompt ? HookDiagnosticKeys.Evaluator : HookDiagnosticKeys.Command, $"exit code {report.ExitCode}: {stderr}"),
        };
        var label = Scrub(Label(hook), hook);
        var diagnostic = new HookDiagnostic(
            sourcePath, key, $"{label} -- {detail}",
            new Dictionary<string, string>
            {
                ["hook"] = label,
                ["seconds"] = seconds,
                ["code"] = report.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["detail"] = stderr,
            });
        lock (_failuresGate)
        {
            _failures.Insert(0, diagnostic);
            if (_failures.Count > MaxFailures)
            {
                _failures.RemoveRange(MaxFailures, _failures.Count - MaxFailures);
            }
        }

        Log.Warn($"[hook] {label}: {detail}");
        RaiseChanged();
    }

    // ───────────────────────── 后台任务(不等待的事件) ─────────────────────────

    /// <summary>fire-and-forget 的事件(Notification / SubagentStop / 收尾的 Stop):不 await、不看结果,但记着任务,关闭时一并取消 / 等待。</summary>
    internal void Fire(Func<CancellationToken, Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Log.Warn("A fire-and-forget hook event failed.", e);
            }
        });
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>等所有不等待的事件跑完(测试和有序关闭用)。</summary>
    public async Task DrainAsync()
    {
        while (_pending.Keys.ToArray() is { Length: > 0 } tasks)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                _pending.TryRemove(t, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
