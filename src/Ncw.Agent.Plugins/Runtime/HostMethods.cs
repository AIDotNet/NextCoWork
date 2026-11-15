using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 宿主 API 的方法表(<c>nextcowork</c> 的非 UI 部分,与 2.x <c>PluginMethodMap</c> 同名同形):<b>每一条都在这里逐个列出,没有通配</b>;
/// 每条声明所需能力(<see cref="HostMethod.Permission"/>,null = 不需要但仍要过白名单)。只有这张表里的方法才会被绑进引擎(<see cref="PluginBinder"/>),
/// 每个绑定调用时先过门:① 白名单(没绑定 = <c>unknown_method</c>)→ ② 授权(<b>实时</b>读授权状态,撤权立刻生效)→ ③ 参数收窄(<see cref="HostGuards"/> + realpath 守卫)→ ④ 行为审批(执行命令)。
/// 这些方法全部经 <see cref="IExecutionEnvironment"/> 访问文件和进程、经可注入的 <see cref="IPluginHttp"/> 联网,不直接碰设备本地。
/// <para>3.0 不实现:<c>scm.*</c>、<c>process.execStream</c>、<c>env.openExternal</c> / 剪贴板、<c>tabs.*</c> / <c>customEditors.*</c> / <c>documents.*</c>(界面类)、<c>plugins.*</c>(插件间通信)。</para>
/// </summary>
internal static class PluginHostMethods
{
    public const string Sleep = "internal.sleep";
    public const string NextAction = "internal.nextAction";

    public static readonly IReadOnlyList<HostMethod> All =
    [
        new("env.appInfo", null, EnvAppInfo),
        new("appearance.get", null, Appearance),
        new("permissions.contains", null, PermissionsContains),
        new("permissions.request", null, PermissionsRequest),
        new("permissions.remove", null, PermissionsRemove),
        new("configuration.get", null, ConfigurationGet),
        new("diagnostics.log", null, DiagnosticsLog),
        new("storage.get", PluginPermissions.Storage, StorageGet),
        new("storage.set", PluginPermissions.Storage, StorageSet),
        new("storage.keys", PluginPermissions.Storage, StorageKeys),
        new("secrets.get", PluginPermissions.Secrets, SecretsGet),
        new("secrets.set", PluginPermissions.Secrets, SecretsSet),
        new("workspace.folders", PluginPermissions.WorkspaceRead, WorkspaceFolders),
        new("workspace.readFile", PluginPermissions.WorkspaceRead, WorkspaceReadFile),
        new("workspace.stat", PluginPermissions.WorkspaceRead, WorkspaceStat),
        new("workspace.findFiles", PluginPermissions.WorkspaceRead, WorkspaceFindFiles),
        new("workspace.writeFile", PluginPermissions.WorkspaceWrite, WorkspaceWriteFile),
        new("workspace.deleteFile", PluginPermissions.WorkspaceWrite, WorkspaceDeleteFile),
        new("process.exec", PluginPermissions.Process, ProcessExec),
        new("net.fetch", PluginPermissions.Net, NetFetch),
        new("window.showMessage", PluginPermissions.WindowNotify, WindowShowMessage),
        new("window.showQuickPick", null, WindowShowQuickPick),
        new("window.showInputBox", null, WindowShowInputBox),
        new("window.showConfirm", null, WindowShowConfirm),
        new("window.setStatusBarItem", null, (_, _) => ValueTask.FromResult(new HostResult(new JsonObject(), "statusBar"))),
        new(Sleep, null, InternalSleep),
        new(NextAction, null, InternalNextAction),
    ];

