using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Discovery;

/// <summary>设备上读文件 / 环境变量的入口(测试换成内存里的)。只读;读不到返回 null,从不抛。</summary>
public interface IDiscoveryHost
{
    /// <summary>家目录。</summary>
    string Home { get; }

    /// <summary>读一个文件的全文;不存在、读不了、太大(超过 1 MB)都是 null。</summary>
    string? ReadFile(string path);

    /// <summary>环境变量;没有或为空是 null。</summary>
    string? Env(string name);

    /// <summary>目录在不在(本地服务只看它的数据目录)。</summary>
    bool DirectoryExists(string path);
}

/// <summary>本机的实现:Agent 进程自己的文件系统和环境变量(发现的是 Agent 所在那台机器上的配置)。</summary>
public sealed class LocalDiscoveryHost : IDiscoveryHost
{
    private const long MaxBytes = 1024 * 1024;

    public static readonly LocalDiscoveryHost Instance = new();

    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>某个来源里读到的一套供应商配置(含密钥明文,只在设备上流转)。</summary>
public sealed record RawCandidate(string Source, string Origin, WireProtocol Protocol, string BaseUrl, string? ApiKey, IReadOnlyList<string> Models, string? Name = null);

/// <summary>
/// 各客户端配置的读取(docs/design/07 §3「供应商自动发现与导入」)。每个来源是一段只吃文本的纯函数,读不懂就当没有;
/// 订阅登录(Claude 订阅 OAuth、Codex 的 ChatGPT 令牌)一律不读。
/// </summary>
public static class DiscoveryReaders
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
    public const string CcSwitch = "cc-switch";
    public const string OpenCode = "opencode";
    public const string GeminiCli = "gemini-cli";
    public const string QwenCode = "qwen-code";
    public const string Crush = "crush";
    public const string Continue = "continue";
    public const string Aider = "aider";
    public const string Shell = "shell";
    public const string Env = "env";
    public const string Local = "local";

    /// <summary>来源的先后(去重时保留排在前面的那个):客户端配置在前,Shell 配置和环境变量在后,本地服务最后。</summary>
    public static readonly string[] Order = [ClaudeCode, Codex, CcSwitch, OpenCode, GeminiCli, QwenCode, Crush, Continue, Aider, Shell, Env, Local];

    /// <summary>Shell 配置文件(相对家目录,按 shell 的加载顺序:同名后面的为准)。</summary>
    private static readonly string[] ShellFiles = [".zshenv", ".zprofile", ".zshrc", ".bash_profile", ".bashrc", ".profile", ".env"];

    private static readonly string[] ClaudeModelVars =
        ["ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_SMALL_FAST_MODEL"];

    /// <summary>Claude Code 自己的模型别名,不是上游模型名。</summary>
    private static readonly string[] ClaudeAliases = ["default", "opus", "sonnet", "haiku", "opusplan", "sonnet[1m]"];

