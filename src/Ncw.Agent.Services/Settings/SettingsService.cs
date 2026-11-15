using System.Text.Json.Nodes;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Settings;

/// <summary>设置补丁不合法(整个补丁都不生效)。<see cref="Key"/> 是稳定的 <c>settings.*</c> key(控制端翻译),<see cref="Field"/> 是出问题的字段。</summary>
public sealed class SettingsException(string key, string field, string message) : Exception(message)
{
    public string Key { get; } = key;

    public string Field { get; } = field;
}

public sealed record SubagentSettingsPatch(ModelPick? Model = null, string? Thinking = null, int? PerSessionLimit = null, int? GlobalLimit = null);

public sealed record ProxySettingsPatch(bool? Enabled = null, string? Mode = null, string? Scheme = null, string? Host = null, int? Port = null, bool? AuthEnabled = null, string? AuthUser = null, string? Bypass = null);

public sealed record GatewaySettingsPatch(bool? Enabled = null, bool? AllowRemote = null, int? PreferredPort = null);

public sealed record DataSettingsPatch(string? BackupDirectory = null, string? BackupFrequency = null);

public sealed record PersonalizationPatch(string? Name = null, string? Background = null, string? Instructions = null);

/// <summary>设置补丁:字段缺省(null)= 不改;嵌套块深合并;「模型 + 供应商」成对整对写。</summary>
public sealed record DeviceSettingsPatch
{
    public ModelPick? DefaultModel { get; init; }
    public ModelPick? PermissionReviewer { get; init; }
    public ModelPick? GoalEvaluator { get; init; }
    public ModelPick? Compact { get; init; }
    public string? CompactThinking { get; init; }
    public ModelPick? Completion { get; init; }
    public ModelPick? InlineCompletionModel { get; init; }
    public ModelPick? Image { get; init; }
    public string? DefaultPermissionMode { get; init; }
    public string? ModelProposedGoals { get; init; }
    public bool? AutoCompact { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool? PromptSuggestions { get; init; }
    public bool? LocalCompletion { get; init; }
    public bool? InlineCompletion { get; init; }
    public bool? ImageGeneration { get; init; }
    public string? Shell { get; init; }
    public int? GoalCheckinMinutes { get; init; }
    public int? UpstreamIdleTimeoutSeconds { get; init; }
    public bool? AccountRotation { get; init; }
    public bool? Failover { get; init; }
    public string? BuiltinSearxngUrl { get; init; }
    public SubagentSettingsPatch? Subagent { get; init; }
    public ProxySettingsPatch? Proxy { get; init; }
    public GatewaySettingsPatch? Gateway { get; init; }
    public DataSettingsPatch? Data { get; init; }
    public PersonalizationPatch? Personalization { get; init; }
}

/// <summary>
/// 设备设置的读写(<c>settings</c> 表第 1 行)。规则:
/// <list type="bullet">
/// <item><b>写是读-改-写在一个事务里</b>,3.0 不认识的键(外观、主题工作室、通知……2.x 的本应用设置)原样保留,不被抹掉。</item>
/// <item><b>补丁先整体校验再落盘</b>:一项不合法整个补丁都不生效(坏值不许落库,也不许悄悄当成别的值生效)。</item>
/// <item><b>深合并</b>:嵌套块只改给出的那一项。</item>
/// <item><b>每次现读</b>(<see cref="Current"/>):没有缓存,改完立即对下一个 Run / 下一次请求生效。</item>
/// <item>变更计数 <see cref="Rev"/> 单调递增(进程内),<see cref="Changed"/> 在落盘之后触发(权威广播)。</item>
/// </list>
/// </summary>
public sealed class SettingsService(SettingsRepository repository)
{
    private readonly Lock _gate = new();
    private long _rev = 1;

    public long Rev
    {
        get
        {
            lock (_gate)
            {
                return _rev;
            }
        }
    }

    /// <summary>落盘之后触发(参数:新版本号、新值)。在写入的锁外调用。</summary>
    public event Action<long, DeviceSettings>? Changed;

    /// <summary>现读。读不了(库没就绪、坏 JSON)时是出厂值。</summary>
    public DeviceSettings Current() => DeviceSettings.From(ReadRoot());

    /// <summary>当前版本号 + 值(取版本号在读之前:值至少和版本一样新,更新的变更随后作为事件到达)。</summary>
    public (long Rev, DeviceSettings Settings) Snapshot()
    {
        var rev = Rev;
        return (rev, Current());
    }

    public DeviceSettings Update(DeviceSettingsPatch patch)
    {
        (long Rev, DeviceSettings Settings)? changed = null;
        DeviceSettings result;
        lock (_gate)
        {
            var before = Current();
            var json = repository.Update(current =>
            {
                var root = ParseRoot(current) ?? new JsonObject();
                Apply(root, patch);
                return root.ToJsonString();
            });
            result = DeviceSettings.From(ParseRoot(json));
            if (result != before)
            {
                _rev++;
                changed = (_rev, result);
            }
        }

        if (changed is { } c)
        {
            Changed?.Invoke(c.Rev, c.Settings);
        }

        return result;
    }

