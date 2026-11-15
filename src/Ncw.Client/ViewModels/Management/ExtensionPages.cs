using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>需要用户二次确认的危险操作(页面内联的确认卡片,而不是弹窗:状态机可测,截图可见)。<see cref="Subject"/> 是被操作对象的名字。</summary>
public sealed record PendingConfirm(string Subject, string Id);

// ═══════════════════════════ Skills ═══════════════════════════

/// <summary>清单里的一条 Skill。事实在设备(<see cref="SkillsMirror"/>);这里只有派生出来的显示。</summary>
public sealed partial class SkillRowViewModel(SkillsViewModel owner, SkillInfoDto dto) : ObservableObject
{
    public SkillInfoDto Dto { get; } = dto;

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public string Description => Dto.Description;

    public string Category => Dto.Category;

    public string Scope => Dto.Scope;

    public string SourceKind => Dto.SourceKind;

    public string? Version => Dto.Version;

    public bool GlobalEnabled => Dto.GlobalEnabled;

    public bool ActiveInWorkspace => Dto.ActiveInWorkspace;

    public bool ClientAssetsOnly => Dto.ClientAssetsOnly;

    public int UsageCount => Dto.UsageCount;

    public bool IsPlugin => Dto.SourceKind == SkillSourceKinds.Plugin || Dto.PluginId is not null;

    /// <summary>插件带来的 Skill 没有卸载入口(由插件管理);内置的也不能卸载。</summary>
    public bool CanUninstall => !IsPlugin && Dto.SourceKind != SkillSourceKinds.Builtin;

    /// <summary>真正会生效 = 全局开着 且 在这个工作区激活 且 没被「依赖本机资源」挡住。</summary>
    public bool IsEffective => Dto.GlobalEnabled && Dto.ActiveInWorkspace && !Dto.ClientAssetsOnly;

    [RelayCommand]
    private Task ToggleGlobalAsync() => owner.SetGlobalAsync(this, !Dto.GlobalEnabled);

    [RelayCommand]
    private Task ToggleActiveAsync() => owner.SetActiveAsync(this, !Dto.ActiveInWorkspace);

    [RelayCommand]
    private void AskUninstall() => owner.AskUninstall(this);
}

/// <summary>市场里的一条。</summary>
public sealed class MarketItemViewModel(SkillMarketItemDto dto)
{
    public SkillMarketItemDto Dto { get; } = dto;

    public string Slug => Dto.Slug;

    public string Title => string.IsNullOrWhiteSpace(Dto.DisplayName) ? Dto.Name : Dto.DisplayName;

    public string Description => Dto.Description;

    public string? Author => Dto.Author;

    public string? Version => Dto.Version;

    public int? Downloads => Dto.DownloadCount;
}

public enum SkillsTab
{
    Installed,
    Market,
}

