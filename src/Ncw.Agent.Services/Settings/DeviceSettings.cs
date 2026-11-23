using System.Text.Json.Nodes;

namespace Ncw.Agent.Services.Settings;

/// <summary>「模型别名 + 供应商」成对。<see cref="Model"/> 为空 = 没选(跟随默认);此时供应商无意义。</summary>
public sealed record ModelPick(string Model, string? ProviderId = null)
{
    public static readonly ModelPick None = new("");

    public bool IsSet => Model.Length > 0;
}

public sealed record SubagentSettings(ModelPick Model, string Thinking, int PerSessionLimit, int GlobalLimit);

public sealed record ProxySettings(bool Enabled, string Mode, string Scheme, string Host, int Port, bool AuthEnabled, string AuthUser, string Bypass);

public sealed record GatewaySettings(bool Enabled, bool AllowRemote, int PreferredPort);

public sealed record DataSettings(string? BackupDirectory, string BackupFrequency);

public sealed record PersonalizationSettings(string Name, string Background, string Instructions);

/// <summary>
/// 设备作用域的设置(2.x 的 <c>settings</c> 表第 1 行里归设备 / Agent 管的那些;本应用的外观、快捷键、通知在客户端,不在这里)。
/// 读不了或不认识的值一律按出厂值(设置文件是用户能手改的输入,绝不抛)。出厂值与 2.x 的 <c>DEFAULT_SETTINGS</c> 一致。
/// </summary>
public sealed record DeviceSettings(
    ModelPick DefaultModel,
    ModelPick PermissionReviewer,
    ModelPick GoalEvaluator,
    ModelPick Compact,
    string CompactThinking,
    ModelPick Completion,
    ModelPick InlineCompletionModel,
    ModelPick Image,
    ModelPick Video,
    ModelPick Audio,
    string DefaultPermissionMode,
    string ModelProposedGoals,
    bool AutoCompact,
    int MaxOutputTokens,
    bool PromptSuggestions,
    bool LocalCompletion,
    bool InlineCompletion,
    bool ImageGeneration,
    string Shell,
    int GoalCheckinMinutes,
    int UpstreamIdleTimeoutSeconds,
    bool AccountRotation,
    bool Failover,
    string BuiltinSearxngUrl,
    SubagentSettings Subagent,
    ProxySettings Proxy,
    GatewaySettings Gateway,
    DataSettings Data,
    PersonalizationSettings Personalization)
{
    public const int DefaultMaxOutputTokens = 32_000;
    public const int MinMaxOutputTokens = 1024;
    public const int MaxMaxOutputTokens = 200_000;
    public const int DefaultUpstreamIdleSeconds = 600;
    public const int MinUpstreamIdleSeconds = 60;
    public const int MaxUpstreamIdleSeconds = 3600;
    public const int DefaultGoalCheckinMinutes = 30;
    public const int DefaultGatewayPort = 19836;
    public const int PersonalizationNameMax = 64;
    public const int PersonalizationBackgroundMax = 2000;
    public const int PersonalizationInstructionsMax = 8000;
    public const string Inherit = "inherit";

    public static readonly IReadOnlyList<string> PermissionModes = ["ask", "auto", "full"];
    public static readonly IReadOnlyList<string> ProposedGoalPolicies = ["auto", "alwaysAsk", "disabled"];
    public static readonly IReadOnlyList<string> ThinkingLevels = ["auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"];
    public static readonly IReadOnlyList<string> Shells = ["system", "cmd", "powershell", "pwsh", "zsh", "bash", "fish", "sh"];
    public static readonly IReadOnlyList<string> ProxyModes = ["system", "manual"];
    public static readonly IReadOnlyList<string> ProxySchemes = ["http", "https", "socks5", "socks4"];
    public static readonly IReadOnlyList<string> BackupFrequencies = ["manual", "daily", "weekly"];

    /// <summary>出厂值(2.x 的 <c>DEFAULT_SETTINGS</c>)。</summary>
    public static DeviceSettings Default { get; } = new(
        ModelPick.None, ModelPick.None, ModelPick.None, ModelPick.None, Inherit,
        ModelPick.None, ModelPick.None, ModelPick.None, ModelPick.None, ModelPick.None,
        "ask", "auto", true, DefaultMaxOutputTokens,
        PromptSuggestions: true, LocalCompletion: true, InlineCompletion: false, ImageGeneration: true,
        "system", DefaultGoalCheckinMinutes, DefaultUpstreamIdleSeconds, AccountRotation: true, Failover: false, "",
        new SubagentSettings(ModelPick.None, Inherit, 4, 4),
        new ProxySettings(false, "system", "http", "", 0, false, "", ""),
        new GatewaySettings(false, false, DefaultGatewayPort),
        new DataSettings(null, "manual"),
        new PersonalizationSettings("", "", ""));

    /// <summary>
    /// 从设置行的 JSON 读出来:缺的、类型不对的、越界的一律按出厂值,绝不抛。
    /// 键名沿用 2.x(<c>defaultModel</c>、<c>contextManagement.autoCompact</c>、<c>gateway.failover</c>……),接管后原样可用。
    /// </summary>
    public static DeviceSettings From(JsonObject? root)
    {
        var d = Default;
        if (root is null)
        {
            return d;
        }

        var gateway = root["gateway"] as JsonObject;
        var subagent = root["subagent"] as JsonObject;
        var proxy = root["proxy"] as JsonObject;
        var data = root["data"] as JsonObject;
        var personalization = root["personalization"] as JsonObject;
        return new DeviceSettings(
            Pick(root, "defaultModel", "defaultModelProviderId"),
            Pick(root, "permissionReviewerModel", "permissionReviewerModelProviderId"),
            Pick(root, "goalEvaluatorModel", "goalEvaluatorModelProviderId"),
            Pick(root, "compactModel", "compactModelProviderId"),
            OneOf(Str(root, "compactThinking"), [Inherit, .. ThinkingLevels], d.CompactThinking),
            Pick(root, "completionModel", "completionModelProviderId"),
            Pick(root, "inlineCompletionModel", "inlineCompletionModelProviderId"),
            Pick(root, "imageModel", "imageModelProviderId"),
            Pick(root, "videoModel", "videoModelProviderId"),
            Pick(root, "audioModel", "audioModelProviderId"),
            OneOf(Str(root, "defaultPermissionMode"), PermissionModes, d.DefaultPermissionMode),
            OneOf(Str(root, "modelProposedGoals"), ProposedGoalPolicies, d.ModelProposedGoals),
            !(root["contextManagement"] is JsonObject cm && cm["autoCompact"] is JsonValue ac && ac.TryGetValue<bool>(out var autoCompact) && !autoCompact),
            InRange(Int(root, "maxOutputTokens"), MinMaxOutputTokens, MaxMaxOutputTokens) ?? d.MaxOutputTokens,
            Bool(root, "promptSuggestions") ?? d.PromptSuggestions,
            Bool(root, "localCompletion") ?? d.LocalCompletion,
            Bool(root, "inlineCompletion") ?? d.InlineCompletion,
            Bool(root, "imageGenerationEnabled") ?? d.ImageGeneration,
            OneOf(Str(root, "shell"), Shells, d.Shell),
            InRange(Int(root, "goalCheckinMinutes"), 1, 1440) ?? d.GoalCheckinMinutes,
            InRange(Int(root, "upstreamIdleTimeoutSeconds"), MinUpstreamIdleSeconds, MaxUpstreamIdleSeconds) ?? d.UpstreamIdleTimeoutSeconds,
            Bool(root, "providerAccountRotation") ?? d.AccountRotation,
            Bool(gateway, "failover") ?? d.Failover,
            root["builtinSearch"] is JsonObject bs ? Str(bs, "searxngUrl") ?? "" : "",
            new SubagentSettings(
                Pick(subagent, "model", "modelProviderId"),
                OneOf(Str(subagent, "thinking"), [Inherit, .. ThinkingLevels], d.Subagent.Thinking),
                InRange(Int(subagent, "perSessionLimit"), 1, 10) ?? d.Subagent.PerSessionLimit,
                InRange(Int(subagent, "globalLimit"), 0, 10) ?? d.Subagent.GlobalLimit),
            new ProxySettings(
                Bool(proxy, "enabled") ?? false,
                OneOf(Str(proxy, "mode"), ProxyModes, d.Proxy.Mode),
                OneOf(Str(proxy, "scheme"), ProxySchemes, d.Proxy.Scheme),
                Str(proxy, "host") ?? "",
                InRange(Int(proxy, "port"), 0, 65535) ?? 0,
                Bool(proxy, "authEnabled") ?? false,
                Str(proxy, "authUser") ?? "",
                Str(proxy, "bypass") ?? ""),
            new GatewaySettings(
                Bool(gateway, "enabled") ?? false,
                Bool(gateway, "allowRemote") ?? false,
                InRange(Int(gateway, "preferredPort"), 1, 65535) ?? DefaultGatewayPort),
            new DataSettings(
                Str(data, "backupDirectory") is { Length: > 0 } dir ? dir : null,
                OneOf(Str(data, "backupFrequency"), BackupFrequencies, d.Data.BackupFrequency)),
            new PersonalizationSettings(
                Clip(Str(personalization, "name") ?? "", PersonalizationNameMax),
                Clip(Str(personalization, "background") ?? "", PersonalizationBackgroundMax),
                Clip(Str(personalization, "instructions") ?? "", PersonalizationInstructionsMax)));
    }

    // ───────────────────────── 宽容读取 ─────────────────────────

    internal static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool? Bool(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static int? Int(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        return v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
    }

    private static ModelPick Pick(JsonObject? o, string modelKey, string providerKey) =>
        Str(o, modelKey) is { Length: > 0 } model ? new ModelPick(model, Str(o, providerKey) is { Length: > 0 } p ? p : null) : ModelPick.None;

    private static string OneOf(string? value, IReadOnlyList<string> allowed, string fallback) =>
        value is not null && allowed.Contains(value) ? value : fallback;

    private static int? InRange(int? value, int min, int max) => value is { } v && v >= min && v <= max ? v : null;

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