    // ───────────────────────── 参数读取 ─────────────────────────

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string RequireStr(JsonElement args, string name) =>
        Str(args, name) ?? throw HostCallException.Invalid($"{name} is required");

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static long? Long(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static IReadOnlyList<string> StrArray(JsonElement args, string name)
    {
        var list = new List<string>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw HostCallException.Invalid($"{name} must be an array of strings");
                }

                list.Add(item.GetString()!);
            }
        }

        return list;
    }

    private static HostResult Result(JsonNode? data, string summary) => new(data, summary);

    private static ValueTask<HostResult> Done(JsonNode? data, string summary) => ValueTask.FromResult(Result(data, summary));

    // ───────────────────────── 不需要能力的 ─────────────────────────

    private static ValueTask<HostResult> EnvAppInfo(HostCall call, JsonElement args)
    {
        var info = call.Services.AppInfo;
        return Done(new JsonObject { ["appName"] = info.AppName, ["appVersion"] = info.AppVersion, ["language"] = info.Language }, "env.appInfo");
    }

    private static ValueTask<HostResult> Appearance(HostCall call, JsonElement args) => Done(new JsonObject { ["appearance"] = "light" }, "appearance.get");

    private static ValueTask<HostResult> PermissionsContains(HostCall call, JsonElement args)
    {
        var wanted = StrArray(args, "permissions");
        var granted = wanted.Count > 0 && wanted.All(PluginPermissions.IsValid) && call.Host.Permissions.HasAll(wanted);
        return Done(new JsonObject { ["granted"] = granted }, "permissions.contains");
    }

    /// <summary>
    /// ★ 只能要 <c>permissions ∪ optionalPermissions</c> 之内的东西。之外的<b>直接返回 false、不弹窗</b>:否则「市场审核看到的能力上界」就不等于「运行期可获得的能力上界」。
    /// 之内、还没批的:发一个 <c>permissions</c> 交互请求(带 reasonKey,l10n key 不是句子),用户点头才授予。
    /// </summary>
    private static async ValueTask<HostResult> PermissionsRequest(HostCall call, JsonElement args)
    {
        var wanted = StrArray(args, "permissions");
        var reasonKey = Str(args, "reasonKey") ?? "";
        var state = call.Host.Permissions;
        if (wanted.Count == 0 || wanted.Any(p => !PluginPermissions.IsValid(p)) || !state.CanRequest(wanted))
        {
            return Result(new JsonObject { ["granted"] = false }, "permissions.request (outside the manifest)");
        }

        if (state.HasAll(wanted))
        {
            return Result(new JsonObject { ["granted"] = true }, "permissions.request (already granted)");
        }

        var reply = await Ask(call, PluginInteractionKinds.Permissions, titleKey: reasonKey, permissions: wanted).ConfigureAwait(false);
        if (reply?.Confirmed != true)
        {
            return Result(new JsonObject { ["granted"] = false }, "permissions.request (declined)");
        }

        call.Services.Grant(call.Host.Id, wanted);
        return Result(new JsonObject { ["granted"] = call.Host.Permissions.HasAll(wanted) }, "permissions.request (granted)");
    }

    private static ValueTask<HostResult> PermissionsRemove(HostCall call, JsonElement args)
    {
        var wanted = StrArray(args, "permissions").Where(PluginPermissions.IsValid).ToList();
        if (wanted.Count > 0)
        {
            call.Services.Revoke(call.Host.Id, wanted);
        }

        return Done(new JsonObject(), "permissions.remove");
    }

    private static ValueTask<HostResult> ConfigurationGet(HostCall call, JsonElement args)
    {
        var values = new JsonObject();
        foreach (var (key, value) in call.Services.Configuration(call.Host.Id))
        {
            values[key] = value.Flag is { } b ? b : value.Number is { } n ? n : value.Text;
        }

        return Done(new JsonObject { ["values"] = values }, "configuration.get");
    }

    private static ValueTask<HostResult> DiagnosticsLog(HostCall call, JsonElement args)
    {
        var level = Str(args, "level") ?? "info";
        if (level is not ("info" or "warn" or "error"))
        {
            throw HostCallException.Invalid("level must be info, warn or error");
        }

        var message = Str(args, "message") ?? "";
        if (message.Length > call.Services.Limits.MaxLogChars)
        {
            message = message[..call.Services.Limits.MaxLogChars] + "…";
        }

        if (level != "info")
        {
            call.Host.Diagnostics.Add(new PluginDiagnostic("diagnostics.log", PluginDiagnosticKeys.PluginLog, message, level));
        }

        return Done(new JsonObject(), $"{level}: {(message.Length > 200 ? message[..200] : message)}");
    }

    // ───────────────────────── storage / secrets ─────────────────────────

    private static string? StorageWorkspace(HostCall call, JsonElement args)
    {
        var scope = Str(args, "scope") ?? "global";
        return scope switch
        {
            "global" => null,
            "workspace" => call.Scope?.WorkspaceId ?? throw HostCallException.Rejected("no workspace is open"),
            _ => throw HostCallException.Invalid("scope must be global or workspace"),
        };
    }

    private static string NarrowKey(string? key) =>
        HostGuards.NarrowStorageKey(key) is { Allowed: true } ok ? ok.Value! : throw HostCallException.Invalid(HostGuards.NarrowStorageKey(key).Reason!);

    private static ValueTask<HostResult> StorageGet(HostCall call, JsonElement args)
    {
        var key = NarrowKey(Str(args, "key"));
        var value = call.Services.Kv(call.Host.Id).Get(StorageWorkspace(call, args), key);
        return Done(new JsonObject { ["value"] = value }, $"storage.get {key}");
    }

    private static ValueTask<HostResult> StorageSet(HostCall call, JsonElement args)
    {
        var key = NarrowKey(Str(args, "key"));
        var workspace = StorageWorkspace(call, args);
        var value = args.TryGetProperty("value", out var v) ? v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            _ => throw HostCallException.Invalid("value must be a string or null"),
        } : throw HostCallException.Invalid("value is required");
        if (!call.Services.Kv(call.Host.Id).Set(workspace, key, value))
        {
            throw HostCallException.Invalid("plugin storage quota exceeded");
        }

        return Done(new JsonObject(), $"storage.set {key}");
    }

    private static ValueTask<HostResult> StorageKeys(HostCall call, JsonElement args)
    {
        var keys = new JsonArray([.. call.Services.Kv(call.Host.Id).Keys(StorageWorkspace(call, args)).Select(k => (JsonNode?)JsonValue.Create(k))]);
        return Done(new JsonObject { ["keys"] = keys }, "storage.keys");
    }

    private static ValueTask<HostResult> SecretsGet(HostCall call, JsonElement args)
    {
        var key = NarrowKey(Str(args, "key"));
        return Done(new JsonObject { ["value"] = call.Services.Secrets.Get(HostGuards.SecretReference(call.Host.Id, key)) }, $"secrets.get {key}");
    }

    private static ValueTask<HostResult> SecretsSet(HostCall call, JsonElement args)
    {
        var key = NarrowKey(Str(args, "key"));
        var reference = HostGuards.SecretReference(call.Host.Id, key);
        var value = args.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (value is null)
        {
            call.Services.Secrets.Remove(reference);
        }
        else
        {
            call.Services.Secrets.Set(reference, value);
        }

        call.Services.Kv(call.Host.Id).TrackSecret(key, value is not null);

        return Done(new JsonObject(), $"secrets.set {key}");
    }

    // ───────────────────────── 工作区文件 ─────────────────────────

    /// <summary>相对路径 → 环境里的绝对路径。词法门 + realpath 守卫(软链指向根外的、<c>..</c> 逃逸的一律拒绝)。★ 路径门不信插件:根是调用的作用域给的,不是插件传的。</summary>
    private static async ValueTask<(IExecutionEnvironment Env, EnvPath Root, EnvPath Absolute, string Relative)> ResolveAsync(HostCall call, string? relative)
    {
        var scope = call.RequireScope();
        if (scope.Environment is null || scope.Root is not { } root)
        {
            throw HostCallException.Rejected("no workspace is open");
        }

        var narrowed = HostGuards.NarrowRelativePath(relative);
        if (!narrowed.Allowed)
        {
            throw HostCallException.Invalid(narrowed.Reason!);
        }

        var resolution = await scope.Environment.Paths.ResolveAsync(root, narrowed.Value!, call.Token).ConfigureAwait(false);
        if (resolution.Outside)
        {
            throw HostCallException.Rejected("path escapes the workspace root");
        }

        return (scope.Environment, root, resolution.Absolute, narrowed.Value!.Replace('\\', '/'));
    }

    /// <summary>与内置写工具同一个键(并行批里的工具和插件写同一个文件时串行)。</summary>
    private static string MutationKey(EnvPath path) => path.Style == PathStyle.Windows ? path.Value.ToUpperInvariant() : path.Value;

    private static long Revision(FsEntryInfo info) => (info.ModifiedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private static ValueTask<HostResult> WorkspaceFolders(HostCall call, JsonElement args)
    {
        var scope = call.Scope;
        var folders = new JsonArray();
        if (scope is { WorkspaceId: { } id, Root: { } root })
        {
            folders.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = scope.WorkspaceName ?? id, ["path"] = root.Value });
        }

        return Done(new JsonObject { ["folders"] = folders }, "workspace.folders");
    }

    private static async ValueTask<HostResult> WorkspaceReadFile(HostCall call, JsonElement args)
    {
        var (env, _, absolute, relative) = await ResolveAsync(call, Str(args, "path")).ConfigureAwait(false);
        var encoding = Str(args, "encoding") ?? "utf8";
        if (encoding is not ("utf8" or "base64"))
        {
            throw HostCallException.Invalid("encoding must be utf8 or base64");
        }

        var info = await env.Fs.StatAsync(absolute, call.Token).ConfigureAwait(false);
        if (info.IsDirectory)
        {
            throw HostCallException.Invalid("path is a directory");
        }

        var limit = call.Services.Limits.MaxFileBytes;
        if (info.Size > limit)
        {
            throw HostCallException.Invalid("file exceeds the plugin read limit");
        }

        var bytes = await env.Fs.ReadBytesAsync(absolute, limit, call.Token).ConfigureAwait(false);
        var data = encoding == "base64" ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
        return Result(new JsonObject { ["data"] = data, ["revision"] = Revision(info) }, $"read {relative}");
    }

    private static async ValueTask<HostResult> WorkspaceStat(HostCall call, JsonElement args)
    {
        var (env, _, absolute, relative) = await ResolveAsync(call, Str(args, "path")).ConfigureAwait(false);
        try
        {
            var info = await env.Fs.StatAsync(absolute, call.Token).ConfigureAwait(false);
            return Result(new JsonObject { ["kind"] = info.IsDirectory ? "dir" : "file", ["size"] = info.Size, ["mtimeMs"] = info.ModifiedAt.ToUnixTimeMilliseconds() }, $"stat {relative}");
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return Result(new JsonObject { ["kind"] = "missing", ["size"] = 0, ["mtimeMs"] = 0 }, $"stat {relative}");
        }
    }

    private static async ValueTask<HostResult> WorkspaceFindFiles(HostCall call, JsonElement args)
    {
        var scope = call.RequireScope();
        if (scope.Environment is null || scope.Root is not { } root)
        {
            throw HostCallException.Rejected("no workspace is open");
        }

        var glob = Str(args, "glob");
        if (HostGuards.NarrowRelativePath(glob) is { Allowed: false } bad)
        {
            throw HostCallException.Invalid(bad.Reason!);
        }

        var limit = (int)Math.Clamp(Long(args, "limit") ?? 100, 1, 1000);
        var paths = await FindFiles.RunAsync(scope.Environment, root, glob!.Replace('\\', '/'), limit, call.Token).ConfigureAwait(false);
        return Result(new JsonObject { ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode?)JsonValue.Create(p))]) }, $"findFiles ({paths.Count})");
    }

    private static async ValueTask<HostResult> WorkspaceWriteFile(HostCall call, JsonElement args)
    {
        var (env, _, absolute, relative) = await ResolveAsync(call, Str(args, "path")).ConfigureAwait(false);
        var scope = call.RequireScope();
        if (scope.Tools?.WriteFence is { } fence && !string.Equals(fence.Value, absolute.Value, PathRules.Comparison(fence.Style)))
        {
            throw HostCallException.Rejected("plan mode may only modify its active plan file; no changes were made");
        }

        var encoding = Str(args, "encoding") ?? "utf8";
        var data = Str(args, "data") ?? throw HostCallException.Invalid("data is required");
        byte[] bytes;
        string? text = null;
        switch (encoding)
        {
            case "utf8":
                text = data;
                bytes = new UTF8Encoding(false).GetBytes(data);
                break;
            case "base64":
                try
                {
                    bytes = Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    throw HostCallException.Invalid("data is not valid base64");
                }

                break;
            default:
                throw HostCallException.Invalid("encoding must be utf8 or base64");
        }

        var limit = call.Services.Limits.MaxFileBytes;
        if (bytes.Length > limit)
        {
            throw HostCallException.Invalid("payload exceeds the plugin write limit");
        }

        var expectedRevision = Long(args, "revision");
        async ValueTask<long> WriteAsync(CancellationToken token)
        {
            FsEntryInfo? existing = null;
            try
            {
                existing = await env.Fs.StatAsync(absolute, token).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
            {
            }

            if (existing is { IsDirectory: true })
            {
                throw HostCallException.Invalid("path is a directory");
            }

            // 乐观锁:Agent 和插件可能在同时改同一个文件
            if (expectedRevision is { } expected && existing is { } current && Revision(current) != expected)
            {
                throw HostCallException.Rejected("the file changed on disk since it was read");
            }

            string? before = null;
            if (existing is { Size: <= 4 * 1024 * 1024 } && text is not null)
            {
                before = await env.Fs.ReadTextAsync(absolute, 4 * 1024 * 1024, token).ConfigureAwait(false);
            }

            await env.Fs.WriteStreamAsync(
                absolute, new MemoryStream(bytes, writable: false),
                new FsWriteStreamOptions(limit, UnixMode: existing is { UnixMode: not 0 } prior ? prior.UnixMode : null, CreateParents: true), token).ConfigureAwait(false);
            // 改动记录(回复底部的审查卡与撤销):只有在一个 Run 里(带工具运行时)的文本写入才记
            if (text is not null && scope.Tools is { } tools && scope.Run is { } run && (existing is null || before is not null))
            {
                tools.Changes.Record(run, absolute, relative, before, text, inWorkspace: true);
            }

            return Revision(await env.Fs.StatAsync(absolute, token).ConfigureAwait(false));
        }

        // 同一个文件的读-改-写串行(并行批里的工具和插件写同一个文件)
        var revision = scope.FileMutations is { } queue
            ? await queue.RunAsync(MutationKey(absolute), WriteAsync, call.Token).ConfigureAwait(false)
            : await WriteAsync(call.Token).ConfigureAwait(false);
        return Result(new JsonObject { ["revision"] = revision }, $"write {relative}");
    }

    private static async ValueTask<HostResult> WorkspaceDeleteFile(HostCall call, JsonElement args)
    {
        var (env, _, absolute, relative) = await ResolveAsync(call, Str(args, "path")).ConfigureAwait(false);
        var scope = call.RequireScope();
        if (scope.Tools?.WriteFence is not null)
        {
            throw HostCallException.Rejected("plan mode does not allow deleting files");
        }

        var info = await env.Fs.LStatAsync(absolute, call.Token).ConfigureAwait(false);
        if (info.IsDirectory)
        {
            throw HostCallException.Invalid("path is a directory");
        }

        await env.Fs.DeleteFileAsync(absolute, call.Token).ConfigureAwait(false);
        return Result(new JsonObject(), $"delete {relative}");
    }

    // ───────────────────────── 命令 ─────────────────────────

    /// <summary>
    /// 跑一条非交互命令。<c>argv[0]</c> 必须命中清单白名单;<b>每次调用都要用户批准</b>(审批带执行环境标识:设备 + 环境);批准之后再复检一次——
    /// 审批是在等人,这段时间里插件可能被禁用、权限可能被撤、调用可能被中止:批准只说明「他当时点了允许」,能不能真的跑由复检说了算。
    /// </summary>
    private static async ValueTask<HostResult> ProcessExec(HostCall call, JsonElement args)
    {
        var scope = call.RequireScope();
        if (scope.Environment is null || scope.Root is not { } root)
        {
            throw HostCallException.Rejected("no workspace is open");
        }

        var narrowed = HostGuards.NarrowCommand(call.Host.Manifest.AllowedCommands, Str(args, "command"), StrArray(args, "args"));
        if (!narrowed.Allowed)
        {
            throw HostCallException.Invalid(narrowed.Reason!);
        }

        var (command, commandArgs) = narrowed.Value;
        var cwd = Str(args, "cwd") is { } relativeCwd ? (await ResolveAsync(call, relativeCwd).ConfigureAwait(false)).Absolute : root;
        var line = string.Join(' ', new[] { command }.Concat(commandArgs.Select(Quote)));
        var reply = await Ask(call, PluginInteractionKinds.Exec, command: line, environment: scope.EnvironmentRef, danger: true).ConfigureAwait(false);
        if (reply?.Confirmed != true)
        {
            throw HostCallException.Rejected("the command was not approved");
        }

        call.Token.ThrowIfCancellationRequested();
        if (call.Host.Retired || !call.Host.Permissions.Has(PluginPermissions.Process))
        {
            throw HostCallException.Rejected("the plugin lost the process permission while waiting for approval");
        }

        var max = call.Services.Limits.MaxExecTimeout;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(Long(args, "timeoutMs") ?? 60_000, 1000, (long)max.TotalMilliseconds));
        var result = await scope.Environment.RunAsync(
            new CommandSpec { Program = command, Args = commandArgs, WorkingDirectory = cwd },
            new RunOptions { Timeout = timeout }, call.Token).ConfigureAwait(false);
        var limit = call.Services.Limits.MaxFileBytes;
        var code = result.Outcome switch
        {
            ProcessOutcome.TimedOut => 124,
            ProcessOutcome.Exited => result.ExitCode ?? -1,
            _ => -1,
        };
        return Result(new JsonObject
        {
            ["code"] = code,
            ["stdout"] = Clip(result.Stdout.ToText(), limit),
            ["stderr"] = Clip(result.Stderr.ToText(), limit),
        }, $"exec {command}");
    }

    private static string Quote(string arg) => arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':' or ',') ? arg : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Clip(string text, int limit) => text.Length > limit ? text[..limit] + "\n… (truncated)" : text;

    // ───────────────────────── 网络 ─────────────────────────

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    private static readonly string[] ForbiddenRequestHeaders = ["host", "content-length", "connection", "transfer-encoding", "upgrade", "te", "trailer", "proxy-authorization", "proxy-connection"];

    /// <summary>
    /// <c>net.fetch</c>:逐 URL 匹配 <c>hostPermissions</c>(只 https、无凭据、不是内网字面量);<b>重定向不自动跟</b>,每一跳重过同一道 URL 门,最多 5 跳,<b>跨 host 的跳转被拒</b>
    /// (跟着走等于让远端替插件改写 <c>hostPermissions</c>);正文按字节截到上限;回来的是数据不是流。
    /// </summary>
    private static async ValueTask<HostResult> NetFetch(HostCall call, JsonElement args)
    {
        var method = (Str(args, "method") ?? "GET").ToUpperInvariant();
        if (!Methods.Contains(method))
        {
            throw HostCallException.Invalid($"unsupported method: {method}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headerElement.EnumerateObject())
            {
                if (headers.Count >= HostGuards.MaxNetHeaderCount)
                {
                    break;
                }

                if (header.Value.ValueKind != JsonValueKind.String || ForbiddenRequestHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase)
                    || header.Name.Any(c => c <= ' ' || c is ':' or '\u007f') || Encoding.UTF8.GetByteCount(header.Value.GetString()!) > HostGuards.MaxNetHeaderValueBytes)
                {
                    continue;
                }

                headers[header.Name] = header.Value.GetString()!;
            }
        }

        var body = method is "GET" or "HEAD" ? null : Str(args, "body");
        var permissions = call.Host.Manifest.HostPermissions;
        var url = HostGuards.NarrowFetchUrl(permissions, Str(args, "url"));
        if (!url.Allowed)
        {
            throw HostCallException.Invalid(url.Reason!);
        }

        var current = url.Value!;
        var origin = current.Authority;
        var limit = call.Services.Limits.MaxFileBytes;
        for (var hop = 0; ; hop++)
        {
            var response = await call.Services.Http.SendAsync(new PluginHttpRequest(method, current, headers, body), limit, call.Token).ConfigureAwait(false);
            // 响应头名大小写不敏感(端口实现各自的字典不一定是)
            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in response.Headers)
            {
                responseHeaders[name] = value;
            }

            if (response.Status is 301 or 302 or 303 or 307 or 308 && responseHeaders.TryGetValue("location", out var location))
            {
                if (hop >= HostGuards.MaxNetRedirects)
                {
                    throw HostCallException.Invalid("too many redirects");
                }

                if (!Uri.TryCreate(current, location, out var next))
                {
                    throw HostCallException.Invalid("the redirect target is not a valid URL");
                }

                var checkedHop = HostGuards.NarrowFetchUrl(permissions, next.ToString());
                if (!checkedHop.Allowed || !string.Equals(checkedHop.Value!.Authority, origin, StringComparison.OrdinalIgnoreCase))
                {
                    throw HostCallException.Invalid("the redirect leaves the declared host; call the new address explicitly");
                }

                if (response.Status is 301 or 302 or 303 && method is not ("GET" or "HEAD"))
                {
                    method = "GET";
                    body = null;
                }

                current = checkedHop.Value;
                continue;
            }

            var headerOut = new JsonObject();
            foreach (var (name, value) in responseHeaders.Take(HostGuards.MaxNetHeaderCount))
            {
                if (Encoding.UTF8.GetByteCount(value) <= HostGuards.MaxNetHeaderValueBytes)
                {
                    headerOut[name.ToLowerInvariant()] = value;
                }
            }

            return Result(new JsonObject { ["status"] = response.Status, ["headers"] = headerOut, ["body"] = Encoding.UTF8.GetString(response.Body) }, $"fetch {current.Host}");
        }
    }

    // ───────────────────────── 交互(由控制端渲染)─────────────────────────

    private static async Task<PluginInteractionReply?> Ask(
        HostCall call, string kind, string? titleKey = null, string? detailKey = null, string? placeholderKey = null, IReadOnlyList<PluginPickItem>? items = null, bool password = false,
        bool danger = false, string? initial = null, IReadOnlyList<string>? permissions = null, string? command = null, PluginEnvironmentRef? environment = null)
    {
        var request = new PluginInteraction(
            "", call.Host.Id, kind, Key(titleKey), Key(detailKey), Key(placeholderKey), items ?? [], password, danger, initial, permissions ?? [], command, environment,
            call.Services.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        return await call.Services.Ask(request, call.Token).ConfigureAwait(false);
    }

    /// <summary>界面文字一律是 l10n key,不是句子:插件传进来的串过长 / 含控制字符的丢掉。</summary>
    private static string? Key(string? value) => value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl) ? value : null;

    private static ValueTask<HostResult> WindowShowMessage(HostCall call, JsonElement args)
    {
        var kind = Str(args, "kind") ?? "info";
        if (kind is not ("info" or "warn" or "error"))
        {
            throw HostCallException.Invalid("kind must be info, warn or error");
        }

        // 3.0 没有承载消息的 UI 通道:记进活动日志(插件详情页的「活动」),不弹窗
        return Done(new JsonObject(), $"message {kind} {Key(Str(args, "messageKey")) ?? "?"}");
    }

    private static async ValueTask<HostResult> WindowShowQuickPick(HostCall call, JsonElement args)
    {
        var items = new List<PluginPickItem>();
        if (args.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(100))
            {
                if (Str(item, "id") is { Length: > 0 } id && Key(Str(item, "labelKey")) is { } label)
                {
                    items.Add(new PluginPickItem(id, label));
                }
            }
        }

        var reply = await Ask(call, PluginInteractionKinds.Pick, placeholderKey: Str(args, "placeholderKey"), items: items).ConfigureAwait(false);
        var picked = reply?.PickedId is { } id2 && items.Any(i => i.Id == id2) ? id2 : null;
        return Result(new JsonObject { ["id"] = picked }, "showQuickPick");
    }

    private static async ValueTask<HostResult> WindowShowInputBox(HostCall call, JsonElement args)
    {
        var reply = await Ask(call, PluginInteractionKinds.Input, titleKey: Str(args, "titleKey"), placeholderKey: Str(args, "placeholderKey"), password: Bool(args, "password") == true, initial: Str(args, "initial")).ConfigureAwait(false);
        // ★ password: true 的值不进活动日志(摘要里本来就没有参数原文)
        return Result(new JsonObject { ["value"] = reply?.Text }, "showInputBox");
    }

    private static async ValueTask<HostResult> WindowShowConfirm(HostCall call, JsonElement args)
    {
        var reply = await Ask(call, PluginInteractionKinds.Confirm, titleKey: Str(args, "titleKey"), detailKey: Str(args, "detailKey"), danger: Bool(args, "danger") == true).ConfigureAwait(false);
        return Result(new JsonObject { ["confirmed"] = reply?.Confirmed == true }, "showConfirm");
    }

    // ───────────────────────── 内部(不对应 2.x 的方法)─────────────────────────

    private static async ValueTask<HostResult> InternalSleep(HostCall call, JsonElement args)
    {
        var ms = Math.Clamp(Long(args, "ms") ?? 0, 0, 60_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Token, call.Host.SleepToken);
        await Task.Delay(TimeSpan.FromMilliseconds(ms), call.Services.Clock, linked.Token).ConfigureAwait(false);
        return Result(null, "sleep");
    }

    /// <summary>实时卡片的动作泵:挂起到用户点了按钮(<c>plugins.cardAction</c>)或这次工具调用结束(通道关闭 → null)。</summary>
    private static async ValueTask<HostResult> InternalNextAction(HostCall call, JsonElement args)
    {
        var callId = RequireStr(args, "callId");
        if (call.Host.ActionsOf(callId) is not { } reader)
        {
            return Result(null, "nextAction");
        }

        try
        {
            var raw = await reader.ReadAsync(call.Token).ConfigureAwait(false);
            return Result(raw is null ? null : JsonNode.Parse(raw), "nextAction");
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return Result(null, "nextAction");
        }
    }
}