/// <summary>
/// 设置 · 设备 · 技能:清单与诊断(<see cref="ClientStores.Skills"/> 的镜像)、全局启停(owner)、按工作区激活(member)、从 zip 安装(owner,≤ 3 MB,文件经 <see cref="IDialogService"/> 选)、
/// 卸载(owner,二次确认)、市场浏览 / 搜索 / 详情 / 安装(安装 owner;设备没有账户令牌时 <c>skills.authRequired</c>,界面提示先登录)。viewer 只读。
/// </summary>
public sealed partial class SkillsViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly ISkillsCommands _commands;
    private readonly Func<IDialogService> _dialogs;
    private long _marketVersion;

    public SkillsViewModel(SettingsScope scope, ClientStores stores, ISkillsCommands commands, Func<IDialogService> dialogs) : base(scope, Need.Viewer)
    {
        _stores = stores;
        _commands = commands;
        _dialogs = dialogs;
        stores.Skills.Changed += Rebuild;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<SkillRowViewModel> Skills { get; } = [];

    public ObservableCollection<SkillDiagnosticDto> Diagnostics { get; } = [];

    public ObservableCollection<MarketItemViewModel> Market { get; } = [];

    public ObservableCollection<ChoiceItem> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledTab), nameof(IsMarketTab))]
    public partial SkillsTab Tab { get; set; } = SkillsTab.Installed;

    public bool IsInstalledTab => Tab == SkillsTab.Installed;

    public bool IsMarketTab => Tab == SkillsTab.Market;

    /// <summary>清单搜索(纯 UI 状态):名称 / 说明 / 分类的子串。</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";

    /// <summary>安装 / 卸载的作用域:<c>global</c> 或 <c>project</c>(后者需要选中项目)。</summary>
    [ObservableProperty] public partial string InstallScope { get; set; } = SkillScopes.Global;

    /// <summary>刚安装好的 Skill 名(显示一条确认)。</summary>
    [ObservableProperty] public partial string? JustInstalled { get; private set; }

    [ObservableProperty] public partial PendingConfirm? PendingUninstall { get; private set; }

    public bool HasPendingUninstall => PendingUninstall is not null;

    partial void OnPendingUninstallChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingUninstall));

    partial void OnFilterChanged(string value) => Rebuild();

    public bool HasSkills => Skills.Count > 0;

    public bool ShowsNoMatch => Skills.Count == 0 && AllCount > 0;

    public bool ShowsEmpty => IsReady && AllCount == 0;

    private int AllCount { get; set; }

    public bool HasDiagnostics => Diagnostics.Count > 0;

    /// <summary>全局启停 / 安装 / 卸载:owner。</summary>
    public Gate GlobalGate => Scope.GateFor(Need.Owner);

    /// <summary>按工作区激活:member 起,而且需要选中项目。</summary>
    public Gate ActivateGate => Scope.GateFor(Need.Member);

    public bool CanActivate => ActivateGate.Allowed && Scope.WorkspaceId is not null;

    protected override void OnGatesChanged()
    {
        OnPropertyChanged(nameof(GlobalGate));
        OnPropertyChanged(nameof(ActivateGate));
        OnPropertyChanged(nameof(CanActivate));
    }

    protected override void OnUnavailable()
    {
        // 设备断开:清单状态未知,不显示成「没有 Skill」
        Skills.Clear();
        AllCount = 0;
        PendingUninstall = null;
    }

    protected override async Task LoadAsync()
    {
        var (device, workspace) = (Scope.DeviceId!, Scope.WorkspaceId);
        var list = await _commands.ListAsync(device, workspace);
        if (!IsCurrent(device, workspace))
        {
            return;
        }

        if (!list.IsOk)
        {
            Fail(list);
            return;
        }

        _ = await _commands.DiagnosticsAsync(device, workspace); // 诊断取不到不影响清单
        Rebuild();
        Ready();
        Rebuild();
        if (Tab == SkillsTab.Market)
        {
            _ = SearchMarketAsync();
        }
    }

    private void Rebuild()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var layer = _stores.Skills.State.Of(device, Scope.WorkspaceId);
        var all = layer is { Known: true } ? layer.Skills : [];
        AllCount = all.Length;
        var filter = (Filter ?? "").Trim();
        var shown = all
            .Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new SkillRowViewModel(this, s)).ToList();
        if (!shown.Select(r => r.Dto).SequenceEqual(Skills.Select(r => r.Dto)))
        {
            Skills.Clear();
            foreach (var row in shown)
            {
                Skills.Add(row);
            }
        }

        var diagnostics = layer is { Known: true, HasDiagnostics: true } ? layer.Diagnostics : [];
        if (!diagnostics.SequenceEqual(Diagnostics))
        {
            Diagnostics.Clear();
            foreach (var d in diagnostics)
            {
                Diagnostics.Add(d);
            }
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(ShowsNoMatch));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(CanActivate));
    }

    internal async Task SetGlobalAsync(SkillRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && GlobalGate.Allowed)
        {
            _ = await Act(_commands.SetGlobalEnabledAsync(device, row.Id, enabled, Scope.WorkspaceId));
            Rebuild();
        }
    }

    internal async Task SetActiveAsync(SkillRowViewModel row, bool active)
    {
        if (Scope.DeviceId is { } device && Scope.WorkspaceId is { } workspace && ActivateGate.Allowed)
        {
            _ = await Act(_commands.SetWorkspaceActiveAsync(device, workspace, row.Id, active));
            Rebuild();
        }
    }

    internal void AskUninstall(SkillRowViewModel row) => PendingUninstall = GlobalGate.Allowed && row.CanUninstall ? new PendingConfirm(row.Name, row.Id) : null;

    [RelayCommand]
    private void CancelUninstall() => PendingUninstall = null;

    [RelayCommand]
    private async Task ConfirmUninstallAsync()
    {
        if (PendingUninstall is not { } pending || Scope.DeviceId is not { } device || !GlobalGate.Allowed)
        {
            return;
        }

        PendingUninstall = null;
        var scope = Skills.FirstOrDefault(s => s.Id == pending.Id)?.Scope;
        _ = await Act(_commands.UninstallAsync(device, pending.Id, scope, Scope.WorkspaceId));
        Rebuild();
    }

    /// <summary>项目作用域的安装必须有选中的项目,否则退回全局(界面也会禁用「项目」选项)。</summary>
    private string EffectiveScope => InstallScope == SkillScopes.Project && Scope.WorkspaceId is not null ? SkillScopes.Project : SkillScopes.Global;

    /// <summary>选一个 zip 安装。超过 3 MB 不读文件、不发请求,直接给 <c>skills.packageTooLarge</c>(更大的包走市场)。</summary>
    [RelayCommand]
    private async Task InstallZipAsync()
    {
        if (Scope.DeviceId is not { } device || !GlobalGate.Allowed)
        {
            return;
        }

        var picked = await _dialogs().PickFileAsync(FilePickKind.SkillZip);
        if (picked is null)
        {
            return;
        }

        JustInstalled = null;
        if (picked.Size is 0 or > SkillsLimits.MaxZipBytes)
        {
            ActionFailure = new Failure(ActionOutcome.Invalid, "skills.packageTooLarge");
            return;
        }

        byte[] bytes;
        await using (var stream = await picked.OpenRead())
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        var result = await Act(_commands.InstallZipAsync(device, bytes, picked.Name, EffectiveScope, Scope.WorkspaceId));
        if (result.IsOk)
        {
            JustInstalled = result.Value!.Installed.Name;
        }

        Rebuild();
    }

    // ───────────────────────── 市场 ─────────────────────────

    public enum MarketState
    {
        Idle,
        Loading,
        Ready,
        Error,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarketLoading), nameof(MarketEmpty), nameof(MarketFailed))]
    public partial MarketState MarketStatus { get; private set; }

    [ObservableProperty] public partial Failure? MarketFailure { get; private set; }

    [ObservableProperty] public partial string MarketQuery { get; set; } = "";

    [ObservableProperty] public partial string MarketCategory { get; set; } = "";

    [ObservableProperty] public partial SkillMarketDetailDto? Detail { get; private set; }

    [ObservableProperty] public partial bool DetailLoading { get; private set; }

    /// <summary>安装被拒是因为设备没有账户令牌(<c>skills.authRequired</c>):界面提示先登录,不当成普通错误。</summary>
    [ObservableProperty] public partial bool AuthRequired { get; private set; }

    public bool MarketLoading => MarketStatus == MarketState.Loading;

    public bool MarketEmpty => MarketStatus == MarketState.Ready && Market.Count == 0;

    public bool MarketFailed => MarketStatus == MarketState.Error;

    [RelayCommand]
    private void ShowInstalled() => Tab = SkillsTab.Installed;

    [RelayCommand]
    private async Task ShowMarketAsync()
    {
        Tab = SkillsTab.Market;
        if (MarketStatus == MarketState.Idle)
        {
            await SearchMarketAsync();
        }
    }

    [RelayCommand]
    private async Task SearchMarketAsync()
    {
        if (Scope.DeviceId is not { } device || !Scope.GateFor(Need.Member).Allowed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _marketVersion);
        MarketStatus = MarketState.Loading;
        MarketFailure = null;
        var query = (MarketQuery ?? "").Trim(); // 双向绑定的控件可能写进 null
        var list = await _commands.MarketListAsync(device, query.Length == 0 ? null : query, string.IsNullOrEmpty(MarketCategory) ? null : MarketCategory);
        if (version != _marketVersion || !IsCurrent(device))
        {
            return; // 迟到的旧结果不盖掉新搜索
        }

        if (!list.IsOk)
        {
            MarketFailure = Failure.Of(list);
            MarketStatus = MarketState.Error;
            return;
        }

        Market.Clear();
        foreach (var item in list.Value!.Items)
        {
            Market.Add(new MarketItemViewModel(item));
        }

        if (Categories.Count == 0)
        {
            var categories = await _commands.MarketCategoriesAsync(device);
            if (categories.IsOk)
            {
                Categories.Add(new ChoiceItem("", null));
                foreach (var c in categories.Value!.Categories)
                {
                    Categories.Add(new ChoiceItem(c, c));
                }
            }
        }

        MarketStatus = MarketState.Ready;
        OnPropertyChanged(nameof(MarketEmpty));
    }

    [RelayCommand]
    private async Task ShowDetailAsync(MarketItemViewModel item)
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        Detail = new SkillMarketDetailDto(item.Dto, []);
        DetailLoading = true;
        var result = await _commands.MarketDetailAsync(device, item.Slug);
        DetailLoading = false;
        if (result.IsOk && IsCurrent(device))
        {
            Detail = result.Value;
        }
        else if (!result.IsOk)
        {
            ActionFailure = Failure.Of(result);
        }
    }

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    [RelayCommand]
    private async Task InstallMarketAsync(string slug)
    {
        if (Scope.DeviceId is not { } device || !GlobalGate.Allowed)
        {
            return;
        }

        AuthRequired = false;
        JustInstalled = null;
        var result = await Act(_commands.InstallMarketAsync(device, slug, Detail?.Item.Slug == slug ? Detail.Item.Version : null, EffectiveScope, Scope.WorkspaceId));
        if (result.IsOk)
        {
            JustInstalled = result.Value!.Installed.Name;
        }
        else if (result.ErrorKey == "skills.authRequired")
        {
            AuthRequired = true;
            ActionFailure = null;
        }

        Rebuild();
    }
}

