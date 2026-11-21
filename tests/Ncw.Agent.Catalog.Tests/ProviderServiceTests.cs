using System.Net;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>ipc/__tests__/provider-write.test.ts</c>(upsert / 凭据 / 删除 / 别名管理 / 协议选项)与 <c>provider-accounts.test.ts</c> 的存量迁移、<c>provider-auth</c> 的登录编排。</summary>
public sealed class ProviderServiceTests : IDisposable
{
    private readonly ProviderFixture _fx = new();

    private ProviderService Service => _fx.Service;

    public void Dispose() => _fx.Dispose();

    private static ProviderException Fails(Action action) => Assert.Throws<ProviderException>(action);

    // ── upsert ──

    [Fact]
    public void Only_http_and_https_addresses_are_accepted()
    {
        foreach (var bad in new[] { "file:///etc/passwd", "data:text/plain,hi", "ftp://x/y", "not a url", "", "  " })
        {
            Assert.Equal(ProviderErrorKeys.InvalidBaseUrl, Fails(() => Service.Upsert(new ProviderRecord("gw", "GW", WireProtocol.OpenAiChat, bad))).Key);
        }

        Assert.Empty(Service.Providers);
        Service.Upsert(new ProviderRecord("gw", "GW", WireProtocol.OpenAiChat, "  http://127.0.0.1:8080/v1  "));
        Assert.Equal("http://127.0.0.1:8080/v1", Service.Providers[0].BaseUrl);
    }

    [Fact]
    public void The_credential_ref_is_derived_from_the_id_and_the_id_is_constrained()
    {
        Assert.Equal("provider:gw", _fx.AddProvider().CredentialRef);
        Assert.Equal(ProviderErrorKeys.InvalidId, Fails(() => _fx.AddProvider("a/b")).Key); // 会让 providerId/alias 复合键歧义
        Assert.Equal(ProviderErrorKeys.InvalidId, Fails(() => _fx.AddProvider("has space")).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, Fails(() => _fx.AddProvider("nextcowork")).Key);
    }

    [Fact]
    public void The_name_is_trimmed_and_a_blank_name_is_rejected()
    {
        Assert.Equal("Mine", Service.Upsert(new ProviderRecord("gw", "  Mine  ", WireProtocol.Anthropic, "https://x")).Name);
        Assert.Equal(ProviderErrorKeys.InvalidName, Fails(() => Service.Upsert(new ProviderRecord("gw", "   ", WireProtocol.Anthropic, "https://x"))).Key);
    }

    [Fact]
    public async Task Updating_keeps_the_stored_key_because_the_ref_is_stable()
    {
        _fx.AddProvider();
        await Service.SetApiKeyAsync("gw", "sk-1");
        Service.Upsert(new ProviderRecord("gw", "Renamed", WireProtocol.OpenAiChat, "https://new.example.com/v1"));
        Assert.Equal("sk-1", _fx.Vault.Values["provider:gw"]);
    }

    [Fact]
    public void Anthropic_cache_ttl_defaults_to_5m_and_an_explicit_1h_is_kept()
    {
        _fx.AddProvider("an", WireProtocol.Anthropic);
        Assert.Equal(AnthropicCacheTtl.FiveMinutes, Service.Provider("an")!.CacheTtl);

        Service.Upsert(new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com") { ProtocolOptions = new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "1h" } } });
        Assert.Equal(AnthropicCacheTtl.OneHour, Service.Provider("an")!.CacheTtl);
    }

    [Fact]
    public void An_update_that_omits_protocol_options_keeps_them_and_a_nested_update_keeps_future_fields()
    {
        Service.Upsert(new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com")
        {
            ProtocolOptions = new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "1h" }, ["future"] = new JsonObject { ["transport"] = new JsonObject { ["region"] = "eu", ["timeout"] = 10 } } },
        });
        // 旧版客户端根本不知道这个字段:省略不是清空。
        Service.Upsert(new ProviderRecord("an", "AN2", WireProtocol.Anthropic, "https://an.example.com"));
        Assert.Equal(AnthropicCacheTtl.OneHour, Service.Provider("an")!.CacheTtl);

