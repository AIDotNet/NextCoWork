using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

public enum PluginsTab
{
    Installed,
    Market,
    Activity,
}

/// <summary>市场 / 活动这类「页面自己的查询」的加载状态。</summary>
public enum PluginQueryState
{
    Idle,
    Loading,
    Ready,
    Error,
}

/// <summary>
/// 设置 · 设备 · 插件(docs/design/07 §3、09 §8.4「插件」):已安装(启停、卸载二次确认、zip 安装 ≤ 3 MB、逐项授予 / 撤销能力、配置表单、插件命令)、
/// 市场(浏览 / 搜索 / 分页 / 详情 / 安装 / 更新)、活动与诊断(最近的宿主调用、诊断、faulted 恢复),以及插件在等用户回答的提示(卡片置顶)。
/// <list type="bullet">
/// <item><b>事实在 Store</b>:清单、授权、诊断、挂起的提示都是 <see cref="ClientStores.Plugins"/> 的镜像(<c>plugins</c> 主题,全量 + 单调 Rev);设备断开时镜像是「未知」,本页不显示成空、也不继续把旧的「运行中」当现状。
/// 活动日志、市场、更新检测、配置值是本页自己的查询结果,不进 Store。</item>
/// <item><b>角色</b>:读取要 member(清单里有命令白名单 / 域名,viewer 按协议读不了 → <see cref="PageState.Forbidden"/>,不是只读);member 可答复提示、运行命令、浏览市场;写入(启停 / 卸载 / 授权 / 配置 / 安装 / 更新)要 owner。</item>
/// <item><b>授权</b>:能力逐项授予 / 撤销;撤销必选能力 = 插件停下来,所以先二次确认。批准是设备上的决定,本页只发意图。</item>
/// <item><b>登录</b>:市场安装需要设备上的账户令牌,没有时 <c>plugins.authRequired</c> → 提示卡 + <see cref="SignInRequested"/>(外壳接到账户登录;本页不碰账户服务)。</item>
/// </list>
/// </summary>
public sealed partial class PluginsViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly IPluginsCommands _commands;
    private readonly Func<IDialogService> _dialogs;
    private readonly Dictionary<string, PluginRowViewModel> _rows = new(StringComparer.Ordinal);
    private long _marketVersion;
    private long _activityVersion;

    public PluginsViewModel(SettingsScope scope, ClientStores stores, IPluginsCommands commands, Func<IDialogService> dialogs) : base(scope, Need.Member)
    {
        _stores = stores;
        _commands = commands;
        _dialogs = dialogs;
        stores.Plugins.Changed += Rebuild;
        stores.Workspace.Changed += RaiseGates;
    }

    /// <summary>用户点了「去登录」(市场安装需要账户)。外壳把它接到账户登录。</summary>
    public event Action? SignInRequested;

    /// <summary>插件词条解析用的语言(界面语言;<see cref="PluginMessages"/> 按它取,取不到回退 en-US)。</summary>
    public string Locale
    {
        get => _locale;
        set
        {
            if (_locale != value)
            {
                _locale = value;
                Rebuild();
            }
        }
    }

    private string _locale = "en-US";

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public ObservableCollection<PluginPromptViewModel> Prompts { get; } = [];

    public ObservableCollection<PluginDiagnosticRow> Diagnostics { get; } = [];

    public ObservableCollection<PluginRowViewModel> Faulted { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledTab), nameof(IsMarketTab), nameof(IsActivityTab))]
    public partial PluginsTab Tab { get; set; } = PluginsTab.Installed;

    public bool IsInstalledTab => Tab == PluginsTab.Installed;

    public bool IsMarketTab => Tab == PluginsTab.Market;

    public bool IsActivityTab => Tab == PluginsTab.Activity;

    partial void OnTabChanged(PluginsTab value)
    {
        if (!IsReady)
        {
            return;
        }

        if (value == PluginsTab.Market && MarketStatus == PluginQueryState.Idle)
        {
            _ = SearchMarketAsync();
        }
        else if (value == PluginsTab.Activity)
        {
            _ = LoadActivityAsync();
        }
    }

    // ───────────────────────── 权限 ─────────────────────────

    /// <summary>写入(启停 / 卸载 / 授权 / 配置 / 安装 / 更新):owner。</summary>
    public Gate OwnerGate => Scope.GateFor(Need.Owner);

    /// <summary>答复提示、运行命令、浏览市场:member 起。</summary>
    public Gate MemberGate => Scope.GateFor(Need.Member);

    protected override void OnGatesChanged()
    {
        OnPropertyChanged(nameof(OwnerGate));
        OnPropertyChanged(nameof(MemberGate));
        OnPropertyChanged(nameof(CanAnswer));
        OnPropertyChanged(nameof(CanUpdateAll));
        foreach (var row in Plugins)
        {
            row.RaiseGates();
        }

        foreach (var prompt in Prompts)
        {
            prompt.RaiseGates();
        }
    }

    public bool CanAnswer => MemberGate.Allowed;

    // ───────────────────────── 已安装 ─────────────────────────

    /// <summary>刚装好的插件名(显示一条确认;新装的插件是禁用的、没有授权,下一步是逐条批准)。</summary>
    [ObservableProperty] public partial string? JustInstalled { get; private set; }

    [ObservableProperty] public partial PendingConfirm? PendingUninstall { get; private set; }

    [ObservableProperty] public partial PendingPermissionRevoke? PendingRevoke { get; private set; }

    /// <summary>刚运行过的命令名(显示一条确认)。</summary>
    [ObservableProperty] public partial string? JustRan { get; private set; }

    /// <summary>回答提示时发现已在另一台设备上处理了(竞态,不是故障)。</summary>
    [ObservableProperty] public partial bool PromptHandledElsewhere { get; private set; }

    public bool HasPendingUninstall => PendingUninstall is not null;

    public bool HasPendingRevoke => PendingRevoke is not null;

    partial void OnPendingUninstallChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingUninstall));

    partial void OnPendingRevokeChanged(PendingPermissionRevoke? value) => OnPropertyChanged(nameof(HasPendingRevoke));

    public bool HasPlugins => Plugins.Count > 0;

    public bool HasPrompts => Prompts.Count > 0;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasFaulted => Faulted.Count > 0;

    public bool ShowsEmpty => IsReady && Plugins.Count == 0;

    /// <summary>设备上的插件 API 版本(清单的 <c>engines</c> 比的是它)。</summary>
    public string ApiVersion => _stores.Plugins.State.Of(Scope.DeviceId ?? "").ApiVersion;

    protected override void OnUnavailable()
    {
        // 设备断开:清单、提示、诊断状态未知(规则 7),不显示成「没有插件」,也不继续把旧的「运行中」当现状
        _rows.Clear();
        Plugins.Clear();
        Prompts.Clear();
        Diagnostics.Clear();
        Faulted.Clear();
        PendingUninstall = null;
        PendingRevoke = null;
        Raise();
    }

    protected override async Task LoadAsync()
    {
        var device = Scope.DeviceId!;
        var list = await _commands.ListAsync(device);
        if (!IsCurrent(device))
        {
            return;
        }

        if (!list.IsOk)
        {
            Fail(list);
            return;
        }

        Rebuild();
        Ready();
        Rebuild();
        if (Tab == PluginsTab.Market && MarketStatus == PluginQueryState.Idle)
        {
            _ = SearchMarketAsync();
        }
        else if (Tab == PluginsTab.Activity)
        {
            _ = LoadActivityAsync();
        }
    }

    /// <summary>从镜像重建派生出来的清单、提示、诊断。行对象按插件 id 复用(展开状态、配置草稿不丢)。</summary>
    private void Rebuild()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var mirror = _stores.Plugins.State.Of(device);
        if (!mirror.Known)
        {
            if (Plugins.Count > 0 || Prompts.Count > 0)
            {
                OnUnavailable();
            }

            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<PluginRowViewModel>();
        foreach (var dto in mirror.Plugins)
        {
            seen.Add(dto.Id);
            if (!_rows.TryGetValue(dto.Id, out var row))
            {
                _rows[dto.Id] = row = new PluginRowViewModel(this, dto);
            }

            row.Update(dto, Locale);
            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        if (!rows.SequenceEqual(Plugins))
        {
            Plugins.Clear();
            foreach (var row in rows)
            {
                Plugins.Add(row);
            }
        }

        var prompts = mirror.Interactions.OrderBy(i => i.CreatedAt).ToList();
        if (!prompts.Select(p => p.RequestId).SequenceEqual(Prompts.Select(p => p.RequestId)))
        {
            Prompts.Clear();
            foreach (var prompt in prompts)
            {
                Prompts.Add(new PluginPromptViewModel(this, prompt, mirror.Find(prompt.PluginId), Locale));
            }

            if (prompts.Count > 0)
            {
                PromptHandledElsewhere = false;
            }
        }

        var diagnostics = rows.SelectMany(r => r.Dto.Diagnostics.Select(d => new PluginDiagnosticRow(r.Id, r.DisplayName, d))).ToList();
        if (!diagnostics.SequenceEqual(Diagnostics))
        {
            Diagnostics.Clear();
            foreach (var d in diagnostics)
            {
                Diagnostics.Add(d);
            }
        }

        var faulted = rows.Where(r => r.IsFaulted).ToList();
        if (!faulted.SequenceEqual(Faulted))
        {
            Faulted.Clear();
            foreach (var row in faulted)
            {
                Faulted.Add(row);
            }
        }

        RebuildMarketFlags(mirror);
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(HasPrompts));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasFaulted));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(ApiVersion));
    }

    // ───────────────────────── 启停 / 卸载 / 安装 ─────────────────────────

    internal async Task SetEnabledAsync(PluginRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && OwnerGate.Allowed)
        {
            _ = await Act(_commands.SetEnabledAsync(device, row.Id, enabled));
            Rebuild();
        }
    }

    internal void AskUninstall(PluginRowViewModel row) => PendingUninstall = OwnerGate.Allowed ? new PendingConfirm(row.DisplayName, row.Id) : null;

    [RelayCommand]
    private void CancelUninstall() => PendingUninstall = null;

    [RelayCommand]
    private async Task ConfirmUninstallAsync()
    {
        if (PendingUninstall is not { } pending || Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        PendingUninstall = null;
        _ = await Act(_commands.UninstallAsync(device, pending.Id));
        Rebuild();
    }

    /// <summary>选一个 zip 安装。超过 3 MB 不读文件、不发请求,直接给 <c>plugins.packageTooLarge</c>(更大的包走市场)。新装的插件是禁用的、没有授权:展开它让用户逐条批准。</summary>
    [RelayCommand]
    private async Task InstallZipAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        var picked = await _dialogs().PickFileAsync(FilePickKind.PluginZip);
        if (picked is null)
        {
            return;
        }

        JustInstalled = null;
        if (picked.Size is 0 or > PluginsLimits.MaxZipBytes)
        {
            ActionFailure = new Failure(ActionOutcome.Invalid, "plugins.packageTooLarge");
            return;
        }

        byte[] bytes;
        await using (var stream = await picked.OpenRead())
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        var result = await Act(_commands.InstallPackageAsync(device, bytes, picked.Name));
        Rebuild();
        if (result.IsOk)
        {
            JustInstalled = result.Value!.Installed.DisplayName;
            if (_rows.TryGetValue(result.Value.Installed.Id, out var row))
            {
                row.IsExpanded = true;
            }
        }
    }

    // ───────────────────────── 权限 ─────────────────────────

    internal async Task TogglePermissionAsync(PluginRowViewModel plugin, PluginPermissionRowViewModel permission)
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        if (!permission.IsGranted)
        {
            _ = await Act(_commands.GrantAsync(device, plugin.Id, [permission.Name]));
            Rebuild();
            return;
        }

        if (permission.IsRequired)
        {
            PendingRevoke = new PendingPermissionRevoke(plugin.Id, plugin.DisplayName, permission.Name, true); // 撤掉必选能力插件就停下来:先确认
            return;
        }

        _ = await Act(_commands.RevokeAsync(device, plugin.Id, [permission.Name]));
        Rebuild();
    }

    [RelayCommand]
    private void CancelRevoke() => PendingRevoke = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        if (PendingRevoke is not { } pending || Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        PendingRevoke = null;
        _ = await Act(_commands.RevokeAsync(device, pending.PluginId, [pending.Permission]));
        Rebuild();
    }

    internal async Task GrantPendingAsync(PluginRowViewModel plugin)
    {
        if (Scope.DeviceId is { } device && OwnerGate.Allowed && plugin.HasPending)
        {
            _ = await Act(_commands.GrantAsync(device, plugin.Id, plugin.PendingPermissions));
            Rebuild();
        }
    }

    internal async Task RecoverAsync(PluginRowViewModel plugin)
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        var off = await Act(_commands.SetEnabledAsync(device, plugin.Id, false));
        if (off.IsOk)
        {
            _ = await Act(_commands.SetEnabledAsync(device, plugin.Id, true));
        }

        Rebuild();
    }

    // ───────────────────────── 配置 ─────────────────────────

    public async Task LoadConfigAsync(PluginRowViewModel plugin)
    {
        if (Scope.DeviceId is not { } device || !plugin.HasConfig)
        {
            return;
        }

        var result = await _commands.GetConfigurationAsync(device, plugin.Id);
        if (!IsCurrent(device) || !result.IsOk)
        {
            return;
        }

        ReconcileConfig(plugin, result.Value!, force: false);
    }

    private static void ReconcileConfig(PluginRowViewModel plugin, PluginsConfigurationResult values, bool force)
    {
        foreach (var field in plugin.ConfigFields)
        {
            field.Reconcile(values.Values.GetValueOrDefault(field.Key), force);
        }
    }

    internal async Task ApplyConfigAsync(PluginRowViewModel plugin, PluginConfigFieldViewModel field, PluginConfigValueDto? value)
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        var result = await Act(_commands.SetConfigurationAsync(device, plugin.Id, field.Key, value));
        if (result.IsOk)
        {
            field.Error = null;
            ReconcileConfig(plugin, result.Value!, force: false);
            field.Reconcile(result.Value!.Values.GetValueOrDefault(field.Key), force: true); // 写成功:这一项跟随设备
        }
        else
        {
            field.Error = result.ErrorKey ?? "plugins.configurationInvalid";
            ActionFailure = null; // 错误回显在字段上,不再重复一条页面级失败
            if (result.Outcome == ActionOutcome.Unavailable)
            {
                ActionFailure = Failure.Of(result);
            }
        }
    }

    // ───────────────────────── 命令 / 提示 ─────────────────────────

    internal async Task RunCommandAsync(PluginRowViewModel plugin, PluginCommandRowViewModel command)
    {
        if (Scope.DeviceId is not { } device || !MemberGate.Allowed)
        {
            return;
        }

        JustRan = null;
        var result = await Act(_commands.RunCommandAsync(device, plugin.Id, command.CommandId, Scope.WorkspaceId));
        if (result.IsOk)
        {
            JustRan = command.Title;
        }
    }

    internal async Task AnswerAsync(PluginPromptViewModel prompt, bool? confirmed, string? text, string? picked)
    {
        if (Scope.DeviceId is not { } device || !MemberGate.Allowed)
        {
            return;
        }

        PromptHandledElsewhere = false;
        var result = await Act(_commands.ReplyInteractionAsync(device, prompt.RequestId, confirmed, text, picked));
        if (result.IsOk)
        {
            return; // 提示从镜像里消失靠设备推送的新状态
        }

        if (result.ErrorKey == "plugins.interactionNotFound")
        {
            // 超时 / 被撤销 / 别的控制端先回答了:竞态,不是故障。清掉失败条,对一下账让这张卡片消失
            ActionFailure = null;
            PromptHandledElsewhere = true;
            _ = await _commands.ListAsync(device);
            Rebuild();
        }
    }

    [RelayCommand]
    private void DismissHandled() => PromptHandledElsewhere = false;

    /// <summary>登录入口由外壳接(账户登录);本页只发出请求。</summary>
    [RelayCommand]
    private void SignIn() => SignInRequested?.Invoke();

    // ───────────────────────── 市场 ─────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarketLoading), nameof(MarketEmpty), nameof(MarketFailed), nameof(MarketReady))]
    public partial PluginQueryState MarketStatus { get; private set; }

    [ObservableProperty] public partial Failure? MarketFailure { get; private set; }

    [ObservableProperty] public partial string MarketQuery { get; set; } = "";

    [ObservableProperty] public partial string MarketCategory { get; set; } = "";

    [ObservableProperty] public partial int MarketPage { get; private set; } = 1;

    [ObservableProperty] public partial bool MarketHasNext { get; private set; }

    [ObservableProperty] public partial PluginMarketDetailDto? Detail { get; private set; }

    [ObservableProperty] public partial bool DetailLoading { get; private set; }

    /// <summary>安装被拒是因为设备没有账户令牌(<c>plugins.authRequired</c>):提示先登录,不当成普通错误。</summary>
    [ObservableProperty] public partial bool AuthRequired { get; private set; }

    /// <summary>正在安装的 slug(其余安装按钮禁用:同一个 slug 的两次安装设备会合流,这里干脆不给点)。</summary>
    [ObservableProperty] public partial string? InstallingSlug { get; private set; }

    public ObservableCollection<PluginMarketRowViewModel> Market { get; } = [];

    public ObservableCollection<ChoiceItem> Categories { get; } = [];

    public ObservableCollection<PluginUpdateRow> Updates { get; } = [];

    public bool MarketLoading => MarketStatus == PluginQueryState.Loading;

    public bool MarketFailed => MarketStatus == PluginQueryState.Error;

    public bool MarketReady => MarketStatus == PluginQueryState.Ready;

    public bool MarketEmpty => MarketStatus == PluginQueryState.Ready && Market.Count == 0;

    public bool HasUpdates => Updates.Count > 0;

    /// <summary>可批量更新的数量(市场来源的;本地装的不计入)。</summary>
    public int UpdatableCount => Updates.Count(u => !u.NotBatchable);

    public bool CanUpdateAll => OwnerGate.Allowed && UpdatableCount > 0;

    public bool HasPreviousPage => MarketPage > 1;

    [ObservableProperty] public partial PluginsUpdateAllResult? UpdateSummary { get; private set; }

    public bool HasUpdateSummary => UpdateSummary is not null;

    partial void OnUpdateSummaryChanged(PluginsUpdateAllResult? value) => OnPropertyChanged(nameof(HasUpdateSummary));

    private string? _lastCategory;
    private string? _lastQuery;

    [RelayCommand]
    private void ShowInstalled() => Tab = PluginsTab.Installed;

    [RelayCommand]
    private void ShowMarket() => Tab = PluginsTab.Market;

    [RelayCommand]
    private void ShowActivity() => Tab = PluginsTab.Activity;

    /// <summary>新的搜索(回到第一页)。</summary>
    [RelayCommand]
    private Task SearchMarketAsync() => LoadMarketPageAsync(1);

    [RelayCommand]
    private Task NextPageAsync() => MarketHasNext ? LoadMarketPageAsync(MarketPage + 1) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousPageAsync() => MarketPage > 1 ? LoadMarketPageAsync(MarketPage - 1) : Task.CompletedTask;

    private async Task LoadMarketPageAsync(int page)
    {
        if (Scope.DeviceId is not { } device || !MemberGate.Allowed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _marketVersion);
        MarketStatus = PluginQueryState.Loading;
        MarketFailure = null;
        var query = (MarketQuery ?? "").Trim(); // 双向绑定的控件可能写进 null
        var category = string.IsNullOrEmpty(MarketCategory) ? null : MarketCategory;
        _lastQuery = query;
        _lastCategory = category;
        var list = await _commands.MarketListAsync(device, query.Length == 0 ? null : query, category, page);
        if (version != _marketVersion || !IsCurrent(device))
        {
            return; // 迟到的旧结果不盖掉新搜索
        }

        if (!list.IsOk)
        {
            MarketFailure = Failure.Of(list);
            MarketStatus = PluginQueryState.Error;
            return;
        }

        MarketPage = list.Value!.Page;
        MarketHasNext = list.Value.HasNext;
        var mirror = _stores.Plugins.State.Of(device);
        Market.Clear();
        foreach (var item in list.Value.Items)
        {
            Market.Add(MarketRow(item, mirror));
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

        MarketStatus = PluginQueryState.Ready;
        OnPropertyChanged(nameof(MarketEmpty));
        OnPropertyChanged(nameof(HasPreviousPage));
    }

    private PluginMarketRowViewModel MarketRow(PluginMarketItemDto item, DevicePlugins mirror)
    {
        var installed = mirror.Plugins.FirstOrDefault(p => p.Id == item.PluginId || (p.Slug is not null && p.Slug == item.Slug));
        var update = Updates.FirstOrDefault(u => u.Dto.PluginId == installed?.Id)?.Dto;
        return new PluginMarketRowViewModel(item, installed?.Version, update);
    }

    /// <summary>快照变了(装好 / 卸载):市场行的「已安装」标记跟着变。</summary>
    private void RebuildMarketFlags(DevicePlugins mirror)
    {
        if (Market.Count == 0)
        {
            return;
        }

        var rows = Market.Select(r => MarketRow(r.Dto, mirror)).ToList();
        if (rows.Zip(Market).All(p => (p.First.InstalledVersion, p.First.HasUpdate) == (p.Second.InstalledVersion, p.Second.HasUpdate)))
        {
            return;
        }

        Market.Clear();
        foreach (var row in rows)
        {
            Market.Add(row);
        }
    }

    [RelayCommand]
    private async Task ShowDetailAsync(PluginMarketRowViewModel item)
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        Detail = new PluginMarketDetailDto(item.Dto, []);
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
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed || InstallingSlug is not null)
        {
            return;
        }

        AuthRequired = false;
        JustInstalled = null;
        InstallingSlug = slug;
        try
        {
            var result = await Act(_commands.InstallMarketAsync(device, slug, Detail?.Item.Slug == slug ? Detail.Item.Version : null));
            if (result.IsOk)
            {
                JustInstalled = result.Value!.Installed.DisplayName;
            }
            else if (result.ErrorKey == "plugins.authRequired")
            {
                AuthRequired = true;
                ActionFailure = null;
            }
        }
        finally
        {
            InstallingSlug = null;
        }

        Rebuild();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (Scope.DeviceId is not { } device || !MemberGate.Allowed)
        {
            return;
        }

        var result = await Act(_commands.CheckUpdatesAsync(device, force: true));
        if (!result.IsOk || !IsCurrent(device))
        {
            return;
        }

        var mirror = _stores.Plugins.State.Of(device);
        Updates.Clear();
        foreach (var update in result.Value!.Updates)
        {
            Updates.Add(new PluginUpdateRow(update, mirror.Find(update.PluginId)?.DisplayName ?? update.DisplayName));
        }

        RebuildMarketFlags(mirror);
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdatableCount));
        OnPropertyChanged(nameof(CanUpdateAll));
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (Scope.DeviceId is not { } device || !OwnerGate.Allowed)
        {
            return;
        }

        UpdateSummary = null;
        var result = await Act(_commands.UpdateAllAsync(device));
        if (result.IsOk)
        {
            UpdateSummary = result.Value;
            await CheckUpdatesAsync(); // 重新检测:更新掉的从列表里消失
        }
    }

    // ───────────────────────── 活动 ─────────────────────────

    public ObservableCollection<PluginActivityRow> Activity { get; } = [];

    /// <summary>活动按插件过滤(空 = 全部;纯 UI 选择)。</summary>
    [ObservableProperty] public partial string ActivityFilter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityLoading), nameof(ActivityFailed), nameof(ActivityEmpty))]
    public partial PluginQueryState ActivityStatus { get; private set; }

    [ObservableProperty] public partial Failure? ActivityFailure { get; private set; }

    public bool ActivityLoading => ActivityStatus == PluginQueryState.Loading;

    public bool ActivityFailed => ActivityStatus == PluginQueryState.Error;

    public bool ActivityEmpty => ActivityStatus == PluginQueryState.Ready && Activity.Count == 0;

    public IReadOnlyList<ChoiceItem> ActivityChoices =>
        [new ChoiceItem("", null), .. Plugins.Select(p => new ChoiceItem(p.Id, p.DisplayName))];

    partial void OnActivityFilterChanged(string value) => _ = LoadActivityAsync();

    [RelayCommand]
    private async Task LoadActivityAsync()
    {
        if (Scope.DeviceId is not { } device || !MemberGate.Allowed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _activityVersion);
        ActivityStatus = PluginQueryState.Loading;
        ActivityFailure = null;
        var filter = string.IsNullOrEmpty(ActivityFilter) ? null : ActivityFilter;
        var result = await _commands.ActivityAsync(device, filter);
        if (version != _activityVersion || !IsCurrent(device))
        {
            return;
        }

        if (!result.IsOk)
        {
            ActivityFailure = Failure.Of(result);
            ActivityStatus = PluginQueryState.Error;
            return;
        }

        var mirror = _stores.Plugins.State.Of(device);
        Activity.Clear();
        foreach (var e in result.Value!.Entries)
        {
            Activity.Add(new PluginActivityRow(DateTimeOffset.FromUnixTimeMilliseconds(e.Ts), e.PluginId, mirror.Find(e.PluginId)?.DisplayName ?? e.PluginId, e.Method, e.Summary, e.Verdict, e.DurationMs));
        }

        ActivityStatus = PluginQueryState.Ready;
        OnPropertyChanged(nameof(ActivityEmpty));
        OnPropertyChanged(nameof(ActivityChoices));
    }
}
