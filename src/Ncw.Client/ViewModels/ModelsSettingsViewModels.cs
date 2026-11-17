using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>设置页顶部设备切换器里的一项。</summary>
public sealed record DeviceChoice(string DeviceId, string Name, DeviceKind Kind, DeviceConnection Connection);

/// <summary>一次连通性测试 / 刷新 / 保存的结局,界面按它写文字。</summary>
public enum TestState
{
    None,
    Testing,
    Ok,
    Failed,
}

/// <summary>读 <c>providers.json</c> 时被跳过的一个坏条目(设置页的诊断行)。</summary>
public sealed record ProviderProblemRow(string? ProviderId, string Key, string Detail);

/// <summary>连通性测试的结果(结构化,界面自己写文字)。</summary>
public sealed record TestResult(TestState State, int? LatencyMs, string? ErrorKey);

/// <summary>供应商下的一个模型别名(设置页的一行)。</summary>
public sealed partial class AliasItemViewModel(ModelsSettingsViewModel owner, ModelAliasDto dto) : ObservableObject
{
    public ModelAliasDto Dto { get; private set; } = dto;

    public string Alias => Dto.Alias;

    public string Upstream => Dto.UpstreamModel;

    public string DisplayName => string.IsNullOrWhiteSpace(Dto.DisplayName) ? Dto.Alias : Dto.DisplayName!;

    public string WindowText => ComposerLogic.WindowText(Dto.ContextWindow);

    public bool SupportsImages => Dto.SupportsImages;

    public bool CanThink => Dto.ThinkingConfigJson is not null || Dto.ReasoningEffortsJson is not null;

    public bool IsEnabled => Dto.Enabled;

    public void Update(ModelAliasDto dto)
    {
        Dto = dto;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task ToggleAsync() => owner.SetAliasEnabledAsync(Dto, !Dto.Enabled);

    [RelayCommand]
    private Task RenameAsync() => owner.RenameAliasAsync(Dto);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAliasAsync(Dto);
}

/// <summary>从上游拉到的一个候选模型(导入弹窗里的一行)。</summary>
public sealed partial class FetchedModelViewModel(string id, string? displayName, bool selected) : ObservableObject
{
    public string Id { get; } = id;

    public string? DisplayName { get; } = displayName;

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
}

/// <summary>
/// 供应商列表里的一个供应商。事实在设备上(<c>providers</c> 主题的镜像);这里只有纯 UI 状态(展开、测试结果)和一份凭据摘要的缓存(只有后四位,从不持有密钥)。
/// </summary>
public sealed partial class ProviderItemViewModel(ModelsSettingsViewModel owner, ProviderDto dto) : ObservableObject
{
    public ProviderDto Dto { get; private set; } = dto;

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public string Protocol => Dto.Protocol;

    public string BaseUrl => Dto.BaseUrl;

    public bool IsEnabled => Dto.Enabled;

    public bool HasCredential => Dto.HasCredential;

