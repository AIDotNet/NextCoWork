using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Plugins;

public sealed partial class PluginManager
{
    // ───────────────────────── 激活 / 下线 ─────────────────────────

    /// <summary>
    /// 为一个启用的插件建运行时并激活。失败是<b>状态</b>,不是异常:装载 / 激活失败 → <c>error</c>(诊断说明);超预算 / 引擎故障 → <c>faulted</c>。Agent 不受影响。
    /// </summary>
    private async Task ActivateAsync(PluginRecord record, CancellationToken ct = default)
    {
        PluginRuntime runtime;
        lock (_gate)
        {
            if (!NeedsActivation(record) || (record.Runtime is { IsRetired: false }))
            {
                return;
            }

            record.Runtime?.Dispose(); // 上一轮故障 / 失败留下的已退役运行时

            var host = new PluginHost(record.Manifest, () => record.Permissions, _services, _activity, record.RuntimeDiagnostics ??= new PluginDiagnostics(Publish));
            host.RegistrationsChanged += () => SyncTools(record);
            runtime = new PluginRuntime(host, _env, record.Directory, _options.Limits);
            runtime.Faulted += failure => OnFaulted(record, runtime, failure);
            record.Host = host;
            record.Runtime = runtime;
            record.Status = PluginStatus.Activating;
        }

        Publish();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            await runtime.ActivateAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (record.Runtime == runtime && record.Status == PluginStatus.Activating)
                {
                    record.Status = PluginStatus.Active;
                }
            }
        }
        catch (PluginRuntimeException e)
        {
            Fail(record, runtime, e);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            // 没预料到的失败也只是这个插件的状态(引擎故障),不往上抛:一个插件不能让别的插件的激活 / 整个启动失败
            Fail(record, runtime, new PluginRuntimeException(PluginFailureKind.Engine, PluginDiagnosticKeys.ActivateFailed, $"Activating the plugin failed unexpectedly ({e.GetType().Name}).", inner: e));
        }
        catch (OperationCanceledException)
        {
            // 管理器关闭 / 调用方取消:运行时随之退役,不是故障
            runtime.Retire();
            lock (_gate)
            {
                if (record.Runtime == runtime && record.Status == PluginStatus.Activating)
                {
                    record.Status = PluginStatus.Idle;
                }
            }
        }

        SyncTools(record);
        Publish();
    }

    private void Fail(PluginRecord record, PluginRuntime runtime, PluginRuntimeException failure)
    {
        if (failure.Kind == PluginFailureKind.NotActive)
        {
            return; // 激活途中被禁用 / 卸载:状态已经由那条路径定了
        }

        runtime.Retire();
        var faulted = failure.Kind is PluginFailureKind.Budget or PluginFailureKind.Engine;
        lock (_gate)
        {
            if (record.Runtime != runtime)
            {
                return;
            }

            record.Status = faulted ? PluginStatus.Faulted : PluginStatus.Error;
            record.RuntimeDiagnostics?.Add(DiagnosticOf(failure));
            ClearRegistrations(record.Host);
        }

        _options.Log.Warn($"[plugin] {record.Manifest.Id}: {failure.Kind}: {failure.Message}");
    }

    /// <summary>故障 / 失败的插件不再有任何在线的贡献(命令、工具、钩子)。</summary>
    private static void ClearRegistrations(PluginHost? host)
    {
        if (host is null)
        {
            return;
        }

        host.Commands.Clear();
        host.Tools.Clear();
        host.Interceptor = false;
        host.ContextProvider = false;
    }

    private static PluginDiagnostic DiagnosticOf(PluginRuntimeException failure) => new(
        failure.Kind == PluginFailureKind.Load ? "main" : "runtime",
        failure.Key,
        failure.Message,
        "error",
        failure.Reason is null ? null : new Dictionary<string, string> { ["reason"] = failure.Reason });

    /// <summary>运行期故障(超预算 / 引擎故障):插件标记为 faulted、贡献撤下、诊断上报——不影响 Agent 和别的插件。</summary>
    private void OnFaulted(PluginRecord record, PluginRuntime runtime, PluginRuntimeException failure)
    {
        lock (_gate)
        {
            if (record.Runtime != runtime)
            {
                return;
            }

            record.Status = PluginStatus.Faulted;
            record.RuntimeDiagnostics?.Add(DiagnosticOf(failure));
            ClearRegistrations(record.Host);
        }

        _options.Log.Warn($"[plugin] {record.Manifest.Id}: faulted ({failure.Reason ?? failure.Kind.ToString()}): {failure.Message}");
        CancelInteractions(record.Manifest.Id);
        SyncTools(record);
        Publish();
    }

    /// <summary>
    /// 停掉一个插件的运行时:<b>下线先于销毁</b>(先注销工具,再让引擎收尾)——反过来的话,注册表里还留着几个指向已销毁引擎的工具,模型下一轮照样会去调它们。
    /// 取消它挂起的交互,清空它的进度出口。幂等。
    /// </summary>
    private async Task StopRuntimeAsync(PluginRecord record, bool deactivate = true)
    {
        PluginRuntime? runtime;
        lock (_gate)
        {
            runtime = record.Runtime;
            record.Runtime = null;
        }

        _options.Tools.UnregisterSource(PluginToolNames.Source(record.Manifest.Id));
        CancelInteractions(record.Manifest.Id);
        if (runtime is not null)
        {
            if (deactivate && !runtime.IsRetired)
            {
                await runtime.DeactivateAsync(DeactivateGrace).ConfigureAwait(false);
            }

            runtime.Dispose();
        }

        lock (_gate)
        {
            record.Host = null;
        }
    }

    // ───────────────────────── 工具同步 ─────────────────────────

    /// <summary>
    /// 把插件当前声明的工具同步进统一注册表(来源 <c>plugin:&lt;id&gt;</c>,整批替换)。只有<b>在线</b>(<c>active</c>)的插件有工具;重名(与内置 / MCP / 别的来源冲突)的被注册表丢弃并留诊断。
    /// </summary>
    private void SyncTools(PluginRecord record)
    {
        var source = PluginToolNames.Source(record.Manifest.Id);
        List<PluginTool> tools;
        PluginManifest manifest;
        lock (_gate)
        {
            manifest = record.Manifest;
            tools = record.Status is PluginStatus.Active or PluginStatus.Activating && record.Host is { Retired: false } host
                ? [.. host.Tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(d => new PluginTool(this, manifest, d))]
                : [];
        }

        if (tools.Count == 0)
        {
            _options.Tools.UnregisterSource(source);
        }
        else
        {
            var dropped = _options.Tools.ReplaceSource(source, tools);
            foreach (var id in dropped)
            {
                record.RuntimeDiagnostics?.Add(new PluginDiagnostic(
                    "tools", PluginDiagnosticKeys.ToolNameClash, $"The tool \"{id}\" clashes with a tool from another source and was not registered.", "warn", new Dictionary<string, string> { ["tool"] = id }));
            }
        }

        Publish();
    }

    internal bool IsToolAvailable(string pluginId, string toolName)
    {
        lock (_gate)
        {
            return _records.TryGetValue(pluginId, out var r) && r.Status == PluginStatus.Active && r.Host is { Retired: false } host && host.Tools.ContainsKey(toolName);
        }
    }

    // ───────────────────────── 工具调用 ─────────────────────────

    internal async Task<ToolResult> InvokeToolAsync(string pluginId, string toolName, JsonElement input, string callId, ToolContext ctx, CancellationToken ct)
    {
        PluginRuntime? runtime;
        PluginHost? host;
        PluginToolDeclaration? declaration = null;
        lock (_gate)
        {
            _records.TryGetValue(pluginId, out var record);
            runtime = record?.Status == PluginStatus.Active ? record.Runtime : null;
            host = record?.Host;
            host?.Tools.TryGetValue(toolName, out declaration);
        }

        if (runtime is null || host is null || declaration is null)
        {
            return ToolResult.Error($"The plugin tool {toolName} is not available right now (the plugin may be disabled or have stopped). Try a different approach or tell the user.", ToolDetails(pluginId, toolName, ctx, null));
        }

        var scope = ScopeOf(ctx, callId);
        var timeout = declaration.Interactive ? _options.Limits.InteractiveToolTimeout : _options.Limits.ToolTimeout;
        host.Progress[callId] = ctx.Progress;
        host.OpenActions(callId);
        try
        {
            var json = await runtime.InvokeAsync("tool", toolName, input.GetRawText(), callId, scope, timeout, timeout, ct).ConfigureAwait(false);
            var (text, isError, card) = PluginTool.ReadResult(json);
            return PluginTool.Finish(text, isError, ToolDetails(pluginId, toolName, ctx, card));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 中止要原样抛(R15):伪装成工具失败的话,用户点了停止,模型却收到一条「工具失败了」,于是它会重试
            throw;
        }
        catch (PluginRuntimeException e)
        {
            return ToolResult.Error(
                e.Kind switch
                {
                    PluginFailureKind.Thrown => $"Plugin tool {toolName} failed: {Clip(e.Message, 2000)}",
                    PluginFailureKind.Budget => $"Plugin tool {toolName} exceeded its execution budget ({e.Reason}) and was stopped; the plugin is switched off until the user re-enables it. Try a different approach.",
                    PluginFailureKind.Busy => $"Plugin tool {toolName} could not start because the plugin is busy with another call. Retry later.",
                    PluginFailureKind.Timeout => $"Plugin tool {toolName} did not finish within its time limit and was stopped. Try again or tell the user.",
                    _ => $"Plugin tool {toolName} could not be run ({Clip(e.Message, 300)}). The plugin may have been disabled; tell the user.",
                },
                ToolDetails(pluginId, toolName, ctx, null));
        }
        catch (JsonException)
        {
            return ToolResult.Error($"Plugin tool {toolName} returned something that cannot be read.", ToolDetails(pluginId, toolName, ctx, null));
        }
        finally
        {
            host.Progress.TryRemove(callId, out _);
            host.CloseActions(callId);
        }
    }

    private InvocationScope ScopeOf(ToolContext ctx, string callId)
    {
        var workspaceId = _options.WorkspaceOfSession?.Invoke(ctx.SessionId);
        var name = workspaceId is null ? null : _services.Workspaces.Resolve(workspaceId)?.Name;
        return ctx switch
        {
            ExecutionToolContext { Runtime: var rt } => new InvocationScope
            {
                WorkspaceId = workspaceId, WorkspaceName = name, Environment = rt.Environment, Root = rt.Root, Tools = rt, Run = ctx.RunId, FileMutations = ctx.FileMutations, CallId = callId,
            },
            Ncw.Agent.Kernel.Session.SessionToolContext s => new InvocationScope
            {
                WorkspaceId = workspaceId, WorkspaceName = name, Environment = s.Execution, Root = s.WorkspaceRoot, Run = ctx.RunId, FileMutations = ctx.FileMutations, CallId = callId,
            },
            _ => new InvocationScope { WorkspaceId = workspaceId, WorkspaceName = name, CallId = callId },
        };
    }

    /// <summary>工具结果的 <c>Details</c>:UI 轨(模型看不到)。带插件 / 工具名和<b>执行环境标识</b>(AGENTS.md §5);<paramref name="card"/> 是消毒过的卡片。</summary>
    internal JsonElement ToolDetails(string pluginId, string toolName, ToolContext ctx, JsonElement? card)
    {
        var scope = ScopeOf(ctx, ctx.CallId);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "plugin");
            w.WriteString("plugin", pluginId);
            w.WriteString("tool", toolName);
            if (scope.EnvironmentRef is { } env)
            {
                w.WriteStartObject("environment");
                w.WriteString("deviceId", env.DeviceId);
                w.WriteString("environment", env.Environment);
                w.WriteString("name", env.Name);
                w.WriteString("type", env.Type);
                w.WriteEndObject();
            }
            else if (ctx.Environment is { } tag)
            {
                w.WriteStartObject("environment");
                w.WriteString("deviceId", tag.DeviceId);
                w.WriteString("environment", tag.EnvironmentId);
                w.WriteEndObject();
            }

            if (card is { } c)
            {
                w.WritePropertyName("card");
                c.WriteTo(w);
            }

            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>用户在实时卡片的某个 <c>button</c> 上点了一下:送进仍在运行的那次工具调用(<c>onAction</c>)。<b>即发即忘</b>:到不了(工具已结束 / callId 不属于该插件)返回 false——一次没送到的点击不该弹错,那是竞态不是故障。</summary>
    public bool DeliverCardAction(string pluginId, string callId, string actionId, JsonElement? value)
    {
        PluginHost? host;
        lock (_gate)
        {
            host = _records.TryGetValue(pluginId, out var record) ? record.Host : null;
        }

        if (host is null || actionId.Length == 0 || actionId.Length > 128)
        {
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("actionId", actionId);
            if (value is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } v && v.GetRawText().Length <= 16 * 1024)
            {
                w.WritePropertyName("value");
                v.WriteTo(w);
            }

            w.WriteEndObject();
        }

        return host.DeliverAction(callId, System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // ───────────────────────── 命令 ─────────────────────────

    /// <summary>
    /// 执行插件贡献的命令(菜单 / 命令面板按命令 id 分发)。命令必须是清单声明过且已注册的;<c>args</c> 是<b>宿主调用点上下文</b>(不是用户输入),固定带 <c>workspaceId</c>(菜单在哪个工作区点的)。
    /// 失败 = <see cref="PluginException"/>(插件 throw 的原因进 <c>commandFailed</c>,超预算 = <c>faulted</c>),不拖垮 Agent。
    /// </summary>
    public async Task RunCommandAsync(string pluginId, string commandId, string? argsJson, string? workspaceId, CancellationToken ct = default)
    {
        var record = Require(pluginId);
        PluginRuntime? runtime;
        PluginHost? host;
        lock (_gate)
        {
            if (record.Status == PluginStatus.Faulted)
            {
                throw new PluginException(PluginErrorKeys.Faulted, "The plugin stopped after exceeding its execution budget; enable it again to retry.", new Dictionary<string, string> { ["plugin"] = pluginId });
            }

            runtime = record.Status == PluginStatus.Active ? record.Runtime : null;
            host = record.Host;
        }

        if (!record.Manifest.Contributes.Commands.Any(c => c.Command == commandId))
        {
            throw new PluginException(PluginErrorKeys.CommandUndeclared, $"The plugin does not declare the command \"{commandId}\".", new Dictionary<string, string> { ["plugin"] = pluginId, ["command"] = commandId });
        }

        if (runtime is null || host is null)
        {
            throw new PluginException(PluginErrorKeys.NotRunnable, "The plugin is not running.", new Dictionary<string, string> { ["plugin"] = pluginId });
        }

        if (!host.Commands.ContainsKey(commandId))
        {
            throw new PluginException(PluginErrorKeys.CommandUndeclared, $"The plugin has not registered the command \"{commandId}\".", new Dictionary<string, string> { ["plugin"] = pluginId, ["command"] = commandId });
        }

        PluginWorkspaceTarget? target = null;
        if (!string.IsNullOrEmpty(workspaceId))
        {
            target = _services.Workspaces.Resolve(workspaceId);
            if (target is null)
            {
                throw new PluginException(PluginErrorKeys.InvalidArgument, "The project does not exist.", new Dictionary<string, string> { ["workspaceId"] = workspaceId });
            }
        }

        var scope = new InvocationScope { WorkspaceId = target?.WorkspaceId, WorkspaceName = target?.Name, Environment = target?.Environment, Root = target?.Root };
        try
        {
            await runtime.InvokeAsync("command", commandId, CommandArgs(argsJson, workspaceId), Guid.NewGuid().ToString("N"), scope, _options.Limits.CommandTimeout, _options.Limits.CommandTimeout, ct).ConfigureAwait(false);
        }
        catch (PluginRuntimeException e)
        {
            throw e.Kind switch
            {
                PluginFailureKind.Budget or PluginFailureKind.Engine => new PluginException(PluginErrorKeys.Faulted, "The plugin stopped after exceeding its execution budget.", new Dictionary<string, string> { ["plugin"] = pluginId, ["reason"] = e.Reason ?? "engine" }, e),
                PluginFailureKind.NotActive => new PluginException(PluginErrorKeys.NotRunnable, "The plugin is not running.", new Dictionary<string, string> { ["plugin"] = pluginId }, e),
                PluginFailureKind.Busy => new PluginException(PluginErrorKeys.CommandFailed, "The plugin is busy with another call.", new Dictionary<string, string> { ["plugin"] = pluginId, ["command"] = commandId }, e),
                PluginFailureKind.Timeout => new PluginException(PluginErrorKeys.CommandFailed, "The command did not finish within its time limit.", new Dictionary<string, string> { ["plugin"] = pluginId, ["command"] = commandId }, e),
                _ => new PluginException(PluginErrorKeys.CommandFailed, $"The command failed: {Clip(e.Message, 500)}", new Dictionary<string, string> { ["plugin"] = pluginId, ["command"] = commandId }, e),
            };
        }
    }

    private static string CommandArgs(string? argsJson, string? workspaceId)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (!string.IsNullOrEmpty(workspaceId))
            {
                w.WriteString("workspaceId", workspaceId);
            }

            if (!string.IsNullOrWhiteSpace(argsJson) && argsJson.Length <= 16 * 1024)
            {
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in doc.RootElement.EnumerateObject())
                        {
                            // 宿主调用点上下文优先:调用方不能改写 workspaceId
                            if (property.Name != "workspaceId")
                            {
                                property.WriteTo(w);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ───────────────────────── 钩子 ─────────────────────────

    /// <summary>
    /// 在所有有拦截器的在线插件里问一圈(<c>agent.registerToolInterceptor</c>)。<b>超时 = 弃权</b>(只记诊断:一个卡住的插件不该把所有工具调用堵死),插件 throw 也是弃权。
    /// 返回收紧的表态(第一个 deny 短路);拦截器的 allow 视为弃权(类型上就没有放行)。
    /// </summary>
    internal async ValueTask<(string? Deny, bool Ask)> InterceptAsync(string toolInternalId, JsonElement input, CancellationToken ct)
    {
        var ask = false;
        foreach (var (record, runtime, host) in HookCandidates(PluginPermissions.AgentIntercept, h => h.Interceptor))
        {
            var response = await CallHookAsync(record, runtime, "interceptor", $"{{\"tool\":{JsonSerializer.Serialize(toolInternalId, PluginJson.Default.String)},\"input\":{input.GetRawText()}}}", ct).ConfigureAwait(false);
            if (response is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var reasonKey = doc.RootElement.TryGetProperty("reasonKey", out var rk) && rk.ValueKind == JsonValueKind.String ? Clip(rk.GetString()!, 100) : null;
                switch (decision.GetString())
                {
                    case "deny":
                        return ($"The plugin \"{record.Manifest.DisplayName}\" ({record.Manifest.Id}) blocked this tool call{(reasonKey is null ? "" : $" (reason: {reasonKey})")}. Do not try to reach the same result through another tool.", false);
                    case "ask":
                        ask = true;
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        return (null, ask);
    }

    /// <summary>各个提供了上下文的在线插件给本轮的一小段(<c>agent.registerContextProvider</c>):强制包裹成 <c>&lt;plugin-context source="…"&gt;</c>(模型必须能看出这一段来自插件),单次 ≤ 2000 字符,每轮合计 ≤ 8000。</summary>
    internal async ValueTask<IReadOnlyList<string>> CollectContextAsync(string sessionId, CancellationToken ct)
    {
        var sections = new List<string>();
        var total = 0;
        foreach (var (record, runtime, host) in HookCandidates(PluginPermissions.AgentContext, h => h.ContextProvider))
        {
            if (total >= PluginContextLimits.PerTurn)
            {
                break;
            }

            var response = await CallHookAsync(record, runtime, "context", $"{{\"sessionId\":{JsonSerializer.Serialize(sessionId, PluginJson.Default.String)}}}", ct).ConfigureAwait(false);
            if (response is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.ValueKind == JsonValueKind.String
                    && HostGuards.WrapContext(record.Manifest.Id, doc.RootElement.GetString()!, Math.Min(PluginContextLimits.PerCall, PluginContextLimits.PerTurn - total)) is { Length: > 0 } wrapped)
                {
                    sections.Add(wrapped);
                    total += wrapped.Length;
                }
            }
            catch (JsonException)
            {
            }
        }

        return sections;
    }

    private List<(PluginRecord Record, PluginRuntime Runtime, PluginHost Host)> HookCandidates(string permission, Func<PluginHost, bool> registered)
    {
        lock (_gate)
        {
            return [.. _records.Values
                .Where(r => r.Status == PluginStatus.Active && r.Runtime is { IsRetired: false } && r.Host is { Retired: false } h && registered(h) && r.Granted.Contains(permission))
                .OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)
                .Select(r => (r, r.Runtime!, r.Host!))];
        }
    }

    private async Task<string?> CallHookAsync(PluginRecord record, PluginRuntime runtime, string kind, string argsJson, CancellationToken ct)
    {
        var timeout = _options.Limits.HookTimeout;
        try
        {
            // 拿不到锁(插件正忙)也按超时弃权:同一个超时预算同时管排队和执行
            return await runtime.InvokeAsync(kind, "", argsJson, "", null, timeout, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginRuntimeException e)
        {
            var key = e.Kind is PluginFailureKind.Budget or PluginFailureKind.Busy or PluginFailureKind.Timeout ? PluginDiagnosticKeys.InterceptorTimeout : kind == "context" ? PluginDiagnosticKeys.ContextFailed : PluginDiagnosticKeys.InterceptorFailed;
            record.RuntimeDiagnostics?.Add(new PluginDiagnostic(
                kind, key, $"The plugin's {kind} hook was skipped ({e.Kind}): {Clip(e.Message, 300)}", "warn", new Dictionary<string, string> { ["hook"] = kind }));
            return null;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary><c>agent.registerContextProvider</c> 的注入上限(2.x 计划 §14 第 4 条)。</summary>
public static class PluginContextLimits
{
    /// <summary>单次注入的字符上限。</summary>
    public const int PerCall = 2000;

    /// <summary>每轮所有插件加起来的字符上限。</summary>
    public const int PerTurn = 8000;
}