    // ───────────────────────── 读 ─────────────────────────

    private JsonObject? ReadRoot()
    {
        try
        {
            return ParseRoot(repository.ReadJson());
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or StorageException)
        {
            return null;
        }
    }

    internal static JsonObject? ParseRoot(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ───────────────────────── 补丁(校验 + 深合并) ─────────────────────────

    /// <summary>校验并合并进 <paramref name="root"/>。先全部校验(抛 <see cref="SettingsException"/>),再写;所以抛出时 root 没有被改动一半。</summary>
    internal static void Apply(JsonObject root, DeviceSettingsPatch p)
    {
        Validate(p);

        SetPick(root, "defaultModel", "defaultModelProviderId", p.DefaultModel);
        SetPick(root, "permissionReviewerModel", "permissionReviewerModelProviderId", p.PermissionReviewer);
        SetPick(root, "goalEvaluatorModel", "goalEvaluatorModelProviderId", p.GoalEvaluator);
        SetPick(root, "compactModel", "compactModelProviderId", p.Compact);
        SetPick(root, "completionModel", "completionModelProviderId", p.Completion);
        SetPick(root, "inlineCompletionModel", "inlineCompletionModelProviderId", p.InlineCompletionModel);
        SetPick(root, "imageModel", "imageModelProviderId", p.Image);
        Set(root, "compactThinking", p.CompactThinking);
        Set(root, "defaultPermissionMode", p.DefaultPermissionMode);
        Set(root, "modelProposedGoals", p.ModelProposedGoals);
        if (p.AutoCompact is { } autoCompact)
        {
            Section(root, "contextManagement")["autoCompact"] = autoCompact;
        }

        Set(root, "maxOutputTokens", p.MaxOutputTokens);
        Set(root, "promptSuggestions", p.PromptSuggestions);
        Set(root, "localCompletion", p.LocalCompletion);
        Set(root, "inlineCompletion", p.InlineCompletion);
        Set(root, "imageGenerationEnabled", p.ImageGeneration);
        Set(root, "shell", p.Shell);
        Set(root, "goalCheckinMinutes", p.GoalCheckinMinutes);
        Set(root, "upstreamIdleTimeoutSeconds", p.UpstreamIdleTimeoutSeconds);
        Set(root, "providerAccountRotation", p.AccountRotation);
        if (p.Failover is { } failover)
        {
            Section(root, "gateway")["failover"] = failover;
        }

        if (p.BuiltinSearxngUrl is { } searxng)
        {
            Section(root, "builtinSearch")["searxngUrl"] = searxng.Trim();
        }

        if (p.Subagent is { } sub)
        {
            var o = Section(root, "subagent");
            SetPick(o, "model", "modelProviderId", sub.Model);
            Set(o, "thinking", sub.Thinking);
            Set(o, "perSessionLimit", sub.PerSessionLimit);
            Set(o, "globalLimit", sub.GlobalLimit);
        }

        if (p.Proxy is { } proxy)
        {
            var o = Section(root, "proxy");
            Set(o, "enabled", proxy.Enabled);
            Set(o, "mode", proxy.Mode);
            Set(o, "scheme", proxy.Scheme);
            Set(o, "host", proxy.Host?.Trim());
            Set(o, "port", proxy.Port);
            Set(o, "authEnabled", proxy.AuthEnabled);
            Set(o, "authUser", proxy.AuthUser);
            Set(o, "bypass", proxy.Bypass);
        }

        if (p.Gateway is { } gateway)
        {
            var o = Section(root, "gateway");
            Set(o, "enabled", gateway.Enabled);
            Set(o, "allowRemote", gateway.AllowRemote);
            Set(o, "preferredPort", gateway.PreferredPort);
        }

        if (p.Data is { } data)
        {
            var o = Section(root, "data");
            if (data.BackupDirectory is { } dir)
            {
                o["backupDirectory"] = dir.Length == 0 ? null : dir;
            }

            Set(o, "backupFrequency", data.BackupFrequency);
        }

        if (p.Personalization is { } per)
        {
            // 三栏会被原样拼进系统提示词的稳定前缀:落库前就截断(不只在输入框上),绕过界面的调用也撑不爆它。
            var o = Section(root, "personalization");
            Set(o, "name", per.Name is null ? null : DeviceSettings.Clip(per.Name, DeviceSettings.PersonalizationNameMax));
            Set(o, "background", per.Background is null ? null : DeviceSettings.Clip(per.Background, DeviceSettings.PersonalizationBackgroundMax));
            Set(o, "instructions", per.Instructions is null ? null : DeviceSettings.Clip(per.Instructions, DeviceSettings.PersonalizationInstructionsMax));
        }
    }

    private static void Validate(DeviceSettingsPatch p)
    {
        foreach (var (field, pick) in new (string, ModelPick?)[]
        {
            ("defaultModel", p.DefaultModel), ("permissionReviewer", p.PermissionReviewer), ("goalEvaluator", p.GoalEvaluator), ("compact", p.Compact),
            ("completion", p.Completion), ("inlineCompletionModel", p.InlineCompletionModel), ("image", p.Image), ("subagent.model", p.Subagent?.Model),
        })
        {
            if (pick is not null && (pick.Model.Length > 256 || (pick.ProviderId?.Length ?? 0) > 256 || pick.Model.Trim() != pick.Model))
            {
                throw new SettingsException("settings.invalid.model", field, $"'{field}' is not a valid model reference.");
            }
        }

        OneOf("compactThinking", p.CompactThinking, [DeviceSettings.Inherit, .. DeviceSettings.ThinkingLevels]);
        OneOf("defaultPermissionMode", p.DefaultPermissionMode, DeviceSettings.PermissionModes);
        OneOf("modelProposedGoals", p.ModelProposedGoals, DeviceSettings.ProposedGoalPolicies);
        OneOf("shell", p.Shell, DeviceSettings.Shells);
        Range("maxOutputTokens", p.MaxOutputTokens, DeviceSettings.MinMaxOutputTokens, DeviceSettings.MaxMaxOutputTokens);
        Range("goalCheckinMinutes", p.GoalCheckinMinutes, 1, 1440);
        Range("upstreamIdleTimeoutSeconds", p.UpstreamIdleTimeoutSeconds, DeviceSettings.MinUpstreamIdleSeconds, DeviceSettings.MaxUpstreamIdleSeconds);
        if (p.BuiltinSearxngUrl is { Length: > 0 } url && (url.Length > 2048 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new SettingsException("settings.invalid.url", "builtinSearxngUrl", "The SearXNG address must be an http(s) URL.");
        }

        if (p.Subagent is { } sub)
        {
            OneOf("subagent.thinking", sub.Thinking, [DeviceSettings.Inherit, .. DeviceSettings.ThinkingLevels]);
            Range("subagent.perSessionLimit", sub.PerSessionLimit, 1, 10);
            Range("subagent.globalLimit", sub.GlobalLimit, 0, 10);
        }

        if (p.Proxy is { } proxy)
        {
            OneOf("proxy.mode", proxy.Mode, DeviceSettings.ProxyModes);
            OneOf("proxy.scheme", proxy.Scheme, DeviceSettings.ProxySchemes);
            Range("proxy.port", proxy.Port, 0, 65535);
            if ((proxy.Host?.Length ?? 0) > 253 || (proxy.AuthUser?.Length ?? 0) > 256 || (proxy.Bypass?.Length ?? 0) > 8192)
            {
                throw new SettingsException("settings.invalid.tooLong", "proxy", "A proxy field is too long.");
            }
        }

        if (p.Gateway is { } gateway)
        {
            Range("gateway.preferredPort", gateway.PreferredPort, 1, 65535);
        }

        if (p.Data is { } data)
        {
            OneOf("data.backupFrequency", data.BackupFrequency, DeviceSettings.BackupFrequencies);
            if ((data.BackupDirectory?.Length ?? 0) > 4096 || data.BackupDirectory?.Contains('\0') == true)
            {
                throw new SettingsException("settings.invalid.path", "data.backupDirectory", "The backup directory is not a valid path.");
            }
        }
    }

    private static void OneOf(string field, string? value, IReadOnlyList<string> allowed)
    {
        if (value is not null && !allowed.Contains(value))
        {
            throw new SettingsException("settings.invalid.choice", field, $"'{value}' is not an allowed value for '{field}'.");
        }
    }

    private static void Range(string field, int? value, int min, int max)
    {
        if (value is { } v && (v < min || v > max))
        {
            throw new SettingsException("settings.invalid.range", field, $"'{field}' must be between {min} and {max}.");
        }
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static void Set(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static void Set(JsonObject o, string key, int? value)
    {
        if (value is { } v)
        {
            o[key] = v;
        }
    }

    private static void Set(JsonObject o, string key, bool? value)
    {
        if (value is { } v)
        {
            o[key] = v;
        }
    }

    /// <summary>成对写:别名给了,供应商就跟着给的走(没给 = 取消锁定,键删掉),不留「新别名 + 旧供应商」。别名为空 = 清掉这一对。</summary>
    private static void SetPick(JsonObject o, string modelKey, string providerKey, ModelPick? pick)
    {
        if (pick is null)
        {
            return;
        }

        o[modelKey] = pick.Model;
        if (pick.IsSet && !string.IsNullOrEmpty(pick.ProviderId))
        {
            o[providerKey] = pick.ProviderId;
        }
        else
        {
            o.Remove(providerKey);
        }
    }
}
