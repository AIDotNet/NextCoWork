using System.Collections.Immutable;
using Ncw.Client.Accounts;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Accounts;

// 账户测试的脚本化后端:假的 IAccountApi、手动推进的时钟、假的委托出口、记录调用的浏览器。
// 同时被 Ncw.UI.Tests 以链接方式引用(截图用)。

/// <summary>手动推进的时钟:<see cref="Advance"/> 之后到期的定时器才触发;<c>Task.Delay(…, clock)</c> 也走它。</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>推进时间并按到期顺序触发定时器(周期定时器按周期重复触发)。</summary>
    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        for (;;)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value > _now ? next.Due.Value : _now;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class RecordingLauncher : IUrlLauncher
{
    public List<string> Opened { get; } = [];

    public bool Fail { get; set; }

    public Task OpenAsync(string url)
    {
        if (Fail)
        {
            throw new InvalidOperationException("no browser");
        }

        Opened.Add(url);
        return Task.CompletedTask;
    }

    public Uri LastUri => new(Opened[^1]);

    public string Query(string name) =>
        LastUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p[1])).Single();
}

public sealed class FakeDelegationTarget : IDelegationTarget
{
    public List<string> Connected { get; } = [];

    public List<(string Device, string Token, DateTimeOffset ExpiresAt, string AccountId, string? TeamId)> Attached { get; } = [];

    public List<string> Detached { get; } = [];

    public bool FailAttach { get; set; }

    public IReadOnlyList<string> ConnectedTargets() => [.. Connected];

    public Task AttachAsync(string deviceId, string token, DateTimeOffset expiresAt, string accountId, string? teamId, CancellationToken ct)
    {
        if (FailAttach)
        {
            throw new InvalidOperationException("device refused");
        }

        Attached.Add((deviceId, token, expiresAt, accountId, teamId));
        return Task.CompletedTask;
    }

    public Task DetachAsync(string deviceId, CancellationToken ct)
    {
        Detached.Add(deviceId);
        return Task.CompletedTask;
    }
}

/// <summary>脚本化的账户后端:每个方法有一个可替换的委托,默认返回一份正常的结果;记录调用。</summary>
public sealed class FakeAccountApi(TimeProvider clock) : IAccountApi
{
    public static readonly AccountUser User = new("u1", "alice", "alice@example.com", "Alice", null);

    public static TeamOption Team(string id, string name = "", bool selected = false) => new(id, name.Length > 0 ? name : id, "team", "owner", 3, selected);

    private int _counter;

    public List<string> Calls { get; } = [];

    public List<(string Code, string Verifier)> Exchanges { get; } = [];

    /// <summary>每次换码用的 redirect_uri(必须和授权请求里的相同)。</summary>
    public List<string> ExchangeRedirects { get; } = [];

    public List<string> Refreshes { get; } = [];

    public List<string> Revoked { get; } = [];

    public List<string> SelectedTeams { get; } = [];

    public TeamContext Teams { get; set; } = new([Team("t1", "Acme")], null);

    public Func<string, string, TokenSet>? OnExchange { get; set; }

    public Func<string, Task<TokenSet>>? OnRefresh { get; set; }

    public Func<AccountUser>? OnUser { get; set; }

    public Queue<DevicePollResult> Polls { get; } = new();

    public Exception? DeviceStartFailure { get; set; }

    public Exception? ExchangeFailure { get; set; }

    public bool DelegationSupported { get; set; }

    public Uri BuildAuthorizeUri(string state, string codeChallenge, string redirectUri) =>
        new($"https://auth.example.test/client/authorize?client_id=nextcowork-desktop&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256&redirect_uri={Uri.EscapeDataString(redirectUri)}");

    public TokenSet NewTokens(string? access = null, string? refresh = null, TimeSpan? ttl = null)
    {
        var n = Interlocked.Increment(ref _counter);
        return new TokenSet(access ?? $"AT-{n}", refresh ?? $"RT-{n}", clock.GetUtcNow() + (ttl ?? TimeSpan.FromMinutes(15)), User);
    }

