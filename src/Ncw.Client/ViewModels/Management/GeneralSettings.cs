using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>「通用 / 设备」页里的哪一项辅助模型。</summary>
public enum ModelPickField
{
    Default,
    PermissionReviewer,
    GoalEvaluator,
    Compact,
    Completion,
    InlineCompletion,
    Image,
    Subagent,
}

/// <summary>一行「模型 + 供应商」选择(跟随默认 = 空)。选中即写入设备;显示的值始终以设备推回来的为准。</summary>
public sealed partial class ModelPickRow(GeneralSettingsViewModel owner, ModelPickField field) : ObservableObject
{
    public ModelPickField Field { get; } = field;

    public ObservableCollection<ChoiceItem> Choices { get; } = [];

    /// <summary>选中项的 key(<see cref="GeneralSettingsViewModel.PickKey"/>);空串 = 跟随默认。</summary>
    [ObservableProperty] public partial string Key { get; set; } = "";

    internal bool Syncing { get; set; }

    partial void OnKeyChanged(string value)
    {
        // 只有用户在加载好的页面上真的选了一项才写:控件在数据到达之前(或候选里没有这个值时)回写的 null / 空值不是用户的选择,
        // 照写会把设备上的设置改成「跟随默认」(03 §11.6 调整布局时发现)
        if (!Syncing && value is not null && owner.IsReady && Choices.Any(c => c.Value == value))
        {
            _ = owner.SetPickAsync(this, value);
        }
    }
}

/// <summary>
/// 设置 · 设备 · 通用(docs/design/07 §3「Agent 配置」):辅助模型、权限与目标、上游空闲超时、账号轮换与故障转移、压缩与上下文、目标检查间隔、子代理上限、代理、个性化指令。
/// 值只读自 <see cref="ClientStores.Settings"/>(<c>settings</c> 主题,设备是真源);开关与下拉立即写入,文本和数字先在草稿里改、点「应用」整块写入(一项不合法设备拒绝整个补丁)。
/// 读取 member 起,写入 owner;代理密码只写、只经本机通道。
/// </summary>
public sealed partial class GeneralSettingsViewModel : ManagementPageViewModel
{
    public const int MinIdle = 60, MaxIdle = 3600, MinCheckin = 1, MaxCheckin = 1440, MinOutput = 1024, MaxOutput = 200_000;
    public const int NameMax = 64, BackgroundMax = 2000, InstructionsMax = 8000;

    public static readonly IReadOnlyList<string> ProxyModes = ["system", "manual"];
    public static readonly IReadOnlyList<string> ProxySchemes = ["http", "https", "socks5", "socks4"];

    private readonly ClientStores _stores;
    private readonly ISettingsCommands _commands;
    private bool _syncing;

    public GeneralSettingsViewModel(SettingsScope scope, ClientStores stores, ISettingsCommands commands) : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        foreach (var field in Enum.GetValues<ModelPickField>())
        {
            Picks.Add(new ModelPickRow(this, field));
        }