// ═══════════════════════════ 命令 ═══════════════════════════

/// <summary>命令清单里的一行(管理清单:被关的也列出)。</summary>
public sealed partial class CommandRowViewModel(CommandsViewModel owner, CommandItemDto dto) : ObservableObject
{
    public CommandItemDto Dto { get; } = dto;

    public string Name => Dto.Name;

    public string Description => Dto.Description;

    public string Scope => Dto.Scope;

    public string Source => Dto.Source;

    public string? ArgumentHint => Dto.ArgumentHint;

    public bool Enabled => Dto.Enabled;

    [RelayCommand]
    private Task ToggleAsync() => owner.SetEnabledAsync(this, !Dto.Enabled);
}

/// <summary>设置 · 设备 · 命令:斜杠命令管理清单、启停(owner)、加载诊断。viewer 可读。</summary>
public sealed partial class CommandsViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly ICommandsAdminCommands _commands;

    public CommandsViewModel(SettingsScope scope, ClientStores stores, ICommandsAdminCommands commands) : base(scope, Need.Viewer)
    {
        _stores = stores;
        _commands = commands;
        stores.Commands.Changed += Rebuild;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<CommandRowViewModel> Commands { get; } = [];

    public ObservableCollection<CommandDiagnosticDto> Diagnostics { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Rebuild();

    private int AllCount { get; set; }

    public bool HasCommands => Commands.Count > 0;

    public bool ShowsEmpty => IsReady && AllCount == 0;

    public bool ShowsNoMatch => Commands.Count == 0 && AllCount > 0;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    protected override void OnUnavailable()
    {
        Commands.Clear();
        AllCount = 0;
    }

    protected override async Task LoadAsync()
    {
        var (device, workspace) = (Scope.DeviceId!, Scope.WorkspaceId);
        var all = await _commands.ListAllAsync(device, workspace);
        if (!IsCurrent(device, workspace))
        {
            return;
        }

        if (!all.IsOk)
        {
            Fail(all);
            return;
        }

        _ = await _commands.DiagnosticsAsync(device, workspace);
        Ready();
        Rebuild();
    }

    private void Rebuild()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var layer = _stores.Commands.State.Of(device, Scope.WorkspaceId);
        var all = layer is { Known: true, HasAll: true } ? layer.All : [];
        AllCount = all.Length;
        var filter = (Filter ?? "").Trim();
        var shown = all.Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Scope == CommandScopes.Builtin ? 1 : 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CommandRowViewModel(this, c)).ToList();
        if (!shown.Select(r => r.Dto).SequenceEqual(Commands.Select(r => r.Dto)))
        {
            Commands.Clear();
            foreach (var row in shown)
            {
                Commands.Add(row);
            }
        }

        var diagnostics = layer is { Known: true, HasDiagnostics: true } ? layer.Diagnostics : [];
        if (!diagnostics.SequenceEqual(Diagnostics))
        {
            Diagnostics.Clear();
            foreach (var d in diagnostics)
            {
                Diagnostics.Add(d);
            }
        }

        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(ShowsNoMatch));
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    internal async Task SetEnabledAsync(CommandRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && CanWrite)
        {
            _ = await Act(_commands.SetEnabledAsync(device, row.Name, enabled, Scope.WorkspaceId));
            Rebuild();
        }
    }
}

