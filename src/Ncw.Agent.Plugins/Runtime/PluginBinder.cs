using System.Text.Json;
using System.Text.Json.Nodes;
using Jaunt;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 把宿主 API 绑进一个插件的 Jaunt 引擎(AGENTS.md §6:宿主 API 只能显式绑定,每个绑定都先做权限检查)。
/// 只有 <see cref="PluginHostMethods.All"/> 里的方法才会被绑定;绑定的名字是全局 <c>__n_&lt;方法名&gt;</c>,由 <see cref="PluginPrelude"/> 收进闭包后从全局里删掉。
/// </summary>
internal static class PluginBinder
{
    public const int MaxToolDescriptionChars = 4096;
    public const int MaxToolSchemaBytes = 64 * 1024;

    public static string NativeName(string method) => "__n_" + method.Replace('.', '_');

    public static void Bind(JsEngine engine, PluginHost host)
    {
        foreach (var method in PluginHostMethods.All)
        {
            var captured = method;
            engine.SetAsyncFunction<string, string>(NativeName(method.Name), (json, ct) => CallAsync(host, captured, json, ct));
        }

        engine.SetValue("__n_methods", new JsonArray([.. PluginHostMethods.All.Select(m => (JsonNode?)JsonValue.Create(m.Name))]).ToJsonString());

        engine.SetFunction("__n_log", (ref JsCallContext c) =>
        {
            var level = c.GetString(0) ?? "info";
            var message = c.GetString(1) ?? "";
            if (message.Length > host.Services.Limits.MaxLogChars)
            {
                message = message[..host.Services.Limits.MaxLogChars] + "…";
            }

            // console.* 只在警告 / 错误时留诊断;info 级别的输出不值得占诊断位
            if (level is "warn" or "error")
            {
                host.Diagnostics.Add(new PluginDiagnostic("console", PluginDiagnosticKeys.PluginLog, message, level));
            }

            return JsValue.Undefined;
        }, 2);

        engine.SetFunction("__n_registerCommand", (ref JsCallContext c) => JsValue.From(RegisterCommand(host, c.GetString(0) ?? "")), 1);
        engine.SetFunction("__n_registerTool", (ref JsCallContext c) => JsValue.From(RegisterTool(host, c.GetString(0) ?? "", c.GetString(1) ?? "{}")), 2);
        engine.SetFunction("__n_unregister", (ref JsCallContext c) =>
        {
            var removed = (c.GetString(0), c.GetString(1)) switch
            {
                ("command", { } name) => host.Commands.TryRemove(name, out _),
                ("tool", { } name) => host.Tools.TryRemove(name, out _),
                _ => false,
            };
            if (removed)
            {
                host.NotifyRegistrationsChanged();
            }

            return JsValue.Undefined;
        }, 2);
        engine.SetFunction("__n_setHook", (ref JsCallContext c) => JsValue.From(SetHook(host, c.GetString(0) ?? "", c.GetBoolean(1))), 2);
        engine.SetFunction("__n_cancelTimers", (ref JsCallContext c) =>
        {
            host.CancelSleeps();
            return JsValue.Undefined;
        }, 0);
        engine.SetFunction("__n_closeActions", (ref JsCallContext c) =>
        {
            host.CloseActions(c.GetString(0) ?? "");
            return JsValue.Undefined;
        }, 1);
        engine.SetFunction("__n_progress", (ref JsCallContext c) =>
        {
            Progress(host, c.GetString(0) ?? "", c.GetString(1) ?? "{}");
            return JsValue.Undefined;
        }, 2);
    }

    // ───────────────────────── 门 ─────────────────────────

