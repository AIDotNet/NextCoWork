using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>某个插件的一项能力(必选 / 可选;已授予 / 待批准)。</summary>
public sealed partial class PluginPermissionRowViewModel(PluginsViewModel owner, PluginRowViewModel plugin, string name, bool required, bool granted) : ObservableObject
{
    public string Name { get; } = name;

    public bool IsRequired { get; } = required;

    public bool IsOptional => !IsRequired;

    public bool IsGranted { get; } = granted;

    public bool IsPending => !IsGranted;

    public bool IsDangerous => PluginPermissionInfo.IsDangerous(Name);

    public Gate OwnerGate => owner.OwnerGate;

    public bool CanWrite => owner.OwnerGate.Allowed;

    internal void RaiseGates()
    {
        OnPropertyChanged(nameof(OwnerGate));
        OnPropertyChanged(nameof(CanWrite));
    }

    /// <summary>授予或撤销(必选能力的撤销要先二次确认:撤掉它插件就停下来)。</summary>
    [RelayCommand]
    private Task ToggleAsync() => owner.TogglePermissionAsync(plugin, this);
}

/// <summary>插件注册的一条命令(菜单 / 命令面板 / 斜杠命令都按命令 id 分发)。</summary>
public sealed partial class PluginCommandRowViewModel(PluginsViewModel owner, PluginRowViewModel plugin, string commandId, string title) : ObservableObject
{
    public string CommandId { get; } = commandId;

    public string Title { get; } = title;

    public string PluginName => plugin.DisplayName;

    /// <summary>运行命令:member 起(命令效果受它被授予的能力约束)。</summary>
    public Gate MemberGate => owner.MemberGate;

    public bool CanRun => owner.MemberGate.Allowed;

    internal void RaiseGates()
    {
        OnPropertyChanged(nameof(MemberGate));
        OnPropertyChanged(nameof(CanRun));
    }

    [RelayCommand]
    private Task RunAsync() => owner.RunCommandAsync(plugin, this);
}

/// <summary>一项配置的表单控件。布尔 / 枚举改了立即写;字符串 / 数字在草稿里改、点「应用」写(类型不符设备会拒绝,错误回显在字段上)。</summary>
public sealed partial class PluginConfigFieldViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private readonly PluginRowViewModel _plugin;
    private bool _syncing;

    public PluginConfigFieldViewModel(PluginsViewModel owner, PluginRowViewModel plugin, PluginConfigPropertyDto schema, string title, string? description)
    {
        _owner = owner;
        _plugin = plugin;
        Schema = schema;
        Title = title;
        Description = description;
        Options = [.. schema.Enum.Select(e => new ChoiceItem(e, e))];
    }

    public PluginConfigPropertyDto Schema { get; }

    public string Key => Schema.Key;

    public string Title { get; }

    public string? Description { get; }

    public ObservableCollection<ChoiceItem> Options { get; }

    public bool IsBoolean => Schema.Type == "boolean";

    public bool IsEnum => Schema.Type == "enum";

    public bool IsNumber => Schema.Type == "number";

    public bool IsText => !IsBoolean && !IsEnum && !IsNumber;

    public Gate OwnerGate => _owner.OwnerGate;

    public bool CanWrite => _owner.OwnerGate.Allowed;

    internal void RaiseGates()
    {
        OnPropertyChanged(nameof(OwnerGate));
        OnPropertyChanged(nameof(CanWrite));
    }

    /// <summary>字符串 / 枚举的草稿(枚举绑定到下拉的选中值)。</summary>
    [ObservableProperty] public partial string Text { get; set; } = "";

    [ObservableProperty] public partial bool Flag { get; set; }

    [ObservableProperty] public partial double? Number { get; set; }

    /// <summary>设备上的值(没改过就是清单默认值)。草稿和它不同 = 有未应用的改动。</summary>
    public PluginConfigValueDto? DeviceValue { get; private set; }

    /// <summary>最近一次写失败的原因 key(<c>plugins.configurationInvalid</c> 等)。</summary>
    [ObservableProperty] public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool IsDirty => IsBoolean || IsEnum ? false : !SameAsDevice();

    /// <summary>字符串 / 数字草稿能不能应用(数字必须有值)。</summary>
    public bool CanApply => IsDirty && (!IsNumber || Number is not null);

    /// <summary>当前值不是清单默认值:显示「恢复默认」。</summary>
    public bool CanReset => DeviceValue is { } value && !Equals(value, Default);

    public PluginConfigValueDto Default => new(Schema.DefaultText, Schema.DefaultNumber, Schema.DefaultFlag);

    private string _lastText = "";
    private bool _restoring;

    partial void OnTextChanged(string value)
    {
        // 双向绑定的输入框在第一次显示时可能把 null 写回来(控件模板应用时清空自己的文本):当作没有这回事,把上一个值还给它
        if (value is null)
        {
            _restoring = true;
            try
            {
                Text = _lastText;
            }
            finally
            {
                _restoring = false;
            }

            return;
        }

        _lastText = value;
        if (!_restoring)
        {
            Edited(isText: !IsEnum);
        }
    }

    partial void OnNumberChanged(double? value) => Edited(isText: true);

    partial void OnFlagChanged(bool value) => Edited(isText: false);

    private void Edited(bool isText)
    {
        if (_syncing)
        {
            return;
        }

        Error = null;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanApply));
        if (!isText)
        {
            _ = ApplyAsync(); // 开关和下拉:改了立即写
        }
    }

    /// <summary>用设备上的值对账:没有未应用改动的字段跟随设备(规则 7:界面跟随真源)。<paramref name="force"/> = 写成功之后无条件采用。</summary>
    internal void Reconcile(PluginConfigValueDto? value, bool force)
    {
        DeviceValue = value ?? Default;
        if (!force && IsDirtyWhileSyncing())
        {
            OnPropertyChanged(nameof(CanReset));
            return;
        }

        _syncing = true;
        try
        {
            var v = DeviceValue;
            Text = v.Text ?? "";
            Number = v.Number;
            Flag = v.Flag ?? false;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanReset));
    }

    private bool IsDirtyWhileSyncing() => DeviceValue is not null && IsDirty;

    private bool SameAsDevice()
    {
        var v = DeviceValue ?? Default;
        return IsNumber ? v.Number == Number : (v.Text ?? "") == Text;
    }

    internal PluginConfigValueDto Draft() => IsBoolean ? new PluginConfigValueDto(null, null, Flag) : IsNumber ? new PluginConfigValueDto(null, Number, null) : new PluginConfigValueDto(Text, null, null);

    [RelayCommand]
    private Task ApplyAsync() => _owner.ApplyConfigAsync(_plugin, this, Draft());

    [RelayCommand]
    private Task ResetAsync() => _owner.ApplyConfigAsync(_plugin, this, null);
}