    /// <summary>走账号登录(OAuth)的供应商。</summary>
    public bool IsOAuth => Dto.OAuthIssuer is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLast4))]
    public partial string? Last4 { get; set; }

    public bool HasLast4 => Last4 is not null;

    /// <summary>OAuth 当前账号的邮箱 / 标签。</summary>
    [ObservableProperty] public partial string? AccountLabel { get; set; }

    [ObservableProperty] public partial bool NeedsReauth { get; set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>在桌面设置页的列表栏里被选中(从 <see cref="ModelsSettingsViewModel.SelectedProviderId"/> 派生)。</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => owner.SelectedProviderId = Id;

    [ObservableProperty] public partial TestResult TestInfo { get; private set; } = new(TestState.None, null, null);

    public bool HasTest => TestInfo.State != TestState.None;

    partial void OnTestInfoChanged(TestResult value) => OnPropertyChanged(nameof(HasTest));

    [ObservableProperty] public partial bool IsSigningIn { get; set; }

    public ObservableCollection<AliasItemViewModel> Aliases { get; } = [];

    public int ModelCount => Aliases.Count;

    public bool HasModels => Aliases.Count > 0;

    public void Update(ProviderDto dto, IReadOnlyList<ModelAliasDto> aliases)
    {
        Dto = dto;
        var byAlias = Aliases.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var next = aliases.OrderBy(a => a.Alias, StringComparer.Ordinal).ToList();
        for (var i = 0; i < next.Count; i++)
        {
            if (byAlias.TryGetValue(next[i].Alias, out var existing))
            {
                existing.Update(next[i]);
                var at = Aliases.IndexOf(existing);
                if (at != i)
                {
                    Aliases.Move(at, i);
                }
            }
            else
            {
                Aliases.Insert(i, new AliasItemViewModel(owner, next[i]));
            }
        }

        while (Aliases.Count > next.Count)
        {
            Aliases.RemoveAt(Aliases.Count - 1);
        }

        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task ToggleEnabledAsync() => owner.SetProviderEnabledAsync(this, !Dto.Enabled);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveProviderAsync(this);

    [RelayCommand]
    private async Task TestAsync()
    {
        TestInfo = new TestResult(TestState.Testing, null, null);
        var result = await owner.TestProviderAsync(Id);
        TestInfo = result is { IsOk: true, Value.Ok: true }
            ? new TestResult(TestState.Ok, result.Value.LatencyMs, null)
            : new TestResult(TestState.Failed, result.Value?.LatencyMs, result.IsOk ? result.Value?.ErrorKey : result.ErrorKey ?? result.Outcome.ToString());
    }

    [RelayCommand]
    private Task SignInAsync() => owner.SignInAsync(this);

    [RelayCommand]
    private Task SignOutAsync() => owner.SignOutAsync(this);

    [RelayCommand]
    private Task RemoveKeyAsync() => owner.RemoveKeyAsync(this);

    [RelayCommand]
    private Task ImportModelsAsync() => owner.OpenImportAsync(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>新建 / 编辑供应商的表单。密钥只写不读:保存后立刻清掉输入,界面之后只显示后四位。</summary>
public sealed partial class ProviderEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Protocols = ["anthropic", "openai-chat", "openai-responses"];

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsNew { get; set; }

    [ObservableProperty] public partial string Id { get; set; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Protocol { get; set; } = "anthropic";

    [ObservableProperty] public partial string BaseUrl { get; set; } = "";

    [ObservableProperty] public partial int Priority { get; set; } = 50;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    /// <summary>API Key 输入(只写不读)。留空 = 不改动已有的 key。</summary>
    [ObservableProperty] public partial string ApiKey { get; set; } = "";

    [ObservableProperty] public partial bool IsSaving { get; set; }

    [ObservableProperty] public partial string? ErrorKey { get; set; }

    public bool HasError => ErrorKey is not null;

    partial void OnErrorKeyChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>必填项齐了才能保存。id 新建时必填;名称、地址必填。</summary>
    public bool CanSave => !IsSaving && Name.Trim().Length > 0 && BaseUrl.Trim().Length > 0 && (!IsNew || Id.Trim().Length > 0);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnBaseUrlChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsNewChanged(bool value) => OnPropertyChanged(nameof(CanSave));
}

/// <summary>导入弹窗:从上游拉到的模型列表,勾选后「更新列表」整表替换这家的别名。</summary>
public sealed partial class ImportModelsViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial string ProviderName { get; set; } = "";

    [ObservableProperty] public partial string? ErrorKey { get; set; }

    public string ProviderId { get; set; } = "";

    public ObservableCollection<FetchedModelViewModel> Models { get; } = [];

    public bool HasError => ErrorKey is not null;

    partial void OnErrorKeyChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>正在进行的登录(<c>providers.oauth</c> 主题的最新进度):授权 URL / 配对码由控制端展示并打开浏览器。</summary>
public sealed partial class OAuthCardViewModel : ObservableObject
{
    public OAuthCardViewModel(OAuthProgressDto progress, string providerName, ModelsSettingsViewModel owner)
    {
        Progress = progress;
        ProviderName = providerName;
        OpenCommand = new AsyncRelayCommand(() => owner.OpenAuthorizeUrlAsync(progress), () => progress.AuthorizeUrl is not null || progress.VerificationUrl is not null);
        CancelCommand = new AsyncRelayCommand(() => owner.CancelOAuthAsync(progress.ProviderId));
        SubmitCommand = new AsyncRelayCommand(() => owner.SubmitCodeAsync(progress.ProviderId, PastedCode));
    }

    public OAuthProgressDto Progress { get; }

    public string ProviderName { get; }

    public string? AuthorizeUrl => Progress.AuthorizeUrl ?? Progress.VerificationUrl;

    public string? UserCode => Progress.UserCode;

    public bool HasUserCode => Progress.UserCode is not null;

    public bool NeedsPastedCode => Progress.NeedsPastedCode;

    [ObservableProperty] public partial string PastedCode { get; set; } = "";

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand SubmitCommand { get; }
}

/// <summary>
/// 设置 · 设备 · 模型与供应商(docs/design/07 §3):供应商列表 / 新建 / 编辑 / 启停、凭据(只写不读,显示后四位)、模型别名、
/// 模型目录状态与手动刷新、连通性测试、OAuth 进度。设备的事实一律从 <see cref="ClientStores.Providers"/> / <see cref="ClientStores.Catalog"/> 读,改动经
/// <see cref="ProvidersActions"/> / <see cref="CatalogActions"/> 发给设备,结果再经主题回到 Store(不在这里缓存供应商配置)。
/// </summary>
public sealed partial class ModelsSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly ProvidersActions _providers;
    private readonly CatalogActions _catalog;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IClientText> _text;
    private readonly Func<IUrlLauncher> _launcher;
    private readonly TimeProvider _clock;
    private string? _openedUrl;
    private readonly HashSet<string> _credentialsRequested = [];
    private string? _settingsFor;

    public ModelsSettingsViewModel(ClientStores stores, ProvidersActions providers, CatalogActions catalog, Func<IDialogService> dialogs,
        Func<IClientText> text, Func<IUrlLauncher> launcher, TimeProvider? clock = null)
    {
        _stores = stores;
        _providers = providers;
        _catalog = catalog;
        _dialogs = dialogs;
        _text = text;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;
        stores.Workspace.Changed += Refresh;
        stores.Providers.Changed += Refresh;
        stores.Catalog.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<DeviceChoice> DeviceChoices { get; } = [];

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];

    public ProviderEditorViewModel Editor { get; } = new();

    public ImportModelsViewModel Import { get; } = new();

    /// <summary>正在设置的设备(设备作用域的页面顶部的切换器)。</summary>
    [ObservableProperty] public partial string? DeviceId { get; set; }

    /// <summary>设备连着、供应商配置和目录都已读到。</summary>
    [ObservableProperty] public partial bool IsAvailable { get; private set; }

    /// <summary>角色允许改配置(写入、凭据、OAuth 默认 owner);其余角色只读。</summary>
    [ObservableProperty] public partial bool CanEdit { get; private set; }

    [ObservableProperty] public partial OAuthCardViewModel? OAuth { get; private set; }

    // ── 目录状态(<c>catalog</c> 主题) ──
    [ObservableProperty] public partial int CatalogModels { get; private set; }

    [ObservableProperty] public partial int CatalogProviders { get; private set; }

    [ObservableProperty] public partial string CatalogSource { get; private set; } = "seed";

    [ObservableProperty] public partial DateTimeOffset? CatalogFetchedAt { get; private set; }

    [ObservableProperty] public partial string? CatalogErrorKey { get; private set; }

    [ObservableProperty] public partial bool IsRefreshing { get; private set; }

    /// <summary>最近一次手动刷新的结局(<see cref="CatalogRefreshOutcomes"/>);没刷新过为 null。</summary>
    [ObservableProperty] public partial string? RefreshOutcome { get; private set; }

    [ObservableProperty] public partial bool AutoUpdate { get; set; } = true;

    [ObservableProperty] public partial int IntervalHours { get; set; } = 6;

    [ObservableProperty] public partial ProviderErrorView? Error { get; private set; }

    public bool HasProviders => Providers.Count > 0;

    public bool HasProblems => Problems.Count > 0;

    /// <summary>读 <c>providers.json</c> 时被跳过的坏条目(诊断)。</summary>
    public ObservableCollection<ProviderProblemRow> Problems { get; } = [];

    public bool HasDevices => DeviceChoices.Count > 0;

    /// <summary>设备切换器:只有一台本机、没有 SSH 时不画(只有一项的切换器是噪音;Core 的 <see cref="Ncw.Core.Domain.DeviceDimension"/>,03 §11.7)。</summary>
    [ObservableProperty] public partial bool ShowsDeviceSwitcher { get; private set; }

    partial void OnDeviceIdChanged(string? value)
    {
        _credentialsRequested.Clear();
        _settingsFor = null;
        SelectedProviderId = null;
        Refresh();
    }

    /// <summary>桌面设置页列表栏里选中的供应商(纯 UI 状态);null = 「模型目录」。供应商没了就回到模型目录。</summary>
    [ObservableProperty] public partial string? SelectedProviderId { get; set; }

    /// <summary>选中的供应商(详情区显示它)。</summary>
    public ProviderItemViewModel? SelectedProvider => SelectedProviderId is { } id ? Providers.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>详情区显示模型目录(没选供应商)。</summary>
    public bool ShowsCatalog => SelectedProvider is null;

    partial void OnSelectedProviderIdChanged(string? value) => SyncSelection();

    /// <summary>列表栏的「模型目录」项。</summary>
    [RelayCommand]
    private void SelectCatalog() => SelectedProviderId = null;

    private void SyncSelection()
    {
        if (SelectedProviderId is { } id && Providers.All(p => p.Id != id))
        {
            SelectedProviderId = null; // 再进来一次
            return;
        }

        foreach (var provider in Providers)
        {
            provider.IsSelected = provider.Id == SelectedProviderId;
        }

        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(ShowsCatalog));
    }

    private void Refresh()
    {
        var workspace = _stores.Workspace.State;

        var choices = workspace.Devices.Values
            .Where(d => d.Connection == DeviceConnection.Connected)
            .OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new DeviceChoice(d.DeviceId, d.Name, d.Kind, d.Connection)).ToList();
        if (!choices.SequenceEqual(DeviceChoices))
        {
            DeviceChoices.Clear();
            foreach (var c in choices)
            {
                DeviceChoices.Add(c);
            }

            OnPropertyChanged(nameof(HasDevices));
        }

        ShowsDeviceSwitcher = HasDevices && DeviceDimension.Shows(
            workspace.Devices.Values.Count(d => d.Connection != DeviceConnection.Revoked),
            workspace.Devices.Values.Any(d => d.Kind != DeviceKind.Local && d.Connection != DeviceConnection.Revoked),
            workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh));

        if (DeviceId is null || !workspace.Devices.ContainsKey(DeviceId))
        {
            DeviceId = choices.FirstOrDefault()?.DeviceId ?? workspace.Devices.Keys.FirstOrDefault();
            return; // OnDeviceIdChanged 会再进来
        }

        var device = workspace.Devices[DeviceId];
        var mirror = _stores.Providers.State.Of(DeviceId);
        var catalog = _stores.Catalog.State.Of(DeviceId);
        IsAvailable = device.Connection == DeviceConnection.Connected && mirror.Known;
        CanEdit = IsAvailable && device.Role == "owner";

        // 供应商:按 id 对齐,保住展开 / 测试结果
        var existing = Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var wanted = mirror.Providers.OrderBy(p => p.Priority).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            var aliases = mirror.Aliases.Where(a => a.ProviderId == wanted[i].Id).ToList();
            if (existing.TryGetValue(wanted[i].Id, out var vm))
            {
                vm.Update(wanted[i], aliases);
                var at = Providers.IndexOf(vm);
                if (at != i)
                {
                    Providers.Move(at, i);
                }
            }
            else
            {
                vm = new ProviderItemViewModel(this, wanted[i]);
                vm.Update(wanted[i], aliases);
                Providers.Insert(i, vm);
            }

            var accounts = mirror.AccountsOf(wanted[i].Id);
            var current = accounts.FirstOrDefault(a => a.Current) ?? accounts.FirstOrDefault();
            vm.AccountLabel = current?.Auth?.Email ?? current?.Label;
            vm.NeedsReauth = current?.NeedsReauth ?? false;
            vm.IsSigningIn = mirror.OAuth is { } progress && progress.ProviderId == vm.Id;
            if (wanted[i].HasCredential && wanted[i].OAuthIssuer is null && _credentialsRequested.Add(wanted[i].Id))
            {
                _ = LoadCredentialAsync(vm);
            }
        }

        while (Providers.Count > wanted.Count)
        {
            Providers.RemoveAt(Providers.Count - 1);
        }

        OnPropertyChanged(nameof(HasProviders));
        SyncSelection();
        Problems.Clear();
        foreach (var problem in mirror.Problems)
        {
            Problems.Add(new ProviderProblemRow(problem.ProviderId, problem.Key, problem.Detail));
        }

        OnPropertyChanged(nameof(HasProblems));

        // OAuth 进度(终态时 Store 里已经清掉)
        if (mirror.OAuth is { } oauth)
        {
            var name = Providers.FirstOrDefault(p => p.Id == oauth.ProviderId)?.Name ?? oauth.ProviderId;
            if (OAuth is null || !Equals(OAuth.Progress, oauth))
            {
                OAuth = new OAuthCardViewModel(oauth, name, this);
                // 授权页由控制端打开:同一个 URL 只自动打开一次(重复进度广播不重复弹浏览器)
                if (oauth.AuthorizeUrl is { } url && url != _openedUrl)
                {
                    _openedUrl = url;
                    _ = _launcher().OpenAsync(url);
                }
            }
        }
        else
        {
            OAuth = null;
            _openedUrl = null;
        }

        CatalogModels = catalog.Status.ModelCount;
        CatalogProviders = catalog.Status.ProviderCount;
        CatalogSource = catalog.Status.Source;
        CatalogFetchedAt = catalog.Status.FetchedAt is { } at2 ? DateTimeOffset.FromUnixTimeMilliseconds(at2) : null;
        CatalogErrorKey = catalog.Status.LastError?.Key;

        if (IsAvailable && _settingsFor != DeviceId)
        {
            _settingsFor = DeviceId;
            _ = LoadCatalogSettingsAsync(DeviceId);
        }
    }

    private async Task LoadCredentialAsync(ProviderItemViewModel vm)
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var result = await _providers.GetCredentialInfoAsync(device, vm.Id);
        if (result.IsOk && result.Value is { } info)
        {
            vm.Last4 = info.Last4;
            OnPropertyChanged(nameof(Providers));
        }
    }

    private async Task LoadCatalogSettingsAsync(string device)
    {
        var result = await _catalog.GetSettingsAsync(device);
        if (result.IsOk && result.Value is { } settings && DeviceId == device)
        {
            _suppressSettings = true;
            AutoUpdate = settings.AutoUpdate;
            IntervalHours = Math.Max(1, settings.IntervalMinutes / 60);
            _suppressSettings = false;
        }
    }

    private bool _suppressSettings;

    /// <summary>间隔输入框(数字输入框是 double,双向绑定这个;内部是整小时)。</summary>
    public double IntervalHoursValue
    {
        get => IntervalHours;
        set => IntervalHours = (int)Math.Clamp(Math.Round(value), 1, 168);
    }

    partial void OnAutoUpdateChanged(bool value) => _ = PushCatalogSettingsAsync();

    partial void OnIntervalHoursChanged(int value)
    {
        OnPropertyChanged(nameof(IntervalHoursValue));
        _ = PushCatalogSettingsAsync();
    }

    private async Task PushCatalogSettingsAsync()
    {
        if (!_suppressSettings && DeviceId is { } device && CanEdit)
        {
            await _catalog.SetSettingsAsync(device, new CatalogSettingsDto(AutoUpdate, IntervalHours * 60));
        }
    }

    // ───────────────────────── 目录 ─────────────────────────

    /// <summary>「立即更新」:马上去 models.dev 检查一次;结局在 <see cref="RefreshOutcome"/>,目录变了经主题回到 Store。</summary>
    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (DeviceId is not { } device || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var result = await _catalog.RefreshAsync(device);
            RefreshOutcome = result.IsOk ? result.Value?.Outcome : CatalogRefreshOutcomes.Failed;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // ───────────────────────── 供应商 ─────────────────────────

    [RelayCommand]
    private void NewProvider()
    {
        Editor.IsNew = true;
        Editor.Id = "";
        Editor.Name = "";
        Editor.Protocol = "anthropic";
        Editor.BaseUrl = "";
        Editor.Priority = 50;
        Editor.Enabled = true;
        Editor.ApiKey = "";
        Editor.ErrorKey = null;
        Editor.IsOpen = true;
    }

    internal void Edit(ProviderItemViewModel provider)
    {
        Editor.IsNew = false;
        Editor.Id = provider.Id;
        Editor.Name = provider.Name;
        Editor.Protocol = provider.Protocol;
        Editor.BaseUrl = provider.BaseUrl;
        Editor.Priority = provider.Dto.Priority;
        Editor.Enabled = provider.IsEnabled;
        Editor.ApiKey = "";
        Editor.ErrorKey = null;
        Editor.IsOpen = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editor.ApiKey = "";
        Editor.IsOpen = false;
    }

    [RelayCommand]
    private async Task SaveProviderAsync()
    {
        if (DeviceId is not { } device || !Editor.CanSave)
        {
            return;
        }

        Editor.IsSaving = true;
        Editor.ErrorKey = null;
        try
        {
            var id = Editor.Id.Trim();
            var upsert = await _providers.UpsertAsync(device, new ProvidersUpsertParams(id, Editor.Name.Trim(), Editor.Protocol, Editor.BaseUrl.Trim(), Editor.Priority, Editor.Enabled));
            if (!upsert.IsOk)
            {
                Editor.ErrorKey = upsert.ErrorKey ?? upsert.Outcome.ToString();
                return;
            }

            // 密钥只写不读:送出去就清掉输入,之后界面只显示后四位
            var key = Editor.ApiKey.Trim();
            Editor.ApiKey = "";
            if (key.Length > 0)
            {
                var set = await _providers.SetApiKeyAsync(device, id, key);
                if (!set.IsOk)
                {
                    Editor.ErrorKey = set.ErrorKey ?? set.Outcome.ToString();
                    return;
                }

                _credentialsRequested.Remove(id);
                Providers.FirstOrDefault(p => p.Id == id)?.Let(p => p.Last4 = set.Value?.Last4);
            }

            if (Editor.IsNew && Providers.Any(p => p.Id == id))
            {
                SelectedProviderId = id; // 新建的供应商:直接选中它(列表栏)
            }

            Editor.IsOpen = false;
        }
        finally
        {
            Editor.IsSaving = false;
        }
    }

    internal async Task SetProviderEnabledAsync(ProviderItemViewModel provider, bool enabled)
    {
        if (DeviceId is { } device)
        {
            var dto = provider.Dto;
            Report(await _providers.UpsertAsync(device, new ProvidersUpsertParams(dto.Id, dto.Name, dto.Protocol, dto.BaseUrl, dto.Priority, enabled, dto.ProtocolOptionsJson)));
        }
    }

    internal async Task RemoveProviderAsync(ProviderItemViewModel provider)
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var text = _text();
        if (await _dialogs().ConfirmAsync(text.DeleteProviderTitle, text.DeleteProviderMessage(provider.Name), text.DeleteLabel))
        {
            Report(await _providers.RemoveAsync(device, provider.Id));
        }
    }

    internal Task<ActionResult<ProviderTestResultDto>> TestProviderAsync(string providerId) =>
        DeviceId is { } device ? _providers.TestAsync(device, providerId) : Task.FromResult(ActionResult<ProviderTestResultDto>.NotConnected());

    internal async Task RemoveKeyAsync(ProviderItemViewModel provider)
    {
        if (DeviceId is { } device && (await _providers.RemoveCredentialAsync(device, provider.Id)).IsOk)
        {
            provider.Last4 = null;
        }
    }

    // ───────────────────────── 模型别名 ─────────────────────────

    internal async Task SetAliasEnabledAsync(ModelAliasDto alias, bool enabled)
    {
        if (DeviceId is { } device)
        {
            Report(await _providers.UpdateModelAsync(device, new ModelUpdateParams(alias.ProviderId, alias.Alias, Enabled: enabled)));
        }
    }

    internal async Task RenameAliasAsync(ModelAliasDto alias)
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var text = _text();
        var next = await _dialogs().PromptTextAsync(text.RenameAliasTitle, text.RenameAliasDescription, text.NameLabel, alias.Alias);
        if (!string.IsNullOrWhiteSpace(next) && next != alias.Alias)
        {
            Report(await _providers.RenameModelAsync(device, alias.ProviderId, alias.Alias, next.Trim()));
        }
    }

    internal async Task RemoveAliasAsync(ModelAliasDto alias)
    {
        if (DeviceId is { } device)
        {
            Report(await _providers.RemoveModelAsync(device, alias.ProviderId, alias.Alias));
        }
    }

    /// <summary>打开「从上游导入」:拉这家的模型列表,已配置的预先勾上。</summary>
    internal async Task OpenImportAsync(ProviderItemViewModel provider)
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        Import.ProviderId = provider.Id;
        Import.ProviderName = provider.Name;
        Import.Models.Clear();
        Import.ErrorKey = null;
        Import.IsLoading = true;
        Import.IsOpen = true;
        var result = await _providers.FetchModelsAsync(device, provider.Id);
        Import.IsLoading = false;
        if (!result.IsOk || result.Value is null)
        {
            Import.ErrorKey = result.ErrorKey ?? result.Outcome.ToString();
            return;
        }

        var configured = provider.Aliases.Select(a => a.Upstream).ToHashSet(StringComparer.Ordinal);
        foreach (var model in result.Value.Models.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            Import.Models.Add(new FetchedModelViewModel(model.Id, model.DisplayName, configured.Contains(model.Id)));
        }
    }

    [RelayCommand]
    private async Task ApplyImportAsync()
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        var selected = Import.Models.Where(m => m.IsSelected).Select(m => m.Id).ToList();
        var result = await _providers.SetAliasesAsync(device, Import.ProviderId, selected);
        if (result.IsOk)
        {
            Import.IsOpen = false;
        }
        else
        {
            Import.ErrorKey = result.ErrorKey ?? result.Outcome.ToString();
        }
    }

    [RelayCommand]
    private void CloseImport() => Import.IsOpen = false;

    // ───────────────────────── OAuth ─────────────────────────

    internal async Task SignInAsync(ProviderItemViewModel provider)
    {
        if (DeviceId is not { } device)
        {
            return;
        }

        provider.IsSigningIn = true;
        try
        {
            var result = await _providers.StartOAuthAsync(device, provider.Id);
            Report(result);
            if (result.IsOk)
            {
                _credentialsRequested.Remove(provider.Id);
            }
        }
        finally
        {
            provider.IsSigningIn = false;
        }
    }

    internal async Task SignOutAsync(ProviderItemViewModel provider)
    {
        if (DeviceId is { } device)
        {
            Report(await _providers.SignOutAsync(device, provider.Id));
        }
    }

    internal Task OpenAuthorizeUrlAsync(OAuthProgressDto progress) =>
        (progress.AuthorizeUrl ?? progress.VerificationUrl) is { } url ? _launcher().OpenAsync(url) : Task.CompletedTask;

    internal async Task CancelOAuthAsync(string providerId)
    {
        if (DeviceId is { } device)
        {
            await _providers.CancelOAuthAsync(device, providerId);
        }
    }

    internal async Task SubmitCodeAsync(string providerId, string code)
    {
        if (DeviceId is { } device && !string.IsNullOrWhiteSpace(code))
        {
            Report(await _providers.SubmitOAuthCodeAsync(device, providerId, code.Trim()));
        }
    }

    [RelayCommand]
    private void DismissError() => Error = null;

    private void Report<T>(ActionResult<T> result) => Error = result.IsOk ? null : new ProviderErrorView(result.Outcome, result.ErrorKey);
}

/// <summary>一次操作失败的原因(结构化:结局 + 设备给的稳定 key),界面自己生成文字。</summary>
public sealed record ProviderErrorView(ActionOutcome Outcome, string? Key);

file static class Ext
{
    public static void Let<T>(this T value, Action<T> action) => action(value);
}
