using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Skills;
using Ncw.Agent.Skills.Commands;
using Ncw.Agent.Skills.Install;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// Skills 与命令在 Host 里的装配:把设备事实(数据根、本机环境、项目登记、kv、工作区设置)适配成服务层的注入接口。
/// 本层是唯一同时认识 <see cref="ExtensionHostContext"/> 和服务层的地方;服务层不知道 kv / SQLite / 协议。
/// </summary>
internal static class SkillsHost
{
    /// <param name="market">市场目录的 HTTP 抓取。缺省(null)= 真实的 <see cref="HttpSkillMarketClient"/>(默认 origin)。账户访问令牌来自 <paramref name="accessToken"/>:
    /// 令牌缺省取自 <c>account.attachDelegatedToken</c> 交付的委托账户(<see cref="DelegatedAccount"/>);还没交付(或已过期)= 没有令牌 → 市场安装返回 <c>skills.authRequired</c>,浏览照常;交付之后不用重启就恢复。</param>
    /// <param name="plugins">插件贡献的 Skill(插件宿主以后实现 <see cref="ISkillPluginSource"/>;现在没有)。</param>
    public static SkillsService CreateSkills(ExtensionHostContext context, ISkillMarketClient? market = null, ISkillPluginSource? plugins = null, Func<string?>? accessToken = null)
    {
        var skills = new SkillsService(
            new HostSkillLocations(context, plugins),
            new HostSkillState(context),
            new HostSkillUsage(context.Kv, context.Clock),
            market ?? new HttpSkillMarketClient(context.Services.MarketHttpClient ?? MarketHttp.Instance, accessToken ?? (() => context.Services.ResolveAccount(context.Clock).AccessToken)),
            context.Log);
        return skills;
    }

    public static CommandsService CreateCommands(ExtensionHostContext context, Func<IReadOnlyList<Ncw.Agent.Kernel.Extensions.CommandDefinition>>? registered = null) =>
        new(new HostCommandLocations(context), new HostCommandSwitches(context.Kv), registered);

    /// <summary>
    /// 启动时把随应用发布的内置 Skill(<c>resources/skills</c>)铺到全局 Skill 目录。源目录不存在 = 没有内置 Skill(诊断,不是错误)。
    /// 失败只记日志,启动继续。
    /// </summary>
    public static async Task InstallBundledAsync(ExtensionHostContext context, string bundledRoot, CancellationToken ct = default)
    {
        var style = context.LocalEnvironment.Facts.PathStyle;
        var diagnostics = await BundledSkills.InstallAsync(
            context.LocalEnvironment, EnvPath.Parse(bundledRoot, style), context.LocalEnvironment, SkillScanInput.GlobalSkillsRoot(context.GlobalPath()), ct).ConfigureAwait(false);
        foreach (var diagnostic in diagnostics)
        {
            context.Log.Warn($"[skill] bundled: {diagnostic.Path}: {diagnostic.Message}");
        }
    }
}

/// <summary>市场请求用的 HttpClient(进程内一份):跟随少量重定向(下载常重定向到 CDN),自动解压,有总超时。</summary>
internal static class MarketHttp
{
    public static HttpClient Instance { get; } = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxAutomaticRedirections = 3,
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };
}

/// <summary>全局层在设备本机(<c>&lt;数据根&gt;/skills</c>),项目层在项目的执行环境里(<c>&lt;项目&gt;/.next-cowork/skills</c>)。</summary>
internal sealed class HostSkillLocations(ExtensionHostContext context, ISkillPluginSource? plugins) : ISkillLocations
{
    public ValueTask<SkillScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct)
    {
        var local = context.LocalEnvironment;
        var global = SkillScanInput.GlobalSkillsRoot(context.GlobalPath());
        var pluginRoots = plugins.CurrentOrEmpty();
        if (workspaceId is null)
        {
            return ValueTask.FromResult(new SkillScanInput(local, global, null, null, pluginRoots));
        }

        // 项目环境暂时不可用(SSH 没连上):协议错误原样往上——那不是「没有 Skill」。
        var target = context.Project(workspaceId);
        return ValueTask.FromResult(new SkillScanInput(local, global, target.Environment, SkillScanInput.ProjectSkillsRoot(target.Root), pluginRoots));
    }

    public ValueTask<SkillInstallRoot> InstallRootAsync(SkillInstallScope scope, string? workspaceId, CancellationToken ct)
    {
        if (scope == SkillInstallScope.Global)
        {
            return ValueTask.FromResult(new SkillInstallRoot(context.LocalEnvironment, SkillScanInput.GlobalSkillsRoot(context.GlobalPath())));
        }

        if (workspaceId is null)
        {
            throw new SkillException(SkillErrorKeys.WorkspaceRequired, "A project install needs a workspace.");
        }

        var target = context.Project(workspaceId);
        return ValueTask.FromResult(new SkillInstallRoot(target.Environment, SkillScanInput.ProjectSkillsRoot(target.Root)));
    }
}

/// <summary>
/// 开关状态:全局开关是 kv 表 <c>skills.disabled</c>(JSON 字符串数组,存被关掉的那些,与 2.x 同);工作区选装是工作区设置里的 <c>activeSkillIds</c> + <c>skillSelectionMode</c>
/// (<c>explicit</c> / 缺省,与 2.x 同;3.0 不认识的设置键由 <c>WorkspaceRepository.UpdateSettings</c> 原样保留)。
/// </summary>
internal sealed class HostSkillState(ExtensionHostContext context) : ISkillStateStore
{
    public const string DisabledKey = "skills.disabled";