/// <summary>已安装列表里的一个插件。同一个插件的行对象跨快照复用(展开状态、配置草稿是这一页的纯 UI 状态);事实全来自快照里的 <see cref="PluginInfoDto"/>。</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private string _locale = "";

    public PluginRowViewModel(PluginsViewModel owner, PluginInfoDto dto)
    {
        _owner = owner;
        Dto = dto;
    }

    public PluginInfoDto Dto { get; private set; }

    public string Id => Dto.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Dto.DisplayName) ? Dto.Name : Dto.DisplayName;

    public string Description => Dto.Description;

    public string Version => Dto.Version;

    public string Publisher => Dto.Publisher;

    public string Status => Dto.Status;

    public string Tone => PluginStatusInfo.Tone(Dto.Status);

    public bool Enabled => Dto.Enabled;

    public bool IsFaulted => Dto.Status == PluginStatuses.Faulted;

    public bool IsError => Dto.Status == PluginStatuses.Error;

    public bool IsPendingApproval => Dto.Status == PluginStatuses.PendingApproval;

    /// <summary>只在装载失败(<c>error</c>)时不能启用(<c>plugins.notRunnable</c>)。</summary>
    public bool CanToggle => Dto.Status != PluginStatuses.Error;

    public string[] PendingPermissions => Dto.PendingPermissions;

    public bool HasPending => Dto.PendingPermissions.Length > 0;

    public string[] AllowedCommands => Dto.AllowedCommands;

    public bool HasAllowedCommands => Dto.AllowedCommands.Length > 0;

    public string[] HostPermissions => Dto.HostPermissions;

    public bool HasHostPermissions => Dto.HostPermissions.Length > 0;

    public string[] RegisteredTools => Dto.RegisteredTools;

    public bool HasTools => Dto.RegisteredTools.Length > 0;

    public int SkillCount => Dto.Skills.Length;

    public bool HasSkills => Dto.Skills.Length > 0;

    public int DiagnosticCount => Dto.Diagnostics.Length;

    public bool HasDiagnostics => Dto.Diagnostics.Length > 0;

    public bool HasUnsupported => Dto.Unsupported.Length > 0;

    public string[] Unsupported => Dto.Unsupported;

    public bool FromMarket => Dto.Slug is not null;

    public Gate OwnerGate => _owner.OwnerGate;

    public bool CanWrite => _owner.OwnerGate.Allowed;

    /// <summary>启停开关:owner,而且装载失败的插件不能启用。</summary>
    public bool CanToggleEnabled => CanWrite && CanToggle;

    internal void RaiseGates()
    {
        OnPropertyChanged(nameof(OwnerGate));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanToggleEnabled));
        foreach (var p in Permissions)
        {
            p.RaiseGates();
        }

        foreach (var c in Commands)
        {
            c.RaiseGates();
        }

        foreach (var f in ConfigFields)
        {
            f.RaiseGates();
        }
    }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public ObservableCollection<PluginPermissionRowViewModel> Permissions { get; } = [];

    public ObservableCollection<PluginConfigFieldViewModel> ConfigFields { get; } = [];

    public ObservableCollection<PluginCommandRowViewModel> Commands { get; } = [];

    public bool HasPermissions => Permissions.Count > 0;

    public bool HasConfig => ConfigFields.Count > 0;

    public bool HasCommands => Commands.Count > 0;

    public string? ConfigTitle { get; private set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = _owner.LoadConfigAsync(this);
        }
    }

    internal void Update(PluginInfoDto dto, string locale)
    {
        var schemaChanged = !SchemaEquals(Dto.Configuration, dto.Configuration) || Dto.Messages.Length != dto.Messages.Length || Dto.Id != dto.Id || _locale != locale;
        _locale = locale;
        Dto = dto;
        SyncPermissions();
        SyncCommands(locale);
        if (schemaChanged || ConfigFields.Count == 0 && dto.Configuration is { Properties.Length: > 0 })
        {
            RebuildConfig(locale);
        }

        OnPropertyChanged(string.Empty);
    }

    private static bool SchemaEquals(PluginConfigurationDto? a, PluginConfigurationDto? b) =>
        a is null || b is null ? a is null && b is null : a.Title == b.Title && a.Properties.SequenceEqual(b.Properties, PropertyComparer.Instance);

    private sealed class PropertyComparer : IEqualityComparer<PluginConfigPropertyDto>
    {
        public static readonly PropertyComparer Instance = new();

        public bool Equals(PluginConfigPropertyDto? x, PluginConfigPropertyDto? y) =>
            x is not null && y is not null && x.Key == y.Key && x.Type == y.Type && x.Title == y.Title && x.Description == y.Description && x.Enum.SequenceEqual(y.Enum)
            && x.DefaultText == y.DefaultText && x.DefaultNumber == y.DefaultNumber && x.DefaultFlag == y.DefaultFlag;

        public int GetHashCode(PluginConfigPropertyDto obj) => HashCode.Combine(obj.Key, obj.Type);
    }

    private void SyncPermissions()
    {
        var granted = Dto.Granted.ToHashSet(StringComparer.Ordinal);
        var rows = new List<PluginPermissionRowViewModel>();
        foreach (var name in Dto.Required.Distinct())
        {
            rows.Add(new PluginPermissionRowViewModel(_owner, this, name, true, granted.Contains(name)));
        }

        foreach (var name in Dto.Optional.Distinct().Where(o => !Dto.Required.Contains(o)))
        {
            rows.Add(new PluginPermissionRowViewModel(_owner, this, name, false, granted.Contains(name)));
        }

        if (rows.Count == Permissions.Count && rows.Zip(Permissions).All(p => (p.First.Name, p.First.IsRequired, p.First.IsGranted) == (p.Second.Name, p.Second.IsRequired, p.Second.IsGranted)))
        {
            return;
        }

        Permissions.Clear();
        foreach (var row in rows)
        {
            Permissions.Add(row);
        }
    }

    private void SyncCommands(string locale)
    {
        var rows = Dto.Commands.Where(c => Dto.RegisteredCommands.Contains(c.Command))
            .Select(c => new PluginCommandRowViewModel(_owner, this, c.Command, PluginMessages.Resolve(Dto, c.Title, locale))).ToList();
        if (rows.Select(r => (r.CommandId, r.Title)).SequenceEqual(Commands.Select(r => (r.CommandId, r.Title))))
        {
            return;
        }

        Commands.Clear();
        foreach (var row in rows)
        {
            Commands.Add(row);
        }
    }

    private void RebuildConfig(string locale)
    {
        ConfigFields.Clear();
        ConfigTitle = Dto.Configuration is { } config ? PluginMessages.Resolve(Dto, config.Title, locale) : null;
        if (Dto.Configuration is { } schema)
        {
            foreach (var property in schema.Properties)
            {
                var description = property.Description is { Length: > 0 } d ? PluginMessages.Resolve(Dto, d, locale) : null;
                var field = new PluginConfigFieldViewModel(_owner, this, property, PluginMessages.Resolve(Dto, property.Title, locale), description);
                field.Reconcile(null, force: true);
                ConfigFields.Add(field);
            }
        }

        OnPropertyChanged(nameof(HasConfig));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private Task ToggleEnabledAsync() => _owner.SetEnabledAsync(this, !Dto.Enabled);

    [RelayCommand]
    private void AskUninstall() => _owner.AskUninstall(this);

    /// <summary>批准还差的必选能力(待批准的插件);批准后设备自动激活已启用的插件。</summary>
    [RelayCommand]
    private Task GrantPendingAsync() => _owner.GrantPendingAsync(this);

    /// <summary>卡死 / 超预算(faulted)的插件恢复:用现有的 setEnabled 禁用再启用(没有单独的「重启」方法)。</summary>
    [RelayCommand]
    private Task RecoverAsync() => _owner.RecoverAsync(this);
}