        stores.Settings.Changed += Sync;
        stores.Providers.Changed += SyncChoices;
        stores.Workspace.Changed += RefreshGates;
    }

    public ObservableCollection<ModelPickRow> Picks { get; } = [];

    /// <summary>默认模型:页面最上面「常用」里的那一项(docs/design/03 §11.6)。</summary>
    public ModelPickRow DefaultPick => Picks[0];

    /// <summary>其余辅助模型(权限审查、目标评估、压缩、补全、图像、子代理):收在「高级」里。</summary>
    public IReadOnlyList<ModelPickRow> AuxPicks => [.. Picks.Skip(1)];

    /// <summary>展开「高级」(纯 UI 状态,不持久化)。</summary>
    [ObservableProperty] public partial bool ShowAdvanced { get; set; }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    /// <summary>设备上的设置(镜像);没读到为 null。</summary>
    [ObservableProperty] public partial DeviceSettingsDto? Settings { get; private set; }

    // ── 立即写入的选项(值来自镜像) ──
    public bool AccountRotation => Settings?.AccountRotation ?? false;

    public bool Failover => Settings?.Failover ?? false;

    public bool AutoCompact => Settings?.AutoCompact ?? false;

    public string PermissionMode => Settings?.DefaultPermissionMode ?? "ask";

    public string ModelProposedGoals => Settings?.ModelProposedGoals ?? "auto";

    public string CompactThinking => Settings?.CompactThinking ?? "inherit";

    public string Shell => Settings?.Shell ?? "system";

    // ── 数字草稿 ──
    [ObservableProperty] public partial double IdleTimeout { get; set; } = 600;

    [ObservableProperty] public partial double GoalCheckin { get; set; } = 30;

    [ObservableProperty] public partial double MaxOutputTokens { get; set; } = 32_000;

    [ObservableProperty] public partial double PerSessionLimit { get; set; } = 3;

    [ObservableProperty] public partial double GlobalLimit { get; set; } = 5;

    [ObservableProperty] public partial bool LimitsDirty { get; private set; }

    /// <summary>数字草稿不在合法范围内(按钮禁用 + 提示范围)。</summary>
    public bool LimitsInvalid => !ValidLimits();

    // ── 代理草稿 ──
    [ObservableProperty] public partial bool ProxyEnabled { get; set; }

    [ObservableProperty] public partial string ProxyMode { get; set; } = "system";

    [ObservableProperty] public partial string ProxyScheme { get; set; } = "http";

    [ObservableProperty] public partial string ProxyHost { get; set; } = "";

    [ObservableProperty] public partial double ProxyPort { get; set; }

    [ObservableProperty] public partial bool ProxyAuthEnabled { get; set; }

    [ObservableProperty] public partial string ProxyUser { get; set; } = "";

    [ObservableProperty] public partial string ProxyBypass { get; set; } = "";

    [ObservableProperty] public partial bool ProxyDirty { get; private set; }

    public bool ProxyManual => ProxyMode == "manual";

    /// <summary>手动代理必须有主机和端口(0–65535 以内,0 = 未设)。</summary>
    public bool ProxyInvalid => ProxyEnabled && ProxyManual && (ProxyHost.Trim().Length == 0 || ProxyPort is < 1 or > 65535);

    public bool ProxyHasPassword => Settings?.Proxy.HasPassword ?? false;

    /// <summary>代理密码输入(只写不读:写出去后立刻清空,界面只显示「已设置」)。</summary>
    [ObservableProperty] public partial string ProxyPassword { get; set; } = "";

    /// <summary>密码只经本机通道由 owner 设置(明文密码不经远程连接)。</summary>
    public Gate PasswordGate => Scope.GateFor(Need.LocalOwner);

    // ── 个性化草稿 ──
    [ObservableProperty] public partial string PersonName { get; set; } = "";

    [ObservableProperty] public partial string Background { get; set; } = "";

    [ObservableProperty] public partial string Instructions { get; set; } = "";

    [ObservableProperty] public partial bool PersonalizationDirty { get; private set; }

    public bool PersonalizationTooLong => PersonName.Length > NameMax || Background.Length > BackgroundMax || Instructions.Length > InstructionsMax;

    protected override void OnGatesChanged() => OnPropertyChanged(nameof(PasswordGate));

    private void RefreshGates() => RaiseGates();

    partial void OnIdleTimeoutChanged(double value) => EditLimits();

    partial void OnGoalCheckinChanged(double value) => EditLimits();

    partial void OnMaxOutputTokensChanged(double value) => EditLimits();

    partial void OnPerSessionLimitChanged(double value) => EditLimits();

    partial void OnGlobalLimitChanged(double value) => EditLimits();

    private void EditLimits()
    {
        if (!_syncing)
        {
            LimitsDirty = true;
        }

        OnPropertyChanged(nameof(LimitsInvalid));
    }

    partial void OnProxyEnabledChanged(bool value) => EditProxy();

    partial void OnProxyModeChanged(string value) => EditProxy();

    partial void OnProxySchemeChanged(string value) => EditProxy();

    partial void OnProxyHostChanged(string value) => EditProxy();

    partial void OnProxyPortChanged(double value) => EditProxy();

    partial void OnProxyAuthEnabledChanged(bool value) => EditProxy();

    partial void OnProxyUserChanged(string value) => EditProxy();

    partial void OnProxyBypassChanged(string value) => EditProxy();

    private void EditProxy()
    {
        if (!_syncing)
        {
            ProxyDirty = true;
        }

        OnPropertyChanged(nameof(ProxyManual));
        OnPropertyChanged(nameof(ProxyInvalid));
    }

    partial void OnPersonNameChanged(string value) => EditPersonalization();

    partial void OnBackgroundChanged(string value) => EditPersonalization();

    partial void OnInstructionsChanged(string value) => EditPersonalization();

    private void EditPersonalization()
    {
        if (!_syncing)
        {
            PersonalizationDirty = true;
        }

        OnPropertyChanged(nameof(PersonalizationTooLong));
    }

    private bool ValidLimits() =>
        IdleTimeout is >= MinIdle and <= MaxIdle && GoalCheckin is >= MinCheckin and <= MaxCheckin && MaxOutputTokens is >= MinOutput and <= MaxOutput
        && PerSessionLimit is >= 1 and <= 10 && GlobalLimit is >= 0 and <= 10;

    protected override Task LoadAsync()
    {
        // 设置经 settings 主题推送(member 起);没有额外要拉的:镜像到了就就绪,没到继续「加载中」。
        Sync();
        return Task.CompletedTask;
    }

    protected override void OnUnavailable() => Settings = null;

    private void Sync()
    {
        if (Scope.DeviceId is not { } device || !Scope.IsConnected || !ReadGate.Allowed)
        {
            return;
        }

        var mirror = _stores.Settings.State.Of(device);
        if (!mirror.Known || mirror.Settings is not { } settings)
        {
            if (State is PageState.Ready)
            {
                State = PageState.Loading;
            }

            return;
        }

        _syncing = true;
        try
        {
            Settings = settings;
            if (!LimitsDirty)
            {
                IdleTimeout = settings.UpstreamIdleTimeoutSeconds;
                GoalCheckin = settings.GoalCheckinMinutes;
                MaxOutputTokens = settings.MaxOutputTokens;
                PerSessionLimit = settings.Subagent.PerSessionLimit;
                GlobalLimit = settings.Subagent.GlobalLimit;
            }

            if (!ProxyDirty)
            {
                var proxy = settings.Proxy;
                ProxyEnabled = proxy.Enabled;
                ProxyMode = proxy.Mode;
                ProxyScheme = proxy.Scheme;
                ProxyHost = proxy.Host;
                ProxyPort = proxy.Port;
                ProxyAuthEnabled = proxy.AuthEnabled;
                ProxyUser = proxy.AuthUser;
                ProxyBypass = proxy.Bypass;
            }

            if (!PersonalizationDirty)
            {
                PersonName = settings.Personalization.Name;
                Background = settings.Personalization.Background;
                Instructions = settings.Personalization.Instructions;
            }
        }
        finally
        {
            _syncing = false;
        }

        SyncChoices();
        OnPropertyChanged(string.Empty);
        if (State is not PageState.Ready)
        {
            Ready();
        }
    }

    // ───────────────────────── 辅助模型 ─────────────────────────

    /// <summary>「模型 + 供应商」的下拉 key(空串 = 跟随默认)。</summary>
    public static string PickKey(ModelPickDto? pick) => pick is { Model.Length: > 0 } ? $"{pick.ProviderId}\n{pick.Model}" : "";

    private static ModelPickDto ParseKey(string key)
    {
        if (key.Length == 0)
        {
            return ModelPickDto.None;
        }

        var cut = key.IndexOf('\n', StringComparison.Ordinal);
        var provider = key[..cut];
        return new ModelPickDto(key[(cut + 1)..], provider.Length == 0 ? null : provider);
    }

    private ModelPickDto? Current(ModelPickField field) => Settings is null ? null : field switch
    {
        ModelPickField.Default => Settings.DefaultModel,
        ModelPickField.PermissionReviewer => Settings.PermissionReviewer,
        ModelPickField.GoalEvaluator => Settings.GoalEvaluator,
        ModelPickField.Compact => Settings.Compact,
        ModelPickField.Completion => Settings.Completion,
        ModelPickField.InlineCompletion => Settings.InlineCompletionModel,
        ModelPickField.Image => Settings.Image,
        _ => Settings.Subagent.Model,
    };

    private void SyncChoices()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var options = _stores.Providers.State.Of(device).Options.Where(o => o.Enabled && o.ProviderEnabled).ToList();
        foreach (var row in Picks)
        {
            var current = Current(row.Field);
            var items = new List<ChoiceItem> { new("", null) };
            items.AddRange(options.Select(o => new ChoiceItem(PickKey(new ModelPickDto(o.Alias, o.ProviderId)), $"{o.DisplayName} · {o.ProviderName}")));
            var key = PickKey(current);
            if (key.Length > 0 && items.All(i => i.Value != key))
            {
                items.Add(new ChoiceItem(key, current!.Model)); // 已停用 / 已删除的别名照实显示,否则用户会以为配置丢了
            }

            row.Syncing = true;
            try
            {
                if (!items.SequenceEqual(row.Choices))
                {
                    row.Choices.Clear();
                    foreach (var item in items)
                    {
                        row.Choices.Add(item);
                    }
                }

                row.Key = key;
            }
            finally
            {
                row.Syncing = false;
            }
        }
    }

    internal async Task SetPickAsync(ModelPickRow row, string key)
    {
        if (Scope.DeviceId is not { } device || !CanWrite || key == PickKey(Current(row.Field)))
        {
            SyncChoices();
            return;
        }

        var pick = ParseKey(key);
        var patch = row.Field switch
        {
            ModelPickField.Default => new DeviceSettingsPatchDto(DefaultModel: pick),
            ModelPickField.PermissionReviewer => new DeviceSettingsPatchDto(PermissionReviewer: pick),
            ModelPickField.GoalEvaluator => new DeviceSettingsPatchDto(GoalEvaluator: pick),
            ModelPickField.Compact => new DeviceSettingsPatchDto(Compact: pick),
            ModelPickField.Completion => new DeviceSettingsPatchDto(Completion: pick),
            ModelPickField.InlineCompletion => new DeviceSettingsPatchDto(InlineCompletionModel: pick),
            ModelPickField.Image => new DeviceSettingsPatchDto(Image: pick),
            _ => new DeviceSettingsPatchDto(Subagent: new SubagentSettingsPatchDto(Model: pick)),
        };
        await Act(_commands.UpdateAsync(device, patch));
        SyncChoices(); // 失败时下拉回到设备的值
    }

    // ───────────────────────── 立即写入的选项 ─────────────────────────

    /// <summary>一个开关或下拉(<paramref name="field"/> 是字段名):写入设备,失败时界面回到设备的值。</summary>
    private async Task SetAsync(string field, string value)
    {
        if (Scope.DeviceId is not { } device || !CanWrite)
        {
            OnPropertyChanged(string.Empty);
            return;
        }

        var on = value == "true";
        var patch = field switch
        {
            nameof(AccountRotation) => new DeviceSettingsPatchDto(AccountRotation: on),
            nameof(Failover) => new DeviceSettingsPatchDto(Failover: on),
            nameof(AutoCompact) => new DeviceSettingsPatchDto(AutoCompact: on),
            nameof(PermissionMode) => new DeviceSettingsPatchDto(DefaultPermissionMode: value),
            nameof(ModelProposedGoals) => new DeviceSettingsPatchDto(ModelProposedGoals: value),
            nameof(CompactThinking) => new DeviceSettingsPatchDto(CompactThinking: value),
            nameof(Shell) => new DeviceSettingsPatchDto(Shell: value),
            _ => null,
        };
        if (patch is not null)
        {
            await Act(_commands.UpdateAsync(device, patch));
        }

        OnPropertyChanged(string.Empty);
    }

    public Task SetBoolAsync(string field, bool value) => SetAsync(field, value ? "true" : "false");

    public Task SetChoiceAsync(string field, string value) => SetAsync(field, value);

    [RelayCommand]
    private Task ToggleAccountRotationAsync() => SetBoolAsync(nameof(AccountRotation), !AccountRotation);

    [RelayCommand]
    private Task ToggleFailoverAsync() => SetBoolAsync(nameof(Failover), !Failover);

    [RelayCommand]
    private Task ToggleAutoCompactAsync() => SetBoolAsync(nameof(AutoCompact), !AutoCompact);

    // 下拉框的双向绑定入口:选中即写入设备,显示的值以设备推回来的为准
    public string PermissionModeValue
    {
        get => PermissionMode;
        set => Choose(nameof(PermissionMode), PermissionMode, value);
    }

    public string ModelProposedGoalsValue
    {
        get => ModelProposedGoals;
        set => Choose(nameof(ModelProposedGoals), ModelProposedGoals, value);
    }

    public string CompactThinkingValue
    {
        get => CompactThinking;
        set => Choose(nameof(CompactThinking), CompactThinking, value);
    }

    public string ShellValue
    {
        get => Shell;
        set => Choose(nameof(Shell), Shell, value);
    }

    private void Choose(string field, string current, string? value)
    {
        if (value is { Length: > 0 } && value != current && !_syncing)
        {
            _ = SetChoiceAsync(field, value);
        }
    }

    // ───────────────────────── 草稿块 ─────────────────────────

    [RelayCommand]
    private async Task ApplyLimitsAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || !ValidLimits())
        {
            return;
        }

        var result = await Act(_commands.UpdateAsync(device, new DeviceSettingsPatchDto(
            GoalCheckinMinutes: (int)GoalCheckin,
            UpstreamIdleTimeoutSeconds: (int)IdleTimeout,
            MaxOutputTokens: (int)MaxOutputTokens,
            Subagent: new SubagentSettingsPatchDto(PerSessionLimit: (int)PerSessionLimit, GlobalLimit: (int)GlobalLimit))));
        if (result.IsOk)
        {
            LimitsDirty = false;
            Sync();
        }
    }

    [RelayCommand]
    private void ResetLimits()
    {
        LimitsDirty = false;
        Sync();
    }

    [RelayCommand]
    private async Task ApplyProxyAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || ProxyInvalid)
        {
            return;
        }

        var result = await Act(_commands.UpdateAsync(device, new DeviceSettingsPatchDto(Proxy: new ProxySettingsPatchDto(
            Enabled: ProxyEnabled, Mode: ProxyMode, Scheme: ProxyScheme, Host: ProxyHost.Trim(), Port: (int)ProxyPort,
            AuthEnabled: ProxyAuthEnabled, AuthUser: ProxyUser.Trim(), Bypass: ProxyBypass))));
        if (result.IsOk)
        {
            ProxyDirty = false;
            Sync();
        }
    }

    [RelayCommand]
    private void ResetProxy()
    {
        ProxyDirty = false;
        Sync();
    }

    /// <summary>设置代理密码(只写不读):送出去就清掉输入。只有本机 owner;空输入 = 不动。</summary>
    [RelayCommand]
    private async Task SetProxyPasswordAsync()
    {
        if (Scope.DeviceId is not { } device || !PasswordGate.Allowed || ProxyPassword.Length == 0)
        {
            return;
        }

        var password = ProxyPassword;
        ProxyPassword = "";
        await Act(_commands.SetProxyPasswordAsync(device, password));
    }

    [RelayCommand]
    private async Task ClearProxyPasswordAsync()
    {
        if (Scope.DeviceId is { } device && PasswordGate.Allowed)
        {
            ProxyPassword = "";
            await Act(_commands.SetProxyPasswordAsync(device, ""));
        }
    }

    [RelayCommand]
    private async Task ApplyPersonalizationAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || PersonalizationTooLong)
        {
            return;
        }

        var result = await Act(_commands.UpdateAsync(device, new DeviceSettingsPatchDto(Personalization: new PersonalizationPatchDto(PersonName, Background, Instructions))));
        if (result.IsOk)
        {
            PersonalizationDirty = false;
            Sync();
        }
    }

    [RelayCommand]
    private void ResetPersonalization()
    {
        PersonalizationDirty = false;
        Sync();
    }
}