// ═══════════════════════════ Hooks ═══════════════════════════

/// <summary>编辑器里的一行环境变量:值只写不读(留空 = 沿用已存的值)。</summary>
public sealed partial class HookEnvRowViewModel(string name, bool hadValue) : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = name;

    /// <summary>新值(留空且已有值 = 不改)。</summary>
    [ObservableProperty] public partial string Value { get; set; } = "";

    /// <summary>设备上已经存了这个变量的值(界面只显示「已设置」,从不显示值)。</summary>
    public bool HadValue { get; } = hadValue;
}

/// <summary>钩子清单里的一行。</summary>
public sealed partial class HookRowViewModel(HooksViewModel owner, HookDto dto) : ObservableObject
{
    public HookDto Dto { get; } = dto;

    public string Event => Dto.Event;

    public string Type => Dto.Type;

    public string Scope => Dto.Scope;

    public bool Enabled => Dto.Enabled;

    public string? Matcher => Dto.Matcher;

    public string? Description => Dto.Description;

    public string Body => Dto.Type == HookNames.TypeCommand ? Dto.Command ?? "" : Dto.Prompt ?? "";

    public double TimeoutSeconds => Dto.TimeoutMs / 1000.0;

    public string[] EnvNames => Dto.EnvNames;

    public bool HasEnv => Dto.EnvNames.Length > 0;