/// <summary>待处理提示卡片:插件在等用户回答的一个请求。文字都是插件词条的 key(<see cref="PluginMessages"/> 解析);执行类审批带命令行和设备 + 环境。</summary>
public sealed partial class PluginPromptViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;

    public PluginPromptViewModel(PluginsViewModel owner, PluginInteractionDto dto, PluginInfoDto? plugin, string locale)
    {
        _owner = owner;
        Dto = dto;
        PluginName = plugin is null ? dto.PluginId : string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.Name : plugin.DisplayName;
        string Resolve(string? key) => plugin is null ? (key ?? "").Trim('%') : PluginMessages.Resolve(plugin, key, locale);
        Title = dto.TitleKey is null ? null : Resolve(dto.TitleKey);
        Detail = dto.DetailKey is null ? null : Resolve(dto.DetailKey);
        Placeholder = dto.PlaceholderKey is null ? null : Resolve(dto.PlaceholderKey);
        Items = [.. dto.Items.Select(i => new PluginPickChoice(i.Id, Resolve(i.LabelKey)))];
        Text = dto.Initial ?? "";
    }

    public PluginInteractionDto Dto { get; }

    public string RequestId => Dto.RequestId;

    public string PluginName { get; }

    public string Kind => Dto.Kind;

    public string? Title { get; }

    public string? Detail { get; }

    public string? Placeholder { get; }

    public IReadOnlyList<PluginPickChoice> Items { get; }

    public bool IsConfirm => Dto.Kind == PluginInteractionKindNames.Confirm;

    public bool IsInput => Dto.Kind == PluginInteractionKindNames.Input;

    public bool IsPick => Dto.Kind == PluginInteractionKindNames.Pick;

    public bool IsPermissions => Dto.Kind == PluginInteractionKindNames.Permissions;

    public bool IsExec => Dto.Kind == PluginInteractionKindNames.Exec;

    /// <summary>需要「允许 / 拒绝」两个按钮的种类。</summary>
    public bool IsAllowDeny => IsConfirm || IsPermissions || IsExec;

    public bool Danger => Dto.Danger || IsExec || Dto.Permissions.Any(PluginPermissionInfo.IsDangerous);

    public bool Password => Dto.Password;

    public string? Command => Dto.Command;

    public string[] Permissions => Dto.Permissions;

    public bool HasPermissions => Dto.Permissions.Length > 0;

    /// <summary>涉及执行的审批所指的环境名(设备 + 环境,AGENTS.md §5)。</summary>
    public string? EnvironmentName => Dto.Environment?.Name;

    public string? EnvironmentType => Dto.Environment?.Type;

    public string? EnvironmentId => Dto.Environment?.Environment;

    public string? DeviceId => Dto.Environment?.DeviceId;

    public bool HasEnvironment => Dto.Environment is not null;

    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(Dto.CreatedAt);

    /// <summary>回答:member 起(和 <c>interactions.respond</c> 同一级)。</summary>
    public Gate MemberGate => _owner.MemberGate;

    public bool CanAnswer => _owner.MemberGate.Allowed && !IsAnswering;

    internal void RaiseGates()
    {
        OnPropertyChanged(nameof(MemberGate));
        OnPropertyChanged(nameof(CanAnswer));
    }

    partial void OnIsAnsweringChanged(bool value) => OnPropertyChanged(nameof(CanAnswer));

    [ObservableProperty] public partial string Text { get; set; } = "";

    private string _lastText = "";

    partial void OnTextChanged(string value)
    {
        if (value is null)
        {
            Text = _lastText; // 输入框第一次显示时可能把 null 写回来:还给它上一个值
            return;
        }

        _lastText = value;
    }

    [ObservableProperty] public partial bool IsAnswering { get; private set; }

    internal async Task AnswerAsync(bool? confirmed, string? text, string? picked)
    {
        IsAnswering = true;
        try
        {
            await _owner.AnswerAsync(this, confirmed, text, picked);
        }
        finally
        {
            IsAnswering = false;
        }
    }

    [RelayCommand]
    private Task AllowAsync() => AnswerAsync(true, null, null);

    [RelayCommand]
    private Task DenyAsync() => AnswerAsync(false, null, null);

    /// <summary>取消 = 全部缺省(不是错误:没理会一个弹窗不是故障)。</summary>
    [RelayCommand]
    private Task CancelAsync() => AnswerAsync(null, null, null);

    [RelayCommand]
    private Task SubmitAsync() => AnswerAsync(null, Text, null);

    [RelayCommand]
    private Task PickAsync(string id) => AnswerAsync(null, null, id);
}