/// <summary>
/// 设置 · 项目 · 项目设置(<c>settings.project.get / update</c>):默认权限档位、默认模型、会话模式、思考档位、联网搜索、最大上下文、压缩模型与思考。
/// 项目设置不走主题(没有项目级镜像),读到的结果是这一页自己的状态;写入后以设备返回的完整设置为准。读取 member 起,写入 owner。
/// </summary>
public sealed partial class ProjectSettingsViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly ISettingsCommands _commands;
    private bool _syncing;

    public ProjectSettingsViewModel(SettingsScope scope, ClientStores stores, ISettingsCommands commands) : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        stores.Providers.Changed += SyncChoices;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<ChoiceItem> ModelChoices { get; } = [];

    public ObservableCollection<ChoiceItem> CompactChoices { get; } = [];

    [ObservableProperty] public partial ProjectSettingsDto? Settings { get; private set; }

    [ObservableProperty] public partial string PermissionMode { get; set; } = "ask";

    [ObservableProperty] public partial string DefaultMode { get; set; } = "code";

    [ObservableProperty] public partial string DefaultThinking { get; set; } = "auto";

    [ObservableProperty] public partial bool WebSearch { get; set; }

    [ObservableProperty] public partial bool MaxContext { get; set; }

    /// <summary>默认模型的下拉 key(<see cref="GeneralSettingsViewModel.PickKey"/>)。</summary>
    [ObservableProperty] public partial string ModelKey { get; set; } = "";

    /// <summary>压缩模型:空串 = 跟随全局设置。</summary>
    [ObservableProperty] public partial string CompactKey { get; set; } = "";

    /// <summary>压缩思考:空串 = 跟随全局设置,<c>inherit</c> = 跟随会话本轮。</summary>
    [ObservableProperty] public partial string CompactThinking { get; set; } = "";

    [ObservableProperty] public partial bool Dirty { get; private set; }

    public bool HasProjects => Scope.HasProjects;

    partial void OnPermissionModeChanged(string value) => Edit();

    partial void OnDefaultModeChanged(string value) => Edit();

    partial void OnDefaultThinkingChanged(string value) => Edit();

    partial void OnWebSearchChanged(bool value) => Edit();

    partial void OnMaxContextChanged(bool value) => Edit();

    partial void OnModelKeyChanged(string value) => Edit();

    partial void OnCompactKeyChanged(string value) => Edit();

    partial void OnCompactThinkingChanged(string value) => Edit();

    /// <summary>
    /// 有没有改动 = 表单和读到的设置<b>按值</b>比,不是「动过没有」:下拉框在页面创建时可能先回写一个空值再写回原值
    /// (页面按需创建,docs/design/03 §12,数据先于页面到达),改了又改回去也不算改动。
    /// </summary>
    private void Edit()
    {
        if (!_syncing)
        {
            Dirty = Settings is not { } dto
                || PermissionMode != dto.PermissionMode
                || DefaultMode != dto.DefaultMode
                || DefaultThinking != dto.DefaultThinking
                || WebSearch != dto.WebSearch
                || MaxContext != dto.MaxContext
                || ModelKey != GeneralSettingsViewModel.PickKey(dto.DefaultModel)
                || CompactKey != GeneralSettingsViewModel.PickKey(dto.CompactModel)
                || CompactThinking != (dto.CompactThinking ?? "");
        }
    }

    protected override void OnGatesChanged() => OnPropertyChanged(nameof(HasProjects));

    protected override async Task LoadAsync()
    {
        OnPropertyChanged(nameof(HasProjects));
        if (Scope.DeviceId is not { } device || Scope.WorkspaceId is not { } workspace)
        {
            Settings = null;
            Ready(); // 这台设备上没有项目:空状态,不是错误
            return;
        }

        var result = await _commands.GetProjectAsync(device, workspace);
        if (!IsCurrent(device, workspace))
        {
            return;
        }

        if (!result.IsOk)
        {
            Fail(result);
            return;
        }

        Apply(result.Value!);
        Ready();
    }

    private void Apply(ProjectSettingsDto dto)
    {
        _syncing = true;
        try
        {
            Settings = dto;
            PermissionMode = dto.PermissionMode;
            DefaultMode = dto.DefaultMode;
            DefaultThinking = dto.DefaultThinking;
            WebSearch = dto.WebSearch;
            MaxContext = dto.MaxContext;
            SyncChoices();
            ModelKey = GeneralSettingsViewModel.PickKey(dto.DefaultModel);
            CompactKey = GeneralSettingsViewModel.PickKey(dto.CompactModel);
            CompactThinking = dto.CompactThinking ?? "";
            Dirty = false;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncChoices()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var options = _stores.Providers.State.Of(device).Options.Where(o => o.Enabled && o.ProviderEnabled)
            .Select(o => new ChoiceItem(GeneralSettingsViewModel.PickKey(new ModelPickDto(o.Alias, o.ProviderId)), $"{o.DisplayName} · {o.ProviderName}")).ToList();
        Fill(ModelChoices, [new ChoiceItem("", null), .. options], Settings?.DefaultModel);
        Fill(CompactChoices, [new ChoiceItem("", null), .. options], Settings?.CompactModel);
    }

    private static void Fill(ObservableCollection<ChoiceItem> target, List<ChoiceItem> items, ModelPickDto? current)
    {
        var key = GeneralSettingsViewModel.PickKey(current);
        if (key.Length > 0 && items.All(i => i.Value != key))
        {
            items.Add(new ChoiceItem(key, current!.Model));
        }

        if (!items.SequenceEqual(target))
        {
            target.Clear();
            foreach (var item in items)
            {
                target.Add(item);
            }
        }
    }

    private static ModelPickDto Pick(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return ModelPickDto.None;
        }

        var cut = key.IndexOf('\n', StringComparison.Ordinal);
        return new ModelPickDto(key[(cut + 1)..], cut > 0 ? key[..cut] : null);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Scope.DeviceId is not { } device || Scope.WorkspaceId is not { } workspace || !CanWrite || !Dirty)
        {
            return;
        }

        var patch = new ProjectSettingsPatchDto(
            workspace,
            PermissionMode: PermissionMode,
            DefaultModel: Pick(ModelKey),
            DefaultMode: DefaultMode,
            DefaultThinking: DefaultThinking,
            WebSearch: WebSearch,
            MaxContext: MaxContext,
            CompactModel: !string.IsNullOrEmpty(CompactKey) ? Pick(CompactKey) : null,
            CompactThinking: !string.IsNullOrEmpty(CompactThinking) ? CompactThinking : null,
            ClearCompactModel: string.IsNullOrEmpty(CompactKey),
            ClearCompactThinking: string.IsNullOrEmpty(CompactThinking));
        var result = await Act(_commands.UpdateProjectAsync(device, patch));
        if (result.IsOk && IsCurrent(device, workspace))
        {
            Apply(result.Value!);
        }
    }

    [RelayCommand]
    private void Reset()
    {
        if (Settings is { } dto)
        {
            Apply(dto);
        }
    }
}
