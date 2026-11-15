using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>db/__tests__/provider-file.test.ts</c>(读、外部工具契约、写、监听)。<c>importLegacy</c> 一组不移植:见 docs/parity.md。</summary>
public sealed class ProviderFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncwprov-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly ProviderFileStore _store;

    public ProviderFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, ProviderFileStore.FileName);
        _store = new ProviderFileStore(_path);
    }

    public void Dispose()
    {
        _store.StopWatching();
        Directory.Delete(_dir, recursive: true);
    }

    private static ProviderRecord Provider(string id = "gw", string name = "Gateway", JsonObject? options = null) =>
        new(id, name, WireProtocol.OpenAiChat, "http://127.0.0.1:8080/v1") { ProtocolOptions = options };

    private static ModelAliasRecord Alias(string alias = "gpt-4o", string providerId = "gw") =>
        new(alias, providerId, alias) { ContextWindow = 128_000, MaxOutputTokens = 4096 };

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_path))!;

    private JsonObject Entry(string id) => (JsonObject)Read()["providers"]![id]!;

    // ── 读 ──

    [Fact]
    public void A_missing_file_is_empty_and_is_not_created()
    {
        Assert.True(_store.Load());
        Assert.Empty(_store.ListProviders());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_minimal_hand_written_entry_gets_defaults()
    {
        File.WriteAllText(_path, """{"providers":{"astra":{"baseUrl":"http://x/v1","protocol":"openai-chat","apiKey":" sk-1 ","models":{"gpt-x":{}}}}}""");
        _store.Load();

        var provider = Assert.Single(_store.ListProviders());
        Assert.Equal(("astra", "astra", 50, true, "provider:astra"), (provider.Id, provider.Name, provider.Priority, provider.Enabled, provider.CredentialRef));
        Assert.Equal("sk-1", _store.GetCredential("astra"));
        var alias = Assert.Single(_store.ListAliases());
        Assert.Equal(("gpt-x", "astra", "gpt-x", 200_000, 8192), (alias.Alias, alias.ProviderId, alias.UpstreamModel, alias.ContextWindow, alias.MaxOutputTokens));
    }

    [Fact]
    public void Providers_are_ordered_by_priority_then_id()
    {
        File.WriteAllText(_path, """{"providers":{"b":{"baseUrl":"u","protocol":"anthropic","priority":10},"a":{"baseUrl":"u","protocol":"anthropic","priority":10},"c":{"baseUrl":"u","protocol":"anthropic","priority":1}}}""");
        _store.Load();
        Assert.Equal(["c", "a", "b"], _store.ListProviders().Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Invalid_entries_are_skipped_with_a_diagnostic_instead_of_failing_the_file()
    {
        File.WriteAllText(_path, """
        {"providers":{
          "ok":{"baseUrl":"u","protocol":"anthropic"},
          "nourl":{"protocol":"anthropic"},
          "badproto":{"baseUrl":"u","protocol":"gemini"},
          "nextcowork":{"baseUrl":"u","protocol":"anthropic"},
          "has space":{"baseUrl":"u","protocol":"anthropic"}}}
        """);
        Assert.True(_store.Load());
        Assert.Equal(["ok"], _store.ListProviders().Select(p => p.Id).ToArray());
        Assert.Equal(["badproto", "has space", "nextcowork", "nourl"], _store.Problems.Select(p => p.ProviderId!).Order().ToArray());
        Assert.Contains(_store.Problems, p => p.ProviderId == "nourl" && p.Key == ProviderFileProblemKeys.MissingBaseUrl);
        Assert.Contains(_store.Problems, p => p.ProviderId == "badproto" && p.Key == ProviderFileProblemKeys.InvalidProtocol);
    }

    [Fact]
    public void Oauth_credentials_are_read_from_the_credential_object()
    {
        File.WriteAllText(_path, """{"providers":{"o":{"baseUrl":"u","protocol":"openai-responses","credential":{"kind":"oauth","issuer":"chatgpt","accessToken":"a","refreshToken":"r","expiresAt":null,"accountId":"acc"}}}}""");
        _store.Load();
        using var credential = JsonDocument.Parse(_store.GetCredential("o")!);
        Assert.Equal("oauth", credential.RootElement.GetProperty("kind").GetString());
        Assert.Equal("a", credential.RootElement.GetProperty("accessToken").GetString());
    }

    [Fact]
    public void A_credential_object_that_is_not_oauth_or_signature_is_reported_and_ignored()
    {
        File.WriteAllText(_path, """{"providers":{"o":{"baseUrl":"u","protocol":"anthropic","credential":{"kind":"api-key","apiKey":"x"}}}}""");
        _store.Load();
        Assert.Null(_store.GetCredential("o"));
        Assert.Contains(_store.Problems, p => p.Key == ProviderFileProblemKeys.InvalidCredential);
        Assert.Single(_store.ListProviders()); // 供应商本身仍然可用
    }

    [Fact]
    public void Comments_and_trailing_commas_do_not_break_a_hand_edited_file()
    {
        File.WriteAllText(_path, "// mine\n{\"providers\":{\"gw\":{\"baseUrl\":\"u\",\"protocol\":\"anthropic\",},},}");
        Assert.True(_store.Load());
        Assert.Single(_store.ListProviders());
    }

    // ── 外部工具契约(2.x 里是 Astra 网关适配器)──

    private const string AstraEntry =
        """{"name":"Astra","protocol":"openai-chat","baseUrl":"http://127.0.0.1:17321/v1","apiKey":"astra-nextcowork-1","models":{"gpt-5.1":{"upstreamModel":"gpt-5.1","displayName":"GPT 5.1","contextWindow":400000,"maxOutputTokens":32000,"capabilities":{"vision":true,"visionInput":true}},"glm-5":{"upstreamModel":"glm-5"}}}""";

    private static string Wrap(string entry) => """{"providers":{"astra":""" + entry + "}}";

    [Fact]
    public void An_external_tools_entry_is_read_and_filled_with_defaults()
    {
        File.WriteAllText(_path, Wrap(AstraEntry));
        _store.Load();
        Assert.Empty(_store.Problems);
        var provider = _store.ListProviders()[0];
        Assert.Equal(("astra", "Astra", WireProtocol.OpenAiChat, 50, true), (provider.Id, provider.Name, provider.Protocol, provider.Priority, provider.Enabled));
        Assert.Equal("astra-nextcowork-1", _store.GetCredential("astra"));
        var aliases = _store.ListAliases();
        Assert.Equal(["glm-5", "gpt-5.1"], aliases.Select(a => a.Alias).ToArray());
        var gpt = aliases.Single(a => a.Alias == "gpt-5.1");
        Assert.Equal(("GPT 5.1", 400_000, 32_000), (gpt.DisplayName, gpt.ContextWindow, gpt.MaxOutputTokens));
        Assert.True(gpt.SupportsImages);
    }

    [Fact]
    public void Saving_the_unchanged_provider_alias_and_key_back_does_not_rewrite_the_external_tools_entry()
    {
        var text = Wrap(AstraEntry);
        File.WriteAllText(_path, text);
        _store.Load();
        // 用户只是打开又保存:有效内容相同。
        foreach (var p in _store.ListProviders())
        {
            _store.PutProvider(p);
        }

        foreach (var a in _store.ListAliases())
        {
            _store.PutAlias(a);
        }

        _store.SetCredential("astra", "astra-nextcowork-1");
        Assert.Equal(text, File.ReadAllText(_path));
    }

    [Fact]
    public void Removing_the_entry_externally_removes_the_provider_its_models_and_its_key_live()
    {
        File.WriteAllText(_path, """{"providers":{"mine":{"baseUrl":"u","protocol":"anthropic"},"astra":""" + AstraEntry + "}}");
        _store.Load();
        File.WriteAllText(_path, """{"providers":{"mine":{"baseUrl":"u","protocol":"anthropic"}}}""");
        _store.Load();
        Assert.Equal(["mine"], _store.ListProviders().Select(p => p.Id).ToArray());
        Assert.Empty(_store.ListAliases());
        Assert.Null(_store.GetCredential("astra"));
    }

    // ── 写 ──

    [Fact]
    public void Provider_alias_and_key_go_into_one_entry_with_owner_only_permissions()
    {
        _store.Load();
        _store.PutProvider(Provider());
        _store.SetCredential("gw", "sk-secret");
        _store.PutAlias(Alias());

        var entry = Entry("gw");
        Assert.Equal(("Gateway", "openai-chat", "sk-secret", 50, true),
            (entry["name"]!.GetValue<string>(), entry["protocol"]!.GetValue<string>(), entry["apiKey"]!.GetValue<string>(), entry["priority"]!.GetValue<int>(), entry["enabled"]!.GetValue<bool>()));
        Assert.Null(entry["credentialRef"]);
        Assert.Equal(["gpt-4o"], ((JsonObject)entry["models"]!).Select(p => p.Key).ToArray());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
        }
    }

    [Fact]
    public void A_key_that_arrives_before_its_provider_is_stashed_until_the_provider_exists()
    {
        _store.Load();
        _store.SetCredential("gw", "sk-early");
        Assert.False(File.Exists(_path));
        _store.PutProvider(Provider());
        Assert.Equal("sk-early", Entry("gw")["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void The_file_is_not_touched_when_the_effective_content_is_unchanged()
    {
        File.WriteAllText(_path, """{"providers":{"gw":{"baseUrl":"http://127.0.0.1:8080/v1","protocol":"openai-chat","name":"Gateway","x-custom":1}}}""");
        _store.Load();
        var before = File.ReadAllText(_path);
        _store.PutProvider(Provider());
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void Other_entries_and_unknown_fields_stay_intact_when_one_entry_changes()
    {
        File.WriteAllText(_path, """
        {"topLevelNote":"keep me","providers":{
          "astra":{"baseUrl":"http://a/v1","protocol":"openai-chat","apiKey":"k","models":{"m":{}},"x-astra":true},
          "gw":{"baseUrl":"http://g/v1","protocol":"openai-chat"}}}
        """);
        _store.Load();
        _store.PutProvider(Provider(name: "Renamed") with { BaseUrl = "http://g/v1" });

        var doc = Read();
        Assert.Equal("keep me", doc["topLevelNote"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"baseUrl":"http://a/v1","protocol":"openai-chat","apiKey":"k","models":{"m":{}},"x-astra":true}"""),
            doc["providers"]!["astra"]));
        Assert.Equal("Renamed", doc["providers"]!["gw"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void A_provider_is_removed_together_with_its_models_and_key()
    {
        _store.Load();
        _store.PutProvider(Provider());
        _store.SetCredential("gw", "k");
        _store.PutAlias(Alias());
        _store.RemoveProvider("gw");
        Assert.Empty((JsonObject)Read()["providers"]!);
        Assert.Empty(_store.ListAliases());
        Assert.Null(_store.GetCredential("gw"));
    }

    [Fact]
    public void Optional_fields_that_were_removed_are_cleared_from_the_file()
    {
        _store.Load();
        _store.PutProvider(Provider(options: new JsonObject { ["anthropic"] = new JsonObject { ["cacheTtl"] = "1h" } }));
        Assert.NotNull(Entry("gw")["protocolOptions"]);
        _store.PutProvider(Provider());
        Assert.Null(Entry("gw")["protocolOptions"]);
    }

    [Fact]
    public void An_alias_for_an_unknown_provider_and_the_managed_provider_id_are_rejected()
    {
        _store.Load();
        Assert.Equal(ProviderErrorKeys.UnknownProvider, Assert.Throws<ProviderException>(() => _store.PutAlias(Alias(providerId: "nope"))).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, Assert.Throws<ProviderException>(() => _store.PutProvider(Provider("nextcowork"))).Key);
        Assert.Equal(ProviderErrorKeys.InvalidId, Assert.Throws<ProviderException>(() => _store.PutProvider(Provider("a/b"))).Key);
    }

    [Fact]
    public void A_file_that_does_not_parse_is_never_overwritten_and_recovers_once_fixed()
    {
        _store.Load();
        _store.PutProvider(Provider());
        File.WriteAllText(_path, "{ \"providers\": { broken");

        Assert.False(_store.Load());
        Assert.Equal(["gw"], _store.ListProviders().Select(p => p.Id).ToArray()); // 上一份好的仍然生效
        Assert.Equal(ProviderErrorKeys.FileBroken, Assert.Throws<ProviderException>(() => _store.PutProvider(Provider(name: "X"))).Key);
        Assert.Equal("{ \"providers\": { broken", File.ReadAllText(_path));

        File.WriteAllText(_path, """{"providers":{"gw":{"baseUrl":"u","protocol":"anthropic"}}}""");
        Assert.True(_store.Load());
        Assert.Null(_store.Error);
    }

    [Fact]
    public void An_external_edit_made_between_the_last_load_and_a_write_is_picked_up()
    {
        _store.Load();
        _store.PutProvider(Provider());
        var doc = Read();
        ((JsonObject)doc["providers"]!)["astra"] = new JsonObject { ["baseUrl"] = "http://a/v1", ["protocol"] = "openai-chat" };
        File.WriteAllText(_path, doc.ToJsonString());

        _store.PutProvider(Provider(name: "Mine"));
        Assert.Equal(["astra", "gw"], ((JsonObject)Read()["providers"]!).Select(p => p.Key).Order().ToArray());
    }

    [Fact]
    public void Non_ascii_text_is_written_as_is_so_a_hand_editable_file_stays_readable()
    {
        _store.Load();
        _store.PutProvider(Provider(name: "我的网关"));
        Assert.Contains("我的网关", File.ReadAllText(_path));
    }

    [Fact]
    public void Model_fields_the_app_does_not_interpret_survive_a_round_trip()
    {
        File.WriteAllText(_path, """{"providers":{"gw":{"baseUrl":"u","protocol":"anthropic","models":{"m":{"upstreamModel":"m-up","capabilities":{"tools":true},"thinkingConfig":{"mode":"budget","defaultEnabled":true},"video":{"profileId":"p"},"catalogOverrides":["thinkingConfig"]}}}}}""");
        _store.Load();
        var alias = _store.ListAliases()[0];
        _store.PutAlias(alias with { Priority = 3 });
        var model = (JsonObject)Entry("gw")["models"]!["m"]!;
        Assert.Equal("m-up", model["upstreamModel"]!.GetValue<string>());
        Assert.Equal("budget", model["thinkingConfig"]!["mode"]!.GetValue<string>());
        Assert.Equal("p", model["video"]!["profileId"]!.GetValue<string>());
        Assert.Equal(3, model["priority"]!.GetValue<int>());
    }

    // ── 监听 ──

    [Fact]
    public async Task An_external_edit_is_announced_but_the_apps_own_writes_are_not()
    {
        _store.Load();
        _store.PutProvider(Provider());
        var seen = new List<string[]>();
        _store.ExternalChange += change => seen.Add(change.Providers.Select(p => p.Id).ToArray());
        _store.StartWatching();

        _store.PutProvider(Provider(name: "Own write"));
        await Task.Delay(500);
        Assert.Empty(seen);

        var doc = Read();
        ((JsonObject)doc["providers"]!)["astra"] = new JsonObject { ["baseUrl"] = "http://a/v1", ["protocol"] = "openai-chat" };
        File.WriteAllText(_path, doc.ToJsonString());
        await WaitAsync(() => seen.Count == 1);
        Assert.Equal(["astra", "gw"], seen[0]);
    }

    [Fact]
    public async Task An_atomic_replacement_by_an_external_tool_is_noticed()
    {
        _store.Load();
        _store.PutProvider(Provider());
        var seen = new List<string[]>();
        _store.ExternalChange += change => seen.Add(change.Providers.Select(p => p.Id).ToArray());
        _store.StartWatching();

        var doc = Read();
        ((JsonObject)doc["providers"]!)["astra"] = new JsonObject { ["baseUrl"] = "http://a/v1", ["protocol"] = "openai-chat" };
        var temp = Path.Combine(_dir, "providers.tmp");
        File.WriteAllText(temp, doc.ToJsonString());
        File.Move(temp, _path, overwrite: true);
        await WaitAsync(() => seen.Count == 1);
        Assert.Equal(["astra", "gw"], seen[0]);
    }

    [Fact]
    public async Task The_last_good_state_stays_when_an_external_edit_breaks_the_file()
    {
        _store.Load();
        _store.PutProvider(Provider());
        var seen = new List<ProviderFileChange>();
        _store.ExternalChange += seen.Add;
        _store.StartWatching();

        File.WriteAllText(_path, "{ nope");
        await WaitAsync(() => _store.Error is not null);
        Assert.Empty(seen);
        Assert.Equal(["gw"], _store.ListProviders().Select(p => p.Id).ToArray());
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "timed out waiting for the file watcher");
    }
}