    private readonly Lock _gate = new();
    private readonly WorkspaceRepository _workspaces = new(context.Database, context.Clock);

    public IReadOnlySet<string> DisabledGlobally()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public void SetGlobalEnabled(string skillId, bool enabled)
    {
        lock (_gate)
        {
            var names = new HashSet<string>(Load(), StringComparer.Ordinal);
            if (enabled ? !names.Remove(skillId) : !names.Add(skillId))
            {
                return;
            }

            context.Kv.Set(DisabledKey, new JsonArray([.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n))]).ToJsonString());
        }
    }

    private HashSet<string> Load()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (context.Kv.Get(DisabledKey) is { } json && JsonNode.Parse(json) is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue<string>(out var name) && name.Length > 0)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // kv 是用户能手改的:一个坏值不该让开关整个失灵
        }

        return names;
    }

    public WorkspaceSkillSelection? Selection(string workspaceId)
    {
        if (_workspaces.Get(workspaceId) is not { } record)
        {
            return null;
        }

        var settings = record.Json["settings"] as JsonObject;
        var ids = new List<string>();
        if (settings?["activeSkillIds"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var id))
                {
                    ids.Add(id);
                }
            }
        }

        return new WorkspaceSkillSelection(ids, settings?["skillSelectionMode"] is JsonValue mode && mode.TryGetValue<string>(out var m) && m == "explicit");
    }

    public void SetSelection(string workspaceId, IReadOnlyList<string> ids, bool explicitSelection) =>
        _workspaces.UpdateSettings(workspaceId, settings =>
        {
            settings["activeSkillIds"] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]);
            if (explicitSelection)
            {
                settings["skillSelectionMode"] = "explicit";
            }
            else
            {
                settings.Remove("skillSelectionMode");
            }
        });
}

/// <summary>
/// Skill 用量(kv 表 <c>skills.stats</c>,形状与 2.x 同:<c>{ id: { count, lastTriggeredAt, workspaces: { ws: n }, lastTriggeredAtByWorkspace: { ws: ms } } }</c>)。
/// 遥测永不阻断一次取用(<see cref="Ncw.Agent.Skills.SkillTool"/> 吞掉这里的异常),所以这里不需要自己兜底。
/// </summary>
internal sealed class HostSkillUsage(KvStore kv, TimeProvider clock) : ISkillUsage
{
    public const string Key = "skills.stats";

    private readonly Lock _gate = new();

    public void Record(string skillId, string? workspaceId)
    {
        lock (_gate)
        {
            var root = Read();
            var entry = root[skillId] as JsonObject ?? new JsonObject();
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            entry["count"] = Count(entry["count"]) + 1;
            entry["lastTriggeredAt"] = now;
            if (workspaceId is not null)
            {
                var workspaces = entry["workspaces"] as JsonObject ?? new JsonObject();
                workspaces[workspaceId] = Count(workspaces[workspaceId]) + 1;
                entry["workspaces"] = workspaces;
                var times = entry["lastTriggeredAtByWorkspace"] as JsonObject ?? new JsonObject();
                times[workspaceId] = now;
                entry["lastTriggeredAtByWorkspace"] = times;
            }

            root[skillId] = entry;
            kv.Set(Key, root.ToJsonString());
        }
    }

    public IReadOnlyDictionary<string, SkillUsage> Usage(string? workspaceId)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, SkillUsage>(StringComparer.Ordinal);
            foreach (var (id, node) in Read())
            {
                if (node is not JsonObject entry)
                {
                    continue;
                }

                result[id] = workspaceId is null
                    ? new SkillUsage(Count(entry["count"]), Long(entry["lastTriggeredAt"]))
                    : new SkillUsage(Count((entry["workspaces"] as JsonObject)?[workspaceId]), Long((entry["lastTriggeredAtByWorkspace"] as JsonObject)?[workspaceId]));
            }

            return result;
        }
    }

    private JsonObject Read()
    {
        try
        {
            return kv.Get(Key) is { } json && JsonNode.Parse(json) is JsonObject root ? root : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static int Count(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;

    private static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var n) ? n : 0;
}

internal sealed class HostCommandLocations(ExtensionHostContext context) : ICommandLocations
{
    public ValueTask<CommandScanInput> ScanInputAsync(string? workspaceId, CancellationToken ct)
    {
        var local = context.LocalEnvironment;
        var global = CommandScanInput.GlobalCommandsRoot(context.GlobalPath());
        if (workspaceId is null)
        {
            return ValueTask.FromResult(new CommandScanInput(local, global, null, null));
        }

        var target = context.Project(workspaceId);
        return ValueTask.FromResult(new CommandScanInput(local, global, target.Environment, CommandScanInput.ProjectCommandsRoot(target.Root)));
    }
}

/// <summary>被关掉的命令名(kv 表 <c>commands.disabled</c>,与 2.x 同):每机器偏好,存被关掉的那些。</summary>
internal sealed class HostCommandSwitches(KvStore kv) : ICommandSwitchStore
{
    public const string Key = "commands.disabled";

    private readonly Lock _gate = new();

    public IReadOnlySet<string> Disabled()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            var names = new HashSet<string>(Load(), StringComparer.Ordinal);
            if (enabled ? !names.Remove(name) : !names.Add(name))
            {
                return;
            }

            kv.Set(Key, new JsonArray([.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n))]).ToJsonString());
        }
    }

    private HashSet<string> Load()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (kv.Get(Key) is { } json && JsonNode.Parse(json) is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue<string>(out var name) && name.Length > 0)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }
}