    public Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        Calls.Add("exchange");
        ExchangeRedirects.Add(redirectUri);
        Exchanges.Add((code, codeVerifier));
        if (ExchangeFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(OnExchange?.Invoke(code, codeVerifier) ?? NewTokens());
    }

    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        Calls.Add("refresh");
        Refreshes.Add(refreshToken);
        return OnRefresh?.Invoke(refreshToken) ?? Task.FromResult(NewTokens());
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        Calls.Add("revoke");
        Revoked.Add(refreshToken);
        return Task.CompletedTask;
    }

    public Task<AccountUser> GetUserAsync(string accessToken, CancellationToken ct) => Task.FromResult(OnUser?.Invoke() ?? User);

    public Exception? TeamsFailure { get; set; }

    public Task<TeamContext> GetTeamsAsync(string accessToken, CancellationToken ct) =>
        TeamsFailure is { } failure ? throw failure : Task.FromResult(Teams);

    public Task SelectTeamAsync(string accessToken, string teamId, CancellationToken ct)
    {
        SelectedTeams.Add(teamId);
        Teams = Teams with { SelectedTeamId = teamId };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountUsageEntry>> GetUsageAsync(string accessToken, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AccountUsageEntry>>([]);

    public Task<DeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken ct)
    {
        Calls.Add("device");
        if (DeviceStartFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new DeviceAuthorization("DEVICE-CODE-SECRET", "ABCD-EFGH", "https://auth.example.test/device", "https://auth.example.test/device?user_code=ABCD-EFGH", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)));
    }

    public Task<DevicePollResult> PollDeviceAuthorizationAsync(string deviceCode, CancellationToken ct)
    {
        Calls.Add("poll");
        return Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : new DevicePollResult(DevicePollKind.Pending));
    }

    public Task<DelegatedGrant?> ExchangeDelegatedAsync(string accessToken, CancellationToken ct)
    {
        Calls.Add("delegate");
        return Task.FromResult<DelegatedGrant?>(DelegationSupported ? new DelegatedGrant("DT-" + accessToken, clock.GetUtcNow() + TimeSpan.FromMinutes(30)) : null);
    }

    public List<ControllerSession> Controllers { get; } = [];

    /// <summary>设了就让 <see cref="ListControllersAsync"/> 等它(模拟慢请求:读取期间退出登录)。</summary>
    public Task? ControllersGate { get; set; }

    public async Task<IReadOnlyList<ControllerSession>> ListControllersAsync(string accessToken, CancellationToken ct)
    {
        if (ControllersGate is { } gate)
        {
            await gate;
        }

        return [.. Controllers];
    }

    public Task RevokeControllerAsync(string accessToken, string controllerId, CancellationToken ct)
    {
        Controllers.RemoveAll(c => c.Id == controllerId);
        return Task.CompletedTask;
    }
}

/// <summary>假的本机回环监听:不开端口,记录启动 / 关闭;<see cref="RedirectUri"/> 是固定的回环地址。</summary>
public sealed class FakeLoginListener : ILoginCallbackListener
{
    public const string RedirectUri = "http://127.0.0.1:49152/callback";

    public List<FakeSession> Sessions { get; } = [];

    public Exception? StartFailure { get; set; }

    public ILoginCallbackSession Start(string expectedState, Func<AuthCallbackLink, Task> onCallback)
    {
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        var session = new FakeSession(expectedState, onCallback);
        Sessions.Add(session);
        return session;
    }

    public sealed class FakeSession(string expectedState, Func<AuthCallbackLink, Task> onCallback) : ILoginCallbackSession
    {
        public string ExpectedState { get; } = expectedState;

        public bool Disposed { get; private set; }

        string ILoginCallbackSession.RedirectUri => RedirectUri;

        /// <summary>模拟浏览器跳回监听(监听自己只放行 state 对得上的)。</summary>
        public Task DeliverAsync(AuthCallbackLink link) => onCallback(link);

        public void Dispose() => Disposed = true;
    }
}

/// <summary>一整套账户夹具:Store + 服务 + 假后端 + 手动时钟,可直接驱动登录流程。</summary>
public sealed class AccountRig
{
    public static readonly DateTimeOffset Start = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    public AccountRig(AccountOptions? options = null, ClientStores? stores = null)
    {
        Clock = new ManualTimeProvider(Start);
        Api = new FakeAccountApi(Clock);
        Stores = stores ?? new ClientStores();
        Service = new AccountService(Stores, Api, Vault, Meta, () => Launcher, Delegation, clock: Clock, options: options, listener: Listener);
    }

    public ManualTimeProvider Clock { get; }

    public FakeAccountApi Api { get; }

    public ClientStores Stores { get; }

    public MemoryRefreshTokenVault Vault { get; } = new();

    public MemoryAccountMetaStore Meta { get; } = new();

    public RecordingLauncher Launcher { get; } = new();

    public FakeDelegationTarget Delegation { get; } = new();

    public FakeLoginListener Listener { get; } = new();

    public AccountService Service { get; }

    public AccountState State => Stores.Account.State;

    /// <summary>另起一个读同一份钥匙串 / 记录的服务(模拟应用重启)。</summary>
    public (AccountService Service, ClientStores Stores) Restart()
    {
        var stores = new ClientStores();
        return (new AccountService(stores, Api, Vault, Meta, () => Launcher, Delegation, clock: Clock, listener: Listener), stores);
    }

    /// <summary>走完一次浏览器登录(回调带正确的 state),返回时已经是登录状态。</summary>
    public async Task SignInAsync(string code = "CODE-1")
    {
        await Service.InitializeAsync();
        Assert.True(await Service.BeginBrowserLoginAsync());
        var link = new AuthCallbackLink(code, Launcher.Query("state"), null, null);
        Assert.Equal(LinkOutcome.Accepted, await Service.HandleLinkAsync(link));
    }

    /// <summary>把本机设备标成已连接(让委托令牌有地方交付),并通知 Store。</summary>
    public void ConnectLocalDevice(string deviceId = "local")
    {
        if (!Delegation.Connected.Contains(deviceId))
        {
            Delegation.Connected.Add(deviceId);
        }

        TouchWorkspace(deviceId, DeviceConnection.Connected);
    }

    public void DisconnectLocalDevice(string deviceId = "local")
    {
        Delegation.Connected.Remove(deviceId);
        TouchWorkspace(deviceId, DeviceConnection.Offline);
    }

    private void TouchWorkspace(string deviceId, DeviceConnection connection) =>
        Stores.Workspace.Update(s => s with
        {
            Devices = s.Devices.SetItem(deviceId, new DeviceInfo(deviceId, "本机", DeviceKind.Local, connection, null, Role: "owner")),
        });
}
