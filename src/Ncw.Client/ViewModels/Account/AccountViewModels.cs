using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Accounts;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>团队列表里的一行。</summary>
public sealed record TeamRow(string Id, string Name, string Role, int MemberCount, bool IsSelected);

/// <summary>「已登录的控制端」列表里的一行。</summary>
public sealed record ControllerRow(string Id, string Name, string Platform, DateTimeOffset? LastActiveAt, bool IsCurrent)
{
    /// <summary>手机 / 平板(图标用)。</summary>
    public bool IsMobile => Platform.Equals("ios", StringComparison.OrdinalIgnoreCase) || Platform.Equals("android", StringComparison.OrdinalIgnoreCase);

    public bool HasLastActive => LastActiveAt is not null;

    public bool CanRevoke => !IsCurrent;
}

/// <summary>「已登录的控制端」这份查询结果的状态(它是页面自己的查询结果,不进 Store)。</summary>
public enum ControllersState
{
    Idle,
    Loading,
    Ready,

    /// <summary>读取失败(<see cref="AccountViewModel.ControllersError"/> 说明原因,可重试)。</summary>
    Error,

    /// <summary>没登录 / 登录已过期:不能查。</summary>
    SignedOut,
}

/// <summary>本机 Agent 上委托令牌的状态(界面一句话:Agent 能不能代你访问账户)。</summary>
public enum AgentLinkState
{
    /// <summary>没有登录,或没有本机设备:不适用。</summary>
    None,

    Pending,
    Attached,
    Failed,
}

/// <summary>头像角标的含义(界面映射成颜色点):没登录不显示;已登录可用;登录过期 / 还要选团队需要处理。</summary>
public enum AccountPresence
{
    None,
    Active,
    NeedsAttention,
}