/// <summary>选择类提示里的一项。</summary>
public sealed record PluginPickChoice(string Id, string Label);

/// <summary>撤销某一项能力之前的二次确认(必选能力:撤掉它插件就停下来)。</summary>
public sealed record PendingPermissionRevoke(string PluginId, string PluginName, string Permission, bool IsRequired);

/// <summary>市场里的一条。</summary>
public sealed class PluginMarketRowViewModel(PluginMarketItemDto dto, string? installedVersion, PluginUpdateDto? update)
{
    public PluginMarketItemDto Dto { get; } = dto;

    public string Slug => Dto.Slug;

    public string Title => string.IsNullOrWhiteSpace(Dto.DisplayName) ? Dto.Slug : Dto.DisplayName;

    public string Description => Dto.Description;

    public string Publisher => Dto.Publisher;

    public string? Version => Dto.Version;

    public string Author => Dto.Author;

    public int Downloads => Dto.DownloadCount;

    public string[] Permissions => Dto.Permissions;

    public bool HasPermissions => Dto.Permissions.Length > 0;

    public bool HasDangerousPermission => Dto.Permissions.Any(PluginPermissionInfo.IsDangerous);

    public string[] Skills => Dto.Skills;

    /// <summary>设备上已装的版本(没装为 null)。</summary>
    public string? InstalledVersion { get; } = installedVersion;

