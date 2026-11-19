using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>「编辑模型」弹窗要的设备命令(<see cref="ProvidersActions"/> 实现;单测换成假的)。</summary>
public interface IModelEditorCommands
{
    Task<ActionResult<ModelAliasDto>> UpdateModelAsync(string deviceId, ModelUpdateParams update, CancellationToken ct = default);

    Task<ActionResult<ModelAliasDto>> RenameModelAsync(string deviceId, string providerId, string alias, string nextAlias, CancellationToken ct = default);
}

/// <summary>「支持的强度」里的一格。<see cref="Value"/> 是线上名(<c>none</c> = 可以关掉思考)。</summary>
public sealed partial class EffortChoice(string value, bool selected) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
}

/// <summary>
/// 「编辑模型」弹窗(docs/design/07「编辑模型」):别名、显示名称、上下文窗口 / 最大输出、图片输入、请求协议、思考配置。
/// 预填设备算好的<b>有效值</b>(<see cref="ModelOptionDto"/>),保存时只把和打开时不一样的字段发给 <c>providers.models.update</c>——
/// 没动的字段继续跟随模型目录。改名先走 <c>providers.models.rename</c>。
/// </summary>
public sealed partial class ModelEditorViewModel : ObservableObject
{
    /// <summary>思考模式的选项值。<see cref="ThinkingAuto"/> 只在别名还没接管思考配置时出现:选它 = 不发思考字段。</summary>
    public const string ThinkingAuto = "auto";

    /// <summary>线上的强度名,从低到高(<c>none</c> = 关)。</summary>
    public static readonly IReadOnlyList<string> AllEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>请求协议的选项;空串 = 跟随供应商(清掉覆盖)。</summary>
    public static readonly IReadOnlyList<string> ProtocolChoices = ["", .. ProviderEditorViewModel.Protocols];

    /// <summary>上下文窗口 / 思考预算的下限(和设备的 <c>ThinkingBudgets.Minimum</c> 一致)。</summary>
    public const int MinimumTokens = 1024;

    /// <summary>客户端自己拦下的输入问题(界面按 key 翻译,和设备给的 <c>providers.*</c> 同一个转换器)。</summary>
    public static class Errors
    {
        public const string AliasRequired = "models.editor.aliasRequired";
        public const string ContextTooSmall = "models.editor.contextTooSmall";
        public const string OutputTooLarge = "models.editor.outputTooLarge";
        public const string BudgetTooSmall = "models.editor.budgetTooSmall";
        public const string NoEffort = "models.editor.noEffort";
    }

    private readonly IModelEditorCommands _commands;
    private readonly string _deviceId;
    private readonly JsonObject? _storedThinking;
    private readonly Snapshot _initial;
    private string _alias;
    private bool _filling = true;

    /// <param name="alias">别名里存着的(显示名称、协议覆盖、存着的思考配置)。</param>
    /// <param name="effective">设备算好的有效值(模型候选里对应的那一行);设备还没给就退回 <paramref name="alias"/> 里存着的。</param>
    public ModelEditorViewModel(IModelEditorCommands commands, string deviceId, ModelAliasDto alias, ModelOptionDto? effective)
    {
        _commands = commands;
        _deviceId = deviceId;
        _alias = alias.Alias;
        ProviderId = alias.ProviderId;
        Upstream = alias.UpstreamModel;
        ProviderProtocol = effective?.Protocol is { } p && alias.ProtocolOverride is null ? p : null;

        var ownsThinking = alias.ThinkingConfigJson is not null && (alias.CatalogOverrides.Length == 0 || alias.CatalogOverrides.Contains("thinkingConfig"));
        _storedThinking = ownsThinking ? Parse(alias.ThinkingConfigJson) as JsonObject : null;
        ThinkingModes = _storedThinking is null ? [ThinkingAuto, .. Modes] : [.. Modes];

        var efforts = (ownsThinking || alias.CatalogOverrides.Contains("reasoningEfforts") ? Strings(alias.ReasoningEffortsJson) : null)
            ?? effective?.ReasoningEfforts
            ?? Strings(alias.ReasoningEffortsJson)
            ?? ["low", "medium", "high"];
        Efforts = [.. AllEfforts.Select(e => new EffortChoice(e, efforts.Contains(e)))];
        foreach (var effort in Efforts)
        {
            effort.PropertyChanged += (_, _) => OnEffortsChanged();
        }

        AliasName = alias.Alias;
        DisplayName = alias.DisplayName ?? "";
        ContextWindow = effective?.ContextWindow ?? alias.ContextWindow;
        MaxOutputTokens = effective?.MaxOutputTokens ?? alias.MaxOutputTokens;
        SupportsImages = effective?.SupportsImages ?? alias.SupportsImages;
        Protocol = alias.ProtocolOverride ?? "";
        ThinkingMode = _storedThinking is not null ? Str(_storedThinking["mode"]) ?? "unsupported" : ThinkingAuto;
        DefaultEnabled = Bool(_storedThinking?["defaultEnabled"]) ?? true;
        DefaultEffort = Str(_storedThinking?["defaultEffort"]) is { } stored && efforts.Contains(stored) ? stored : PreferredEffort(efforts);
        BudgetTokens = Int(_storedThinking?["defaultBudgetTokens"]) ?? 10_000;
        EffectiveThinkingMode = effective?.ThinkingMode;
        _initial = Capture();
        _filling = false;
    }