/// <summary>
/// 设置 · 本应用 · 账户(docs/design/07 §3):账户信息、团队、已登录的控制端、退出登录(<b>不涉及支付</b>);登录进行中 / 失败 / 过期的状态。
/// 事实都在 <see cref="ClientStores.Account"/>(本控制端的账户会话,真源),这里只派生;动作都转给 <see cref="AccountService"/>(同一个用户意图只有一个方法)。
/// 「已登录的控制端」是向账户后端查询的结果,属于这一页自己的状态。service 为 null(没接线的外壳、预览)时所有动作都是空操作。
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly AccountService? _service;
    private long _controllersVersion;

    public AccountViewModel(ClientStores stores, AccountService? service)
    {
        _stores = stores;
        _service = service;
        stores.Account.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<TeamRow> Teams { get; } = [];

    public ObservableCollection<ControllerRow> Controllers { get; } = [];

    [ObservableProperty] public partial AccountMode Mode { get; private set; }

    [ObservableProperty] public partial string? UserLabel { get; private set; }

    [ObservableProperty] public partial string? Email { get; private set; }

    /// <summary>头像上显示的一个字(没有头像图片时)。</summary>
    [ObservableProperty] public partial string Initial { get; private set; } = "";

    /// <summary>账户后端给的头像地址(只认 http / https,其它当作没有);界面加载失败时退回 <see cref="UserLabel"/> 的首字母。</summary>
    [ObservableProperty] public partial string? AvatarUrl { get; private set; }

    /// <summary>项目栏头像的角标。</summary>
    public AccountPresence Presence => !IsSignedIn ? AccountPresence.None : IsExpired || NeedsTeam ? AccountPresence.NeedsAttention : AccountPresence.Active;

    [ObservableProperty] public partial string? SelectedTeamName { get; private set; }

    [ObservableProperty] public partial bool NeedsTeam { get; private set; }

    [ObservableProperty] public partial AccountBannerKind Banner { get; private set; }

    [ObservableProperty] public partial string? LoginError { get; private set; }

    [ObservableProperty] public partial LoginProgress? Login { get; private set; }

    [ObservableProperty] public partial SessionHealth Health { get; private set; }

    /// <summary>此平台无法安全保存登录状态(不降级明文):本次会话只在内存里。</summary>
    [ObservableProperty] public partial bool SecureStorageUnavailable { get; private set; }

    [ObservableProperty] public partial AgentLinkState AgentLink { get; private set; }

    [ObservableProperty] public partial ControllersState ControllersStatus { get; private set; }

    [ObservableProperty] public partial string? ControllersError { get; private set; }

    /// <summary>二次确认:退出登录。</summary>
    [ObservableProperty] public partial bool ConfirmingSignOut { get; private set; }

    /// <summary>二次确认:撤销另一个控制端的登录。</summary>
    [ObservableProperty] public partial ControllerRow? ConfirmingRevoke { get; private set; }

    /// <summary>最近一次账户操作(选团队、撤销控制端)失败的原因 key。</summary>
    [ObservableProperty] public partial string? ActionError { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    /// <summary>有账户服务可用(没接线的外壳里为 false,按钮禁用)。</summary>
    public bool HasService => _service is not null;

    public bool IsSignedIn => Mode == AccountMode.Authenticated;

    public bool IsSignedOut => Mode is AccountMode.Offline or AccountMode.Undecided;

    public bool IsExpired => IsSignedIn && Health == SessionHealth.Expired;

    /// <summary>已登录且会话有效(没过期):显示团队、控制端这些需要账户的内容。</summary>
    public bool IsActive => IsSignedIn && Health != SessionHealth.Expired;

    public bool HasTeams => Teams.Count > 0;

    /// <summary>团队区只在会话有效时显示(过期之后选团队没有意义,也会失败)。</summary>
    public bool ShowsTeams => HasTeams && IsActive;

    public bool HasMultipleTeams => Teams.Count > 1;

    public bool HasControllers => Controllers.Count > 0;

    /// <summary>读到了,而且除了这个应用自己没有别的控制端(显示一句「没有别的应用」)。</summary>
    public bool ControllersEmptyOthers => ControllersStatus == ControllersState.Ready && Controllers.All(c => c.IsCurrent);

    public bool IsLoggingIn => Login is not null;

    public bool IsExchanging => Login is { Phase: LoginPhase.Exchanging };

    public bool IsBrowserLogin => Login is { Method: LoginMethod.Browser };

    public bool IsDeviceCodeLogin => Login is { Method: LoginMethod.DeviceCode };

    /// <summary>设备授权码的短码(登录进行中且是登录码方式)。</summary>
    public string? UserCode => Login?.UserCode;

    public string? VerificationUri => Login?.VerificationUri;

    public bool HasLoginError => LoginError is not null;

    public bool CanSignIn => HasService && !IsLoggingIn;

    public bool ControllersLoading => ControllersStatus == ControllersState.Loading;

    public bool ControllersReady => ControllersStatus == ControllersState.Ready;

    public bool ControllersFailed => ControllersStatus == ControllersState.Error;

    public bool ControllersSignedOut => ControllersStatus == ControllersState.SignedOut;

    public bool HasActionError => ActionError is not null;

    public bool HasConfirmingRevoke => ConfirmingRevoke is not null;

    public bool ShowsBanner => Banner != AccountBannerKind.None;

    public bool AgentLinkAttached => AgentLink == AgentLinkState.Attached;

    public bool AgentLinkFailed => AgentLink == AgentLinkState.Failed;

    public bool AgentLinkPending => AgentLink == AgentLinkState.Pending;

    public bool HasAgentLink => AgentLink != AgentLinkState.None;

    private void Refresh()
    {
        var state = _stores.Account.State;
        Mode = state.Mode;
        var session = state.Session;
        UserLabel = session?.User?.Label;
        Email = session?.User?.Email is { Length: > 0 } email && email != UserLabel ? email : null;
        Initial = (UserLabel is { Length: > 0 } label ? label[..1] : "").ToUpperInvariant();
        AvatarUrl = session?.User?.AvatarUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : null;
        SelectedTeamName = session?.SelectedTeam?.Name;
        NeedsTeam = session is { ContextRequired: true };
        Banner = AccountBanner.Of(state);
        LoginError = state.LoginError;
        Login = state.Login;
        Health = state.Health;
        SecureStorageUnavailable = state.SecureStorageUnavailable;
        AgentLink = state.Delegations.Count == 0 ? AgentLinkState.None : state.Delegations.Values.Any(d => d.Status == DelegationStatus.Failed) ? AgentLinkState.Failed
            : state.Delegations.Values.Any(d => d.Status == DelegationStatus.Pending) ? AgentLinkState.Pending : AgentLinkState.Attached;

        var teams = session is null ? [] : session.Teams.Select(t => new TeamRow(t.Id, t.Name, t.Role, t.MemberCount, t.Id == session.SelectedTeamId)).ToList();
        if (!teams.SequenceEqual(Teams))
        {
            Teams.Clear();
            foreach (var team in teams)
            {
                Teams.Add(team);
            }
        }

        // 会话没了 / 过期了:控制端列表不能当作还有效(规则 7:状态未知,不显示旧值)
        if (!IsActive && ControllersStatus != ControllersState.SignedOut)
        {
            Interlocked.Increment(ref _controllersVersion);
            Controllers.Clear();
            ControllersStatus = ControllersState.SignedOut;
            ControllersError = null;
            ConfirmingRevoke = null;
        }
        else if (IsActive && ControllersStatus == ControllersState.SignedOut)
        {
            ControllersStatus = ControllersState.Idle;
        }

        RaiseDerived();
    }

    private void RaiseDerived()
    {
        foreach (var name in new[]
        {
            nameof(IsSignedIn), nameof(IsSignedOut), nameof(IsExpired), nameof(IsActive), nameof(HasTeams), nameof(ShowsTeams), nameof(HasMultipleTeams), nameof(IsLoggingIn), nameof(IsExchanging),
            nameof(IsBrowserLogin), nameof(IsDeviceCodeLogin), nameof(UserCode), nameof(VerificationUri), nameof(HasLoginError), nameof(CanSignIn), nameof(ShowsBanner),
            nameof(AgentLinkAttached), nameof(AgentLinkFailed), nameof(AgentLinkPending), nameof(HasAgentLink), nameof(Presence),
        })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnControllersStatusChanged(ControllersState value)
    {
        OnPropertyChanged(nameof(ControllersLoading));
        OnPropertyChanged(nameof(ControllersReady));
        OnPropertyChanged(nameof(ControllersFailed));
        OnPropertyChanged(nameof(ControllersSignedOut));
        OnPropertyChanged(nameof(ControllersEmptyOthers));
    }

    partial void OnActionErrorChanged(string? value) => OnPropertyChanged(nameof(HasActionError));

    partial void OnConfirmingRevokeChanged(ControllerRow? value) => OnPropertyChanged(nameof(HasConfirmingRevoke));

    // ───────────────────────── 登录 ─────────────────────────

    /// <summary>在浏览器里登录(PKCE + <c>nextcowork://</c> 回跳)。</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_service is not null)
        {
            ActionError = null;
            await _service.BeginBrowserLoginAsync();
        }
    }

    /// <summary>改用登录码(设备授权流程,RFC 8628)。</summary>
    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        if (_service is not null)
        {
            ActionError = null;
            await _service.BeginDeviceCodeLoginAsync();
        }
    }

    [RelayCommand]
    private void CancelLogin() => _service?.CancelLogin();

    [RelayCommand]
    private void DismissLoginError() => _service?.DismissLoginError();

    // ───────────────────────── 退出 / 团队 / 控制端 ─────────────────────────

    [RelayCommand]
    private void AskSignOut() => ConfirmingSignOut = IsSignedIn;

    [RelayCommand]
    private void CancelSignOut() => ConfirmingSignOut = false;

    [RelayCommand]
    private async Task ConfirmSignOutAsync()
    {
        ConfirmingSignOut = false;
        if (_service is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _service.SignOutAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectTeamAsync(string teamId)
    {
        if (_service is null || Teams.FirstOrDefault(t => t.Id == teamId) is not { IsSelected: false })
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _service.SelectTeamAsync(teamId);
            ActionError = result.Ok ? null : result.ErrorKey;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>登录已过期时,用户选择继续离线使用(清掉过期的会话资料)。</summary>
    [RelayCommand]
    private async Task UseOfflineAsync()
    {
        if (_service is not null)
        {
            await _service.UseOfflineAsync();
        }
    }

    /// <summary>读「已登录的控制端」(页面被打开时、手动刷新时)。</summary>
    [RelayCommand]
    public async Task LoadControllersAsync()
    {
        if (_service is null || !IsActive)
        {
            return;
        }

        var version = Interlocked.Increment(ref _controllersVersion);
        ControllersStatus = ControllersState.Loading;
        ControllersError = null;
        var result = await _service.ListControllersAsync();
        if (version != Interlocked.Read(ref _controllersVersion))
        {
            return; // 期间退出 / 过期 / 又发起了一次:丢弃迟到的结果
        }

        if (!result.Ok)
        {
            ControllersError = result.ErrorKey;
            ControllersStatus = ControllersState.Error;
            return;
        }

        Controllers.Clear();
        foreach (var session in result.Value!.OrderByDescending(c => c.IsCurrent).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Controllers.Add(new ControllerRow(session.Id, session.Name, session.Platform, session.LastActiveAt, session.IsCurrent));
        }

        OnPropertyChanged(nameof(HasControllers));
        ControllersStatus = ControllersState.Ready;
    }

    [RelayCommand]
    private void AskRevoke(ControllerRow row) => ConfirmingRevoke = row.IsCurrent ? null : row; // 当前这个控制端用「退出登录」

    [RelayCommand]
    private void CancelRevoke() => ConfirmingRevoke = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        if (_service is null || ConfirmingRevoke is not { } row)
        {
            return;
        }

        ConfirmingRevoke = null;
        IsBusy = true;
        try
        {
            var result = await _service.RevokeControllerAsync(row.Id);
            ActionError = result.Ok ? null : result.ErrorKey;
            if (result.Ok)
            {
                await LoadControllersAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DismissActionError() => ActionError = null;

    /// <summary>刷新账户信息(重新取令牌并重读控制端)。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_service is null)
        {
            return;
        }

        await _service.RefreshAsync(force: true);
        await LoadControllersAsync();
    }

    /// <summary>设置页打开「账户」标签时调用。</summary>
    public Task ActivateAsync() => IsActive ? LoadControllersAsync() : Task.CompletedTask;

    internal ImmutableArray<TeamRow> TeamSnapshot => [.. Teams];
}