    /// <summary>读遍设备上的所有来源。</summary>
    public static List<RawCandidate> ReadAll(IDiscoveryHost host)
    {
        var found = new List<RawCandidate>();
        var home = host.Home;
        var configHome = host.Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        var dataHome = host.Env("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");

        // Shell 配置里的变量:自己是一个来源,也补给别的来源查环境变量(macOS 桌面应用拿不到 shell 里 export 的变量)
        var shell = new Dictionary<string, (string Value, string Origin)>(StringComparer.Ordinal);
        foreach (var (file, fish) in ShellFiles.Select(f => (Path.Combine(home, f), false)).Append((Path.Combine(configHome, "fish", "config.fish"), true)))
        {
            if (host.ReadFile(file) is { } text)
            {
                foreach (var (name, value) in ShellVars.Parse(text, fish))
                {
                    shell[name] = (value, Display(file, home));
                }
            }
        }

        string? Lookup(string name) => host.Env(name) ?? (shell.TryGetValue(name, out var v) ? v.Value : null);

        // Claude Code
        var claudeDir = host.Env("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        if (Json(host.ReadFile(settingsPath)) is JsonObject settings && settings["env"] is JsonObject env)
        {
            found.AddRange(FromClaudeEnv(env, ClaudeCode, Display(settingsPath, home)));
        }

        var claudeJsonPath = Path.Combine(home, ".claude.json");
        if (Json(host.ReadFile(claudeJsonPath)) is JsonObject claudeJson && Str(claudeJson["primaryApiKey"]) is { } primary)
        {
            found.Add(new RawCandidate(ClaudeCode, Display(claudeJsonPath, home), WireProtocol.Anthropic, "https://api.anthropic.com", primary, []));
        }

        // Codex
        var codexDir = host.Env("CODEX_HOME") ?? Path.Combine(home, ".codex");
        var codexConfigPath = Path.Combine(codexDir, "config.toml");
        var codexAuthPath = Path.Combine(codexDir, "auth.json");
        var codexConfig = host.ReadFile(codexConfigPath);
        var codexAuth = Json(host.ReadFile(codexAuthPath)) as JsonObject;
        if (codexConfig is not null || codexAuth is not null)
        {
            found.AddRange(FromCodex(codexConfig, codexAuth, Lookup, Codex, Display(codexConfig is not null ? codexConfigPath : codexAuthPath, home)));
        }

        // cc-switch
        var ccPath = Path.Combine(home, ".cc-switch", "config.json");
        if (Json(host.ReadFile(ccPath)) is JsonObject cc)
        {
            found.AddRange(FromCcSwitch(cc, Lookup, Display(ccPath, home)));
        }

        // opencode
        var openCodeAuthPath = Path.Combine(dataHome, "opencode", "auth.json");
        if (Json(host.ReadFile(openCodeAuthPath)) is JsonObject openCodeAuth)
        {
            found.AddRange(FromOpenCodeAuth(openCodeAuth, Display(openCodeAuthPath, home)));
        }

        var openCodeConfigPath = Path.Combine(configHome, "opencode", "opencode.json");
        if (Json(host.ReadFile(openCodeConfigPath)) is JsonObject openCodeConfig)
        {
            found.AddRange(FromOpenCodeConfig(openCodeConfig, Lookup, Display(openCodeConfigPath, home)));
        }

        // Gemini CLI
        var geminiEnvPath = Path.Combine(home, ".gemini", ".env");
        if (host.ReadFile(geminiEnvPath) is { } geminiEnv)
        {
            found.AddRange(FromGeminiCli(ShellVars.Parse(geminiEnv), Display(geminiEnvPath, home)));
        }

        // Qwen Code
        var qwenEnvPath = Path.Combine(home, ".qwen", ".env");
        var qwenSettingsPath = Path.Combine(home, ".qwen", "settings.json");
        var qwenEnv = host.ReadFile(qwenEnvPath) is { } qe ? ShellVars.Parse(qe) : null;
        var qwenSettings = Json(host.ReadFile(qwenSettingsPath)) as JsonObject;
        if (qwenEnv is not null || qwenSettings is not null)
        {
            found.AddRange(FromQwenCode(qwenEnv, qwenSettings, Display(qwenEnv is not null ? qwenEnvPath : qwenSettingsPath, home)));
        }

        // Crush
        foreach (var crushPath in new[] { Path.Combine(configHome, "crush", "crush.json"), Path.Combine(dataHome, "crush", "crush.json") })
        {
            if (Json(host.ReadFile(crushPath)) is JsonObject crush)
            {
                found.AddRange(FromCrush(crush, Lookup, Display(crushPath, home)));
            }
        }

        // Continue
        var continueJsonPath = Path.Combine(home, ".continue", "config.json");
        if (Json(host.ReadFile(continueJsonPath)) is JsonObject continueJson && continueJson["models"] is JsonArray continueModels)
        {
            found.AddRange(FromContinue(continueModels.OfType<JsonObject>().Select(m => (IReadOnlyDictionary<string, string>)m
                .Where(p => Str(p.Value) is not null).ToDictionary(p => p.Key, p => Str(p.Value)!, StringComparer.Ordinal)), Display(continueJsonPath, home)));
        }

        var continueYamlPath = Path.Combine(home, ".continue", "config.yaml");
        if (host.ReadFile(continueYamlPath) is { } continueYaml && MiniYaml.Parse(continueYaml).Maps.GetValueOrDefault("models") is { } yamlModels)
        {
            found.AddRange(FromContinue(yamlModels, Display(continueYamlPath, home)));
        }

        // Aider
        var aiderPath = Path.Combine(home, ".aider.conf.yml");
        if (host.ReadFile(aiderPath) is { } aider)
        {
            found.AddRange(FromAider(MiniYaml.Parse(aider), Display(aiderPath, home)));
        }

        // Shell 配置、环境变量
        found.AddRange(FromVars(name => shell.TryGetValue(name, out var v) ? (v.Value, $"{v.Origin} · {name}") : null, Shell));
        found.AddRange(FromVars(name => host.Env(name) is { } v ? (v, name) : null, Env));

        // 本地服务:数据目录在就算(不探测端口)
        if (host.DirectoryExists(Path.Combine(home, ".ollama")))
        {
            found.Add(LocalService("ollama", Display(Path.Combine(home, ".ollama"), home)));
        }

        foreach (var dir in new[] { Path.Combine(home, ".lmstudio"), Path.Combine(home, ".cache", "lm-studio") })
        {
            if (host.DirectoryExists(dir))
            {
                found.Add(LocalService("lmstudio", Display(dir, home)));
                break;
            }
        }

        return found;
    }

    private static RawCandidate LocalService(string id, string origin)
    {
        var service = KnownServices.ById(id)!;
        return new RawCandidate(Local, origin, service.Protocol, service.BaseUrl, null, []);
    }

    /// <summary>Claude Code 的 <c>env</c> 块(<c>settings.json</c> 和 cc-switch 里存的一样)。没有 key 就没有候选。</summary>
    public static IEnumerable<RawCandidate> FromClaudeEnv(JsonObject env, string source, string origin, string? name = null)
    {
        var key = Str(env["ANTHROPIC_AUTH_TOKEN"]) ?? Str(env["ANTHROPIC_API_KEY"]);
        if (key is null)
        {
            yield break;
        }

        var baseUrl = Str(env["ANTHROPIC_BASE_URL"]) ?? "https://api.anthropic.com";
        var models = ClaudeModelVars.Select(v => Str(env[v])).OfType<string>()
            .Where(m => !ClaudeAliases.Contains(m, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
        yield return new RawCandidate(source, origin, WireProtocol.Anthropic, baseUrl, key, models, name);
    }

    /// <summary>
    /// Codex:<c>config.toml</c> 的 <c>model_providers.&lt;id&gt;</c>(<c>base_url</c>、<c>env_key</c>、<c>wire_api</c>、<c>requires_openai_auth</c>)
    /// 和 <c>auth.json</c> 的 <c>OPENAI_API_KEY</c>。没有被自定义供应商用掉的 <c>OPENAI_API_KEY</c> 算内置的 OpenAI。<c>tokens</c>(ChatGPT 登录)不读。
    /// </summary>
    public static IEnumerable<RawCandidate> FromCodex(string? configToml, JsonObject? auth, Func<string, string?> env, string source, string origin, string? name = null)
    {
        var tables = configToml is null ? new Dictionary<string, Dictionary<string, string>>() : MiniToml.Parse(configToml);
        var root = tables.GetValueOrDefault("") ?? [];
        var active = root.GetValueOrDefault("model_provider") ?? "openai";
        var model = root.GetValueOrDefault("model");
        var authKey = auth is null ? null : Str(auth["OPENAI_API_KEY"]);
        var authUsed = false;

        foreach (var (path, table) in tables)
        {
            if (!path.StartsWith("model_providers.", StringComparison.Ordinal) || table.GetValueOrDefault("base_url") is not { Length: > 0 } baseUrl)
            {
                continue;
            }

            var id = path["model_providers.".Length..];
            string? key = null;
            if (table.GetValueOrDefault("env_key") is { Length: > 0 } envKey)
            {
                key = env(envKey);
            }

            if (key is null && (table.GetValueOrDefault("requires_openai_auth") == "true" || id == active) && authKey is not null)
            {
                key = authKey;
                authUsed = true;
            }

            if (key is null && table.GetValueOrDefault("experimental_bearer_token") is { Length: > 0 } bearer)
            {
                key = bearer;
            }

            var protocol = table.GetValueOrDefault("wire_api") == "responses" ? WireProtocol.OpenAiResponses : WireProtocol.OpenAiChat;
            var local = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
            if (key is null && !local)
            {
                continue; // 没 key 的远程地址用不了(本地 Ollama 之类不要 key)
            }

            yield return new RawCandidate(source, origin, protocol, baseUrl, key, id == active && model is not null ? [model] : [], name ?? table.GetValueOrDefault("name"));
        }

        if (authKey is not null && !authUsed)
        {
            yield return new RawCandidate(source, origin, WireProtocol.OpenAiResponses, "https://api.openai.com/v1", authKey,
                active == "openai" && model is not null ? [model] : [], name);
        }
    }

    /// <summary>
    /// cc-switch 的 <c>config.json</c>:<c>claude.providers.*.settingsConfig.env</c>(同 Claude Code)和
    /// <c>codex.providers.*.settingsConfig</c>(<c>auth</c> 对象 + <c>config</c> TOML 文本,同 Codex);老版本根上直接是 Claude 的 <c>providers</c>。
    /// </summary>
    public static IEnumerable<RawCandidate> FromCcSwitch(JsonObject root, Func<string, string?> env, string origin)
    {
        var claude = root["claude"] as JsonObject ?? (root["providers"] is JsonObject ? root : null);
        foreach (var (entryName, settings) in Entries(claude))
        {
            if (settings["env"] is JsonObject claudeEnv)
            {
                foreach (var c in FromClaudeEnv(claudeEnv, CcSwitch, $"{origin} · {entryName}", entryName))
                {
                    yield return c;
                }
            }
        }

        foreach (var (entryName, settings) in Entries(root["codex"] as JsonObject))
        {
            foreach (var c in FromCodex(Str(settings["config"]), settings["auth"] as JsonObject, env, CcSwitch, $"{origin} · {entryName}", entryName))
            {
                yield return c;
            }
        }

        static IEnumerable<(string Name, JsonObject Settings)> Entries(JsonObject? app)
        {
            if (app?["providers"] is not JsonObject providers)
            {
                yield break;
            }

            foreach (var (id, node) in providers)
            {
                if (node is JsonObject entry && entry["settingsConfig"] is JsonObject settings)
                {
                    yield return (Str(entry["name"]) ?? id, settings);
                }
            }
        }
    }

    /// <summary>opencode 的 <c>auth.json</c>:<c>{ "&lt;供应商 id&gt;": { "type": "api", "key": "…" } }</c>,按已知服务表对上地址;OAuth 条目不读。</summary>
    public static IEnumerable<RawCandidate> FromOpenCodeAuth(JsonObject auth, string origin)
    {
        foreach (var (id, node) in auth)
        {
            if (node is JsonObject entry && Str(entry["type"]) == "api" && Str(entry["key"]) is { } key && KnownServices.ByAlias(id) is { } known)
            {
                yield return new RawCandidate(OpenCode, origin, known.Protocol, known.BaseUrl, key, []);
            }
        }
    }

    /// <summary>opencode 的 <c>opencode.json</c> 里自定义的供应商:<c>provider.&lt;id&gt;.options.baseURL / apiKey</c>(<c>{env:NAME}</c> 换成环境变量)、<c>models</c> 的键。</summary>
    public static IEnumerable<RawCandidate> FromOpenCodeConfig(JsonObject config, Func<string, string?> env, string origin)
    {
        if (config["provider"] is not JsonObject providers)
        {
            yield break;
        }

        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject entry || entry["options"] is not JsonObject options)
            {
                continue;
            }

            var known = KnownServices.ByAlias(id);
            var baseUrl = Str(options["baseURL"]) ?? known?.BaseUrl;
            var key = Str(options["apiKey"]) is { } raw && raw.StartsWith("{env:", StringComparison.Ordinal) && raw.EndsWith('}') ? env(raw[5..^1]) : Str(options["apiKey"]);
            if (baseUrl is null || key is null)
            {
                continue;
            }

            var protocol = Str(entry["npm"]) == "@ai-sdk/anthropic" ? WireProtocol.Anthropic : known?.Protocol ?? WireProtocol.OpenAiChat;
            var models = entry["models"] is JsonObject m ? m.Select(p => p.Key).ToList() : [];
            yield return new RawCandidate(OpenCode, origin, protocol, baseUrl, key, models, Str(entry["name"]));
        }
    }

    /// <summary>
    /// 常见服务的变量(环境变量 / Shell 配置共用):每家几个常见名字,取第一个有值的;OpenAI / Anthropic 还读 <c>*_BASE_URL</c>。
    /// <paramref name="lookup"/> 给值和给人看的出处。
    /// </summary>
    public static IEnumerable<RawCandidate> FromVars(Func<string, (string Value, string Origin)?> lookup, string source)
    {
        foreach (var service in KnownServices.All)
        {
            if (service.EnvVars.Select(lookup).FirstOrDefault(v => v is not null) is not { } found)
            {
                continue;
            }

            var baseUrl = service.Id switch
            {
                "openai" => lookup("OPENAI_BASE_URL")?.Value ?? lookup("OPENAI_API_BASE")?.Value,
                "anthropic" => lookup("ANTHROPIC_BASE_URL")?.Value,
                _ => null,
            } ?? service.BaseUrl;
            var protocol = service.Id == "openai" && baseUrl != service.BaseUrl ? WireProtocol.OpenAiChat : service.Protocol;
            yield return new RawCandidate(source, found.Origin, protocol, baseUrl, found.Value, []);
        }
    }

    /// <summary>旧入口:只看进程环境变量。</summary>
    public static IEnumerable<RawCandidate> FromEnv(Func<string, string?> env) => FromVars(name => env(name) is { } v ? (v, name) : null, Env);

    /// <summary>Gemini CLI 的 <c>~/.gemini/.env</c>:<c>GEMINI_API_KEY</c> / <c>GOOGLE_API_KEY</c>、<c>GEMINI_MODEL</c>(Google 账号登录不读)。</summary>
    public static IEnumerable<RawCandidate> FromGeminiCli(IReadOnlyDictionary<string, string> vars, string origin)
    {
        var gemini = KnownServices.ById("gemini")!;
        if (gemini.EnvVars.Select(vars.GetValueOrDefault).FirstOrDefault(v => v is not null) is { } key)
        {
            yield return new RawCandidate(GeminiCli, origin, gemini.Protocol, gemini.BaseUrl, key, vars.GetValueOrDefault("GEMINI_MODEL") is { } m ? [m] : []);
        }
    }

    /// <summary>
    /// Qwen Code:<c>~/.qwen/.env</c> 的 <c>OPENAI_API_KEY</c> + <c>OPENAI_BASE_URL</c> + <c>OPENAI_MODEL</c>,
    /// 或 <c>settings.json</c> 里 <c>security.auth</c> 的 <c>apiKey</c> / <c>baseUrl</c> 和 <c>model.name</c>(Qwen OAuth 登录不读)。
    /// </summary>
    public static IEnumerable<RawCandidate> FromQwenCode(IReadOnlyDictionary<string, string>? vars, JsonObject? settings, string origin)
    {
        var auth = settings?["security"]?["auth"] as JsonObject;
        var model = vars?.GetValueOrDefault("OPENAI_MODEL") ?? Str(settings?["model"]?["name"]) ?? Str(settings?["model"]);
        var key = vars?.GetValueOrDefault("OPENAI_API_KEY") ?? Str(auth?["apiKey"]);
        var baseUrl = vars?.GetValueOrDefault("OPENAI_BASE_URL") ?? Str(auth?["baseUrl"]) ?? "https://api.openai.com/v1";
        if (key is not null)
        {
            var protocol = baseUrl == "https://api.openai.com/v1" ? WireProtocol.OpenAiResponses : WireProtocol.OpenAiChat;
            yield return new RawCandidate(QwenCode, origin, protocol, baseUrl, key, model is not null ? [model] : []);
        }

        if (vars?.GetValueOrDefault("DASHSCOPE_API_KEY") is { } dashKey)
        {
            var dash = KnownServices.ById("dashscope")!;
            yield return new RawCandidate(QwenCode, origin, dash.Protocol, dash.BaseUrl, dashKey, []);
        }
    }

    /// <summary>Crush 的 <c>crush.json</c>:<c>providers.&lt;id&gt;</c> 的 <c>type</c>、<c>base_url</c>、<c>api_key</c>(<c>$NAME</c> / <c>${NAME}</c> 换成环境变量)、<c>models[].id</c>。</summary>
    public static IEnumerable<RawCandidate> FromCrush(JsonObject config, Func<string, string?> env, string origin)
    {
        if (config["providers"] is not JsonObject providers)
        {
            yield break;
        }

        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject entry || (entry["disable"] is JsonValue disable && disable.TryGetValue<bool>(out var off) && off))
            {
                continue;
            }

            var known = KnownServices.ByAlias(id);
            var baseUrl = Str(entry["base_url"]) ?? known?.BaseUrl;
            var key = Str(entry["api_key"]) is { } raw && raw.StartsWith('$') ? env(raw.TrimStart('$').Trim('{', '}')) : Str(entry["api_key"]);
            if (baseUrl is null || (key is null && !IsLocal(baseUrl)))
            {
                continue;
            }

            var protocol = Str(entry["type"]) == "anthropic" ? WireProtocol.Anthropic : known is { } k && Str(entry["base_url"]) is null ? k.Protocol : WireProtocol.OpenAiChat;
            var models = entry["models"] is JsonArray list ? list.OfType<JsonObject>().Select(m => Str(m["id"])).OfType<string>().ToList() : [];
            yield return new RawCandidate(Crush, $"{origin} · {id}", protocol, baseUrl, key, models, Str(entry["name"]));
        }
    }

    /// <summary>Continue 的 <c>models[]</c>(<c>config.json</c> 和 <c>config.yaml</c> 同样的字段):<c>provider</c>、<c>model</c>、<c>apiKey</c>、<c>apiBase</c>、<c>title</c> / <c>name</c>。</summary>
    public static IEnumerable<RawCandidate> FromContinue(IEnumerable<IReadOnlyDictionary<string, string>> models, string origin)
    {
        foreach (var m in models)
        {
            var provider = m.GetValueOrDefault("provider") ?? "";
            var known = KnownServices.ByAlias(provider);
            var baseUrl = m.GetValueOrDefault("apiBase") ?? known?.BaseUrl;
            var key = m.GetValueOrDefault("apiKey");
            if (baseUrl is null || (key is null && !IsLocal(baseUrl)))
            {
                continue;
            }

            var protocol = provider == "anthropic" ? WireProtocol.Anthropic
                : known is { } k && m.GetValueOrDefault("apiBase") is null ? k.Protocol : WireProtocol.OpenAiChat;
            if (known?.LocalPort is not null && !baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.TrimEnd('/') + "/v1"; // Continue 给 Ollama / LM Studio 写的是不带 /v1 的根地址
            }

            yield return new RawCandidate(Continue, origin, protocol, baseUrl, key, m.GetValueOrDefault("model") is { } model ? [model] : [],
                m.GetValueOrDefault("title") ?? m.GetValueOrDefault("name"));
        }
    }

    /// <summary>
    /// Aider 的 <c>.aider.conf.yml</c>:<c>openai-api-key</c>(+<c>openai-api-base</c>)、<c>anthropic-api-key</c>、
    /// <c>api-key</c> 列表里的 <c>供应商=key</c>;<c>model</c> 形如 <c>deepseek/deepseek-chat</c> 时挂到对应那家。
    /// </summary>
    internal static IEnumerable<RawCandidate> FromAider(MiniYaml.Doc doc, string origin)
    {
        var model = doc.Scalars.GetValueOrDefault("model");
        var pairs = new List<(KnownService Service, string Key, string? BaseUrl)>();
        if (doc.Scalars.GetValueOrDefault("openai-api-key") is { } openaiKey)
        {
            pairs.Add((KnownServices.ById("openai")!, openaiKey, doc.Scalars.GetValueOrDefault("openai-api-base")));
        }

        if (doc.Scalars.GetValueOrDefault("anthropic-api-key") is { } anthropicKey)
        {
            pairs.Add((KnownServices.ById("anthropic")!, anthropicKey, null));
        }

        var apiKeys = doc.Lists.GetValueOrDefault("api-key") ?? (doc.Scalars.GetValueOrDefault("api-key") is { } one ? [one] : []);
        foreach (var entry in apiKeys)
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && KnownServices.ByAlias(entry[..eq].Trim()) is { } service && entry[(eq + 1)..].Trim() is { Length: > 0 } key)
            {
                pairs.Add((service, key, null));
            }
        }

        foreach (var (service, key, baseUrl) in pairs)
        {
            var models = model is not null && model.IndexOf('/', StringComparison.Ordinal) is var slash && slash > 0
                && KnownServices.ByAlias(model[..slash]) == service ? [model[(slash + 1)..]] : new List<string>();
            var protocol = baseUrl is not null && service.Id == "openai" ? WireProtocol.OpenAiChat : service.Protocol;
            yield return new RawCandidate(Aider, origin, protocol, baseUrl ?? service.BaseUrl, key, models);
        }
    }

    private static bool IsLocal(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && KnownServices.IsLocal(uri);

    private static JsonNode? Json(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>给人看的路径:家目录写成 <c>~</c>。</summary>
    private static string Display(string path, string home) =>
        home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..].Replace('\\', '/') : path;
}
