using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>托管的 NextCoWork 供应商:登录后同步平台模型,Claude 系强制 Anthropic 协议,令牌只在内存里,写入口全部拒绝。</summary>
public sealed class ManagedProviderTests : IDisposable
{
    private const string Models = """{"object":"list","data":[{"id":"claude-opus-4-5"},{"id":"gpt-5"},{"id":"anthropic/claude-sonnet-4"},{"id":"deepseek-chat"}]}""";

    private readonly ProviderFixture _fx = new();
    private readonly MemoryCredentialVault _inner = new();
    private string? _token = "tok-1";
    private ProviderService? _service;
    private ProviderConfigSource? _resolver;

    private ProviderService Service => _service ??= new ProviderService(
        _fx.File, new ManagedCredentialVault(_inner, () => _token), _fx.Accounts, _fx.Http, _fx.Catalog, _fx.Clock);

    private ProviderConfigSource Resolver => _resolver ??= new ProviderConfigSource(Service, _fx.Catalog);

    public void Dispose() => _fx.Dispose();

    // ── 协议规则(唯一一处) ──

    [Theory]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("Claude-Sonnet-4", true)]
    [InlineData("anthropic/claude-sonnet-4", true)]
    [InlineData("openrouter/anthropic/claude-3.5-haiku", true)]
    [InlineData("us.anthropic.claude-3-5-haiku-20241022-v1:0", true)]
    [InlineData("gpt-5", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData("gemini-3-pro", false)]
    [InlineData("not-claude", false)]
    [InlineData("", false)]
    public void Only_claude_models_are_forced_onto_the_anthropic_protocol(string model, bool claude)
    {
        Assert.Equal(claude, ManagedProvider.IsClaude(model));
        Assert.Equal(claude ? WireProtocol.Anthropic : null, ManagedProvider.ProtocolFor(model));
    }

    // ── 同步 ──

    [Fact]
    public async Task Syncing_adds_the_built_in_provider_first_and_forces_claude_models_onto_anthropic()
    {
        _fx.AddProvider("mine", priority: 50);
        _fx.Http.Ok(Models);

        var count = await Service.SyncManagedAsync();

        Assert.Equal(4, count);
        Assert.Equal(["nextcowork", "mine"], Service.Snapshot.Providers.Select(p => p.Id).ToArray());
        var provider = Service.Provider("nextcowork")!;
        Assert.Equal(("NextCoWork", WireProtocol.OpenAiResponses, "https://nextco.work/v1", ManagedProvider.Priority), (provider.Name, provider.Protocol, provider.BaseUrl, provider.Priority));

        Assert.Equal(WireProtocol.Anthropic, Resolver.Resolve(new ModelSelection("claude-opus-4-5")).Protocol);
        Assert.Equal(WireProtocol.Anthropic, Resolver.Resolve(new ModelSelection("anthropic/claude-sonnet-4", "nextcowork")).Protocol);
        Assert.Equal(WireProtocol.OpenAiResponses, Resolver.Resolve(new ModelSelection("gpt-5")).Protocol);
        Assert.Equal(WireProtocol.OpenAiResponses, Resolver.Resolve(new ModelSelection("deepseek-chat")).Protocol);
        Assert.All(Resolver.Options().Where(o => o.ProviderId == "nextcowork" && ManagedProvider.IsClaude(o.UpstreamModel)), o => Assert.Equal(WireProtocol.Anthropic, o.Protocol));
    }

    [Fact]
    public async Task The_model_list_is_fetched_from_the_platform_with_the_delegated_token()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var seen = _fx.Http.Requests.Single();
        Assert.Equal("https://nextco.work/v1/models", seen.Url);
        Assert.Equal("Bearer tok-1", seen.Header("authorization"));
    }

    [Fact]
    public async Task A_claude_request_goes_to_the_anthropic_messages_endpoint_under_the_same_base()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var resolved = Resolver.Resolve(new ModelSelection("claude-opus-4-5"));
        // 路由用的就是这一对 (base, 协议):/v1 去重,Anthropic 打到 /v1/messages,Responses 打到 /responses。
        Assert.Equal("https://nextco.work/v1/messages", WireProtocols.JoinUrl(resolved.Provider!.BaseUrl, resolved.Protocol!.Value.PathOf()));
        Assert.Equal("https://nextco.work/v1/responses", WireProtocols.JoinUrl(resolved.Provider.BaseUrl, Resolver.Resolve(new ModelSelection("gpt-5")).Protocol!.Value.PathOf()));
    }

    [Fact]
    public async Task Without_a_token_nothing_is_requested_and_no_provider_appears()
    {
        _token = null;
        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(ProviderErrorKeys.NoCredential, error.Key);
        Assert.Empty(_fx.Http.Requests);
        Assert.False(Service.HasManaged);
        Assert.Empty(Service.Providers);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_previous_models_so_the_picker_does_not_go_empty()
    {
        _fx.Http.Ok(Models).Status(System.Net.HttpStatusCode.InternalServerError, "boom");
        await Service.SyncManagedAsync();

        await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(4, Service.Aliases("nextcowork").Count);
    }

    [Fact]
    public async Task Signing_out_while_the_fetch_is_in_flight_does_not_bring_the_provider_back()
    {
        _fx.Http.Reply((_, _) =>
        {
            _token = null; // 请求发出去之后、结果回来之前:退出登录
            return Task.FromResult(FakeHttp.Json(System.Net.HttpStatusCode.OK, Models));
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(ProviderErrorKeys.NoCredential, error.Key);
        Assert.False(Service.HasManaged);
    }

    [Fact]
    public async Task Clearing_removes_the_provider_and_its_models_and_announces_it_once()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();
        var announced = 0;
        Service.Changed += _ => announced++;

        Service.ClearManaged();
        Service.ClearManaged(); // 已经没有了:不再广播

        Assert.Equal(1, announced);
        Assert.Empty(Service.Providers);
        Assert.Empty(Service.Aliases());
        Assert.False(Resolver.Resolve(new ModelSelection("claude-opus-4-5")).Found);
    }

    [Fact]
    public async Task A_resync_replaces_the_whole_list_with_what_the_platform_says()
    {
        _fx.Http.Ok(Models).Ok("""{"data":[{"id":"claude-haiku-4-5"}]}""");
        await Service.SyncManagedAsync();
        await Service.SyncManagedAsync();

        Assert.Equal(["claude-haiku-4-5"], Service.Aliases("nextcowork").Select(a => a.Alias).ToArray());
    }

    // ── 不可编辑、不泄露 ──

    [Fact]
    public async Task Every_write_path_refuses_the_managed_provider()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        static void Refused(Action action) => Assert.Equal(ProviderErrorKeys.ManagedProvider, Assert.Throws<ProviderException>(action).Key);
        Refused(() => Service.SetAliases("nextcowork", ["x"]));
        Refused(() => Service.RemoveAlias("nextcowork", "gpt-5"));
        Refused(() => Service.RenameAlias("nextcowork", "gpt-5", "g"));
        Refused(() => Service.UpdateAlias(new AliasUpdate("nextcowork", "claude-opus-4-5", ClearProtocolOverride: true)));
        Refused(() => Service.Remove("nextcowork"));
        Refused(() => Service.Upsert(new ProviderRecord("nextcowork", "Mine", WireProtocol.OpenAiChat, "https://evil.example.com")));
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync("nextcowork", "k"))).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.RemoveCredentialAsync("nextcowork"))).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.RevealCredentialAsync("nextcowork"))).Key);

        // 协议覆盖没被任何一条路径改掉。
        Assert.Equal(WireProtocol.Anthropic, Service.Aliases("nextcowork").Single(a => a.Alias == "claude-opus-4-5").ProtocolOverride);
        Assert.Empty(_fx.File.ListAliases());
    }

    [Fact]
    public async Task The_credential_summary_only_says_whether_a_token_is_there_never_its_characters()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var info = await Service.GetCredentialInfoAsync("nextcowork");
        Assert.True(info.HasKey);
        Assert.Null(info.Last4);

        _token = null;
        Assert.False((await Service.GetCredentialInfoAsync("nextcowork")).HasKey);
    }

    // ── 凭据来源 ──

    [Fact]
    public async Task The_managed_credential_ref_reads_the_token_and_never_writes_it_anywhere()
    {
        var vault = new ManagedCredentialVault(_inner, () => _token);
        var reference = ProviderRefs.ForProvider("nextcowork");

        Assert.Equal("tok-1", await vault.GetAsync(reference, default));
        await vault.SetAsync(reference, "other", default);
        await vault.RemoveAsync(reference, default);
        vault.Remove(reference);
        Assert.Empty(_inner.Values);

        _token = null;
        Assert.Null(await vault.GetAsync(reference, default));
    }

    [Fact]
    public async Task Other_refs_pass_through_to_the_real_vault()
    {
        var vault = new ManagedCredentialVault(_inner, () => _token);
        await vault.SetAsync("provider:gw", "sk-1", default);
        Assert.Equal("sk-1", await vault.GetAsync("provider:gw", default));
        vault.Remove("provider:gw");
        Assert.Null(await vault.GetAsync("provider:gw", default));
    }
}
