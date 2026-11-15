using Microsoft.Data.Sqlite;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>凭据 ref 的作用域换算与 NCK1 落库(移植自 2.x <c>config-profile</c> 的 <c>physicalCredentialRef</c> 部分与 <c>provider-file-repo.test.ts</c> 的「ref 只映射非托管供应商」)。</summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwvault-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly CredentialStore _store;

    public VaultTests()
    {
        _db = AgentDatabase.Open(_root);
        _store = new CredentialStore(_db, _root, NoLegacySecrets.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private byte[] Blob(string reference)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = "SELECT blob FROM credentials WHERE ref = $r";
        command.Parameters.AddWithValue("$r", reference);
        return (byte[])command.ExecuteScalar()!;
    }

    [Fact]
    public void Local_scope_uses_the_bare_ref_and_an_account_scope_prefixes_it_except_for_the_platform_keys()
    {
        Assert.Equal("provider:gw", CredentialScopes.Physical("provider:gw", "local"));
        Assert.Equal("acct-1\0provider:gw", CredentialScopes.Physical("provider:gw", "acct-1"));
        Assert.Equal("provider:gw#a1", CredentialScopes.Logical("acct-1\0provider:gw#a1", "acct-1"));
        // 平台账户自己的显式键永不加前缀:它们描述的是「这台机器当前登录了谁」。
        foreach (var global in new[] { "nextcowork:client-access-token", "nextcowork:client-refresh-token", "config-sync:x", "nextcowork:sync-y" })
        {
            Assert.Equal(global, CredentialScopes.Physical(global, "acct-1"));
        }

        // 含分隔符的行属于某个账户作用域,不是 local 的。
        Assert.Null(CredentialScopes.Logical("acct-1\0provider:gw", "local"));
        Assert.Null(CredentialScopes.Logical("nextcowork:client-access-token", "local"));
        Assert.Equal("provider:gw", CredentialScopes.Logical("provider:gw", "local"));
        Assert.Null(CredentialScopes.Logical("acct-2\0provider:gw", "acct-1"));
    }

    [Fact]
    public async Task Keys_are_stored_as_nck1_ciphertext_under_the_physical_ref()
    {
        var scope = "local";
        var vault = new ProviderCredentialVault(_store, scope: () => scope);
        await vault.SetAsync("provider:gw", "sk-secret", default);

        var blob = Blob("provider:gw");
        Assert.True(CredentialCrypto.IsProgramEncrypted(blob));
        Assert.DoesNotContain("sk-secret", System.Text.Encoding.UTF8.GetString(blob));
        Assert.Equal("sk-secret", await vault.GetAsync("provider:gw", default));

        // 同一个逻辑 ref 在另一个作用域是另一把密钥——不存在读串的可能。
        scope = "acct-1";
        Assert.Null(await vault.GetAsync("provider:gw", default));
        await vault.SetAsync("provider:gw", "sk-other", default);
        scope = "local";
        Assert.Equal("sk-secret", await vault.GetAsync("provider:gw", default));

        await vault.RemoveAsync("provider:gw", default);
        Assert.Null(await vault.GetAsync("provider:gw", default));
    }

    [Fact]
    public async Task A_key_an_external_tool_wrote_into_providers_json_is_readable_and_the_next_write_moves_it_to_nck1_and_clears_the_plaintext()
    {
        var file = new ProviderFileStore(Path.Combine(_root, ProviderFileStore.FileName));
        File.WriteAllText(file.Path, """{"providers":{"astra":{"baseUrl":"http://a/v1","protocol":"openai-chat","apiKey":"astra-key"}}}""");
        file.Load();
        var vault = new ProviderCredentialVault(_store, file);

        Assert.Equal("astra-key", await vault.GetAsync("provider:astra", default));
        await vault.SetAsync("provider:astra", "new-key", default);

        Assert.Equal("new-key", await vault.GetAsync("provider:astra", default));
        Assert.DoesNotContain("astra-key", File.ReadAllText(file.Path)); // 一个供应商任何时刻只有一个凭据来源
        Assert.DoesNotContain("new-key", File.ReadAllText(file.Path));
        Assert.Single(file.ListProviders()); // 供应商本身还在
    }

    [Fact]
    public async Task Account_secrets_and_the_managed_provider_never_fall_back_to_the_file()
    {
        // 账号密文 provider:<id>#<accountId> 共用 provider: 前缀但不归文件管。
        Assert.Null(ProviderRefs.FileOwnedProviderId("provider:codex#a1"));
        Assert.Null(ProviderRefs.FileOwnedProviderId("provider:nextcowork"));
        Assert.Null(ProviderRefs.FileOwnedProviderId("nextcowork:client-access-token"));
        Assert.Equal("gw", ProviderRefs.FileOwnedProviderId("provider:gw"));

        var file = new ProviderFileStore(Path.Combine(_root, ProviderFileStore.FileName));
        File.WriteAllText(file.Path, """{"providers":{"codex":{"baseUrl":"u","protocol":"openai-responses","apiKey":"file-key"}}}""");
        file.Load();
        var vault = new ProviderCredentialVault(_store, file);
        Assert.Null(await vault.GetAsync("provider:codex#a1", default));
        Assert.Equal("file-key", await vault.GetAsync("provider:codex", default));
    }

    [Fact]
    public void Account_refs_parse_back_with_the_first_hash_as_the_separator()
    {
        Assert.Equal("provider:codex#a1", ProviderRefs.ForAccount("codex", "a1"));
        Assert.Equal(("codex", "a1"), ProviderRefs.ParseAccountRef("provider:codex#a1"));
        Assert.Equal(("codex", "a#1"), ProviderRefs.ParseAccountRef("provider:codex#a#1"));
        Assert.Null(ProviderRefs.ParseAccountRef("provider:codex")); // 旧槽不是账号 ref:认错的话刷新广播会指向一个不存在的账号
        Assert.Null(ProviderRefs.ParseAccountRef("provider:codex#"));
        Assert.Null(ProviderRefs.ParseAccountRef("other:codex#a1"));
    }

    [Fact]
    public async Task The_router_refreshes_and_reads_credentials_through_the_vault_with_logical_refs()
    {
        // 路由 / 刷新器只认逻辑 ref;物理 ref(作用域前缀)是 vault 的事。
        var vault = new ProviderCredentialVault(_store, scope: () => "acct-1");
        var resolver = new CredentialResolver(vault, new OAuthRegistry([]), new FakeHttp(), TimeProvider.System);
        await vault.SetAsync("provider:gw#a1", CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", null, "u")), default);

        var credential = (OAuthCredential)(await resolver.ResolveAsync("provider:gw#a1", default))!;
        Assert.Equal("at", credential.AccessToken);
    }
}

/// <summary>选择解析、绑定转换和与路由的一致性。</summary>
public sealed class ResolverTests : IDisposable
{
    private readonly ProviderFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ModelResolution Resolve(string alias, string? pinned = null) => _fx.Resolver.Resolve(new ModelSelection(alias, pinned));

    // ── 选择 ──

    [Fact]
    public void An_unpinned_alias_goes_to_the_enabled_provider_with_the_best_priority()
    {
        _fx.AddProvider("slow", priority: 80);
        _fx.AddProvider("fast", priority: 10);
        _fx.AddProvider("off", priority: 1, enabled: false);
        foreach (var id in new[] { "slow", "fast", "off" })
        {
            _fx.Service.SetAliases(id, ["claude-sonnet-4-5"]);
        }

        var resolution = Resolve("claude-sonnet-4-5");
        Assert.True(resolution.Found);
        Assert.Equal("fast", resolution.Provider!.Id);
        Assert.Equal(new ModelRef(null, "claude-sonnet-4-5"), resolution.Ref);
    }

    [Fact]
    public void A_pinned_provider_is_a_hard_constraint_with_no_fallback_and_says_why_it_failed()
    {
        _fx.AddProvider("a", priority: 1);
        _fx.AddProvider("b", priority: 2);
        _fx.AddProvider("off", enabled: false);
        _fx.Service.SetAliases("a", ["m"]);
        _fx.Service.SetAliases("b", ["m", "only-b"]);
        _fx.Service.SetAliases("off", ["m"]);

        Assert.Equal("b", Resolve("m", "b").Provider!.Id); // 钉住 b,就是 b,哪怕 a 的优先级更高
        Assert.Equal(new ModelRef("b", "m"), Resolve("m", "b").Ref);
        Assert.Equal(ModelResolutionIssue.PinnedModelMissing, Resolve("only-b", "a").Issue); // 那家不再有这个别名:不换一家给你
        Assert.Equal(ModelResolutionIssue.PinnedProviderMissing, Resolve("m", "deleted").Issue);
        Assert.Equal(ModelResolutionIssue.PinnedProviderDisabled, Resolve("m", "off").Issue);
        Assert.Null(Resolve("only-b", "a").Binding);
    }

    [Fact]
    public void Unknown_aliases_and_aliases_with_no_enabled_provider_are_told_apart()
    {
        _fx.AddProvider("off", enabled: false);
        _fx.Service.SetAliases("off", ["m"]);
        _fx.AddProvider("on");
        _fx.Service.SetAliases("on", ["x"]);
        _fx.Service.UpdateAlias(new AliasUpdate("on", "x", Enabled: false));

        Assert.Equal(ModelResolutionIssue.UnknownAlias, Resolve("nope").Issue);
        Assert.Equal(ModelResolutionIssue.NoEnabledProvider, Resolve("m").Issue);
        Assert.Equal(ModelResolutionIssue.NoEnabledProvider, Resolve("x").Issue);
    }

    [Fact]
    public void Ties_on_priority_keep_the_input_order()
    {
        _fx.AddProvider("b", priority: 5);
        _fx.AddProvider("a", priority: 5);
        _fx.Service.SetAliases("a", ["m"]);
        _fx.Service.SetAliases("b", ["m"]);
        // providers.json 里 priority 相同时按 id 排:a 在 b 前。
        Assert.Equal("a", Resolve("m").Provider!.Id);
    }

    [Fact]
    public void What_the_resolver_picks_is_what_the_router_picks()
    {
        // 界面说 A、请求发给 B 且不报错——这个模块存在要消灭的那个 bug:两边必须落在同一条绑定上。
        _fx.AddProvider("a", priority: 30);
        _fx.AddProvider("b", priority: 20);
        _fx.AddProvider("c", priority: 10, enabled: false);
        foreach (var id in new[] { "a", "b", "c" })
        {
            _fx.Service.SetAliases(id, ["m"]);
        }

        var router = new LlmRouter(_fx.Resolver, new FakeHttp(), new CredentialResolver(_fx.Vault, new OAuthRegistry([]), new FakeHttp(), _fx.Clock), _fx.Clock);
        foreach (var pinned in new string?[] { null, "a", "b", "c", "zzz" })
        {
            var selection = new ModelSelection("m", pinned);
            Assert.Equal(_fx.Resolver.Resolve(selection).Binding, router.ResolveBinding(selection.ToRef()));
        }
    }

    [Fact]
    public void Failover_is_read_fresh_from_the_switch_every_time()
    {
        Assert.True(_fx.Resolver.FailoverEnabled);
        _fx.Failover = false;
        Assert.False(_fx.Resolver.FailoverEnabled);
    }

    // ── 绑定转换 ──

    [Fact]
    public void Provider_config_carries_the_derived_credential_ref_protocol_and_cache_ttl()
    {
        _fx.Service.Upsert(new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com", 7, true)
        {
            ProtocolOptions = new System.Text.Json.Nodes.JsonObject { ["anthropic"] = new System.Text.Json.Nodes.JsonObject { ["cacheTtl"] = "1h" } },
        });
        var config = Assert.Single(_fx.Resolver.Providers);
        Assert.Equal(new ProviderConfig("an", "AN", "https://an.example.com", WireProtocol.Anthropic, 7, true, "provider:an", AnthropicCacheTtl.OneHour), config);
    }

    [Fact]
    public void Context_window_and_capabilities_follow_the_catalog_and_user_edits_win()
    {
        _fx.AddProvider();
        _fx.Service.SetAliases("gw", ["gpt-4.1", "deepseek-chat", "private-model"]);

        var gpt = _fx.Resolver.Bindings.Single(b => b.Alias == "gpt-4.1");
        Assert.Equal((1_047_576, 32_768, true), (gpt.ContextWindow, gpt.MaxOutputTokens, gpt.SupportsImages));
        var deepseek = _fx.Resolver.Bindings.Single(b => b.Alias == "deepseek-chat");
        Assert.False(deepseek.SupportsImages); // 目录说它只收文本:图片会被换成占位文字,而不是发给一个会 400 的上游
        var unknown = _fx.Resolver.Bindings.Single(b => b.Alias == "private-model");
        Assert.Equal((200_000, 8192, true), (unknown.ContextWindow, unknown.MaxOutputTokens, unknown.SupportsImages)); // 目录不认识:保守缺省

        _fx.Service.UpdateAlias(new AliasUpdate("gw", "gpt-4.1", ContextWindow: 64_000));
        Assert.Equal(64_000, _fx.Resolver.Bindings.Single(b => b.Alias == "gpt-4.1").ContextWindow);
    }

    [Fact]
    public async Task A_catalog_update_reaches_bindings_that_follow_the_catalog_but_not_user_edited_fields()
    {
        _fx.AddProvider();
        _fx.Service.SetAliases("gw", ["gpt-5", "gpt-4o"]);
        _fx.Service.UpdateAlias(new AliasUpdate("gw", "gpt-4o", ContextWindow: 50_000));
        var before = _fx.Resolver.Bindings;

        _fx.Catalog.SetOverride(new ModelOverride("openai", "gpt-5", ContextWindow: 300_000));
        _fx.Catalog.SetOverride(new ModelOverride("openai", "gpt-4o", ContextWindow: 300_000));

        var after = _fx.Resolver.Bindings;
        Assert.NotSame(before, after); // 目录版本变了,缓存失效
        Assert.Equal(300_000, after.Single(b => b.Alias == "gpt-5").ContextWindow);
        Assert.Equal(50_000, after.Single(b => b.Alias == "gpt-4o").ContextWindow);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_binding_list_is_cached_until_the_providers_or_the_catalog_change()
    {
        _fx.AddProvider();
        _fx.Service.SetAliases("gw", ["gpt-5"]);
        var first = _fx.Resolver.Bindings;
        Assert.Same(first, _fx.Resolver.Bindings);
        _fx.Service.UpdateAlias(new AliasUpdate("gw", "gpt-5", ContextWindow: 1000));
        Assert.NotSame(first, _fx.Resolver.Bindings);
    }

    [Fact]
    public void An_output_limit_never_exceeds_the_context_window()
    {
        _fx.AddProvider();
        _fx.Service.SetAliases("gw", ["m"]);
        _fx.Service.UpdateAlias(new AliasUpdate("gw", "m", ContextWindow: 4000, MaxOutputTokens: 9000));
        Assert.Equal(4000, _fx.Resolver.Bindings[0].MaxOutputTokens);
    }

    // ── 思考声明 ──

    private static ThinkingConfig? ThinkingOf(ProviderFixture fx, string alias) => fx.Resolver.Bindings.Single(b => b.Alias == alias).Thinking;

    [Fact]
    public void Thinking_declarations_are_derived_conservatively_from_the_catalog()
    {
        _fx.AddProvider("an", WireProtocol.Anthropic);
        _fx.AddProvider("oa", WireProtocol.OpenAiResponses);
        _fx.AddProvider("ds", WireProtocol.OpenAiChat);
        _fx.Service.SetAliases("an", ["claude-sonnet-4-5", "claude-no-such"]);
        _fx.Service.SetAliases("oa", ["gpt-5", "gpt-4o"]);
        _fx.Service.SetAliases("ds", ["deepseek-reasoner", "deepseek-chat"]);

        // Anthropic 协议的推理模型:预算模式,默认不主动开启。
        var sonnet = ThinkingOf(_fx, "claude-sonnet-4-5")!;
        Assert.Equal((ThinkingMode.Budget, false), (sonnet.Mode, sonnet.DefaultEnabled));
        // 一手厂商的推理模型经 OpenAI 族协议:强度模式。
        Assert.Equal(ThinkingMode.Effort, ThinkingOf(_fx, "gpt-5")!.Mode);
        // 非推理模型:不支持,任何思考字段都会被剥掉。
        Assert.Equal(ThinkingMode.Unsupported, ThinkingOf(_fx, "gpt-4o")!.Mode);
        Assert.Equal(ThinkingMode.Unsupported, ThinkingOf(_fx, "deepseek-chat")!.Mode);
        // 方言 models.dev 不提供:推不出来就按不支持处理,宁可少一个开关也不发一个会被上游 400 的参数。
        Assert.Equal(ThinkingMode.Unsupported, ThinkingOf(_fx, "deepseek-reasoner")!.Mode);
        // 目录不认识:不碰请求体。
        Assert.Null(ThinkingOf(_fx, "claude-no-such"));
    }

    [Fact]
    public void Effort_levels_from_the_catalog_switch_the_model_to_effort_mode()
    {
        _fx.AddProvider("ds", WireProtocol.OpenAiChat);
        _fx.Catalog.SetOverride(new ModelOverride("deepseek", "deepseek-reasoner", Reasoning: true));
        _fx.Service.SetAliases("ds", ["deepseek-reasoner"]);
        Assert.Equal(ThinkingMode.Unsupported, ThinkingOf(_fx, "deepseek-reasoner")!.Mode);

        var info = new ModelInfo("m", "M", 1000, 100, new ModelCapabilities(true, true, false, false, ["text"], ["text"]), [ReasoningEffort.Low, ReasoningEffort.High], null, "x", false);
        var thinking = ModelBindingResolver.Derive(info, WireProtocol.OpenAiChat, info.ReasoningEfforts)!;
        Assert.Equal((ThinkingMode.Effort, ReasoningEffort.Low), (thinking.Mode, thinking.DefaultEffort)); // 没有 medium 就取第一个非 none
    }

    [Fact]
    public void A_2x_alias_with_its_own_thinking_config_keeps_it_and_the_request_adapter_is_read()
    {
        // 接管 2.x 的数据:老别名没有 catalogOverrides,存着的思考声明当年来自已核实的内置目录,一律照用。
        File.WriteAllText(_fx.File.Path, """
        {"providers":{"gw":{"baseUrl":"https://gw.example.com/v1","protocol":"openai-chat","models":{
          "kimi":{"upstreamModel":"kimi-k2","contextWindow":262144,"maxOutputTokens":16384,
                  "capabilities":{"tools":true,"vision":false,"thinking":true},
                  "thinkingConfig":{"mode":"effort","defaultEnabled":true,"defaultEffort":"high","effortMap":{"high":"HIGH"},"reasoningReplay":"text-required"},
                  "reasoningEfforts":["low","high"],
                  "requestAdapter":{"preset":"custom","patches":[{"op":"add","path":"/temperature","value":0.2},{"op":"bogus","path":"/x"}]}}}}}}
        """);
        _fx.File.Reload(notify: true);

        var binding = _fx.Resolver.Bindings.Single();
        Assert.Equal((262_144, 16_384, false), (binding.ContextWindow, binding.MaxOutputTokens, binding.SupportsImages));
        Assert.Equal((ThinkingMode.Effort, ReasoningEffort.High, ReasoningReplay.TextRequired), (binding.Thinking!.Mode, binding.Thinking.DefaultEffort, binding.Thinking.ReasoningReplay));
        Assert.Equal("HIGH", binding.Thinking.EffortMap![ReasoningEffort.High].GetString());
        Assert.Equal([ReasoningEffort.Low, ReasoningEffort.High], binding.ReasoningEfforts!.ToArray());
        Assert.Equal(AdapterPreset.Custom, binding.Preset);
        var patch = Assert.Single(binding.Patches!); // 认不出的 op 被丢掉
        Assert.Equal((PatchOp.Add, "/temperature"), (patch.Op, patch.Path));
        Assert.Equal(0.2, patch.Value!.Value.GetDouble());
    }

    [Fact]
    public void A_2x_alias_with_placeholder_defaults_is_treated_as_following_the_catalog()
    {
        File.WriteAllText(_fx.File.Path, """{"providers":{"gw":{"baseUrl":"https://gw.example.com/v1","protocol":"openai-chat","models":{"gpt-4.1":{"upstreamModel":"gpt-4.1","contextWindow":200000,"maxOutputTokens":8192,"capabilities":{"vision":true}}}}}}""");
        _fx.File.Reload(notify: true);
        var binding = _fx.Resolver.Bindings.Single();
        Assert.Equal((1_047_576, 32_768), (binding.ContextWindow, binding.MaxOutputTokens));
    }

    // ── 选择器 ──

    [Fact]
    public void The_picker_lists_everything_the_user_configured_including_disabled_ones_in_provider_then_priority_order()
    {
        _fx.AddProvider("b", priority: 2);
        _fx.AddProvider("a", priority: 1, enabled: false);
        _fx.Service.SetAliases("b", ["zeta", "alpha"]);
        _fx.Service.SetAliases("a", ["m"]);
        _fx.Service.SetAliases("b", ["zeta", "alpha", "claude-sonnet-4-5"]);
        _fx.Service.UpdateAlias(new AliasUpdate("b", "claude-sonnet-4-5", DisplayName: "Sonnet"));

        var options = _fx.Resolver.Options();
        Assert.Equal([("a", "m"), ("b", "zeta"), ("b", "alpha"), ("b", "claude-sonnet-4-5")], options.Select(o => (o.ProviderId, o.Alias)).ToArray());
        Assert.False(options[0].ProviderEnabled); // 停用的显示出来并标成不可用,而不是消失
        Assert.Equal("Sonnet", options[3].DisplayName);
        Assert.True(options[3].SupportsImages);
        Assert.All(options, o => Assert.True(o.IsChatModel));
    }

    [Fact]
    public void An_image_generation_model_is_not_a_chat_candidate()
    {
        _fx.AddProvider();
        _fx.Service.SetAliases("gw", ["img"]);
        _fx.Service.UpdateAlias(new AliasUpdate("gw", "img", Enabled: true));
        _fx.Catalog.SetOverride(new ModelOverride(null, "img", ContextWindow: 1000));
        // 手建的只有模态:modality=image 即可。
        var alias = _fx.Service.Aliases("gw")[0];
        _fx.File.PutAlias(alias with { Modality = "image" });
        Assert.False(_fx.Resolver.Options()[0].IsChatModel);
    }
}
