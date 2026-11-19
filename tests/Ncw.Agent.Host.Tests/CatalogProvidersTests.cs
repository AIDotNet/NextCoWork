using System.Text;
using Ncw.Agent.Catalog;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm.Auth;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 模型目录与供应商经协议的端到端(docs/design/09 §8.4、02 A8):真实 Agent + 真实 DeviceClient,上游(models.dev、供应商接口、模型接口)是脚本化的假 HTTP。
/// 覆盖:配置供应商 → 目录查询 → 选模型对话 → 用量按目录计价;目录刷新的各种结局;定时器随 Host 启停;角色与承载;凭据永不上线;OAuth 进度。
/// </summary>
public sealed class CatalogProvidersTests : IAsyncLifetime
{
    private const string Secret = "sk-SECRET-1234567890";
    private const string ModelsDevUrl = "models.dev/api.json";
    private const string ChatUrl = "gw.example.com/v1/chat/completions";

    private readonly FakeUpstream _up = new();
    private readonly ReleasableDelay _catalogTimer = new();
    private readonly FakeLogin _login = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private int _port;

    public CatalogProvidersTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Http = _up,
            CatalogDelayer = _catalogTimer,
            SessionTitles = false, // 这里数的是正文的 HTTP 请求 / 用量行:标题的旁路请求另有测试(SessionTitleTests)
            CatalogStore = new MemoryCatalogStore(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            OAuthLogins = issuer => issuer == "chatgpt" ? _login : null,
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        await _rig.AddProjectAsync(_owner); // 等数据就绪
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    // ───────────────────────── 端到端:配置供应商 → 目录 → 对话 → 计价 ─────────────────────────

    [Fact]
    public async Task A_provider_configured_over_the_protocol_runs_a_conversation_and_usage_is_priced_from_the_catalog()
    {
        var (owner, tap) = await _rig.ConnectTappedAsync("recorded-owner");
        var project = (await owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single();
        var catalogTopic = await Watch<CatalogTopicState>.OpenAsync(owner, CatalogMethods.Topic);
        var providersTopic = await Watch<ProvidersTopicState>.OpenAsync(owner, ProvidersMethods.Topic);

        // 1. 目录:先刷新拿到(假的)models.dev 数据
        _up.On(ModelsDevUrl, () => Replies.Json(Replies.ModelsDev(12), etag: "\"v1\""));
        var refreshed = await owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
                Assert.Equal((CatalogRefreshOutcomes.Updated, 12), (refreshed.Outcome, refreshed.ModelCount));
        var found = await owner.CallAsync(CatalogMethods.Query, new CatalogQueryParams(Text: "claude-test"));
        Assert.Equal(12, found.Total);
        var model = found.Models.Single(m => m.Id == "claude-test-0");
        Assert.Equal(("anthropic", 200_000, 64_000), (model.ProviderId, model.ContextWindow, model.MaxOutputTokens));
        Assert.Equal(("usd", 3d, 15d), (model.Price!.Currency, model.Price.Tiers[0].Rates.Input, model.Price.Tiers[0].Rates.Output));
        await catalogTopic.WaitAsync(() => catalogTopic.Decoded<CatalogTopicState>(CatalogEvents.Changed).Any(s => s.Status.ModelCount == 12), "catalog.changed after the refresh");

        // 2. 供应商:upsert + 凭据 + 把目录里的模型挂上来
        var provider = await owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        Assert.False(provider.HasCredential);
        var info = await owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", Secret));
        Assert.True(info.HasKey);
        Assert.Equal(Secret[^4..], info.Last4);
        var aliases = await owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["claude-test-0"]));
        Assert.Equal("claude-test-0", Assert.Single(aliases.Aliases).Alias);
        await providersTopic.WaitAsync(
            () => providersTopic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(s => s.Aliases.Length == 1 && s.Providers.Single().HasCredential), "providers.changed with the credential");

        var option = Assert.Single((await owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())).Options);
        Assert.Equal(("gw", "claude-test-0", 200_000, 64_000, true), (option.ProviderId, option.Alias, option.ContextWindow, option.MaxOutputTokens, option.Enabled));
        var resolution = await owner.CallAsync(ProvidersMethods.Resolve, new ProvidersResolveParams("claude-test-0"));
        Assert.True(resolution.Found);
        Assert.Equal(3d, resolution.Price!.Tiers[0].Rates.Input); // 价格取自目录