    /// <summary>
    /// 一次宿主调用的四道门(顺序不能换:未知方法在最前,所以拼错方法名收到的是 <c>unknown_method</c>,不是误导性的「没有权限」):
    /// ① 白名单(能走到这里 = 绑定过)→ ② 授权(实时读,撤权立刻生效)→ ③ 参数收窄(在处理函数里)→ ④ 行为审批(执行命令,在处理函数里)。
    /// 结果一律是信封(<c>{ok,data}</c> / <c>{ok:false,code,message}</c>),业务失败不抛进引擎;每次调用记一条活动(不含参数原文)。
    /// </summary>
    private static async ValueTask<string> CallAsync(PluginHost host, HostMethod method, string json, CancellationToken ct)
    {
        var clock = host.Services.Clock;
        var started = clock.GetTimestamp();
        var verdict = "ok";
        var summary = method.Name;
        var counters = host.Calls.Enter();
        try
        {
            if (host.Retired)
            {
                verdict = "denied";
                return Fail("rejected", "the plugin is not active");
            }

            if (method.Permission is { } permission && !host.Permissions.Has(permission))
            {
                verdict = "denied";
                summary = $"{method.Name}: needs {permission}";
                host.Diagnostics.Add(new PluginDiagnostic(
                    method.Name, PluginDiagnosticKeys.PermissionDenied, $"The call to {method.Name} needs the \"{permission}\" permission, which has not been granted.", "warn",
                    new Dictionary<string, string> { ["method"] = method.Name, ["permission"] = permission }));
                return Fail("permission_denied", $"the plugin has not been granted the \"{permission}\" permission");
            }

            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json);
            var args = doc.RootElement.ValueKind == JsonValueKind.Null ? JsonDocument.Parse("{}").RootElement : doc.RootElement;
            var result = await method.Handler(new HostCall(host, host.Scope, ct), args).ConfigureAwait(false);
            summary = result.Summary;
            return Ok(result.Data);
        }
        catch (HostCallException e)
        {
            verdict = e.Code is "rejected" or "permission_denied" ? "denied" : "error";
            summary = $"{method.Name}: {e.Message}";
            return Fail(e.Code, e.Message);
        }
        catch (OperationCanceledException)
        {
            verdict = "timeout";
            summary = $"{method.Name}: cancelled";
            CallAccounting.NoteAbandoned(counters);
            throw;
        }
        catch (EnvironmentException e)
        {
            verdict = e.Code is EnvironmentErrorCode.PermissionDenied or EnvironmentErrorCode.PathEscape ? "denied" : "error";
            var (code, text) = e.Code switch
            {
                EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory => ("not_found", "the path does not exist"),
                EnvironmentErrorCode.PermissionDenied => ("rejected", "permission denied by the file system"),
                EnvironmentErrorCode.PathEscape => ("rejected", "path escapes the workspace root"),
                EnvironmentErrorCode.TooLarge => ("invalid_argument", "the file is too large"),
                EnvironmentErrorCode.InvalidPath => ("invalid_argument", "the path is not valid"),
                _ => ("unavailable", "the execution environment could not complete the request"),
            };
            summary = $"{method.Name}: {text}";
            return Fail(code, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            verdict = "error";
            host.Services.Log.Warn($"[plugin] {host.Id}: {method.Name} failed", e);
            summary = $"{method.Name}: failed";
            return Fail("internal_error", "the host call failed");
        }
        finally
        {
            CallAccounting.Exit(counters);
            if (!method.Name.StartsWith("internal.", StringComparison.Ordinal))
            {
                host.Activity.Record(new PluginActivity(
                    clock.GetUtcNow().ToUnixTimeMilliseconds(), host.Id, method.Name, summary, verdict, (long)clock.GetElapsedTime(started).TotalMilliseconds));
            }
        }
    }

    private static string Ok(JsonNode? data) => new JsonObject { ["ok"] = true, ["data"] = data }.ToJsonString();

