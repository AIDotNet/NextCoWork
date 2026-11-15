using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Client.Accounts;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Accounts;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>
/// 账户登录到本机 Agent 的端到端:真实 Agent(进程内)+ 真实 <c>DeviceLink</c> + 真实 <see cref="AccountService"/>(假的账户后端、手动时钟)+ 假的市场 HTTP。
/// 登录完成 → 委托令牌经 <c>account.attachDelegatedToken</c> 交给本机 Agent → 市场安装从 <c>authRequired</c> 恢复;令牌刷新后 Agent 用的是新令牌;退出登录撤销。
/// </summary>
public sealed class AccountLinkTests : IAsyncLifetime
{
    private const string AgentToken = "account-link-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwa-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _cts = new();
    private readonly MarketHandler _market = new();
    private readonly LockedDispatcher _ui = new();
    private readonly ClientStores _stores = new();
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow); // Agent 用真实时钟核对令牌过期,所以账户这边也从「现在」起算
    private readonly FakeAccountApi _api;
    private readonly RecordingLauncher _launcher = new();
    private readonly MemoryRefreshTokenVault _vault = new();
    private AgentHost _host = null!;
    private Task _run = Task.CompletedTask;
    private DeviceLink _link = null!;
    private DeviceRegistry _devices = null!;
    private AccountService _account = null!;
    private PluginsActions _plugins = null!;

    public AccountLinkTests()
    {
        _api = new FakeAccountApi(_clock);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var endpoint = LocalTransport.DefaultEndpoint(_root);
        _host = new AgentHost(
            new AgentOptions(AgentMode.Local, _root, endpoint, AgentToken, null),
            services: new AgentServices
            {
                Models = new FakeModels(new ScriptedLlm()),
                Delayer = new NoDelay(),
                Loop = new AgentLoopOptions { ResumeDelays = [] },
                MarketHttpClient = new HttpClient(_market),
            });
        _run = _host.RunAsync(_cts.Token);
        _link = new DeviceLink(_stores, _ui, "local", DeviceKind.Local, "本机", async ct => await LocalTransport.ConnectAsync(endpoint, ct), new ClientHello("account-link", "3.0.0", LocalToken: AgentToken));
        _devices = new DeviceRegistry { Local = _link };
        _plugins = new PluginsActions(_devices, _stores.Plugins);
        _account = new AccountService(_stores, _api, _vault, new MemoryAccountMetaStore(), () => _launcher, new DeviceDelegationTarget(_devices), _ui, _clock, listener: new Ncw.Client.Tests.Accounts.FakeLoginListener());
        await _account.InitializeAsync();
        _link.Start();
        await WaitUntil(() => Read(s => s.Workspace.State.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == DataStates.Ready)), "the local device is ready");
    }

    public async ValueTask DisposeAsync()
    {
        await _account.DisposeAsync();
        await _link.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private T Read<T>(Func<ClientStores, T> read)
    {
        lock (_ui.Gate)
        {
            return read(_stores);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private async Task SignInAsync()
    {
        Assert.True(await _account.BeginBrowserLoginAsync());
        Assert.Equal(LinkOutcome.Accepted, await _account.HandleLinkAsync(new AuthCallbackLink("CODE", _launcher.Query("state"), null, null)));
    }

    private string DeviceId => Read(s => s.Workspace.State.Devices.Keys.Single());

    private Task<ActionResult<PluginsInstallResult>> InstallAsync() => _plugins.InstallMarketAsync(DeviceId, "hello", null);

    [Fact]
    public async Task Signing_in_delegates_the_token_to_the_local_agent_and_the_market_recovers_from_auth_required()
    {
        // 登录之前:浏览照常,安装是 authRequired
        var list = await _plugins.MarketListAsync(DeviceId, null, null);
        Assert.True(list.IsOk, list.Detail);
        var before = await InstallAsync();
        Assert.Equal((ActionOutcome.Forbidden, "plugins.authRequired"), (before.Outcome, before.ErrorKey));

        await SignInAsync();
        await WaitUntil(() => Read(s => s.Account.State.Delegations.TryGetValue(DeviceId, out var d) && d.Status == DelegationStatus.Attached), "the delegation is attached");

        var installed = await InstallAsync();
        Assert.True(installed.IsOk, installed.ErrorKey ?? installed.Detail);
        Assert.Equal("acme.hello", installed.Value!.Installed.Id);
        Assert.Equal(["Bearer AT-1"], _market.GrantAuthorizations); // Agent 用的是控制端交付的令牌
        Assert.True(Read(s => s.Plugins.State.Of(DeviceId).Find("acme.hello")) is not null);
    }

    [Fact]
    public async Task A_refreshed_token_replaces_the_agents_token_and_signing_out_withdraws_it()
    {
        await SignInAsync();
        await WaitUntil(() => Read(s => s.Account.State.Delegations.ContainsKey(DeviceId)), "attached");
        Assert.True((await InstallAsync()).IsOk);

        // 访问令牌快到期:后台刷新,新令牌立刻交给 Agent
        var firstStatus = await _link.CallAsync(AccountMethods.GetStatus, Unit.Value);
        _clock.Advance(TimeSpan.FromMinutes(14));
        await _account.RefreshAsync();
        var refreshed = await _link.CallAsync(AccountMethods.GetStatus, Unit.Value);
        Assert.True(refreshed.Attached);
        Assert.True(refreshed.ExpiresAt > firstStatus.ExpiresAt, "the agent holds the refreshed token");
        await _plugins.UninstallAsync(DeviceId, "acme.hello");
        var second = await InstallAsync();
        Assert.True(second.IsOk, second.ErrorKey ?? second.Detail);
        Assert.Equal(["Bearer AT-1", "Bearer AT-2"], _market.GrantAuthorizations);

        // 退出登录:Agent 上的令牌撤销,市场又是 authRequired
        await _plugins.UninstallAsync(DeviceId, "acme.hello");
        await _account.SignOutAsync();
        var status = await _link.CallAsync(AccountMethods.GetStatus, Unit.Value);
        Assert.False(status.Attached);
        var after = await InstallAsync();
        Assert.Equal("plugins.authRequired", after.ErrorKey);
        Assert.Equal(2, _market.GrantAuthorizations.Count); // 退出之后没有再发授权请求
    }

    [Fact]
    public async Task The_status_the_agent_reports_matches_the_account_and_team_without_exposing_the_token()
    {
        _api.Teams = new TeamContext([FakeAccountApi.Team("t1", "Acme"), FakeAccountApi.Team("t2", "Beta")], null);
        await SignInAsync();
        Assert.False((await _link.CallAsync(AccountMethods.GetStatus, Unit.Value)).Attached); // 还没选团队:不交付

        Assert.True((await _account.SelectTeamAsync("t2")).Ok);
        await WaitUntil(() => Read(s => s.Account.State.Delegations.ContainsKey(DeviceId)), "attached");
        var status = await _link.CallAsync(AccountMethods.GetStatus, Unit.Value);
        Assert.Equal((true, "u1", "t2"), (status.Attached, status.AccountId, status.TeamId));
        Assert.DoesNotContain("AT-1", status.ToString());
    }

    // ───────────────────────── 假市场 ─────────────────────────

    private sealed class MarketHandler : HttpMessageHandler
    {
        private static readonly byte[] Zip = BuildZip();
        private readonly object _gate = new();
        private readonly List<string> _grants = [];

        public List<string> GrantAuthorizations
        {
            get
            {
                lock (_gate)
                {
                    return [.. _grants];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var auth = request.Headers.Authorization?.ToString() ?? "(none)";
            if (path.EndsWith("/install", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _grants.Add(auth);
                }

                var sha = Convert.ToHexStringLower(SHA256.HashData(Zip));
                return Task.FromResult(Json("{\"data\":{\"version\":\"1.0.0\",\"sha256\":\"" + sha + "\"}}"));
            }

            return Task.FromResult(path switch
            {
                "/api/plugins/hello" => Json("""{"data":{"plugin":{"slug":"hello","publisher":"acme","name":"hello","displayName":"Hello","version":"1.0.0"},"versions":[{"version":"1.0.0"}]}}"""),
                "/api/plugins/hello/versions/1.0.0/download" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) },
                "/api/plugins" => Json("""{"data":{"items":[{"slug":"hello","publisher":"acme","name":"hello","displayName":"Hello","version":"1.0.0"}],"hasNext":false}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

        private static byte[] BuildZip()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string path, string content)
                {
                    using var w = new StreamWriter(zip.CreateEntry($"acme.hello/{path}").Open(), new UTF8Encoding(false));
                    w.Write(content);
                }

                Add("package.json", """{ "name": "hello", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./main.js", "permissions": ["storage"] }""");
                Add("main.js", "export function activate() {}");
            }

            return memory.ToArray();
        }
    }
}