    [RelayCommand]
    private Task ToggleAsync() => owner.SetEnabledAsync(this, !Dto.Enabled);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private void AskDelete() => owner.AskDelete(this);
}

/// <summary>钩子编辑器(新建 / 编辑一条)。校验是纯属性:不合法的字段列在 <see cref="Problems"/>,保存按钮据此禁用。</summary>
public sealed partial class HookEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Events = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SubagentStop"];

    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public const int MaxTextLength = 4096;

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsNew { get; set; } = true;

    [ObservableProperty] public partial string? Id { get; set; }

    [ObservableProperty] public partial string Scope { get; set; } = HookNames.ScopeGlobal;

    [ObservableProperty] public partial string Event { get; set; } = "PreToolUse";

    [ObservableProperty] public partial string Type { get; set; } = HookNames.TypeCommand;

    [ObservableProperty] public partial string Matcher { get; set; } = "";

    [ObservableProperty] public partial string Command { get; set; } = "";

    [ObservableProperty] public partial string Prompt { get; set; } = "";

    [ObservableProperty] public partial double TimeoutSeconds { get; set; } = 30;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    [ObservableProperty] public partial string Description { get; set; } = "";

    public ObservableCollection<HookEnvRowViewModel> Env { get; } = [];

    public HookEditorViewModel() => Env.CollectionChanged += (_, e) =>
    {
        foreach (var row in e.NewItems?.OfType<HookEnvRowViewModel>() ?? [])
        {
            row.PropertyChanged += (_, _) => Touch(); // 改变量名 / 值要立刻重新校验
        }

        Touch();
    };

    /// <summary>保存后设备给的软警告 key(<c>hooks.warn.weakMatcher</c>)。</summary>
    [ObservableProperty] public partial string? WarningKey { get; set; }

    [ObservableProperty] public partial Failure? SaveFailure { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    public bool IsCommand => Type == HookNames.TypeCommand;

    public bool IsProjectScope => Scope == HookNames.ScopeProject;

    partial void OnTypeChanged(string value) => Touch();

    partial void OnScopeChanged(string value) => Touch();

    partial void OnEventChanged(string value) => Touch();

    partial void OnCommandChanged(string value) => Touch();

    partial void OnPromptChanged(string value) => Touch();

    partial void OnTimeoutSecondsChanged(double value) => Touch();

    private void Touch()
    {
        OnPropertyChanged(nameof(IsCommand));
        OnPropertyChanged(nameof(IsProjectScope));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>当前草稿的问题(界面按字段写文字)。</summary>
    public IReadOnlyList<HookProblem> Problems
    {
        get
        {
            var problems = new List<HookProblem>();
            if (!Events.Contains(Event))
            {
                problems.Add(HookProblem.Event);
            }

            if (IsCommand && (Command.Trim().Length == 0))
            {
                problems.Add(HookProblem.EmptyCommand);
            }

            if (!IsCommand && Prompt.Trim().Length == 0)
            {
                problems.Add(HookProblem.EmptyPrompt);
            }

            if ((IsCommand ? Command : Prompt).Length > MaxTextLength)
            {
                problems.Add(HookProblem.TooLong);
            }

            if (TimeoutSeconds is < 1 or > 600)
            {
                problems.Add(HookProblem.Timeout);
            }

            if (Env.Any(e => !EnvName.IsMatch(e.Name)))
            {
                problems.Add(HookProblem.EnvName);
            }

            return problems;
        }
    }

    public bool CanSave => !IsSaving && Problems.Count == 0;

    public HookUpsertDto ToDraft() => new(
        IsNew ? null : Id,
        Event,
        Type,
        Matcher.Trim().Length == 0 ? null : Matcher.Trim(),
        Enabled,
        (int)Math.Round(TimeoutSeconds * 1000),
        Description.Trim().Length == 0 ? null : Description.Trim(),
        IsCommand ? Command : null,
        IsCommand ? null : Prompt,
        null,
        null,
        [.. Env.Select(e => new HookEnvVarDto(e.Name, e.Value.Length > 0 ? e.Value : e.HadValue ? null : ""))]);

    internal void Refresh() => Touch();

    internal void AddEnv()
    {
        Env.Add(new HookEnvRowViewModel("", false));
        Touch();
    }

    internal void RemoveEnv(HookEnvRowViewModel row)
    {
        Env.Remove(row);
        Touch();
    }
}

public enum HookProblem
{
    Event,
    EmptyCommand,
    EmptyPrompt,
    TooLong,
    Timeout,
    EnvName,
}

/// <summary>
/// 设置 · 设备 · Hooks:清单(全局层 + 当前项目层,<see cref="ClientStores.Hooks"/> 的镜像)、新建 / 编辑(事件、匹配器、命令 / 提示词、超时、环境变量)、启停、删除(二次确认)、试运行、配置诊断。
/// 命令文本 member 起可见,写入与试运行 owner(试运行真的会在设备上执行命令)。环境变量的值只写不读;试运行输出由设备脱敏(抹掉环境变量的值)后显示。
/// </summary>
public sealed partial class HooksViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly IHooksCommands _commands;

    public HooksViewModel(SettingsScope scope, ClientStores stores, IHooksCommands commands) : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        stores.Hooks.Changed += Rebuild;
        stores.Workspace.Changed += RaiseGates;
    }

    public ObservableCollection<HookRowViewModel> Hooks { get; } = [];

    public ObservableCollection<HookDiagnosticDto> Diagnostics { get; } = [];

    public HookEditorViewModel Editor { get; } = new();

    [ObservableProperty] public partial PendingConfirm? PendingDelete { get; private set; }

    public bool HasPendingDelete => PendingDelete is not null;

    partial void OnPendingDeleteChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingDelete));

    /// <summary>最近一次试运行的结果(设备已脱敏;纯 UI 状态)。</summary>
    [ObservableProperty] public partial HookRunReportDto? TestReport { get; private set; }

    [ObservableProperty] public partial bool IsTesting { get; private set; }

    public bool HasHooks => Hooks.Count > 0;

    public bool ShowsEmpty => IsReady && Hooks.Count == 0;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    protected override void OnUnavailable()
    {
        Hooks.Clear();
        Editor.IsOpen = false;
        PendingDelete = null;
    }

    protected override async Task LoadAsync()
    {
        var (device, workspace) = (Scope.DeviceId!, Scope.WorkspaceId);
        var list = await _commands.ListAsync(device, workspace);
        if (!IsCurrent(device, workspace))
        {
            return;
        }

        if (!list.IsOk)
        {
            Fail(list);
            return;
        }

        _ = await _commands.DiagnosticsAsync(device, workspace);
        Ready();
        Rebuild();
    }

    private void Rebuild()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var layer = _stores.Hooks.State.Of(device, Scope.WorkspaceId);
        var all = layer is { Known: true } ? layer.Hooks : [];
        var rows = all.OrderBy(h => h.Scope == HookNames.ScopeGlobal ? 0 : 1).ThenBy(h => Array.IndexOf([.. HookEditorViewModel.Events], h.Event)).Select(h => new HookRowViewModel(this, h)).ToList();
        if (!rows.Select(r => r.Dto).SequenceEqual(Hooks.Select(r => r.Dto)))
        {
            Hooks.Clear();
            foreach (var row in rows)
            {
                Hooks.Add(row);
            }
        }

        var diagnostics = layer is { Known: true, HasDiagnostics: true } ? layer.Diagnostics : [];
        if (!diagnostics.SequenceEqual(Diagnostics))
        {
            Diagnostics.Clear();
            foreach (var d in diagnostics)
            {
                Diagnostics.Add(d);
            }
        }

        OnPropertyChanged(nameof(HasHooks));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    [RelayCommand]
    private void New()
    {
        if (!CanWrite)
        {
            return;
        }

        var e = Editor;
        e.IsNew = true;
        e.Id = null;
        e.Scope = HookNames.ScopeGlobal;
        e.Event = "PreToolUse";
        e.Type = HookNames.TypeCommand;
        e.Matcher = "";
        e.Command = "";
        e.Prompt = "";
        e.TimeoutSeconds = 30;
        e.Enabled = true;
        e.Description = "";
        e.Env.Clear();
        e.WarningKey = null;
        e.SaveFailure = null;
        TestReport = null;
        e.IsOpen = true;
    }

    internal void Edit(HookRowViewModel row)
    {
        if (!CanWrite)
        {
            return;
        }

        var (e, d) = (Editor, row.Dto);
        e.IsNew = false;
        e.Id = d.Id;
        e.Scope = d.Scope;
        e.Event = d.Event;
        e.Type = d.Type;
        e.Matcher = d.Matcher ?? "";
        e.Command = d.Command ?? "";
        e.Prompt = d.Prompt ?? "";
        e.TimeoutSeconds = d.TimeoutMs / 1000.0;
        e.Enabled = d.Enabled;
        e.Description = d.Description ?? "";
        e.Env.Clear();
        foreach (var name in d.EnvNames)
        {
            e.Env.Add(new HookEnvRowViewModel(name, true)); // 值从不下发:只有名字,留空 = 沿用
        }

        e.WarningKey = null;
        e.SaveFailure = null;
        TestReport = null;
        e.IsOpen = true;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        foreach (var row in Editor.Env)
        {
            row.Value = "";
        }

        Editor.IsOpen = false;
    }

    [RelayCommand]
    private void AddEnv() => Editor.AddEnv();

    [RelayCommand]
    private void RemoveEnv(HookEnvRowViewModel row) => Editor.RemoveEnv(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || !Editor.CanSave)
        {
            return;
        }

        var workspace = Editor.IsProjectScope ? Scope.WorkspaceId : null;
        if (Editor.IsProjectScope && workspace is null)
        {
            Editor.SaveFailure = new Failure(ActionOutcome.Invalid, "hooks.error.noWorkspace");
            return;
        }

        Editor.IsSaving = true;
        Editor.SaveFailure = null;
        try
        {
            var result = await _commands.SaveAsync(device, Editor.Scope, workspace, Editor.ToDraft());
            if (!result.IsOk)
            {
                Editor.SaveFailure = Failure.Of(result);
                return;
            }

            Editor.WarningKey = result.Value!.WarningKey;
            foreach (var row in Editor.Env)
            {
                row.Value = ""; // 值送出去就清掉
            }

            if (Editor.WarningKey is null)
            {
                Editor.IsOpen = false;
            }

            Rebuild();
        }
        finally
        {
            Editor.IsSaving = false;
            Editor.Refresh();
        }
    }

    /// <summary>试运行当前草稿(owner;command 型真的在设备上执行)。输出由设备脱敏。</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (Scope.DeviceId is not { } device || !CanWrite || Editor.Problems.Count > 0)
        {
            return;
        }

        IsTesting = true;
        TestReport = null;
        try
        {
            var result = await _commands.TestAsync(device, Editor.Scope, Editor.IsProjectScope ? Scope.WorkspaceId : null, Editor.ToDraft());
            if (result.IsOk)
            {
                TestReport = result.Value;
            }
            else
            {
                Editor.SaveFailure = Failure.Of(result);
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    internal async Task SetEnabledAsync(HookRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && CanWrite)
        {
            _ = await Act(_commands.SetEnabledAsync(device, row.Scope, row.Scope == HookNames.ScopeProject ? Scope.WorkspaceId : null, row.Dto.Id, enabled));
            Rebuild();
        }
    }

    internal void AskDelete(HookRowViewModel row) => PendingDelete = CanWrite ? new PendingConfirm(row.Dto.Description ?? row.Dto.Event, row.Dto.Id) : null;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } pending || Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        PendingDelete = null;
        var row = Hooks.FirstOrDefault(h => h.Dto.Id == pending.Id);
        if (row is null)
        {
            return;
        }

        _ = await Act(_commands.DeleteAsync(device, row.Scope, row.Scope == HookNames.ScopeProject ? Scope.WorkspaceId : null, pending.Id));
        Rebuild();
    }
}