    private static readonly string[] Modes = ["unsupported", "always", "toggle", "budget", "effort"];

    public string ProviderId { get; }

    public string Upstream { get; }

    /// <summary>「跟随供应商」那一项后面括号里写的协议(没有覆盖时设备算出的协议);有覆盖时为 null。</summary>
    public string? ProviderProtocol { get; }

    /// <summary>设备眼下实际生效的思考模式(「跟随模型目录」那一项的说明);null = 目录没给。</summary>
    public string? EffectiveThinkingMode { get; }

    public IReadOnlyList<string> ThinkingModes { get; }

    public ObservableCollection<EffortChoice> Efforts { get; }

    /// <summary>「默认强度」的候选:勾上的、不含「关」的档位。</summary>
    public IReadOnlyList<string> DefaultEffortChoices => [.. Efforts.Where(e => e.IsSelected && e.Value != "none").Select(e => e.Value)];

    [ObservableProperty] public partial string AliasName { get; set; } = "";

    [ObservableProperty] public partial string DisplayName { get; set; } = "";

    [ObservableProperty] public partial double ContextWindow { get; set; }

    [ObservableProperty] public partial double MaxOutputTokens { get; set; }

    [ObservableProperty] public partial bool SupportsImages { get; set; }

    [ObservableProperty] public partial string Protocol { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThinkingDetails), nameof(ShowsDefaultEnabled), nameof(ShowsBudget), nameof(ShowsEfforts), nameof(IsThinkingAuto))]
    public partial string ThinkingMode { get; set; } = ThinkingAuto;

    [ObservableProperty] public partial bool DefaultEnabled { get; set; }

    [ObservableProperty] public partial string? DefaultEffort { get; set; }

    [ObservableProperty] public partial double BudgetTokens { get; set; }

    public bool IsThinkingAuto => ThinkingMode == ThinkingAuto;

    public bool ShowsThinkingDetails => ShowsDefaultEnabled || ShowsBudget || ShowsEfforts;

    public bool ShowsDefaultEnabled => ThinkingMode is "toggle" or "budget" or "effort";

    public bool ShowsBudget => ThinkingMode == "budget";

    public bool ShowsEfforts => ThinkingMode == "effort";

    /// <summary>用户动过表单(界面据此决定点遮罩关不关窗口)。程序预填不算。</summary>
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    [ObservableProperty] public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorKey { get; private set; }

    public bool HasError => ErrorKey is not null;

    /// <summary>保存成功(或没有任何改动),界面据此关窗口。</summary>
    public event Action? Completed;

    /// <summary>保存后别名的最终名字(改了名就是新的)。</summary>
    public string Alias => _alias;

    partial void OnAliasNameChanged(string value) => Touch();

    partial void OnDisplayNameChanged(string value) => Touch();

    partial void OnContextWindowChanged(double value) => Touch();

    partial void OnMaxOutputTokensChanged(double value) => Touch();

    partial void OnSupportsImagesChanged(bool value) => Touch();

    partial void OnProtocolChanged(string value) => Touch();

    partial void OnThinkingModeChanged(string value) => Touch();

    partial void OnDefaultEnabledChanged(bool value) => Touch();

    partial void OnDefaultEffortChanged(string? value) => Touch();

    partial void OnBudgetTokensChanged(double value) => Touch();

    private void OnEffortsChanged()
    {
        OnPropertyChanged(nameof(DefaultEffortChoices));
        // 默认强度跟着勾选走:取消勾选了当前默认,就换成剩下里最合适的那一个
        if (DefaultEffort is null || !DefaultEffortChoices.Contains(DefaultEffort))
        {
            DefaultEffort = PreferredEffort(DefaultEffortChoices);
        }

        Touch();
    }

    private void Touch()
    {
        if (!_filling)
        {
            IsDirty = true;
            ErrorKey = null;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        var current = Capture();
        if (Validate(current) is { } problem)
        {
            ErrorKey = problem;
            return;
        }

        IsSaving = true;
        ErrorKey = null;
        try
        {
            // 先改名:撞名时其余字段都还没动
            if (current.Alias != _alias)
            {
                var renamed = await _commands.RenameModelAsync(_deviceId, ProviderId, _alias, current.Alias);
                if (!renamed.IsOk)
                {
                    ErrorKey = renamed.ErrorKey ?? renamed.Outcome.ToString();
                    return;
                }

                _alias = renamed.Value?.Alias ?? current.Alias;
            }

            if (BuildUpdate(current) is { } update)
            {
                var updated = await _commands.UpdateModelAsync(_deviceId, update);
                if (!updated.IsOk)
                {
                    ErrorKey = updated.ErrorKey ?? updated.Outcome.ToString();
                    return;
                }
            }

            Completed?.Invoke();
        }
        finally
        {
            IsSaving = false;
        }
    }

    private string? Validate(Snapshot s)
    {
        if (s.Alias.Length == 0)
        {
            return Errors.AliasRequired;
        }

        if (s.ContextWindow < MinimumTokens)
        {
            return Errors.ContextTooSmall;
        }

        if (s.MaxOutput < 1 || s.MaxOutput > s.ContextWindow)
        {
            return Errors.OutputTooLarge;
        }

        if (s.Mode == "budget" && s.Budget < MinimumTokens)
        {
            return Errors.BudgetTooSmall;
        }

        return s.Mode == "effort" && !s.Efforts.Any(e => e != "none") ? Errors.NoEffort : null;
    }

    /// <summary>和打开时比,只放变了的字段;什么都没变返回 null(不发请求)。</summary>
    private ModelUpdateParams? BuildUpdate(Snapshot s)
    {
        var i = _initial;
        var thinkingChanged = s.Mode != i.Mode
            || (s.Mode != ThinkingAuto && (s.DefaultEnabled != i.DefaultEnabled || s.DefaultEffort != i.DefaultEffort || s.Budget != i.Budget || !s.Efforts.SequenceEqual(i.Efforts)));
        var update = new ModelUpdateParams(
            ProviderId,
            _alias,
            DisplayName: s.DisplayName != i.DisplayName ? s.DisplayName : null,
            ContextWindow: s.ContextWindow != i.ContextWindow ? s.ContextWindow : null,
            MaxOutputTokens: s.MaxOutput != i.MaxOutput ? s.MaxOutput : null,
            ProtocolOverride: s.Protocol != i.Protocol && s.Protocol.Length > 0 ? s.Protocol : null,
            ClearProtocolOverride: s.Protocol != i.Protocol && s.Protocol.Length == 0,
            Vision: s.Vision != i.Vision ? s.Vision : null,
            ThinkingConfigJson: thinkingChanged && s.Mode != ThinkingAuto ? ThinkingJson(s) : null,
            ReasoningEffortsJson: thinkingChanged && s.Mode == "effort" ? new JsonArray([.. s.Efforts.Select(e => (JsonNode?)JsonValue.Create(e))]).ToJsonString() : null);
        return update == new ModelUpdateParams(ProviderId, _alias) ? null : update;
    }

    /// <summary>别名里原有的高级键(参数路径、档位映射……)原样保留,只改模式和默认值。</summary>
    private string ThinkingJson(Snapshot s)
    {
        var config = _storedThinking?.DeepClone() as JsonObject ?? [];
        config["mode"] = s.Mode;
        config.Remove("defaultEffort");
        config.Remove("defaultBudgetTokens");
        if (s.Mode is "toggle" or "budget" or "effort")
        {
            config["defaultEnabled"] = s.DefaultEnabled;
        }
        else
        {
            config.Remove("defaultEnabled");
        }

        if (s.Mode == "effort" && s.DefaultEffort is { } effort)
        {
            config["defaultEffort"] = effort;
        }

        if (s.Mode == "budget")
        {
            config["defaultBudgetTokens"] = s.Budget;
        }

        return config.ToJsonString();
    }

    private Snapshot Capture() => new(
        AliasName.Trim(),
        DisplayName.Trim(),
        (int)Math.Round(ContextWindow),
        (int)Math.Round(MaxOutputTokens),
        SupportsImages,
        Protocol,
        ThinkingMode,
        DefaultEnabled,
        DefaultEffort,
        (int)Math.Round(BudgetTokens),
        [.. Efforts.Where(e => e.IsSelected).Select(e => e.Value)]);

    private sealed record Snapshot(
        string Alias, string DisplayName, int ContextWindow, int MaxOutput, bool Vision, string Protocol,
        string Mode, bool DefaultEnabled, string? DefaultEffort, int Budget, string[] Efforts);

    private static string? PreferredEffort(IReadOnlyCollection<string> efforts) =>
        efforts.Contains("medium") ? "medium" : efforts.FirstOrDefault(e => e != "none");

    private static JsonNode? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string[]? Strings(string? json) =>
        Parse(json) is JsonArray array ? [.. array.Select(Str).OfType<string>()] : null;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
