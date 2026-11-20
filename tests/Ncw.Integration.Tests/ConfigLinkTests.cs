using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Catalog;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>
/// 控制端 Store / Actions 对真实 Agent 的目录与供应商链路集成测试:Actions 发起、设备推送经 <see cref="DeviceLink"/> 折进
/// <see cref="ClientStores.Catalog"/> / <see cref="ClientStores.Providers"/>(key 带 deviceId),OAuth 进度进 Store、授权页由控制端(这里的测试)打开,
/// 断线标为未知(规则 7)、重连后追平。上游是脚本化的假 HTTP。
/// </summary>
public sealed class ConfigLinkTests : IAsyncLifetime
{
    private const string Token = "config-token";
    private const string Secret = "sk-CONFIG-SECRET-42";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwk-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _agentCts = new();
    private readonly FakeUpstream _up = new();
    private readonly FakeLogin _login = new();
    private readonly FakeDiscoveryHost _discovery = new();
    private Task _agent = Task.CompletedTask;
    private AgentHost _host = null!;
    private Controller _c = null!;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    private sealed class Controller
    {
        public ClientStores Stores { get; } = new();

        public LockedDispatcher Ui { get; } = new();

        public DeviceLink Link { get; set; } = null!;

        /// <summary>设了就让下一次(重)连先等它:测试要在「断开着」的时候做事时用。</summary>
        public TaskCompletionSource? HoldConnect { get; set; }

        public DeviceRegistry Devices { get; set; } = null!;

        public CatalogActions Catalog { get; set; } = null!;

        public ProvidersActions Providers { get; set; } = null!;

        public CuttableStream? Current { get; set; }

        public DeviceCatalog CatalogState { get { lock (Ui.Gate) { return Stores.Catalog.State.Of(Link.DeviceId); } } }

        public DeviceProviders ProvidersState { get { lock (Ui.Gate) { return Stores.Providers.State.Of(Link.DeviceId); } } }
    }

    private sealed class FakeDiscoveryHost : Ncw.Agent.Catalog.Discovery.IDiscoveryHost
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public string Home => "/discovery-home";

        public string? ReadFile(string path) => Files.GetValueOrDefault(path.Replace('\\', '/'));

        public string? Env(string name) => null;