    private static string Fail(string code, string message) => new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message }.ToJsonString();

    // ───────────────────────── 注册类 ─────────────────────────

    /// <summary>命令必须是清单 <c>contributes.commands</c> 里声明过的:没声明的静默忽略(菜单、快捷键、命令面板都按 id 分发,允许运行期注册任意 id 等于让插件能劫持别人的命令),但留诊断。</summary>
    private static bool RegisterCommand(PluginHost host, string id)
    {
        if (!host.Manifest.Contributes.Commands.Any(c => c.Command == id))
        {
            host.Diagnostics.Add(new PluginDiagnostic(
                $"commands.{id}", PluginDiagnosticKeys.CommandUndeclared, $"registerCommand(\"{id}\") was ignored: the command is not declared in contributes.commands.", "warn",
                new Dictionary<string, string> { ["command"] = id }));
            return false;
        }

        host.Commands[id] = true;
        host.NotifyRegistrationsChanged();
        return true;
    }

    /// <summary>工具只能注册清单 <c>contributes.tools</c> 里声明的;描述消毒(进系统提示词,第三方来源 = 不可信输入),schema 体积封顶、不是对象的退回空对象。</summary>
    private static bool RegisterTool(PluginHost host, string name, string declarationJson)
    {
        if (!host.Manifest.Contributes.Tools.Any(t => t.Name == name))
        {
            host.Diagnostics.Add(new PluginDiagnostic(
                $"tools.{name}", PluginDiagnosticKeys.ToolUndeclared, $"registerTool(\"{name}\") was ignored: the tool is not declared in contributes.tools.", "warn",
                new Dictionary<string, string> { ["tool"] = name }));
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(declarationJson);
            var root = doc.RootElement;
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            description = PromptText.Clamp(PromptText.StripControlChars(description), MaxToolDescriptionChars);
            var schema = root.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetRawText() : "{\"type\":\"object\",\"properties\":{}}";
            if (schema.Length > MaxToolSchemaBytes)
            {
                host.Diagnostics.Add(new PluginDiagnostic($"tools.{name}", PluginDiagnosticKeys.ToolUndeclared, $"The input schema of \"{name}\" is larger than {MaxToolSchemaBytes} bytes; the tool was not registered.", "warn"));
                return false;
            }

            bool Flag(string property) => root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
            host.Tools[name] = new PluginToolDeclaration(name, description, schema, Flag("readOnly"), Flag("destructive"), Flag("needsNetwork"), Flag("interactive"));
        }
        catch (JsonException)
        {
            return false;
        }

        host.NotifyRegistrationsChanged();
        return true;
    }

    /// <summary>钩子需要对应的能力(<c>agent.intercept</c> / <c>agent.context</c>);拦截器<b>只能收紧</b>(接入口类型上没有 allow)。</summary>
    private static bool SetHook(PluginHost host, string kind, bool enabled)
    {
        var permission = kind switch
        {
            "interceptor" => PluginPermissions.AgentIntercept,
            "context" => PluginPermissions.AgentContext,
            _ => null,
        };
        if (permission is null)
        {
            return false;
        }

        if (enabled && !host.Permissions.Has(permission))
        {
            host.Diagnostics.Add(new PluginDiagnostic(
                $"agent.{kind}", PluginDiagnosticKeys.PermissionDenied, $"Registering a {kind} needs the \"{permission}\" permission, which has not been granted.", "warn",
                new Dictionary<string, string> { ["method"] = $"agent.{kind}", ["permission"] = permission }));
            return false;
        }

        if (kind == "interceptor")
        {
            host.Interceptor = enabled;
        }
        else
        {
            host.ContextProvider = enabled;
        }

        host.NotifyRegistrationsChanged();
        return true;
    }

    private static void Progress(PluginHost host, string callId, string json)
    {
        if (!host.Progress.TryGetValue(callId, out var sink))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? PromptText.Clamp(PromptText.StripControlChars(m.GetString()!), 500) : null;
            JsonElement? card = doc.RootElement.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Object ? c.Clone() : null;
            sink.Report(new ToolProgress(message, null, card));
        }
        catch (JsonException)
        {
            // 进度是易失的
        }
    }
}