    public bool IsInstalled => InstalledVersion is not null;

    public PluginUpdateDto? Update { get; } = update;

    public bool HasUpdate => Update is not null;
}

/// <summary>「已装插件有新版」的一行。</summary>
public sealed record PluginUpdateRow(PluginUpdateDto Dto, string DisplayName)
{
    public bool HasEscalation => Dto.EscalatedPermissions.Length > 0;

    public bool HasDangerousEscalation => Dto.EscalatedPermissions.Any(PluginPermissionInfo.IsDangerous);

    /// <summary>本地装的(不是市场来源):检测得到新版,但不计入批量更新。</summary>
    public bool NotBatchable => !Dto.FromMarket;
}

/// <summary>活动日志的一行(不含参数原文)。</summary>
public sealed record PluginActivityRow(DateTimeOffset At, string PluginId, string PluginName, string Method, string Summary, string Verdict, long DurationMs)
{
    public bool IsOk => Verdict == "ok";

    public string Tone => Verdict switch { "ok" => "ok", "denied" => "warn", _ => "bad" };
}

/// <summary>诊断的一行(来自快照里的 <see cref="PluginDiagnosticDto"/>)。</summary>
public sealed record PluginDiagnosticRow(string PluginId, string PluginName, PluginDiagnosticDto Dto)
{
    /// <summary><c>plugins.diag.activateFailed</c> → <c>activateFailed</c>(界面按它翻译)。</summary>
    public string KeySuffix => Dto.Key.StartsWith("plugins.diag.", StringComparison.Ordinal) ? Dto.Key["plugins.diag.".Length..] : Dto.Key;

    public bool IsError => Dto.Level == "error";

    public bool IsWarn => Dto.Level == "warn";

    public string Tone => Dto.Level switch { "error" => "bad", "warn" => "warn", _ => "mute" };
}
