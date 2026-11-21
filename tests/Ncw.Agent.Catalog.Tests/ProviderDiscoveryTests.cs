using Ncw.Agent.Catalog.Discovery;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>
/// 供应商自动发现与导入(docs/design/07 §3「供应商自动发现与导入」):各来源的读取、去重、和现有供应商的比对、导入;
/// 坏文件当没有;订阅登录不读;密钥明文不进候选的字符串形式。
/// </summary>
public sealed class ProviderDiscoveryTests : IDisposable
{
    private const string Home = "/home/u";

    private readonly ProviderFixture _f = new();
    private readonly FakeHost _host = new();

    public void Dispose() => _f.Dispose();

    private sealed class FakeHost : IDiscoveryHost
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);

        public string Home => ProviderDiscoveryTests.Home;

        public string? ReadFile(string path) => Files.GetValueOrDefault(path.Replace('\\', '/'));

        public string? Env(string name) => Vars.GetValueOrDefault(name);

        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public bool DirectoryExists(string path) => Directories.Contains(path.Replace('\\', '/'));
    }

    private ProviderDiscovery Discovery => new(_f.Service, _host);

    private void File(string relative, string text) => _host.Files[$"{Home}/{relative}"] = text;

    private const string ClaudeDeepSeek = """
        {
          // Claude Code 指向 DeepSeek 的 Anthropic 兼容端点
          "env": {
            "ANTHROPIC_BASE_URL": "https://api.deepseek.com/anthropic",
            "ANTHROPIC_AUTH_TOKEN": "sk-deepseek-secret-1234",
            "ANTHROPIC_MODEL": "deepseek-chat",
            "ANTHROPIC_SMALL_FAST_MODEL": "deepseek-chat",
            "ANTHROPIC_DEFAULT_OPUS_MODEL": "opus"
          },
          "permissions": {}
        }
        """;

    [Fact]
    public async Task Claude_code_settings_become_an_anthropic_candidate_named_after_the_known_service()
    {
        File(".claude/settings.json", ClaudeDeepSeek);

        var c = Assert.Single(await Discovery.ScanAsync());
        Assert.Equal(DiscoveryReaders.ClaudeCode, c.Source);
        Assert.Equal("~/.claude/settings.json", c.Origin);
        Assert.Equal(WireProtocol.Anthropic, c.Protocol);
        Assert.Equal("https://api.deepseek.com/anthropic", c.BaseUrl);
        Assert.Equal("deepseek-anthropic", c.SuggestedId); // DeepSeek 的默认协议是 openai-chat,这条是它的 Anthropic 端点
        Assert.Equal("DeepSeek", c.Name);
        Assert.Equal("1234", c.KeyLast4);
        Assert.Equal(["deepseek-chat"], c.Models); // 去重;Claude Code 自己的别名(opus)不算
        Assert.Equal(ProviderDiscovery.StatusNew, c.Status);
        Assert.DoesNotContain("secret", c.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_providers_read_base_url_wire_api_and_keys_but_never_chatgpt_tokens()
    {
        File(".codex/config.toml", """
            model = "kimi-k2"   # 当前模型
            model_provider = "kimi"

            [model_providers.kimi]
            name = "Kimi"
            base_url = "https://api.moonshot.cn/v1"
            env_key = "KIMI_KEY"

            [model_providers."my proxy"]
            base_url = 'https://proxy.example.com/v1'
            wire_api = "responses"
            requires_openai_auth = true

            [model_providers.nokey]
            base_url = "https://nokey.example.com/v1"

            [profiles.fast]
            model = "x"
            tools = ["a", "b",
                     "c"]
            """);
        File(".codex/auth.json", """{ "OPENAI_API_KEY": "sk-openai-aaaa", "tokens": { "access_token": "chatgpt-token-zzzz" } }""");
        _host.Vars["KIMI_KEY"] = "sk-kimi-bbbb";

        var found = await Discovery.ScanAsync();
        Assert.Equal(2, found.Count);
        var kimi = found.Single(c => c.SuggestedId == "moonshot");
        Assert.Equal(WireProtocol.OpenAiChat, kimi.Protocol);
        Assert.Equal("bbbb", kimi.KeyLast4);
        Assert.Equal(["kimi-k2"], kimi.Models);
        var proxy = found.Single(c => c.BaseUrl == "https://proxy.example.com/v1");
        Assert.Equal(WireProtocol.OpenAiResponses, proxy.Protocol);
        Assert.Equal("aaaa", proxy.KeyLast4); // requires_openai_auth → auth.json 的 key
        Assert.Equal("proxy-example-com", proxy.SuggestedId);
        Assert.DoesNotContain(found, c => c.KeyLast4 == "zzzz"); // ChatGPT 登录令牌不读
    }

    [Fact]
    public async Task An_unused_codex_api_key_is_the_builtin_openai_provider()
    {
        File(".codex/auth.json", """{ "OPENAI_API_KEY": "sk-openai-cccc" }""");
        File(".codex/config.toml", "model = \"gpt-5\"\n");

        var c = Assert.Single(await Discovery.ScanAsync());
        Assert.Equal("openai", c.SuggestedId);
        Assert.Equal(WireProtocol.OpenAiResponses, c.Protocol);
        Assert.Equal(["gpt-5"], c.Models);
    }

    [Fact]
    public async Task Cc_switch_entries_are_read_and_the_same_url_and_key_is_merged_with_claude_code()
    {
        File(".claude/settings.json", ClaudeDeepSeek);
        File(".cc-switch/config.json", """
            {
              "claude": {
                "current": "ds",
                "providers": {
                  "ds": { "name": "DeepSeek (cc)", "settingsConfig": { "env": { "ANTHROPIC_BASE_URL": "https://api.deepseek.com/anthropic/", "ANTHROPIC_AUTH_TOKEN": "sk-deepseek-secret-1234", "ANTHROPIC_MODEL": "deepseek-reasoner" } } },
                  "glm": { "name": "智谱", "settingsConfig": { "env": { "ANTHROPIC_BASE_URL": "https://open.bigmodel.cn/api/anthropic", "ANTHROPIC_AUTH_TOKEN": "glm-key-5678" } } }
                }
              },
              "codex": {
                "providers": {
                  "or": { "name": "OpenRouter", "settingsConfig": { "auth": { "OPENAI_API_KEY": "sk-or-9999" }, "config": "model_provider = \"or\"\nmodel = \"x/y\"\n[model_providers.or]\nbase_url = \"https://openrouter.ai/api/v1\"\nrequires_openai_auth = true\n" } }
                }
              }
            }
            """);

        var found = await Discovery.ScanAsync();
        Assert.Equal(3, found.Count);
        var ds = found.Single(c => c.SuggestedId == "deepseek-anthropic");
        Assert.Equal(DiscoveryReaders.ClaudeCode, ds.Source); // 来源顺序靠前的留下
        Assert.Equal([DiscoveryReaders.CcSwitch], ds.AlsoSeenIn);
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], ds.Models); // 模型取并集

        var glm = found.Single(c => c.BaseUrl.Contains("bigmodel", StringComparison.Ordinal));
        Assert.Equal("~/.cc-switch/config.json · 智谱", glm.Origin);
        Assert.Equal("zhipu-anthropic", glm.SuggestedId);

        var or = found.Single(c => c.SuggestedId == "openrouter");
        Assert.Equal(["x/y"], or.Models);
    }

    [Fact]
    public async Task Opencode_api_keys_and_custom_providers_are_read()
    {
        File(".local/share/opencode/auth.json", """{ "deepseek": { "type": "api", "key": "sk-ds-1111" }, "anthropic": { "type": "oauth", "access": "x" }, "unknown": { "type": "api", "key": "k" } }""");
        File(".config/opencode/opencode.json", """
            { "provider": { "local": { "name": "My vLLM", "npm": "@ai-sdk/openai-compatible", "options": { "baseURL": "http://127.0.0.1:8000/v1", "apiKey": "{env:VLLM_KEY}" }, "models": { "qwen3": {} } } } }
            """);
        _host.Vars["VLLM_KEY"] = "local-2222";

        var found = await Discovery.ScanAsync();
        Assert.Equal(2, found.Count);
        Assert.Equal("https://api.deepseek.com", found.Single(c => c.SuggestedId == "deepseek").BaseUrl);
        var local = found.Single(c => c.Name == "My vLLM");
        Assert.Equal("localhost-8000", local.SuggestedId);
        Assert.Equal(["qwen3"], local.Models);
        Assert.Equal("2222", local.KeyLast4);
    }

    [Fact]
    public async Task Environment_variables_of_known_services_are_candidates()
    {
        _host.Vars["DEEPSEEK_API_KEY"] = "sk-env-3333";
        _host.Vars["OPENAI_API_KEY"] = "sk-env-4444";
        _host.Vars["OPENAI_BASE_URL"] = "https://gateway.example.com/v1";

        var found = await Discovery.ScanAsync();
        Assert.Equal(2, found.Count);
        Assert.Equal("DEEPSEEK_API_KEY", found.Single(c => c.SuggestedId == "deepseek").Origin);
        var openai = found.Single(c => c.KeyLast4 == "4444");
        Assert.Equal("https://gateway.example.com/v1", openai.BaseUrl);
        Assert.Equal(WireProtocol.OpenAiChat, openai.Protocol); // 自定义地址多半是 OpenAI 兼容网关,不假设它支持 Responses
    }

    [Fact]
    public async Task Broken_or_missing_files_are_skipped_without_failing_the_scan()
    {
        File(".claude/settings.json", "{ not json");
        File(".codex/config.toml", "[[[[\n= = =\nbase_url = \"unterminated");
        File(".cc-switch/config.json", "[]");
        _host.Vars["GROQ_API_KEY"] = "gsk-5555";

        Assert.Equal("groq", Assert.Single(await Discovery.ScanAsync()).SuggestedId);
    }

    [Fact]
    public async Task Status_compares_against_existing_providers_on_the_device()
    {
        _f.Service.Upsert(new ProviderRecord("deepseek", "DeepSeek", WireProtocol.OpenAiChat, "https://api.deepseek.com/"));
        await _f.Service.SetApiKeyAsync("deepseek", "sk-env-3333");
        _f.Service.Upsert(new ProviderRecord("moonshot", "Mine", WireProtocol.OpenAiChat, "https://api.moonshot.cn/v1"));
        await _f.Service.SetApiKeyAsync("moonshot", "different-key");
        _f.Service.Upsert(new ProviderRecord("groq", "Not groq", WireProtocol.OpenAiChat, "https://elsewhere.example.com/v1"));
        _host.Vars["DEEPSEEK_API_KEY"] = "sk-env-3333";
        _host.Vars["MOONSHOT_API_KEY"] = "sk-moon-6666";
        _host.Vars["GROQ_API_KEY"] = "gsk-5555";
        _host.Vars["XAI_API_KEY"] = "xai-7777";

        var found = await Discovery.ScanAsync();
        Assert.Equal((ProviderDiscovery.StatusImported, "deepseek"), Of("deepseek"));  // 同地址同 key
        Assert.Equal((ProviderDiscovery.StatusConflict, "moonshot"), Of("moonshot"));  // 同地址,key 不同
        Assert.Equal((ProviderDiscovery.StatusConflict, "groq"), Of("groq"));          // 建议的 id 被占
        Assert.Equal((ProviderDiscovery.StatusNew, (string?)null), Of("xai"));

        (string, string?) Of(string suggested) => found.Single(c => c.SuggestedId == suggested) is var c ? (c.Status, c.ExistingProviderId) : default;
    }

    [Fact]
    public async Task Import_creates_providers_with_keys_and_aliases_on_the_device_and_reports_each_candidate()
    {
        File(".claude/settings.json", ClaudeDeepSeek);
        _f.Service.Upsert(new ProviderRecord("groq", "Taken", WireProtocol.OpenAiChat, "https://elsewhere.example.com/v1"));
        _f.Service.Upsert(new ProviderRecord("xai", "Same", WireProtocol.OpenAiChat, "https://api.x.ai/v1"));
        await _f.Service.SetApiKeyAsync("xai", "xai-7777");
        _host.Vars["GROQ_API_KEY"] = "gsk-5555";
        _host.Vars["XAI_API_KEY"] = "xai-7777";
        var discovery = Discovery;
        var scan = await discovery.ScanAsync();
        var ds = scan.Single(c => c.SuggestedId == "deepseek-anthropic");
        var groq = scan.Single(c => c.SuggestedId == "groq");
        var xai = scan.Single(c => c.SuggestedId == "xai");

        var outcomes = await discovery.ImportAsync([ds.Id, groq.Id, xai.Id, "0000000000000000"]);

        Assert.Equal(new DiscoveryImportOutcome(ds.Id, "deepseek-anthropic", null), outcomes[0]);
        Assert.Equal(new DiscoveryImportOutcome(groq.Id, "groq-2", null), outcomes[1]);       // id 冲突加后缀,不覆盖已有的
        Assert.Equal(new DiscoveryImportOutcome(xai.Id, null, ProviderDiscovery.ErrorAlreadyImported), outcomes[2]);
        Assert.Equal(new DiscoveryImportOutcome("0000000000000000", null, ProviderDiscovery.ErrorGone), outcomes[3]);

        var created = _f.Service.Provider("deepseek-anthropic")!;
        Assert.Equal(Ncw.Agent.Llm.Media.ProviderProtocol.Anthropic, created.Protocol);
        Assert.Equal("DeepSeek", created.Name);
        Assert.Equal(new RevealedApiKey("sk-deepseek-secret-1234"), await _f.Service.RevealCredentialAsync("deepseek-anthropic"));
        Assert.Equal(["deepseek-chat"], _f.Service.Snapshot.Aliases.Where(a => a.ProviderId == "deepseek-anthropic").Select(a => a.UpstreamModel));
        Assert.Equal("Taken", _f.Service.Provider("groq")!.Name);

        // 导入以后再扫:这几条都是「已导入」
        Assert.All((await discovery.ScanAsync()).Where(c => c.Id == ds.Id || c.Id == groq.Id), c => Assert.Equal(ProviderDiscovery.StatusImported, c.Status));
    }

    [Fact]
    public async Task A_candidate_whose_source_changed_after_the_scan_is_gone()
    {
        _host.Vars["XAI_API_KEY"] = "xai-7777";
        var id = Assert.Single(await Discovery.ScanAsync()).Id;
        _host.Vars["XAI_API_KEY"] = "xai-8888";

        Assert.Equal(ProviderDiscovery.ErrorGone, Assert.Single(await Discovery.ImportAsync([id])).ErrorKey);
        Assert.Null(_f.Service.Provider("xai"));
    }

    [Fact]
    public async Task Shell_profiles_are_a_source_and_also_feed_other_sources_env_lookups()
    {
        File(".zshrc", """
            # 我的 key
            export DEEPSEEK_API_KEY="sk-shell-1111"
            export KIMI_API_KEY='sk-kimi-2222'
            export OPENROUTER_API_KEY=$(security find-generic-password -s openrouter -w)
            export GROQ_API_KEY="${OTHER}"
            alias ll='ls -l'
            """);
        File(".bashrc", "export DEEPSEEK_API_KEY=sk-shell-9999   # 后加载的为准\n");
        File(".config/fish/config.fish", "set -gx MISTRAL_API_KEY mist-3333\n");
        File(".codex/config.toml", "[model_providers.or]\nbase_url = \"https://openrouter.ai/api/v1\"\nenv_key = \"MY_OR_KEY\"\n");
        File(".profile", "MY_OR_KEY=or-4444\n");

        var found = await Discovery.ScanAsync();
        var ds = found.Single(c => c.SuggestedId == "deepseek");
        Assert.Equal((DiscoveryReaders.Shell, "~/.bashrc · DEEPSEEK_API_KEY", "9999"), (ds.Source, ds.Origin, ds.KeyLast4));
        Assert.Equal("2222", found.Single(c => c.SuggestedId == "moonshot").KeyLast4);   // KIMI_API_KEY 也算 Kimi
        Assert.Equal("3333", found.Single(c => c.SuggestedId == "mistral").KeyLast4);    // fish
        Assert.DoesNotContain(found, c => c.SuggestedId is "openrouter" && c.Source == DiscoveryReaders.Shell); // $(…) 不执行
        Assert.DoesNotContain(found, c => c.SuggestedId == "groq");                     // ${…} 不展开
        Assert.Equal((DiscoveryReaders.Codex, "4444"), found.Single(c => c.SuggestedId == "openrouter") is var or ? (or.Source, or.KeyLast4) : default); // Codex 的 env_key 从 shell 配置里找到
    }

    [Fact]
    public async Task The_same_key_in_the_shell_and_the_process_environment_is_one_candidate()
    {
        File(".zshrc", "export XAI_API_KEY=xai-5555\n");
        _host.Vars["XAI_API_KEY"] = "xai-5555";

        var c = Assert.Single(await Discovery.ScanAsync());
        Assert.Equal(DiscoveryReaders.Shell, c.Source);
        Assert.Equal([DiscoveryReaders.Env], c.AlsoSeenIn);
    }

    [Fact]
    public async Task Gemini_cli_and_qwen_code_dotenv_files_are_read()
    {
        File(".gemini/.env", "GEMINI_API_KEY=AIza-gem-6666\nGEMINI_MODEL=gemini-2.5-pro\n");
        File(".qwen/.env", "OPENAI_API_KEY=sk-qwen-7777\nOPENAI_BASE_URL=https://dashscope.aliyuncs.com/compatible-mode/v1\nOPENAI_MODEL=qwen3-coder-plus\n");

        var found = await Discovery.ScanAsync();
        var gemini = found.Single(c => c.Source == DiscoveryReaders.GeminiCli);
        Assert.Equal(("gemini", "6666", "gemini-2.5-pro"), (gemini.SuggestedId, gemini.KeyLast4, gemini.Models.Single()));
        var qwen = found.Single(c => c.Source == DiscoveryReaders.QwenCode);
        Assert.Equal(("dashscope", "Qwen (DashScope)", "qwen3-coder-plus"), (qwen.SuggestedId, qwen.Name, qwen.Models.Single()));
    }

    [Fact]
    public async Task Qwen_code_settings_auth_is_read_when_there_is_no_dotenv()
    {
        File(".qwen/settings.json", """{ "security": { "auth": { "selectedType": "openai", "apiKey": "sk-qwen-8888", "baseUrl": "https://api.siliconflow.cn/v1" } }, "model": { "name": "Qwen/Qwen3-Coder" } }""");

        var c = Assert.Single(await Discovery.ScanAsync());
        Assert.Equal(("siliconflow", "~/.qwen/settings.json", "Qwen/Qwen3-Coder"), (c.SuggestedId, c.Origin, c.Models.Single()));
    }

    [Fact]
    public async Task Crush_providers_resolve_env_references_and_list_their_models()
    {
        File(".config/crush/crush.json", """
            {
              "providers": {
                "deepseek": { "type": "openai", "base_url": "https://api.deepseek.com/v1", "api_key": "$DEEPSEEK_KEY", "models": [ { "id": "deepseek-chat" }, { "id": "deepseek-reasoner" } ] },
                "claude-proxy": { "name": "My Claude proxy", "type": "anthropic", "base_url": "https://claude.example.com", "api_key": "sk-proxy-1212" },
                "off": { "type": "openai", "base_url": "https://off.example.com/v1", "api_key": "k", "disable": true }
              }
            }
            """);
        _host.Vars["DEEPSEEK_KEY"] = "sk-crush-1313";

        var found = await Discovery.ScanAsync();
        Assert.Equal(2, found.Count);
        var ds = found.Single(c => c.SuggestedId == "deepseek");
        Assert.Equal(("1313", "~/.config/crush/crush.json · deepseek"), (ds.KeyLast4, ds.Origin));
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], ds.Models);
        var proxy = found.Single(c => c.Name == "My Claude proxy");
        Assert.Equal(WireProtocol.Anthropic, proxy.Protocol);
    }

    [Fact]
    public async Task Continue_models_from_json_and_yaml_are_read_and_local_ones_need_no_key()
    {
        File(".continue/config.json", """
            { "models": [
                { "title": "DeepSeek Coder", "provider": "deepseek", "model": "deepseek-chat", "apiKey": "sk-cont-1414" },
                { "title": "Local Qwen", "provider": "ollama", "model": "qwen2.5-coder:7b" },
                { "title": "No key", "provider": "openai", "model": "gpt-4o" }
            ] }
            """);
        File(".continue/config.yaml", """
            name: My Assistant
            version: 1.0.0
            models:
              - name: Claude via proxy   # 注释
                provider: anthropic
                model: claude-sonnet-4-5
                apiKey: "sk-yaml-1515"
                apiBase: https://claude-proxy.example.com
              - name: Groq
                provider: groq
                model: llama-3.3-70b
                apiKey: gsk-1616
            """);

        var found = await Discovery.ScanAsync();
        Assert.Equal(4, found.Count);
        Assert.Equal("1414", found.Single(c => c.SuggestedId == "deepseek").KeyLast4);
        var ollama = found.Single(c => c.SuggestedId == "ollama");
        Assert.Equal(("http://localhost:11434/v1", (string?)null, "qwen2.5-coder:7b"), (ollama.BaseUrl, ollama.KeyLast4, ollama.Models.Single()));
        var proxy = found.Single(c => c.Name == "Claude via proxy");
        Assert.Equal((WireProtocol.Anthropic, "1515", "claude-sonnet-4-5"), (proxy.Protocol, proxy.KeyLast4, proxy.Models.Single()));
        Assert.Equal(["llama-3.3-70b"], found.Single(c => c.SuggestedId == "groq").Models);
    }

    [Fact]
    public async Task Aider_keys_and_the_model_prefix_are_read()
    {
        File(".aider.conf.yml", """
            # aider 配置
            model: deepseek/deepseek-chat
            openai-api-key: sk-aider-1717
            openai-api-base: https://gw.example.com/v1
            api-key:
              - deepseek=sk-aider-1818
              - gemini=AIza-1919
            dark-mode: true
            """);

        var found = await Discovery.ScanAsync();
        Assert.Equal(3, found.Count);
        var ds = found.Single(c => c.SuggestedId == "deepseek");
        Assert.Equal(("1818", "deepseek-chat"), (ds.KeyLast4, ds.Models.Single()));
        var gw = found.Single(c => c.KeyLast4 == "1717");
        Assert.Equal(("https://gw.example.com/v1", WireProtocol.OpenAiChat), (gw.BaseUrl, gw.Protocol));
        Assert.Equal("gemini", found.Single(c => c.KeyLast4 == "1919").SuggestedId);
    }

    [Fact]
    public async Task Aider_inline_api_key_lists_are_read_too()
    {
        File(".aider.conf.yml", "api-key: [mistral=mist-2020, unknown=x]\n");

        Assert.Equal("mistral", Assert.Single(await Discovery.ScanAsync()).SuggestedId);
    }

    [Fact]
    public async Task Local_ollama_and_lm_studio_are_offered_without_a_key_when_their_data_directory_exists()
    {
        _host.Directories.Add($"{Home}/.ollama");
        _host.Directories.Add($"{Home}/.cache/lm-studio");

        var found = await Discovery.ScanAsync();
        Assert.Equal(["lmstudio", "ollama"], found.Select(c => c.SuggestedId).Order());
        Assert.All(found, c => Assert.Equal((DiscoveryReaders.Local, (string?)null), (c.Source, c.KeyLast4)));
        Assert.Equal(("LM Studio", "http://localhost:1234/v1"), found.Single(c => c.SuggestedId == "lmstudio") is var l ? (l.Name, l.BaseUrl) : default);

        // 导入不要 key
        var outcome = Assert.Single(await Discovery.ImportAsync([found.Single(c => c.SuggestedId == "ollama").Id]));
        Assert.Equal("ollama", outcome.ProviderId);
        Assert.False((await _f.Service.GetCredentialInfoAsync("ollama")).HasKey);
    }

    [Fact]
    public async Task An_existing_media_provider_on_the_same_gateway_url_does_not_mark_a_text_candidate_as_imported_or_conflict()
    {
        // 用户已经配了火山方舟图片供应商(地址与文本方舟完全一致)
        _f.Service.Upsert(new ProviderRecord(
            "volcengine-image",
            "火山方舟(图片)",
            Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages,
            "https://ark.cn-beijing.volces.com/api/v3",
            Kind: Ncw.Agent.Llm.Media.ProviderKind.Image));
        await _f.Service.SetApiKeyAsync("volcengine-image", "ark-same-9999");

        File(".codex/config.toml", """
            model = "doubao-seed-2-0-pro"
            model_provider = "ark"
            [model_providers.ark]
            name = "Volcengine"
            base_url = "https://ark.cn-beijing.volces.com/api/v3"
            env_key = "ARK_API_KEY"
            """);
        _host.Vars["ARK_API_KEY"] = "ark-same-9999";

        var candidate = Assert.Single(await Discovery.ScanAsync());
        Assert.Equal("volcengine", candidate.SuggestedId);
        Assert.Equal(ProviderDiscovery.StatusNew, candidate.Status);
        Assert.Null(candidate.ExistingProviderId);
    }
}