        // 局部更新只改 timeout,不能抹掉旁边的 region。
        Service.Upsert(new ProviderRecord("an", "AN2", WireProtocol.Anthropic, "https://an.example.com")
        {
            ProtocolOptions = new JsonObject { ["future"] = new JsonObject { ["transport"] = new JsonObject { ["timeout"] = 30 } } },
        });
        var options = Service.Provider("an")!.ProtocolOptions!;
        Assert.Equal("eu", options["future"]!["transport"]!["region"]!.GetValue<string>());
        Assert.Equal(30, options["future"]!["transport"]!["timeout"]!.GetValue<int>());
        Assert.Equal("1h", options["anthropic"]!["cacheTtl"]!.GetValue<string>());
    }

    [Fact]
    public void An_explicitly_invalid_cache_ttl_is_rejected_and_the_old_config_stays()
    {
        Service.Upsert(new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com") { ProtocolOptions = new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "1h" } } });
        var bad = new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com") { ProtocolOptions = new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "2h" } } };
        Assert.Equal(ProviderErrorKeys.InvalidOptions, Fails(() => Service.Upsert(bad)).Key);
        Assert.Equal(AnthropicCacheTtl.OneHour, Service.Provider("an")!.CacheTtl);

        // 旧值 off 按强制的 5m 使用。
        Service.Upsert(new ProviderRecord("an", "AN", WireProtocol.Anthropic, "https://an.example.com") { ProtocolOptions = new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "off" } } });
        Assert.Equal(AnthropicCacheTtl.FiveMinutes, Service.Provider("an")!.CacheTtl);
    }

    [Fact]
    public void Video_generation_from_the_caller_is_not_trusted_but_the_stored_one_is_preserved()
    {
        _fx.AddProvider();
        _fx.File.PutProvider(Service.Provider("gw")! with { VideoGeneration = new JsonObject { ["adapter"] = "x", ["baseUrl"] = "https://v.example.com" } });
        Service.Upsert(new ProviderRecord("gw", "GW", WireProtocol.OpenAiChat, "https://gw.example.com/v1") { VideoGeneration = new JsonObject { ["baseUrl"] = "https://evil.example.com" } });
        Assert.Equal("https://v.example.com", Service.Provider("gw")!.VideoGeneration!["baseUrl"]!.GetValue<string>());
    }

    [Fact]
    public void The_changed_event_carries_the_full_snapshot_with_a_rising_version()
    {
        var seen = new List<ProviderSnapshot>();
        Service.Changed += seen.Add;
        _fx.AddProvider();
        _fx.AddProvider("two");
        Assert.Equal(2, seen.Count);
        Assert.True(seen[1].Version > seen[0].Version);
        Assert.Equal(["gw", "two"], seen[1].Providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void An_external_edit_of_providers_json_is_announced_after_the_hot_reload()
    {
        _fx.AddProvider();
        var seen = new List<ProviderSnapshot>();
        Service.Changed += seen.Add;
        var doc = (JsonObject)JsonNode.Parse(_fx.ReadFile())!;
        ((JsonObject)doc["providers"]!)["ext"] = new JsonObject { ["baseUrl"] = "http://e/v1", ["protocol"] = "openai-chat" };
        File.WriteAllText(_fx.File.Path, doc.ToJsonString());
        _fx.File.Reload(notify: true);

        Assert.Single(seen);
        Assert.Equal(["ext", "gw"], seen[0].Providers.Select(p => p.Id).Order().ToArray());
    }

    // ── 凭据 ──

    [Fact]
    public async Task Keys_are_stored_in_the_vault_never_in_providers_json_and_the_summary_has_no_plaintext()
    {
        _fx.AddProvider();
        var info = await Service.SetApiKeyAsync("gw", "  sk-secret-1234  ");

        Assert.Equal(new CredentialInfo(true, "1234"), info);
        Assert.Equal("sk-secret-1234", _fx.Vault.Values["provider:gw"]);
        Assert.DoesNotContain("sk-secret", _fx.ReadFile());
        Assert.Equal(info, await Service.GetCredentialInfoAsync("gw"));
        Assert.DoesNotContain("secret", info.ToString());
    }

    [Fact]
    public async Task A_blank_key_and_the_managed_provider_are_rejected_and_unknown_providers_are_not_found()
    {
        _fx.AddProvider();
        Assert.Equal(ProviderErrorKeys.EmptyKey, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync("gw", "   "))).Key);
        Assert.Equal(ProviderErrorKeys.UnknownProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync("ghost", "k"))).Key);
        Assert.Equal(ProviderErrorKeys.UnknownProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync(ProviderRefs.ManagedProviderId, "k"))).Key);
    }

    [Fact]
    public async Task Plaintext_comes_back_only_from_the_explicit_reveal_and_only_for_a_stored_credential()
    {
        _fx.AddProvider();
        Assert.Equal(ProviderErrorKeys.NoCredential, (await Assert.ThrowsAsync<ProviderException>(() => Service.RevealCredentialAsync("gw"))).Key);

        await Service.SetApiKeyAsync("gw", "sk-1");
        Assert.Equal(new RevealedApiKey("sk-1"), await Service.RevealCredentialAsync("gw"));

        _fx.Vault.Seed("provider:gw", CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", null, "acc")));
        Assert.Equal(new RevealedOAuth("at", "rt"), await Service.RevealCredentialAsync("gw"));
    }

    [Fact]
    public async Task The_summary_of_an_oauth_credential_has_the_account_but_no_tokens_and_expiry_is_computed_here()
    {
        _fx.AddProvider("codex");
        var now = _fx.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        _fx.Vault.Seed("provider:codex", CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", now - 1, "acc", "me@example.com", "plus")));
        var info = await Service.GetCredentialInfoAsync("codex");
        Assert.True(info.HasKey);
        Assert.Null(info.Last4);
        Assert.Equal(new CredentialAuthInfo("chatgpt", "acc", "me@example.com", "plus", now - 1, true, false), info.Auth);

        // 过期时间未知落到「先当它有效」,不是已过期。
        _fx.Vault.Seed("provider:codex", CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", null, "acc")));
        Assert.False((await Service.GetCredentialInfoAsync("codex")).Auth!.Expired);
    }

    [Fact]
    public async Task A_signature_credential_shows_the_access_key_id_and_never_the_secret_tail()
    {
        _fx.AddProvider("tc");
        _fx.Vault.Seed("provider:tc", CredentialFormat.Serialize(new SignatureCredential(SignatureScheme.TencentTc3, "AKID123", "supersecret9999")));
        var info = await Service.GetCredentialInfoAsync("tc");
        Assert.Equal(new CredentialInfo(true, null, null, SignatureScheme.TencentTc3, "AKID123"), info);
    }

    [Fact]
    public async Task Removing_a_provider_removes_its_aliases_accounts_and_key_so_a_new_provider_with_the_same_id_starts_clean()
    {
        _fx.AddProvider("codex");
        Service.SetAliases("codex", ["m1", "m2"]);
        await Service.SetApiKeyAsync("codex", "sk-1");
        _fx.Accounts.Put(new ProviderAccountRow("a1", "codex", "chatgpt", null, 0, true, true, false, null, null, 1, 1));
        _fx.Vault.Seed(ProviderRefs.ForAccount("codex", "a1"), "tok");

        Service.Remove("codex");

        Assert.Empty(Service.Providers);
        Assert.Empty(Service.Aliases());
        Assert.Empty(_fx.Vault.Values);
        Assert.Empty(_fx.Accounts.List("codex"));
        Service.Remove("codex"); // 删一个不存在的 id 是无操作
        Assert.Equal(ProviderErrorKeys.ManagedProvider, Fails(() => Service.Remove(ProviderRefs.ManagedProviderId)).Key);
    }

    // ── 别名 ──

    [Fact]
    public void Replacing_the_alias_list_keeps_existing_rows_by_upstream_model_and_removes_the_rest()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["a", "b", "c"]);
        Service.UpdateAlias(new AliasUpdate("gw", "b", ContextWindow: 12_345)); // 用户调过
        var kept = Service.SetAliases("gw", ["c", "b", "d", " d ", ""]);

        Assert.Equal(["c", "b", "d"], kept.OrderBy(a => a.Priority).Select(a => a.Alias).ToArray());
        Assert.Equal(12_345, kept.Single(a => a.Alias == "b").ContextWindow); // 重拉一次不重置用户配置
        Assert.Equal([0, 1, 2], kept.OrderBy(a => a.Priority).Select(a => a.Priority!.Value).ToArray());
        Assert.DoesNotContain(kept, a => a.Alias == "a");
    }

    [Fact]
    public void New_aliases_follow_the_catalog_by_default()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["claude-sonnet-4-5"]);
        var alias = Service.Aliases("gw")[0];
        // catalogOverrides = []:全部字段跟随目录,models.dev 更新后窗口和能力自动跟上。
        Assert.Empty((JsonArray)alias.Extra["catalogOverrides"]!);
        Assert.Equal(200_000, _fx.Resolver.Bindings[0].ContextWindow);
        Assert.Equal(64_000, _fx.Resolver.Bindings[0].MaxOutputTokens);
    }

    [Fact]
    public void A_generation_model_the_catalog_knows_is_tagged_with_its_modality()
    {
        _fx.AddProvider();
        _fx.Catalog.SetOverride(new ModelOverride(null, "gpt-image-x", ContextWindow: 1000)); // 保证有这个 id 的覆盖不影响
        Service.SetAliases("gw", ["gpt-5", "unknown-model"]);
        Assert.All(Service.Aliases("gw"), a => Assert.Null(a.Modality));
    }

    [Fact]
    public void Renaming_keeps_the_binding_and_rejects_empty_and_duplicate_names()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["a", "b"]);
        Service.UpdateAlias(new AliasUpdate("gw", "a", MaxOutputTokens: 999));

        var renamed = Service.RenameAlias("gw", "a", "  fancy  ");
        Assert.Equal(("fancy", "a", 999), (renamed.Alias, renamed.UpstreamModel, renamed.MaxOutputTokens));
        Assert.Equal(["b", "fancy"], Service.Aliases("gw").Select(a => a.Alias).Order().ToArray());
        Assert.Equal(ProviderErrorKeys.AliasExists, Fails(() => Service.RenameAlias("gw", "fancy", "b")).Key);
        Assert.Equal(ProviderErrorKeys.UnknownAlias, Fails(() => Service.RenameAlias("gw", "fancy", "  ")).Key);
        Assert.Equal(ProviderErrorKeys.UnknownAlias, Fails(() => Service.RenameAlias("gw", "ghost", "x")).Key);
        Assert.Equal("fancy", Service.RenameAlias("gw", "fancy", "fancy").Alias);
    }

    [Fact]
    public void Editing_an_alias_records_the_edited_fields_as_owned_by_the_provider()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["claude-sonnet-4-5"]);
        var edited = Service.UpdateAlias(new AliasUpdate("gw", "claude-sonnet-4-5", DisplayName: "My Sonnet", ContextWindow: 100_000, Vision: false, Enabled: true));

        Assert.Equal(["capabilities.vision", "contextWindow", "displayName"], ModelBindingResolver.OverridesOf(edited)!.Order().ToArray());
        var binding = _fx.Resolver.Bindings[0];
        Assert.Equal(100_000, binding.ContextWindow);   // 用户设的
        Assert.Equal(64_000, binding.MaxOutputTokens);  // 没设的跟随目录
        Assert.False(binding.SupportsImages);
        Assert.Equal(ProviderErrorKeys.UnknownAlias, Fails(() => Service.UpdateAlias(new AliasUpdate("gw", "ghost"))).Key);
    }

    [Fact]
    public void The_protocol_override_can_be_set_and_explicitly_cleared()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["m"]);
        Assert.Equal(Ncw.Agent.Llm.Media.ProviderProtocol.Anthropic, Service.UpdateAlias(new AliasUpdate("gw", "m", ProtocolOverride: Ncw.Agent.Llm.Media.ProviderProtocol.Anthropic)).ProtocolOverride);
        Assert.Equal(WireProtocol.Anthropic, _fx.Resolver.Bindings[0].ProtocolOverride);
        Assert.Null(Service.UpdateAlias(new AliasUpdate("gw", "m", ClearProtocolOverride: true)).ProtocolOverride);
        // 只改别的字段不能顺手清掉它。
        Service.UpdateAlias(new AliasUpdate("gw", "m", ProtocolOverride: Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiResponses));
        Assert.Equal(Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiResponses, Service.UpdateAlias(new AliasUpdate("gw", "m", Priority: 5)).ProtocolOverride);
    }

    [Fact]
    public void Removing_an_alias_and_the_alias_order_survive_a_reload_from_disk()
    {
        _fx.AddProvider();
        Service.SetAliases("gw", ["z", "a", "m"]);
        Service.RemoveAlias("gw", "a");
        var reloaded = new ProviderFileStore(_fx.File.Path);
        reloaded.Load();
        // 保存的优先级顺序不会被字母序覆盖。
        Assert.Equal(["z", "m"], reloaded.ListAliases().OrderBy(a => a.Priority).Select(a => a.Alias).ToArray());
    }

    // ── 拉取模型列表 ──

    [Fact]
    public async Task Fetching_models_uses_the_stored_key_with_the_protocols_auth_shape_and_returns_the_list()
    {
        _fx.AddProvider("an", WireProtocol.Anthropic, baseUrl: "https://api.anthropic.example.com");
        await Service.SetApiKeyAsync("an", "sk-ant");
        _fx.Http.Ok("""{"data":[{"id":"claude-a","display_name":"Claude A"},{"id":"claude-b"}],"has_more":false}""");

        var models = await Service.FetchModelsAsync("an");

        Assert.Equal([new FetchedModel("claude-a", "Claude A"), new FetchedModel("claude-b")], models.ToArray());
        var seen = _fx.Http.Requests.Single();
        Assert.StartsWith("https://api.anthropic.example.com/v1/models?limit=", seen.Url);
        Assert.Equal("sk-ant", seen.Header("x-api-key"));
        Assert.Equal("NextCoWork/3.x", seen.Header("user-agent"));
    }

    [Fact]
    public async Task Fetching_without_a_key_sends_no_auth_header_so_public_catalogs_work()
    {
        _fx.AddProvider();
        _fx.Http.Ok("""{"data":[{"id":"m"}]}""");
        await Service.FetchModelsAsync("gw");
        Assert.Null(_fx.Http.Requests[0].Header("authorization"));
    }

    [Fact]
    public async Task Fetching_uses_the_current_accounts_credential_when_the_provider_has_accounts()
    {
        _fx.AddProvider("codex", WireProtocol.OpenAiResponses);
        _fx.Accounts.Put(new ProviderAccountRow("a1", "codex", "chatgpt", null, 0, true, true, false, null, null, 1, 1));
        _fx.Vault.Seed(ProviderRefs.ForAccount("codex", "a1"), CredentialFormat.Serialize(new OAuthCredential("chatgpt", "account-token", "rt", null, "u")));
        _fx.Vault.Seed("provider:codex", "stale-mirror");
        _fx.Http.Ok("""{"data":[{"id":"m"}]}""");
        await Service.FetchModelsAsync("codex");
        Assert.Equal("Bearer account-token", _fx.Http.Requests[0].Header("authorization"));
    }

    [Theory]
    [InlineData(401, ProviderErrorKeys.FetchRejected)]
    [InlineData(404, ProviderErrorKeys.FetchNotFound)]
    [InlineData(500, ProviderErrorKeys.FetchFailed)]
    public async Task Upstream_failures_become_structured_errors(int status, string key)
    {
        _fx.AddProvider();
        _fx.Http.Status((HttpStatusCode)status, """{"error":{"message":"nope"}}""");
        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.FetchModelsAsync("gw"));
        Assert.Equal(key, error.Key);
        Assert.Equal(status.ToString(), error.Parameters["status"]);
        Assert.Equal("nope", error.Parameters["detail"]);
    }

    [Fact]
    public async Task A_200_with_nothing_recognisable_is_reported_as_unparseable_not_as_no_models()
    {
        _fx.AddProvider();
        _fx.Http.Ok("""{"unexpected":true}""");
        Assert.Equal(ProviderErrorKeys.FetchEmpty, (await Assert.ThrowsAsync<ProviderException>(() => Service.FetchModelsAsync("gw"))).Key);
    }

    [Fact]
    public async Task An_address_that_never_answers_times_out_and_a_refused_connection_is_reported_without_secrets()
    {
        using var fx = new ProviderFixture(new ProviderServiceOptions { FetchTimeout = TimeSpan.FromMilliseconds(80) });
        fx.AddProvider();
        // 每次拉取会试两族列表端点(这家协议那一族没拉到才试另一族),所以每种结局各排两次
        var leaked = new HttpRequestException("failed: -----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----");
        fx.Http.Hang().Hang().Throw(leaked).Throw(leaked);

        Assert.Equal(ProviderErrorKeys.FetchTimeout, (await Assert.ThrowsAsync<ProviderException>(() => fx.Service.FetchModelsAsync("gw"))).Key);
        var refused = await Assert.ThrowsAsync<ProviderException>(() => fx.Service.FetchModelsAsync("gw"));
        Assert.Equal(ProviderErrorKeys.FetchFailed, refused.Key);
        Assert.DoesNotContain("PRIVATE KEY", refused.Message);
    }

    [Fact]
    public async Task Cancelling_a_fetch_is_a_cancellation_not_a_timeout()
    {
        _fx.AddProvider();
        _fx.Http.Hang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.FetchModelsAsync("gw", cts.Token));
    }

    [Fact]
    public async Task When_the_protocols_own_list_endpoint_fails_the_other_familys_is_tried()
    {
        // Anthropic 兼容中转常常没有 /v1/models,但 OpenAI 族的 /models 在
        _fx.AddProvider("relay", WireProtocol.Anthropic, baseUrl: "https://relay.example.com/anthropic");
        await Service.SetApiKeyAsync("relay", "sk-relay");
        _fx.Http.Status(HttpStatusCode.NotFound).Ok("""{"data":[{"id":"claude-a"},{"id":"glm-4"}]}""");

        var models = await Service.FetchModelsAsync("relay");

        Assert.Equal(["claude-a", "glm-4"], models.Select(m => m.Id).ToArray());
        Assert.Equal(2, _fx.Http.Requests.Count);
        Assert.StartsWith("https://relay.example.com/anthropic/v1/models", _fx.Http.Requests[0].Url);
        Assert.Equal("sk-relay", _fx.Http.Requests[0].Header("x-api-key"));
        Assert.Equal("https://relay.example.com/anthropic/models", _fx.Http.Requests[1].Url);
        Assert.Equal("Bearer sk-relay", _fx.Http.Requests[1].Header("authorization"));
    }

    [Fact]
    public async Task When_both_families_fail_the_error_is_the_protocols_own()
    {
        _fx.AddProvider("relay", WireProtocol.Anthropic, baseUrl: "https://relay.example.com/anthropic");
        _fx.Http.Status(HttpStatusCode.Unauthorized, """{"error":{"message":"who are you"}}""").Status(HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.FetchModelsAsync("relay"));

        Assert.Equal(ProviderErrorKeys.FetchRejected, error.Key);
        Assert.Equal("who are you", error.Parameters["detail"]);
    }

    // ── 调试(providers.debug)──

    [Fact]
    public async Task Debug_candidates_try_both_families_merge_them_and_report_every_attempt()
    {
        _fx.AddProvider();
        await Service.SetApiKeyAsync("gw", "sk-gw-1234567890");
        _fx.Http.Fallback = (request, _) => Task.FromResult(request.Headers.Contains("x-api-key") // 两族的 URL 在 …/v1 地址上是同一个,按鉴权头分
            ? FakeHttp.Json(HttpStatusCode.OK, """{"data":[{"id":"claude-a","display_name":"Claude A"},{"id":"shared"}]}""")
            : FakeHttp.Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-x"},{"id":"shared"}]}"""));

        var listing = await Service.ListModelsAsync(new DebugTarget("gw"));

        Assert.Equal(2, listing.Attempts.Count);
        Assert.All(listing.Attempts, a => Assert.True(a.Ok));
        // 先这家协议那一族(OpenAI 族),再另一族;两族都报的模型带两个族
        Assert.Equal(["gpt-x", "shared", "claude-a"], listing.Models.Select(m => m.Id).ToArray());
        Assert.Equal([WireProtocol.OpenAiChat, WireProtocol.Anthropic], listing.Models.Single(m => m.Id == "shared").Families.ToArray());
        Assert.Equal("Claude A", listing.Models.Single(m => m.Id == "claude-a").DisplayName);
    }

    [Fact]
    public async Task A_draft_target_uses_its_own_address_and_key_and_failures_do_not_echo_the_key()
    {
        _fx.Http.Fallback = (_, _) => Task.FromResult(FakeHttp.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key sk-draft-abcdef123456"}}"""));

        var listing = await Service.ListModelsAsync(new DebugTarget(null, " https://draft.example.com/v1 ", Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiChat, "sk-draft-abcdef123456"));

        Assert.Empty(listing.Models);
        Assert.All(listing.Attempts, a => Assert.Equal(ProviderErrorKeys.FetchRejected, a.ErrorKey));
        Assert.All(listing.Attempts, a => Assert.DoesNotContain("sk-draft", a.Detail));
        Assert.Contains(_fx.Http.Requests, r => r.Url == "https://draft.example.com/v1/models" && r.Header("authorization") == "Bearer sk-draft-abcdef123456");
        Assert.Empty(Service.Providers); // 草稿不落盘
        Assert.Equal(ProviderErrorKeys.InvalidBaseUrl, (await Assert.ThrowsAsync<ProviderException>(() => Service.ListModelsAsync(new DebugTarget(null)))).Key);
        Assert.Equal(ProviderErrorKeys.InvalidBaseUrl, (await Assert.ThrowsAsync<ProviderException>(() => Service.ListModelsAsync(new DebugTarget(null, "ftp://x")))).Key);
    }

    private static HttpResponseMessage ChatSse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"}}]}\n\n"
            + "data: {\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n",
            System.Text.Encoding.UTF8,
            "text/event-stream"),
    };

    [Fact]
    public async Task Debugging_a_saved_provider_sends_one_request_with_the_chosen_protocol_and_the_aliases_upstream_model()
    {
        _fx.AddProvider("gw", WireProtocol.Anthropic, baseUrl: "https://gw.example.com/v1");
        await Service.SetApiKeyAsync("gw", "sk-saved-1234567890");
        Service.SetAliases("gw", ["upstream-model"]);
        Service.RenameAlias("gw", "upstream-model", "nice");
        _fx.Http.Reply((_, _) => Task.FromResult(ChatSse("OK")));
        var events = new List<Ncw.Agent.Llm.Diagnostics.ProbeEvent>();

        var result = await Service.DebugAsync(
            new DebugRequest(new DebugTarget("gw"), Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiChat, "nice", "", 32, Ncw.Agent.Llm.ThinkingLevel.Auto, false), events.Add);

        Assert.True(result.Ok, result.Failure?.Message);
        Assert.Equal("OK", result.Text);
        var seen = Assert.Single(_fx.Http.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("https://gw.example.com/v1/chat/completions", seen.Url);
        Assert.Equal("Bearer sk-saved-1234567890", seen.Header("authorization"));
        Assert.Contains("\"upstream-model\"", result.RequestBody); // 别名 → 上游模型名
        Assert.DoesNotContain("sk-saved", string.Join("\n", result.RequestHeaders.Select(h => h.Value)));
        Assert.Equal(result.Events, events);
    }

    [Fact]
    public async Task Debugging_needs_a_model()
    {
        _fx.AddProvider();
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            Service.DebugAsync(new DebugRequest(new DebugTarget("gw"), Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiChat, "  ", "", 32, Ncw.Agent.Llm.ThinkingLevel.Auto, false)));
        Assert.Equal(ProviderErrorKeys.DebugModelRequired, error.Key);
        Assert.Empty(_fx.Http.Requests);
    }

    // ── 连通性测试 ──

    [Fact]
    public async Task The_connectivity_test_probes_the_list_endpoint_and_classifies_the_outcome()
    {
        _fx.AddProvider();
        await Service.SetApiKeyAsync("gw", "k");
        _fx.Http.Ok("""{"data":[]}""").Status(HttpStatusCode.Unauthorized, "{}").Status(HttpStatusCode.NotFound).Throw(new HttpRequestException("refused"));

        var ok = await Service.TestAsync("gw");
        Assert.True(ok.Ok);
        Assert.NotNull(ok.LatencyMs);
        Assert.Equal(ProviderErrorKeys.FetchRejected, (await Service.TestAsync("gw")).ErrorKey);
        Assert.Equal(ProviderErrorKeys.FetchNotFound, (await Service.TestAsync("gw")).ErrorKey);
        var down = await Service.TestAsync("gw");
        Assert.False(down.Ok);
        Assert.Null(down.Status);
        Assert.Equal(ProviderErrorKeys.FetchFailed, down.ErrorKey);
    }

    // ── 存量迁移 ──

    [Fact]
    public async Task A_pre_upgrade_oauth_login_becomes_account_one_and_the_old_slot_is_kept()
    {
        _fx.AddProvider("codex");
        var raw = CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", null, "u", NeedsReauth: true));
        _fx.Vault.Seed("provider:codex", raw);

        Assert.Equal(1, await Service.EnsureAccountsSeededAsync());

        var row = Assert.Single(_fx.Accounts.List("codex"));
        Assert.Equal(("chatgpt", 0, true, true, true), (row.Issuer, row.Order, row.Enabled, row.Current, row.NeedsReauth)); // needsReauth 一起带过来
        Assert.Equal(raw, _fx.Vault.Values[ProviderRefs.ForAccount("codex", row.Id)]);
        Assert.Equal(raw, _fx.Vault.Values["provider:codex"]); // 回退旧版本仍是已登录
        Assert.Equal(0, await Service.EnsureAccountsSeededAsync()); // 幂等
    }

    [Fact]
    public async Task Api_keys_unsupported_providers_and_logged_out_providers_are_left_alone()
    {
        _fx.AddProvider("codex");     // 支持账号登录,但槽里是 API Key
        _fx.AddProvider("zhipu-coding");
        _fx.AddProvider("openai");    // 不支持账号登录
        _fx.AddProvider("kimi-coding"); // 支持,没登录过
        _fx.Vault.Seed("provider:codex", "sk-plain-key");
        _fx.Vault.Seed("provider:openai", CredentialFormat.Serialize(new OAuthCredential("chatgpt", "at", "rt", null, "u")));

        Assert.Equal(0, await Service.EnsureAccountsSeededAsync());
        Assert.Empty(_fx.Accounts.List());
    }

    // ── 按类型过滤模型与媒体供应商路由隔离 ──

    [Fact]
    public async Task Fetching_models_filters_upstream_models_by_provider_kind_and_returns_empty_when_only_other_kinds_exist()
    {
        const string MixedUpstream = """
            {"data":[
              {"id":"gpt-5.4"},
              {"id":"doubao-seedream-4-5-251128"},
              {"id":"doubao-seedance-1-5-pro-251215"},
              {"id":"whisper-large-v3"},
              {"id":"text-embedding-3-large"}
            ]}
            """;

        Service.Upsert(new ProviderRecord("p-text", "Text", Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiChat, "https://x.example/v1", Kind: Ncw.Agent.Llm.Media.ProviderKind.Text));
        Service.Upsert(new ProviderRecord("p-img", "Image", Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages, "https://ark.cn-beijing.volces.com/api/v3", Kind: Ncw.Agent.Llm.Media.ProviderKind.Image));
        Service.Upsert(new ProviderRecord("p-vid", "Video", Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineVideos, "https://ark.cn-beijing.volces.com/api/v3", Kind: Ncw.Agent.Llm.Media.ProviderKind.Video));
        Service.Upsert(new ProviderRecord("p-aud", "Audio", Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiAudio, "https://x.example/v1", Kind: Ncw.Agent.Llm.Media.ProviderKind.Audio));

        _fx.Http.Fallback = (_, _) => Task.FromResult(FakeHttp.Json(HttpStatusCode.OK, MixedUpstream));

        Assert.Equal(["gpt-5.4"], (await Service.FetchModelsAsync("p-text")).Select(m => m.Id).ToArray());
        Assert.Equal(["doubao-seedream-4-5-251128"], (await Service.FetchModelsAsync("p-img")).Select(m => m.Id).ToArray());
        Assert.Equal(["doubao-seedance-1-5-pro-251215"], (await Service.FetchModelsAsync("p-vid")).Select(m => m.Id).ToArray());
        Assert.Equal(["whisper-large-v3"], (await Service.FetchModelsAsync("p-aud")).Select(m => m.Id).ToArray());

        // 上游合法返回了模型但全是文本模型时,图片供应商过滤后得空数组而非抛 FetchEmpty
        _fx.Http.Fallback = (_, _) => Task.FromResult(FakeHttp.Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-5.4"}]}"""));
        Assert.Empty(await Service.FetchModelsAsync("p-img"));
    }

    [Fact]
    public void Media_providers_stay_out_of_text_router_appear_as_non_chat_options_and_resolve_via_media_resolver()
    {
        Service.Upsert(new ProviderRecord("chat-gw", "Chat GW", Ncw.Agent.Llm.Media.ProviderProtocol.OpenAiChat, "https://chat.example/v1", Priority: 10, Kind: Ncw.Agent.Llm.Media.ProviderKind.Text));
        Service.SetAliases("chat-gw", ["gpt-5.4"]);
        Service.Upsert(new ProviderRecord("ark-img", "火山图片", Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages, "https://ark.cn-beijing.volces.com/api/v3", Priority: 20, Kind: Ncw.Agent.Llm.Media.ProviderKind.Image));
        Service.SetAliases("ark-img", ["doubao-seedream-4-5-251128"]);

        Assert.Equal(["chat-gw"], _fx.Resolver.Providers.Select(p => p.Id).ToArray());
        Assert.Equal(["gpt-5.4"], _fx.Resolver.Bindings.Select(b => b.Alias).ToArray());
        Assert.False(_fx.Resolver.Resolve(new ModelSelection("doubao-seedream-4-5-251128")).Found);

        var imgOption = _fx.Resolver.Options().Single(o => o.ProviderId == "ark-img");
        Assert.False(imgOption.IsChatModel);
        Assert.Equal(Ncw.Agent.Llm.Media.ProviderKind.Image, imgOption.Kind);
        Assert.Equal(Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages, imgOption.Protocol);

        var media = _fx.Resolver.ResolveMedia(new ModelSelection("doubao-seedream-4-5-251128"), Ncw.Agent.Llm.Media.ProviderKind.Image);
        Assert.NotNull(media);
        Assert.Equal(("ark-img", Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages), (media.Provider.Id, media.Protocol));
    }
}