        // 3. 创建会话选这个模型,真实路由经假上游完成一轮
        _up.On(ChatUrl, () => Replies.ChatStream("claude-test-0", "hello there", promptTokens: 1000, completionTokens: 500));
        var session = await owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "claude-test-0"));
        var first = await StartAsync(owner, session.Id, "hi", "claude-test-0");
        var firstRun = await RunWatch.OpenAsync(owner, first.RunId);
        await firstRun.SettledAsync();

        Assert.Equal("done", firstRun.Of<RunEndEventDto>().Single().Status);
        var request = Assert.Single(_up.To(ChatUrl));
        Assert.Equal($"Bearer {Secret}", request.Headers["authorization"]); // 凭据走的是保险库,发给了上游
        Assert.Contains("\"model\":\"claude-test-0\"", request.Body);       // 上游模型名,不是别名之外的东西

        // 4. 用量按目录计价:1000 × 3 + 500 × 15 = 10500 微美元,冻结进 usage_records
        var end = firstRun.Of<MessageEndEventDto>().Single(e => e.Message.Role == "assistant");
        Assert.Equal((1000L, 500L), (end.Usage!.InputTokens, end.Usage.OutputTokens));
        Assert.Equal(new CostDto("known", 10_500, "usd"), end.Cost);
        var page = await owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id));
        Assert.Equal(new CostDto("known", 10_500, "usd"), page.RunUsage[first.RunId].Cost);
        Assert.Equal("claude-test-0", page.RunModel[first.RunId]);

        // 5. 用户价格覆盖对后续请求生效;已冻结的费用不回算
        var overridden = await owner.CallAsync(CatalogMethods.OverrideSet, new ModelOverrideDto(null, "claude-test-0", Price: new PriceOverrideDto("usd", 1, 2)));
        Assert.Single(overridden.Overrides);
        Assert.Equal(1d, (await owner.CallAsync(CatalogMethods.Get, new CatalogGetParams("anthropic", "claude-test-0"))).Model!.Price!.Tiers[0].Rates.Input);
        var second = await StartAsync(owner, session.Id, "again", "claude-test-0");
        await (await RunWatch.OpenAsync(owner, second.RunId)).SettledAsync();
        page = await owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id));
        Assert.Equal(new CostDto("known", 2_000, "usd"), page.RunUsage[second.RunId].Cost); // 1000 × 1 + 500 × 2
        Assert.Equal(new CostDto("known", 10_500, "usd"), page.RunUsage[first.RunId].Cost);

        // 6. 凭据不在这个控制端收到的任何字节里(快照、事件、响应),也不在 providers.json 里
        var wire = Encoding.Latin1.GetString(tap.Transcript);
        Assert.DoesNotContain(Secret, wire);
        Assert.DoesNotContain("sk-SECRET", wire);
        var file = Path.Combine(_rig.DataRoot, ProviderFileStore.FileName);
        Assert.DoesNotContain("sk-SECRET", File.ReadAllText(file));
        Assert.Contains("\"gw\"", File.ReadAllText(file)); // 供应商本身落在 providers.json
    }

    [Fact]
    public async Task Without_a_credential_the_run_ends_with_a_structured_missing_credential_error_not_a_request()
    {
        var project = (await _owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single();
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["some-model"]));
        var session = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "some-model"));

        var start = await StartAsync(_owner, session.Id, "hi", "some-model");
        var watch = await RunWatch.OpenAsync(_owner, start.RunId);
        await watch.SettledAsync();

        Assert.Equal("error", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Empty(_up.To(ChatUrl));
    }

    [Fact]
    public async Task A_disabled_provider_is_reported_as_no_enabled_provider_and_the_selector_still_lists_the_model()
    {
        var project = (await _owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single();
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1", Enabled: false));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["some-model"]));

        var option = Assert.Single((await _owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())).Options);
        Assert.False(option.ProviderEnabled); // 停用的也显示出来(标成不可用),用户不会以为配置丢了
        Assert.Equal(ModelResolutionIssues.NoEnabledProvider, (await _owner.CallAsync(ProvidersMethods.Resolve, new ProvidersResolveParams("some-model"))).Issue);

        var session = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "some-model"));
        var watch = await RunWatch.OpenAsync(_owner, (await StartAsync(_owner, session.Id, "hi", "some-model")).RunId);
        await watch.SettledAsync();
        Assert.Equal(Ncw.Agent.Llm.LlmErrorKeys.NoEnabledProvider, watch.Of<RunEndEventDto>().Single().Error!.MessageKey);
    }

    // ───────────────────────── 目录刷新的结局 ─────────────────────────

    [Fact]
    public async Task Refresh_reports_updated_then_not_modified_on_304_and_rejects_a_shrunk_catalog_keeping_the_previous_one()
    {
        var topic = await Watch<CatalogTopicState>.OpenAsync(_owner, CatalogMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "catalog snapshot");
        var seedCount = topic.Snapshot.Status.ModelCount;
        Assert.Equal("seed", topic.Snapshot.Status.Source); // 断网的首次启动:内置种子照样可用

        _up.On(ModelsDevUrl, () => Replies.Json(Replies.ModelsDev(10), etag: "\"v1\""))
           .On(ModelsDevUrl, Replies.NotModified)
           .On(ModelsDevUrl, () => Replies.Json(Replies.ModelsDev(4), etag: "\"v2\"")) // 10 → 4:缩水一半以上
           .On(ModelsDevUrl, () => Replies.Json("<html>not json</html>"));

        var first = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        Assert.Equal((CatalogRefreshOutcomes.Updated, 10), (first.Outcome, first.ModelCount));
        var versionAfterUpdate = (await _owner.CallAsync(CatalogMethods.Status, Unit.Value)).Version;

        var notModified = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        Assert.Equal(CatalogRefreshOutcomes.NotModified, notModified.Outcome);
        Assert.Equal("\"v1\"", _up.To(ModelsDevUrl)[1].Headers["if-none-match"]); // 条件请求
        Assert.Equal(versionAfterUpdate, (await _owner.CallAsync(CatalogMethods.Status, Unit.Value)).Version); // 304 不算可见变化

        var shrunk = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        Assert.Equal(CatalogRefreshOutcomes.Rejected, shrunk.Outcome);
        Assert.Equal("catalog.shrunk", shrunk.Error!.Key);
        var invalid = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        Assert.Equal(("catalog.invalidJson", CatalogRefreshOutcomes.Rejected), (invalid.Error!.Key, invalid.Outcome));

        // 被拒之后仍是上一份(10 个模型),错误记在状态里,而且经主题推给了控制端
        var status = await _owner.CallAsync(CatalogMethods.Status, Unit.Value);
        Assert.Equal((10, "remote", versionAfterUpdate), (status.ModelCount, status.Source, status.Version));
        Assert.Equal("catalog.invalidJson", status.LastError!.Key);
        Assert.Equal(10, (await _owner.CallAsync(CatalogMethods.Query, new CatalogQueryParams(ProviderId: "anthropic"))).Total);
        await topic.WaitAsync(() => topic.Decoded<CatalogTopicState>(CatalogEvents.Changed).Any(s => s.Status.LastError?.Key == "catalog.invalidJson"), "the last error reaches the controller");
        Assert.True(topic.Decoded<CatalogTopicState>(CatalogEvents.Changed).Select(s => s.Status.Version).Distinct().Count() <= 2); // 只有「更新」那一次版本变了
        Assert.NotEqual(10, seedCount);
    }

    [Fact]
    public async Task Refresh_failures_are_results_not_protocol_errors()
    {
        _up.On(ModelsDevUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);

        Assert.Equal((CatalogRefreshOutcomes.Failed, "catalog.httpStatus"), (result.Outcome, result.Error!.Key));
        Assert.Equal("catalog.httpStatus", (await _owner.CallAsync(CatalogMethods.Status, Unit.Value)).LastError!.Key);
    }

    [Fact]
    public async Task The_catalog_timer_starts_with_the_host_and_updates_on_its_own_and_the_controller_is_told()
    {
        var topic = await Watch<CatalogTopicState>.OpenAsync(_owner, CatalogMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "catalog snapshot");
        _up.On(ModelsDevUrl, () => Replies.Json(Replies.ModelsDev(12), etag: "\"t\""));

        // 数据就绪后定时器自己起来,先睡 30 秒(02 A8)
        await Watch<Unit>.UntilAsync(() => _catalogTimer.Delays.Count > 0, "the timer starts");
        Assert.Equal(TimeSpan.FromSeconds(30), _catalogTimer.Delays[0]);
        Assert.Empty(_up.To(ModelsDevUrl));

        await _catalogTimer.ReleaseNextAsync();

        await topic.WaitAsync(() => topic.Decoded<CatalogTopicState>(CatalogEvents.Changed).Any(s => s.Status.ModelCount == 12), "the timer-driven update is pushed");
        Assert.Single(_up.To(ModelsDevUrl));
        await Watch<Unit>.UntilAsync(() => _catalogTimer.Delays.Count > 1, "the next sleep");
        Assert.Equal(TimeSpan.FromHours(6), _catalogTimer.Delays[1]); // 成功后 6 小时一次

        // 设置可以关掉自动更新(定时器停);再打开又起来
        var off = await _owner.CallAsync(CatalogMethods.SettingsSet, new CatalogSettingsDto(false, 60));
        Assert.Equal((false, 60), (off.AutoUpdate, off.IntervalMinutes));
        Assert.Equal(60, (await _owner.CallAsync(CatalogMethods.SettingsGet, Unit.Value)).IntervalMinutes);
        var clamped = await _owner.CallAsync(CatalogMethods.SettingsSet, new CatalogSettingsDto(true, 1));
        Assert.Equal(15, clamped.IntervalMinutes); // 夹在 15 分钟到 7 天之间
    }

    [Fact]
    public async Task Overrides_and_deleted_models_are_visible_through_the_catalog_and_hidden_models_come_back_on_restore()
    {
        _up.On(ModelsDevUrl, () => Replies.Json(Replies.ModelsDev(12)));
        await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        var topic = await Watch<CatalogTopicState>.OpenAsync(_owner, CatalogMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "catalog snapshot");

        var renamed = await _owner.CallAsync(CatalogMethods.OverrideSet, new ModelOverrideDto("anthropic", "claude-test-1", DisplayName: "My Claude", ContextWindow: 123_000));
        Assert.Single(renamed.Overrides);
        var model = (await _owner.CallAsync(CatalogMethods.Get, new CatalogGetParams("anthropic", "claude-test-1"))).Model!;
        Assert.Equal(("My Claude", 123_000, true), (model.Name, model.ContextWindow, model.Overridden));

        await _owner.CallAsync(CatalogMethods.ModelDelete, new CatalogModelRefParams("anthropic", "claude-test-1"));
        Assert.Null((await _owner.CallAsync(CatalogMethods.Get, new CatalogGetParams("anthropic", "claude-test-1"))).Model);
        Assert.Equal(11, (await _owner.CallAsync(CatalogMethods.Query, new CatalogQueryParams(Text: "claude-test"))).Total);

        await _owner.CallAsync(CatalogMethods.ModelRestore, new CatalogModelRefParams("anthropic", "claude-test-1"));
        Assert.Equal("My Claude", (await _owner.CallAsync(CatalogMethods.Get, new CatalogGetParams("anthropic", "claude-test-1"))).Model!.Name); // 已有的覆盖一起回来

        await _owner.CallAsync(CatalogMethods.OverrideRemove, new CatalogModelRefParams("anthropic", "claude-test-1"));
        Assert.Empty((await _owner.CallAsync(CatalogMethods.OverridesList, Unit.Value)).Overrides);
        Assert.Equal("Claude Test 1", (await _owner.CallAsync(CatalogMethods.Get, new CatalogGetParams("anthropic", "claude-test-1"))).Model!.Name);
        // 主题的增量和调用的返回值走两条路:返回了不代表「覆盖已清空」那条增量已经到了(Windows CI 上撞到过两次),所以等它
        await topic.WaitAsync(() => topic.Decoded<CatalogTopicState>(CatalogEvents.Changed).LastOrDefault() is { } last && !last.Overrides.Any(), "the catalog topic to show no overrides");

        // 目录供应商列表不带模型
        var providers = await _owner.CallAsync(CatalogMethods.Providers, Unit.Value);
        Assert.Equal(12, providers.Providers.Single(p => p.Id == "anthropic").ModelCount);
    }

    // ───────────────────────── 角色与承载 ─────────────────────────

    private async Task<DeviceClient> ConnectRemoteAsync(string name, string role)
    {
        if (_port == 0)
        {
            _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
        }

        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<string> CodeOf<T>(Func<Task<T>> call) => (await Assert.ThrowsAsync<ProtocolException>(call)).Code;

    [Fact]
    public async Task A_device_advertises_the_catalog_and_providers_capabilities()
    {
        Assert.Contains(Capabilities.Catalog, _owner.Welcome.Features);
        Assert.Contains(Capabilities.Providers, _owner.Welcome.Features);
    }

    [Fact]
    public async Task A_viewer_can_read_the_catalog_but_never_sees_providers_or_credentials_and_cannot_change_anything()
    {
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", Secret));
        var identity = PeerIdentity.Create("viewer");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, "viewer");
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, "viewer"));
        var tap = new TapStream(await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint));
        await using var viewer = await DeviceClient.ConnectAsync(tap, new ClientHello("viewer", "3.0.0"));

        // 读目录:行
        Assert.True((await viewer.CallAsync(CatalogMethods.Query, new CatalogQueryParams(Limit: 5))).Total > 0);
        Assert.NotEmpty((await viewer.CallAsync(CatalogMethods.Providers, Unit.Value)).Providers);
        _ = await viewer.CallAsync(CatalogMethods.Status, Unit.Value);
        _ = await viewer.CallAsync(CatalogMethods.OverridesList, Unit.Value);
        var topic = await Watch<CatalogTopicState>.OpenAsync(viewer, CatalogMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "catalog snapshot");

        // 供应商配置、凭据、账号、登录进度:一概不行(含订阅)
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ProvidersMethods.List, Unit.Value)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ProvidersMethods.AccountsList, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<ProvidersTopicState>.OpenAsync(viewer, ProvidersMethods.Topic)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<OAuthTopicState>.OpenAsync(viewer, ProvidersMethods.OAuthTopic)));

        // 目录的写入:一概不行
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(CatalogMethods.Refresh, Unit.Value)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(CatalogMethods.OverrideSet, new ModelOverrideDto(null, "x", DisplayName: "y"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(CatalogMethods.ModelDelete, new CatalogModelRefParams(null, "x"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(CatalogMethods.SettingsSet, new CatalogSettingsDto(false, 60))));

        Assert.DoesNotContain("sk-SECRET", Encoding.Latin1.GetString(tap.Transcript)); // viewer 收到的任何字节里都没有凭据
    }

    [Fact]
    public async Task A_member_can_read_providers_but_only_an_owner_can_write_them_and_nobody_remote_can_reveal_a_credential()
    {
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", Secret));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["m1"]));
        await using var member = await ConnectRemoteAsync("member-1", "member");
        await using var remoteOwner = await ConnectRemoteAsync("owner-2", "owner");

        // member:只读可以
        var list = await member.CallAsync(ProvidersMethods.List, Unit.Value);
        Assert.True(list.Providers.Single().HasCredential); // 只是个布尔
        Assert.Equal("m1", (await member.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())).Options.Single().Alias);
        Assert.True((await member.CallAsync(ProvidersMethods.Resolve, new ProvidersResolveParams("m1"))).Found);
        var topic = await Watch<ProvidersTopicState>.OpenAsync(member, ProvidersMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "providers snapshot");

        // member:写入、凭据、账号、登录、目录写入都不行
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("evil", "Evil", "openai-chat", "https://evil.example.com/v1"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.Remove, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", "stolen"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.CredentialRemove, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.ModelsFetch, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ProvidersMethods.OAuthStart, new ProviderRefParams("gw"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(CatalogMethods.Refresh, Unit.Value)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<OAuthTopicState>.OpenAsync(member, ProvidersMethods.OAuthTopic)));
        Assert.Single((await _owner.CallAsync(ProvidersMethods.List, Unit.Value)).Providers); // 什么都没变

        // 远程的 owner:可以写、可以看摘要,但明文只给本机通道
        var created = await remoteOwner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("second", "Second", "anthropic", "https://second.example.com"));
        Assert.Equal("second", created.Id);
        Assert.Equal(Secret[^4..], (await remoteOwner.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams("gw"))).Last4);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => remoteOwner.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams("gw"))));

        // 本机的 owner:reveal 单次返回明文
        var revealed = await _owner.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams("gw"));
        Assert.Equal(("apiKey", Secret), (revealed.Kind, revealed.ApiKey));
    }

    // ───────────────────────── 凭据永不上线 ─────────────────────────

    [Fact]
    public async Task Credentials_and_tokens_never_appear_in_any_snapshot_event_or_response_except_reveal()
    {
        var (owner, tap) = await _rig.ConnectTappedAsync("recorded-owner");
        var catalog = await Watch<CatalogTopicState>.OpenAsync(owner, CatalogMethods.Topic);
        var providers = await Watch<ProvidersTopicState>.OpenAsync(owner, ProvidersMethods.Topic);
        var oauth = await Watch<OAuthTopicState>.OpenAsync(owner, ProvidersMethods.OAuthTopic);
        var interactions = await Watch<InteractionsDto>.OpenAsync(owner, ConversationMethods.InteractionsTopic);
        var device = await Watch<DeviceTopicState>.OpenAsync(owner, DeviceMethods.DeviceTopic);

        await owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1", ProtocolOptionsJson: "{\"anthropic\":{\"cacheTtl\":\"1h\"}}"));
        await owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", Secret));
        await owner.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams("gw"));
        await owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["a", "b"]));
        await owner.CallAsync(ProvidersMethods.ModelsUpdate, new ModelUpdateParams("gw", "a", RequestAdapterJson: "{\"headers\":{\"x-token\":\"" + Secret + "\"}}", ThinkingConfigJson: "{\"mode\":\"toggle\"}"));
        _up.On("gw.example.com/v1/models", () => Replies.Json("{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}"));
        await owner.CallAsync(ProvidersMethods.ModelsFetch, new ProviderRefParams("gw"));
        await owner.CallAsync(ProvidersMethods.Test, new ProviderRefParams("gw"));
        await owner.CallAsync(ProvidersMethods.List, Unit.Value);
        await owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams());
        // 登录:令牌从不上线
        await owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("codex", "Codex", "openai-responses", "https://codex.example.com/v1"));
        _login.Script = async (sink, ct) =>
        {
            sink.OpenAuthorizeUrl("https://auth.example.com/authorize?state=s1");
            var code = await ((IOAuthPasteSink)sink).AwaitPastedCodeAsync(ct);
            return new OAuthCredential("chatgpt", "AT-SECRET-" + code, "RT-SECRET-" + code, null, "acc-1", "me@example.com");
        };
        var start = Task.Run(() => owner.CallAsync(ProvidersMethods.OAuthStart, new ProviderRefParams("codex")));
        await oauth.WaitAsync(() => oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).Any(p => p.Phase == OAuthPhases.Waiting && p.AuthorizeUrl is not null), "authorize url");
        await owner.CallAsync(ProvidersMethods.OAuthSubmitCode, new OAuthSubmitCodeParams("codex", "CODE1"));
        var info = await start;
        Assert.Equal("me@example.com", info.Auth!.Email);
        await providers.WaitAsync(() => providers.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(s => s.Accounts.Length == 1 && s.Accounts[0].Accounts.Length == 1), "the account arrives");
        await owner.CallAsync(ProvidersMethods.AccountsList, new ProviderRefParams("codex"));
        await owner.CallAsync(CatalogMethods.OverrideSet, new ModelOverrideDto(null, "a", DisplayName: "A"));
        await Task.Delay(100);

        var wire = Encoding.Latin1.GetString(tap.Transcript);
        foreach (var secret in new[] { Secret, "sk-SECRET", "AT-SECRET", "RT-SECRET", "CODE1" })
        {
            Assert.DoesNotContain(secret, wire);
        }

        Assert.Contains("me@example.com", wire); // 登录态摘要(邮箱、计划)是给控制端看的
        _ = catalog; _ = interactions; _ = device;
    }

    // ───────────────────────── OAuth 进度 ─────────────────────────

    private sealed class FakeLogin : IOAuthLogin
    {
        public string Issuer => "chatgpt";

        public OAuthGrantKind Grant => OAuthGrantKind.AuthorizationCode;

        public Func<IOAuthLoginSink, CancellationToken, Task<ProviderCredential>>? Script { get; set; }

        public Task<ProviderCredential> LoginAsync(IOAuthLoginSink sink, CancellationToken ct) =>
            Script is { } script ? script(sink, ct) : throw new InvalidOperationException("no login script");
    }

    [Fact]
    public async Task Oauth_progress_reaches_the_owner_the_pasted_code_completes_the_login_and_cancel_ends_a_pending_one()
    {
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("codex", "Codex", "openai-responses", "https://codex.example.com/v1"));
        _login.Script = async (sink, ct) =>
        {
            sink.OpenAuthorizeUrl("https://auth.example.com/authorize?state=s1");
            var code = await ((IOAuthPasteSink)sink).AwaitPastedCodeAsync(ct);
            return new OAuthCredential("chatgpt", "at-" + code, "rt", null, "acc-" + code, code + "@example.com");
        };
        var oauth = await Watch<OAuthTopicState>.OpenAsync(_owner, ProvidersMethods.OAuthTopic);
        var providers = await Watch<ProvidersTopicState>.OpenAsync(_owner, ProvidersMethods.Topic);
        await oauth.WaitAsync(() => oauth.HasSnapshot && providers.HasSnapshot, "snapshots");
        Assert.Null(oauth.Snapshot.Current);

        // 登录:授权页由控制端打开(URL 经进度事件下发),回调码经 submitCode 回来
        var start = Task.Run(() => _owner.CallAsync(ProvidersMethods.OAuthStart, new ProviderRefParams("codex")));
        await oauth.WaitAsync(() => oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).Any(p => p.Phase == OAuthPhases.Waiting), "waiting");
        var waiting = oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).First(p => p.Phase == OAuthPhases.Waiting);
        Assert.Equal(("codex", "https://auth.example.com/authorize?state=s1", true), (waiting.ProviderId, waiting.AuthorizeUrl, waiting.NeedsPastedCode));
        await _owner.CallAsync(ProvidersMethods.OAuthSubmitCode, new OAuthSubmitCodeParams("codex", "c1"));
        var info = await start;
        Assert.Equal(("c1@example.com", true), (info.Auth!.Email, info.HasKey));
        await oauth.WaitAsync(() => oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).Any(p => p.Phase == OAuthPhases.Succeeded), "succeeded");
        Assert.Equal("c1@example.com", Assert.Single(Assert.Single((await _owner.CallAsync(ProvidersMethods.List, Unit.Value)).Accounts).Accounts).Auth!.Email);

        // 第二个账号:取消一个还在等的登录
        var add = Task.Run(() => _owner.CallAsync(ProvidersMethods.OAuthAddAccount, new ProviderRefParams("codex")));
        await oauth.WaitAsync(() => oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).Count(p => p.Phase == OAuthPhases.Waiting) == 2, "second waiting");
        await _owner.CallAsync(ProvidersMethods.OAuthCancel, new ProviderRefParams("codex"));
        var cancelled = await Assert.ThrowsAsync<ProtocolException>(() => add);
        Assert.Equal(ErrorCodes.Conflict, cancelled.Code);
        Assert.Equal("providers.loginCancelled", FrameCodec.DecodePayload(cancelled.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key);
        await oauth.WaitAsync(() => oauth.Decoded<OAuthProgressDto>(ProvidersEvents.OAuthProgress).Any(p => p.Phase == OAuthPhases.Cancelled), "cancelled");

        // 没有登录在等码时提交回调码:结构化错误
        var stale = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.OAuthSubmitCode, new OAuthSubmitCodeParams("codex", "late")));
        Assert.Equal("providers.noLoginInProgress", FrameCodec.DecodePayload(stale.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key);

        // 不走账号登录的供应商
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        Assert.Equal(ErrorCodes.Unsupported, await CodeOf(() => _owner.CallAsync(ProvidersMethods.OAuthStart, new ProviderRefParams("gw"))));
        Assert.NotNull(providers.Snapshot);
    }

    [Fact]
    public async Task Provider_failures_are_structured_errors_with_a_stable_key_in_the_details()
    {
        static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

        var badUrl = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("x", "X", "openai-chat", "file:///etc/passwd")));
        Assert.Equal((ErrorCodes.InvalidRequest, "providers.invalidBaseUrl"), (badUrl.Code, KeyOf(badUrl)));
        var badProtocol = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("x", "X", "soap", "https://x.example.com")));
        Assert.Equal(ErrorCodes.InvalidRequest, badProtocol.Code);
        var unknown = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("nope", "k")));
        Assert.Equal((ErrorCodes.NotFound, "providers.unknownProvider"), (unknown.Code, KeyOf(unknown)));

        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        var empty = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", "   ")));
        Assert.Equal("providers.emptyKey", KeyOf(empty));
        var none = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams("gw")));
        Assert.Equal((ErrorCodes.NotFound, "providers.noCredential"), (none.Code, KeyOf(none)));

        _up.On("gw.example.com/v1/models", () => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"bad key " + Secret + "\"}") });
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", Secret));
        var test = await _owner.CallAsync(ProvidersMethods.Test, new ProviderRefParams("gw"));
        Assert.False(test.Ok);
        Assert.Equal(401, test.Status);
        Assert.DoesNotContain(Secret, test.Detail ?? ""); // 上游回显了 key:诊断里已脱敏

        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["a", "b"]));
        var clash = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(ProvidersMethods.ModelsRename, new ModelRenameParams("gw", "a", "b")));
        Assert.Equal((ErrorCodes.Conflict, "providers.aliasExists"), (clash.Code, KeyOf(clash)));
        var renamed = await _owner.CallAsync(ProvidersMethods.ModelsRename, new ModelRenameParams("gw", "a", "renamed"));
        Assert.Equal("renamed", renamed.Alias);
        await _owner.CallAsync(ProvidersMethods.ModelsRemove, new ModelRefParams("gw", "renamed"));
        Assert.Equal("b", Assert.Single((await _owner.CallAsync(ProvidersMethods.List, Unit.Value)).Aliases).Alias);

        // 删供应商:连密钥一起走
        await _owner.CallAsync(ProvidersMethods.Remove, new ProviderRefParams("gw"));
        Assert.Empty((await _owner.CallAsync(ProvidersMethods.List, Unit.Value)).Providers);
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => _owner.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams("gw"))));
    }

    // ───────────────────────── 帮助 ─────────────────────────

    private static Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string text, string model) =>
        client.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto(model, PermissionMode: "ask")));
}