        public bool DirectoryExists(string path) => false;
    }

    private sealed class LockedDispatcher : IUiDispatcher
    {
        public readonly object Gate = new();

        public void Post(Action action)
        {
            lock (Gate)
            {
                action();
            }
        }
    }

    private sealed class CuttableStream(Stream inner) : Stream
    {
        public void Cut() => inner.Dispose();

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NeverDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class FakeUpstream : IUpstreamHttp
    {
        private readonly object _gate = new();

        public string? ModelsDev { get; set; }

        public int Requests { get; private set; }

        /// <summary>最近一次对话请求(调试)带的鉴权头:上游收到的应当是真 key。</summary>
        public string? LastChatAuthorization { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate)
            {
                Requests++;
            }

            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                LastChatAuthorization = request.Headers.TryGetValues("authorization", out var auth) ? string.Join(",", auth) : null;
                const string Sse = "data: {\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\n"
                    + "data: {\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream") });
            }

            if (url.Contains("models.dev", StringComparison.Ordinal) && ModelsDev is { } body)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }

            if (url.EndsWith("/models", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"m1\"},{\"id\":\"m2\"}]}", Encoding.UTF8, "application/json") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeLogin : IOAuthLogin
    {
        public string Issuer => "chatgpt";

        public OAuthGrantKind Grant => OAuthGrantKind.AuthorizationCode;

        public async Task<ProviderCredential> LoginAsync(IOAuthLoginSink sink, CancellationToken ct)
        {
            sink.OpenAuthorizeUrl("https://auth.example.com/authorize?state=abc");
            var code = await ((IOAuthPasteSink)sink).AwaitPastedCodeAsync(ct);
            return new OAuthCredential("chatgpt", "AT-" + code, "RT-" + code, null, "acc-1", "me@example.com", "plus");
        }
    }

    private static string ModelsDevJson(int count)
    {
        var sb = new StringBuilder("{\"anthropic\":{\"id\":\"anthropic\",\"name\":\"Anthropic\",\"models\":{");
        for (var i = 0; i < count; i++)
        {
            sb.Append(i > 0 ? "," : "").Append($"\"c{i}\":{{\"id\":\"c{i}\",\"name\":\"C{i}\",\"tool_call\":true,\"modalities\":{{\"input\":[\"text\"],\"output\":[\"text\"]}},\"limit\":{{\"context\":100000,\"output\":8000}}}}");
        }

        return sb.Append("}}}").ToString();
    }

    public ValueTask InitializeAsync()
    {
        _host = new AgentHost(
            new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null),
            services: new AgentServices
            {
                Http = _up,
                CatalogDelayer = new NeverDelay(),
                CatalogStore = new MemoryCatalogStore(),
                OAuthLogins = issuer => issuer == "chatgpt" ? _login : null,
                DiscoveryHost = _discovery, // 不读开发机真实的家目录
            });
        _agent = _host.RunAsync(_agentCts.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_c is not null)
        {
            await _c.Link.DisposeAsync();
        }

        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<Controller> StartControllerAsync()
    {
        var c = new Controller();
        c.Link = new DeviceLink(c.Stores, c.Ui, "local-config", DeviceKind.Local, "本机",
            async ct =>
            {
                if (c.HoldConnect is { } hold)
                {
                    await hold.Task.WaitAsync(ct);
                }

                var stream = new CuttableStream(await LocalTransport.ConnectAsync(Endpoint, ct));
                c.Current = stream;
                return stream;
            },
            new ClientHello("ctrl-config", "3.0.0", LocalToken: Token));
        c.Devices = new DeviceRegistry { Local = c.Link };
        c.Catalog = new CatalogActions(c.Devices);
        c.Providers = new ProvidersActions(c.Devices);
        c.Link.Start();
        await WaitUntil(() => c.Link.IsConnected && c.CatalogState.Known && c.ProvidersState.Known, "connected and config mirrored");
        return c;
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Catalog_and_providers_changes_made_through_actions_arrive_in_the_stores_keyed_by_device()
    {
        _up.ModelsDev = ModelsDevJson(20);
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.Equal(_host.DeviceId, deviceId);
        Assert.Equal("seed", _c.CatalogState.Status.Source);

        // 目录:立即更新 → 状态进 Store;目录的供应商列表按版本重拉
        var refreshed = await _c.Catalog.RefreshAsync(deviceId);
        Assert.Equal((ActionOutcome.Ok, CatalogRefreshOutcomes.Updated, 20), (refreshed.Outcome, refreshed.Value!.Outcome, refreshed.Value.ModelCount));
        await WaitUntil(() => _c.CatalogState is { Status.ModelCount: 20 } s && s.ProvidersVersion == s.Status.Version && s.Providers.Any(p => p.Id == "anthropic"), "catalog mirrored with its providers");
        var queried = await _c.Catalog.QueryAsync(deviceId, new CatalogQueryParams(ProviderId: "anthropic", Limit: 5));
        Assert.Equal((20, 5), (queried.Value!.Total, queried.Value.Models.Length));

        // 供应商:upsert + 凭据 + 别名 → Store 里有供应商、别名、「有 key」(没有 key 本身)、选择器候选
        Assert.True((await _c.Providers.UpsertAsync(deviceId, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"))).IsOk);
        var set = await _c.Providers.SetApiKeyAsync(deviceId, "gw", Secret);
        Assert.Equal((true, Secret[^4..]), (set.Value!.HasKey, set.Value.Last4));
        var fetched = await _c.Providers.FetchModelsAsync(deviceId, "gw");
        Assert.Equal(["m1", "m2"], fetched.Value!.Models.Select(m => m.Id));
        Assert.True((await _c.Providers.SetAliasesAsync(deviceId, "gw", ["c0", "m1"])).IsOk);
        await WaitUntil(() => _c.ProvidersState is { Providers.Length: 1, Aliases.Length: 2 } p && p.Providers[0].HasCredential, "providers mirrored");
        await WaitUntil(() => _c.ProvidersState.Options.Length == 2 && _c.ProvidersState.OptionsFor == (_c.ProvidersState.Version, _c.CatalogState.Status.Version), "model options mirrored for the current versions");
        var c0 = _c.ProvidersState.Options.Single(o => o.Alias == "c0");
        Assert.Equal((100_000, 8_000), (c0.ContextWindow, c0.MaxOutputTokens)); // 窗口跟随目录

        // 另一台设备的 key 空间互不干扰(key 带 deviceId)
        Assert.False(_c.Stores.Providers.State.Of("some-other-device").Known);

        // 失败是结构化的结局,不是异常
        var bad = await _c.Providers.UpsertAsync(deviceId, new ProvidersUpsertParams("x", "X", "openai-chat", "file:///etc/passwd"));
        Assert.Equal((ActionOutcome.Invalid, "providers.invalidBaseUrl"), (bad.Outcome, bad.ErrorKey));
        var missing = await _c.Providers.SetApiKeyAsync(deviceId, "nope", "k");
        Assert.Equal((ActionOutcome.NotFound, "providers.unknownProvider"), (missing.Outcome, missing.ErrorKey));
        Assert.Equal(ActionOutcome.Unavailable, (await _c.Providers.RemoveAsync("not-connected-device", "gw")).Outcome);

        // 明文只经 reveal 单次取回,Store 里没有
        var revealed = await _c.Providers.RevealCredentialAsync(deviceId, "gw");
        Assert.Equal(Secret, revealed.Value!.ApiKey);
        Assert.DoesNotContain("SECRET", _c.ProvidersState.ToString());
        Assert.DoesNotContain("SECRET", _c.CatalogState.ToString());

        // 覆盖
        var overridden = await _c.Catalog.SetOverrideAsync(deviceId, new ModelOverrideDto(null, "c1", DisplayName: "My C1"));
        Assert.True(overridden.IsOk);
        await WaitUntil(() => _c.CatalogState.Overrides.Any(o => o.ModelId == "c1"), "override mirrored");
        Assert.True((await _c.Catalog.RemoveOverrideAsync(deviceId, null, "c1")).IsOk);
        await WaitUntil(() => _c.CatalogState.Overrides.IsEmpty, "override removed");

        // 删供应商:别名跟着走
        Assert.True((await _c.Providers.RemoveAsync(deviceId, "gw")).IsOk);
        await WaitUntil(() => _c.ProvidersState is { Providers.IsEmpty: true, Aliases.IsEmpty: true, Options.IsEmpty: true }, "provider removed everywhere");
    }

    [Fact]
    public void The_agent_and_the_protocol_name_discovery_sources_the_same_way()
    {
        // Agent 层不引用协议:来源名在两边各写一份,这里守它们一致(控制端按协议的值翻译)
        Assert.Equal(DiscoverySources.All, Ncw.Agent.Catalog.Discovery.DiscoveryReaders.Order);
        Assert.Equal((DiscoveryStatus.New, DiscoveryStatus.Imported, DiscoveryStatus.Conflict),
            (Ncw.Agent.Catalog.Discovery.ProviderDiscovery.StatusNew, Ncw.Agent.Catalog.Discovery.ProviderDiscovery.StatusImported, Ncw.Agent.Catalog.Discovery.ProviderDiscovery.StatusConflict));
        Assert.Equal((DiscoveryErrorKeys.Gone, DiscoveryErrorKeys.AlreadyImported),
            (Ncw.Agent.Catalog.Discovery.ProviderDiscovery.ErrorGone, Ncw.Agent.Catalog.Discovery.ProviderDiscovery.ErrorAlreadyImported));
    }

    [Fact]
    public async Task Discovered_providers_are_listed_without_their_keys_and_imported_on_the_device()
    {
        const string DiscoveredSecret = "sk-DISCOVERED-SECRET-7788";
        _discovery.Files["/discovery-home/.claude/settings.json"] =
            $$"""{ "env": { "ANTHROPIC_BASE_URL": "https://api.deepseek.com/anthropic", "ANTHROPIC_AUTH_TOKEN": "{{DiscoveredSecret}}", "ANTHROPIC_MODEL": "deepseek-chat" } }""";
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.True(_c.Providers.SupportsDiscovery(deviceId)); // 设备在 welcome.features 里报了这个能力

        var scan = await _c.Providers.ScanDiscoveryAsync(deviceId);
        var candidate = Assert.Single(scan.Value!.Candidates);
        Assert.Equal(("deepseek-anthropic", "anthropic", "7788", DiscoverySources.ClaudeCode, DiscoveryStatus.New),
            (candidate.SuggestedId, candidate.Protocol, candidate.KeyLast4, candidate.Source, candidate.Status));
        // 线上载荷里没有明文(控制端收到的就是这些字节)
        var wire = Encoding.UTF8.GetString(Ncw.Protocol.Wire.FrameCodec.EncodePayload(scan.Value, PayloadShapes.GetShape<DiscoveryScanResult>()));
        Assert.DoesNotContain("SECRET", wire, StringComparison.Ordinal);

        var imported = await _c.Providers.ImportDiscoveredAsync(deviceId, [candidate.Id]);
        Assert.Equal("deepseek-anthropic", Assert.Single(imported.Value!.Items).ProviderId);
        await WaitUntil(() => _c.ProvidersState.Providers.Any(p => p.Id == "deepseek-anthropic" && p.HasCredential)
            && _c.ProvidersState.Aliases.Any(a => a.ProviderId == "deepseek-anthropic" && a.UpstreamModel == "deepseek-chat"), "imported provider mirrored");
        Assert.DoesNotContain("SECRET", _c.ProvidersState.ToString(), StringComparison.Ordinal);

        // key 存在设备上(只有本机 owner 的 reveal 能取回来)
        Assert.Equal(DiscoveredSecret, (await _c.Providers.RevealCredentialAsync(deviceId, "deepseek-anthropic")).Value!.ApiKey);
        Assert.Equal(DiscoveryStatus.Imported, Assert.Single((await _c.Providers.ScanDiscoveryAsync(deviceId)).Value!.Candidates).Status);
    }

    [Fact]
    public void The_agent_and_the_protocol_name_template_groups_and_variants_the_same_way()
    {
        // Agent 层不引用协议:分组 / 端点标签在两边各写一份,这里守它们一致(控制端按协议的值翻译)
        Assert.Equal(ProviderTemplateGroups.All, Ncw.Agent.Catalog.Templates.ProviderTemplateGroups.All);
        Assert.Equal(ProviderTemplateVariants.All, Ncw.Agent.Catalog.Templates.ProviderTemplateVariants.All);
    }

    [Fact]
    public async Task A_draft_is_debugged_on_the_device_with_live_progress_and_its_key_never_comes_back()
    {
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.True(_c.Providers.SupportsDebug(deviceId)); // 设备在 welcome.features 里报了这个能力
        var target = new ProviderDebugTargetDto(null, "https://draft.example.com/v1", "openai-chat", Secret);

        // 候选:两族列表端点都试了(假上游只认 OpenAI 族的 /models)
        var models = await _c.Providers.DebugModelsAsync(deviceId, target);
        Assert.Equal(["m1", "m2"], models.Value!.Models.Select(m => m.Id));
        Assert.Equal(2, models.Value.Attempts.Length);

        // 发一条:实时进度经 providers.debug/<runId> 主题到达,跑完返回完整记录
        var live = new List<ProviderDebugEventDto>();
        var run = await _c.Providers.DebugRunAsync(deviceId, new ProviderDebugRunParams("integration-1", target, "openai-chat", "m1", "", 64, DebugThinking.Auto, false),
            ev =>
            {
                lock (live)
                {
                    live.Add(ev);
                }
            });
        var record = run.Value!;
        Assert.True(record.Ok, record.Detail);
        Assert.Equal("OK", record.Text);
        Assert.Equal("https://draft.example.com/v1/chat/completions", record.Url);
        Assert.Equal($"Bearer ••••{Secret[^4..]}", record.RequestHeaders.Single(h => h.Name == "authorization").Value);
        Assert.Equal($"Bearer {Secret}", _up.LastChatAuthorization); // 上游收到的是真 key
        lock (live)
        {
            Assert.NotEmpty(live);
            Assert.All(live, ev => Assert.Contains(ev, record.Events));
        }

        // 线上载荷里没有明文;草稿不落盘
        var wire = Encoding.UTF8.GetString(Ncw.Protocol.Wire.FrameCodec.EncodePayload(record, PayloadShapes.GetShape<ProviderDebugResultDto>()));
        Assert.DoesNotContain(Secret, wire, StringComparison.Ordinal);
        Assert.Empty(_c.ProvidersState.Providers);

        // 调试对象不合法是结构化的结局
        var bad = await _c.Providers.DebugModelsAsync(deviceId, new ProviderDebugTargetDto(null, "file:///etc/passwd"));
        Assert.Equal((ActionOutcome.Invalid, "providers.invalidBaseUrl"), (bad.Outcome, bad.ErrorKey));
        var noModel = await _c.Providers.DebugRunAsync(deviceId, new ProviderDebugRunParams("integration-2", target, "openai-chat", " ", "", 64, DebugThinking.Auto, false));
        Assert.Equal("providers.debug.modelRequired", noModel.ErrorKey);
    }

    [Fact]
    public async Task The_device_hands_out_its_built_in_templates_and_they_match_its_providers()
    {
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.True(_c.Providers.SupportsTemplates(deviceId)); // 设备在 welcome.features 里报了这个能力

        var templates = (await _c.Providers.ListTemplatesAsync(deviceId)).Value!.Templates;

        Assert.Equal(Ncw.Agent.Catalog.Templates.ProviderTemplates.All.Count, templates.Length);
        var deepseek = Assert.Single(templates, t => t.Id == "deepseek-anthropic");
        Assert.Equal(("deepseek", ProviderTemplateVariants.Anthropic, "anthropic", "https://api.deepseek.com/anthropic"),
            (deepseek.Service, deepseek.Variant, deepseek.Protocol, deepseek.BaseUrl));
        Assert.Contains(templates, t => t is { Id: "ollama", NeedsKey: false, KeyUrl: null });
        // 已有供应商的 logo 从同一份模版派生(按地址)
        var refs = templates.Select(t => new Ncw.Core.Domain.TemplateRef(t.Id, t.Service, t.BaseUrl, t.Protocol)).ToList();
        Assert.Equal("moonshot", Ncw.Core.Domain.ProviderTemplateMatch.Find("https://api.moonshot.ai/v1/", "openai-chat", refs)?.Service);
    }

    [Fact]
    public async Task A_dropped_connection_marks_the_mirrors_unknown_and_the_reconnect_catches_up_with_what_changed_meanwhile()
    {
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.True((await _c.Providers.UpsertAsync(deviceId, new ProvidersUpsertParams("a", "A", "openai-chat", "https://a.example.com/v1"))).IsOk);
        await WaitUntil(() => _c.ProvidersState.Providers.Length == 1, "first provider");
        var versionBefore = _c.ProvidersState.Version;

        // 断线期间先不让它重连:重连很快,慢的机器上「断开之后、重连之前」这个窗口可能在下面两行之间就过去了(macOS CI 上删除成功过)
        _c.HoldConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _c.Current!.Cut();
        await WaitUntil(() => !_c.CatalogState.Known && !_c.ProvidersState.Known, "unknown after the drop"); // 断开 = 未知,不沿用旧值当现状
        Assert.Equal(ActionOutcome.Unavailable, (await _c.Providers.RemoveAsync(deviceId, "a")).Outcome);
        _c.HoldConnect.SetResult();

        await WaitUntil(() => _c.Link.IsConnected && _c.ProvidersState.Known && _c.CatalogState.Known, "reconnected and mirrored again", 30_000);
        Assert.Equal("a", Assert.Single(_c.ProvidersState.Providers).Id);
        Assert.True(_c.ProvidersState.Version >= versionBefore);
    }

    [Fact]
    public async Task Oauth_progress_lands_in_the_store_for_the_controller_to_open_the_authorize_page_and_the_pasted_code_finishes_the_login()
    {
        _c = await StartControllerAsync();
        var deviceId = _c.Link.DeviceId;
        Assert.True((await _c.Providers.UpsertAsync(deviceId, new ProvidersUpsertParams("codex", "Codex", "openai-responses", "https://codex.example.com/v1"))).IsOk);
        await WaitUntil(() => _c.ProvidersState.Providers.Length == 1 && _c.ProvidersState.Providers[0].OAuthIssuer == "chatgpt", "oauth provider mirrored");

        var login = Task.Run(() => _c.Providers.StartOAuthAsync(deviceId, "codex"));
        await WaitUntil(() => _c.ProvidersState.OAuth is { Phase: OAuthPhases.Waiting, AuthorizeUrl: not null }, "authorize url in the store");
        var progress = _c.ProvidersState.OAuth!;
        Assert.Equal(("codex", "https://auth.example.com/authorize?state=abc", true), (progress.ProviderId, progress.AuthorizeUrl, progress.NeedsPastedCode));

        Assert.True((await _c.Providers.SubmitOAuthCodeAsync(deviceId, "codex", "the-code")).IsOk);
        var result = await login;
        Assert.Equal((ActionOutcome.Ok, "me@example.com", "plus"), (result.Outcome, result.Value!.Auth!.Email, result.Value.Auth.PlanType));
        await WaitUntil(() => _c.ProvidersState.OAuth is null, "progress cleared at the terminal state");
        await WaitUntil(() => _c.ProvidersState.AccountsOf("codex").Length == 1, "account mirrored");
        var account = _c.ProvidersState.AccountsOf("codex")[0];
        Assert.Equal(("chatgpt", true, true), (account.Issuer, account.Current, account.Enabled));

        // 登出当前账号
        Assert.True((await _c.Providers.SignOutAsync(deviceId, "codex")).IsOk);
        await WaitUntil(() => _c.ProvidersState.AccountsOf("codex").IsEmpty, "account removed");
    }
}
