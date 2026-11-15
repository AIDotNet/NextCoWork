using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

public static class ProviderFileProblemKeys
{
    public const string InvalidId = "providers.file.invalidId";
    public const string ManagedId = "providers.file.managedId";
    public const string NotObject = "providers.file.notObject";
    public const string MissingBaseUrl = "providers.file.missingBaseUrl";
    public const string InvalidProtocol = "providers.file.invalidProtocol";
    public const string InvalidCredential = "providers.file.invalidCredential";
    public const string ModelsNotObject = "providers.file.modelsNotObject";
    public const string ParseFailed = "providers.file.parseFailed";
}

/// <summary>读文件时的诊断(坏条目被跳过而不是让整份文件失败)。<see cref="Key"/> 供控制端翻译,<see cref="Detail"/> 是英文。</summary>
public sealed record ProviderFileProblem(string? ProviderId, string Key, string Detail);

public sealed record ProviderFileChange(IReadOnlyList<ProviderRecord> Providers, IReadOnlyList<ModelAliasRecord> Aliases);

/// <summary>
/// 用户自己添加的供应商(+ 模型别名)的文件存储:数据根的 profile 根下的 <c>providers.json</c>(沿用 2.x 的路径与格式,接管数据后直接可用)。
/// 为什么是文件而不是库:外部工具(本地网关、脚本、用户手改)需要一个能读能写的入口;文件变化时热加载。
/// <para>格式(version 1):provider 与模型都是「id → 对象」的 map,外部工具可以按 key 路径整条增删。</para>
/// <para>
/// ★★ <b>没改动的条目原样保留</b>(包括不认识的字段),只在「这一条的有效内容真的变了」时才改写那一条:外部工具会记下它写进去的值,
/// 事后用「当前值是否还等于我写的」判断用户有没有改过,一次无谓的「规范化回写」会让它误判。
/// </para>
/// <para>★★ <b>解析失败不覆盖</b>:文件当前是坏的(用户改到一半)时内存里保留上一份好的,并拒绝任何写入。</para>
/// <para>
/// 3.0 的差异:密钥不再写进这个文件(2.x 文件模式把 <c>apiKey</c> 明文写在里面,0600 权限)。读仍然兼容——外部工具手写的 <c>apiKey</c> /
/// <c>credential</c> 照常可用(<see cref="GetCredential"/>);写走 NCK1(<see cref="ProviderCredentialVault"/>),并清掉文件里的旧明文。
/// </para>
/// </summary>
public sealed class ProviderFileStore(string path)
{
    public const string FileName = "providers.json";
    public const int FileVersion = 1;

    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan WatchPoll = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private JsonObject _doc = NewDoc();
    private Dictionary<string, ParsedProvider> _parsed = [];
    private string? _lastHash;
    private string? _loadError;
    private IReadOnlyList<ProviderFileProblem> _problems = [];
    private readonly Dictionary<string, string?> _pendingCredentials = [];
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _poll;

    public string Path { get; } = path;

    /// <summary>外部修改(外部工具、用户手改)并已热加载、且内容确实变了时触发。自己的写入不触发(靠内容哈希挡掉回声)。</summary>
    public event Action<ProviderFileChange>? ExternalChange;

    private sealed record ParsedProvider(ProviderRecord Provider, string? Credential, List<ModelAliasRecord> Aliases);

    private static JsonObject NewDoc() => new() { ["version"] = FileVersion, ["providers"] = new JsonObject() };

    // ── 读 ──

    /// <summary>从磁盘(重新)载入。文件不存在 = 空配置;损坏 = 保留上一份并记下原因。</summary>
    public bool Load() => Reload(notify: false);

    /// <summary>文件当前不可解析时的原因(英文诊断);null = 正常。非 null 时一切写入都被拒绝。</summary>
    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _loadError;
            }
        }
    }

    public IReadOnlyList<ProviderFileProblem> Problems
    {
        get
        {
            lock (_gate)
            {
                return _problems;
            }
        }
    }

    public bool Exists() => File.Exists(Path);

    public IReadOnlyList<ProviderRecord> ListProviders()
    {
        lock (_gate)
        {
            return _parsed.Values.Select(p => p.Provider).OrderBy(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<ModelAliasRecord> ListAliases()
    {
        lock (_gate)
        {
            return _parsed.Values.SelectMany(p => p.Aliases)
                .OrderBy(a => a.ProviderId, StringComparer.Ordinal).ThenBy(a => a.Alias, StringComparer.Ordinal).ToList();
        }
    }

    public bool HasProvider(string id)
    {
        lock (_gate)
        {
            return _parsed.ContainsKey(id);
        }
    }

    /// <summary>文件里写着的凭据(已序列化:裸 key 或 JSON 串);没有 = null。</summary>
    public string? GetCredential(string providerId)
    {
        lock (_gate)
        {
            return _parsed.GetValueOrDefault(providerId)?.Credential;
        }
    }

    // ── 写 ──

    public ProviderRecord PutProvider(ProviderRecord provider)
    {
        if (provider.Id == ProviderRefs.ManagedProviderId)
        {
            throw new ProviderException(ProviderErrorKeys.ManagedProvider, "The managed provider is not stored in the file.");
        }

        if (!ProviderRefs.IsValidProviderId(provider.Id))
        {
            throw new ProviderException(ProviderErrorKeys.InvalidId, $"Invalid provider id: {provider.Id}");
        }

        lock (_gate)
        {
            Mutate(providers =>
            {
                var existing = _parsed.GetValueOrDefault(provider.Id);
                var raw = providers[provider.Id] as JsonObject;
                if (existing is not null && raw is not null && Signature(existing.Provider) == Signature(provider))
                {
                    return false; // 有效内容没变:一个字节都不动
                }

                var next = raw ?? new JsonObject();
                next["name"] = provider.Name;
                next["protocol"] = provider.Protocol.NameOf();
                next["baseUrl"] = provider.BaseUrl;
                next["priority"] = provider.Priority;
                next["enabled"] = provider.Enabled;
                // 这两个可选字段被清掉时,文件里也要清掉,不能残留旧值。
                SetOrRemove(next, "protocolOptions", provider.ProtocolOptions);
                SetOrRemove(next, "videoGeneration", provider.VideoGeneration);
                if (_pendingCredentials.Remove(provider.Id, out var pending))
                {
                    ApplyCredential(next, pending);
                }

                providers[provider.Id] = next;
                return true;
            });
            return _parsed.GetValueOrDefault(provider.Id)?.Provider ?? provider;
        }
    }

    /// <summary>删供应商,连同它的模型与文件里的密钥(它们都在同一个条目里)。</summary>
    public void RemoveProvider(string id)
    {
        lock (_gate)
        {
            _pendingCredentials.Remove(id);
            Mutate(providers => providers.Remove(id));
        }
    }

    public ModelAliasRecord PutAlias(ModelAliasRecord alias)
    {
        lock (_gate)
        {
            if (!_parsed.ContainsKey(alias.ProviderId))
            {
                throw new ProviderException(ProviderErrorKeys.UnknownProvider, $"Unknown provider: {alias.ProviderId}");
            }

            Mutate(providers =>
            {
                if (providers[alias.ProviderId] is not JsonObject entry)
                {
                    return false;
                }

                var models = entry["models"] as JsonObject ?? new JsonObject();
                var current = _parsed.GetValueOrDefault(alias.ProviderId)?.Aliases.FirstOrDefault(a => a.Alias == alias.Alias);
                var next = SerializeModel(alias);
                if (current is not null && models[alias.Alias] is not null && Canonical(SerializeModel(current)) == Canonical(next))
                {
                    return false;
                }

                models[alias.Alias] = next;
                entry["models"] = models;
                return true;
            });
            return alias;
        }
    }

    public void RemoveAlias(string providerId, string alias)
    {
        lock (_gate)
        {
            Mutate(providers =>
                providers[providerId] is JsonObject { } entry && entry["models"] is JsonObject models && models.Remove(alias));
        }
    }

    /// <summary>写文件里的凭据(已序列化)。供应商还不存在时先暂存,<see cref="PutProvider"/> 时并入。3.0 的服务层不再调它(密钥走 NCK1),保留给外部兼容与测试。</summary>
    public void SetCredential(string providerId, string serialized)
    {
        if (CredentialFormat.Parse(serialized) is null)
        {
            throw new ProviderException(ProviderErrorKeys.EmptyKey, "The credential is not valid.");
        }

        lock (_gate)
        {
            if (!_parsed.ContainsKey(providerId))
            {
                _pendingCredentials[providerId] = serialized;
                return;
            }

            Mutate(providers =>
            {
                if (providers[providerId] is not JsonObject entry || GetCredential(providerId) == serialized)
                {
                    return false;
                }

                ApplyCredential(entry, serialized);
                return true;
            });
        }
    }

    /// <summary>清掉文件里这家的密钥(供应商留着)。没有可清的就不碰文件。</summary>
    public void RemoveCredential(string providerId)
    {
        lock (_gate)
        {
            _pendingCredentials.Remove(providerId);
            if (!_parsed.ContainsKey(providerId))
            {
                return;
            }

            Mutate(providers =>
            {
                if (providers[providerId] is not JsonObject entry || (!entry.ContainsKey("apiKey") && !entry.ContainsKey("credential")))
                {
                    return false;
                }

                ApplyCredential(entry, null);
                return true;
            });
        }
    }

    // ── 监听 ──

    /// <summary>
    /// 监听磁盘上的外部修改。监听的是<b>目录</b>而不是文件:编辑器与外部工具常用「写临时文件再 rename」,对文件本身的 watch 会在 rename 之后悄悄失效;
    /// 另有低频轮询兜底(满载、网络盘、inotify 额度耗尽时会丢事件)。
    /// </summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null || _poll is not null)
            {
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            Directory.CreateDirectory(directory);
            try
            {
                var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                var name = System.IO.Path.GetFileName(Path);
                void OnEvent(object? _, FileSystemEventArgs e)
                {
                    if (e.Name is not null && e.Name != name)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _debounce ??= new Timer(_ => Reload(notify: true));
                        _debounce.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
                    }
                }

                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += (s, e) => OnEvent(s, e);
                watcher.Error += (_, _) => StopWatching();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
            {
                // 目录不可监听(权限 / 文件系统不支持):靠下面的轮询兜底,不抛。
                _watcher = null;
            }

            _poll = new Timer(_ => Reload(notify: true), null, WatchPoll, WatchPoll);
        }
    }

    public void StopWatching()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            _poll?.Dispose();
            _poll = null;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    // ── 内部 ──

    /// <summary>载入磁盘内容。<paramref name="notify"/> = 来自监听,内容有变化时通知订阅者。返回 false = 文件当前不可解析(保留上一份)。</summary>
    public bool Reload(bool notify)
    {
        ProviderFileChange? change = null;
        bool ok;
        lock (_gate)
        {
            ok = ReloadLocked(notify, out change);
        }

        if (change is not null)
        {
            foreach (var handler in ExternalChange?.GetInvocationList().Cast<Action<ProviderFileChange>>() ?? [])
            {
                try
                {
                    handler(change);
                }
                catch (Exception)
                {
                    // 一个订阅者出错不能挡住其余订阅者。
                }
            }
        }

        return ok;
    }

    private bool ReloadLocked(bool notify, out ProviderFileChange? change)
    {
        change = null;
        string? text;
        try
        {
            text = File.Exists(Path) ? File.ReadAllText(Path, Encoding.UTF8) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _loadError = $"Cannot read {FileName}: {e.Message}";
            return false;
        }

        var hash = text is null ? "missing" : Sha(text);
        if (hash == _lastHash && _loadError is null)
        {
            return true;
        }

        var doc = NewDoc();
        if (text is not null && text.Trim().Length > 0)
        {
            try
            {
                var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (node is not JsonObject root)
                {
                    throw new JsonException("The top level must be an object.");
                }

                if (root["providers"] is not null and not JsonObject)
                {
                    throw new JsonException("providers must be an object of id to config.");
                }

                // 保留顶层所有不认识的键;providers 缺失补空。
                doc = new JsonObject { ["version"] = FileVersion };
                foreach (var (key, value) in root.ToList())
                {
                    root.Remove(key);
                    doc[key] = value;
                }

                doc["providers"] ??= new JsonObject();
            }
            catch (JsonException e)
            {
                _lastHash = hash;
                _loadError = $"{FileName} could not be parsed: {e.Message}";
                return false;
            }
        }

        _lastHash = hash;
        _loadError = null;
        _doc = doc;
        var before = notify ? Snapshot() : "";
        Rebuild();
        if (notify && Snapshot() != before)
        {
            change = new ProviderFileChange(ListProviders(), ListAliases());
        }

        return true;
    }

    private string Snapshot() => Canonical(new JsonObject
    {
        ["p"] = new JsonArray(_parsed.Values.OrderBy(p => p.Provider.Id, StringComparer.Ordinal).Select(p => (JsonNode?)new JsonObject
        {
            ["provider"] = Signature(p.Provider),
            ["credential"] = p.Credential,
            ["aliases"] = new JsonArray(p.Aliases.OrderBy(a => a.Alias, StringComparer.Ordinal).Select(a => (JsonNode?)SerializeModel(a)).ToArray()),
        }).ToArray()),
    });

    private void Rebuild()
    {
        var problems = new List<ProviderFileProblem>();
        var parsed = new Dictionary<string, ParsedProvider>(StringComparer.Ordinal);
        if (_doc["providers"] is JsonObject providers)
        {
            foreach (var (id, raw) in providers)
            {
                if (ParseProvider(id, raw, problems) is { } provider)
                {
                    parsed[id] = provider;
                }
            }
        }

        _parsed = parsed;
        _problems = problems;
    }

    private static ParsedProvider? ParseProvider(string id, JsonNode? raw, List<ProviderFileProblem> problems)
    {
        if (!ProviderRefs.IsValidProviderId(id))
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.InvalidId, $"Provider id \"{id}\" is invalid (empty, or contains whitespace or \"/\"); ignored."));
            return null;
        }

        if (id == ProviderRefs.ManagedProviderId)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.ManagedId, $"\"{id}\" is the built-in managed provider and cannot be defined in {FileName}; ignored."));
            return null;
        }

        if (raw is not JsonObject entry)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.NotObject, "The entry is not an object; ignored."));
            return null;
        }

        var baseUrl = NonEmpty(entry["baseUrl"]);
        if (baseUrl is null)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.MissingBaseUrl, "baseUrl is missing; ignored."));
            return null;
        }

        if (WireProtocols.Parse(NonEmpty(entry["protocol"])) is not { } protocol)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.InvalidProtocol, "protocol must be anthropic, openai-chat or openai-responses; ignored."));
            return null;
        }

        var (credential, error) = CredentialOf(entry);
        if (error is not null)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.InvalidCredential, error));
        }

        var provider = new ProviderRecord(
            id,
            NonEmpty(entry["name"]) ?? id,
            protocol,
            baseUrl.Trim(),
            Int(entry["priority"]) ?? ProviderRecord.DefaultPriority,
            Bool(entry["enabled"]) ?? true)
        {
            ProtocolOptions = entry["protocolOptions"] is JsonObject po ? Clone(po) : null,
            VideoGeneration = entry["videoGeneration"] is JsonObject vg ? Clone(vg) : null,
        };

        var aliases = new List<ModelAliasRecord>();
        if (entry["models"] is not null and not JsonObject)
        {
            problems.Add(new ProviderFileProblem(id, ProviderFileProblemKeys.ModelsNotObject, "models must be an object of alias to config; the model list is ignored."));
        }
        else if (entry["models"] is JsonObject models)
        {
            foreach (var (alias, model) in models)
            {
                if (alias.Trim().Length > 0)
                {
                    aliases.Add(ParseModel(id, alias, model));
                }
            }
        }

        return new ParsedProvider(provider, credential, aliases);
    }

    private static readonly HashSet<string> TypedModelKeys =
        ["upstreamModel", "displayName", "name", "contextWindow", "maxOutputTokens", "protocolOverride", "priority", "modality", "enabled"];

    private static ModelAliasRecord ParseModel(string providerId, string alias, JsonNode? raw)
    {
        var m = raw as JsonObject ?? new JsonObject();
        var extra = new JsonObject();
        foreach (var (key, value) in m)
        {
            if (!TypedModelKeys.Contains(key))
            {
                extra[key] = value?.DeepClone();
            }
        }

        return new ModelAliasRecord(alias, providerId, NonEmpty(m["upstreamModel"]) ?? alias)
        {
            ContextWindow = PositiveInt(m["contextWindow"]) ?? ModelAliasRecord.DefaultContextWindow,
            MaxOutputTokens = PositiveInt(m["maxOutputTokens"]) ?? ModelAliasRecord.DefaultMaxOutputTokens,
            DisplayName = NonEmpty(m["displayName"]) ?? NonEmpty(m["name"]),
            ProtocolOverride = WireProtocols.Parse(NonEmpty(m["protocolOverride"])),
            Priority = Int(m["priority"]),
            Modality = NonEmpty(m["modality"]),
            Enabled = Bool(m["enabled"]),
            Extra = extra,
        };
    }

    /// <summary>别名 → 文件里的模型条目(不含 alias / providerId:它们由 map 的键与父级给出)。</summary>
    internal static JsonObject SerializeModel(ModelAliasRecord a)
    {
        var o = new JsonObject { ["upstreamModel"] = a.UpstreamModel };
        if (a.DisplayName is not null)
        {
            o["displayName"] = a.DisplayName;
        }

        o["contextWindow"] = a.ContextWindow;
        o["maxOutputTokens"] = a.MaxOutputTokens;
        if (a.ProtocolOverride is { } po)
        {
            o["protocolOverride"] = po.NameOf();
        }

        if (a.Priority is { } priority)
        {
            o["priority"] = priority;
        }

        if (a.Modality is not null)
        {
            o["modality"] = a.Modality;
        }

        if (a.Enabled is { } enabled)
        {
            o["enabled"] = enabled;
        }

        foreach (var (key, value) in a.Extra)
        {
            o[key] = value?.DeepClone();
        }

        return o;
    }

    private static (string? Credential, string? Error) CredentialOf(JsonObject entry)
    {
        if (entry["credential"] is JsonObject credential)
        {
            var parsed = CredentialFormat.Parse(credential.ToJsonString());
            // credential 字段只用于 oauth / signature;api-key 写在 apiKey 里。
            return parsed is null or ApiKeyCredential
                ? (null, "credential is not a valid oauth or signature credential.")
                : (CredentialFormat.Serialize(parsed), null);
        }

        return (NonEmpty(entry["apiKey"])?.Trim(), null);
    }

    private static void ApplyCredential(JsonObject entry, string? serialized)
    {
        entry.Remove("apiKey");
        entry.Remove("credential");
        if (string.IsNullOrEmpty(serialized) || CredentialFormat.Parse(serialized) is not { } parsed)
        {
            return;
        }

        if (parsed is ApiKeyCredential key)
        {
            entry["apiKey"] = key.ApiKey;
        }
        else
        {
            entry["credential"] = JsonNode.Parse(CredentialFormat.Serialize(parsed));
        }
    }

    /// <summary>读-改-写。写之前先按磁盘现状重读一次(外部工具可能刚改过而监听事件还在防抖里);当前文件是坏的就拒绝。</summary>
    private void Mutate(Func<JsonObject, bool> change)
    {
        ReloadLocked(notify: false, out _);
        if (_loadError is { } error)
        {
            throw new ProviderException(ProviderErrorKeys.FileBroken, error);
        }

        var providers = (JsonObject)(_doc["providers"] ??= new JsonObject());
        if (!change(providers))
        {
            return;
        }

        Persist();
        Rebuild();
    }

    private void Persist()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        var text = ToIndentedJson(_doc) + "\n";
        var temp = $"{Path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            TryRestrictPermissions(temp);
            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // 临时文件本来就没写出来
            }

            throw;
        }

        _lastHash = Sha(text);
        _loadError = null;
    }

    /// <summary>文件可能被外部工具写进明文 key,所以仍收紧权限(0600);Windows 上是 no-op。</summary>
    private static void TryRestrictPermissions(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 不支持 chmod 的文件系统
        }
    }

    // ── 工具 ──

    private static string ToIndentedJson(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static JsonObject Clone(JsonObject o) => (JsonObject)o.DeepClone();

    private static void SetOrRemove(JsonObject target, string key, JsonObject? value)
    {
        if (value is null)
        {
            target.Remove(key);
        }
        else
        {
            target[key] = value.DeepClone();
        }
    }

    private static string? NonEmpty(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s : null;

    /// <summary>
    /// 读数字。内存里新赋的节点(<c>node = 5</c>)是 <c>JsonValue&lt;int&gt;</c>,<c>TryGetValue&lt;double&gt;</c> 对它返回 false;
    /// 从文本解析出来的是 JsonElement 背书的,两种都认。
    /// </summary>
    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        }

        return v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;
    }

    private static int? PositiveInt(JsonNode? node) => Int(node) is > 0 and var n ? n : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>文件里 provider 条目的「有效内容」签名——用来判断一次 put 是不是真的改了什么。</summary>
    private static string Signature(ProviderRecord p) => Canonical(new JsonObject
    {
        ["name"] = p.Name,
        ["protocol"] = p.Protocol.NameOf(),
        ["baseUrl"] = p.BaseUrl,
        ["priority"] = p.Priority,
        ["enabled"] = p.Enabled,
        ["protocolOptions"] = p.ProtocolOptions?.DeepClone(),
        ["videoGeneration"] = p.VideoGeneration?.DeepClone(),
    });

    /// <summary>键序无关的稳定序列化(用于「内容是否相同」的比较)。</summary>
    internal static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(key, ProviderJson.Context.String)).Append(':');
                    WriteCanonical(value, sb);
                }

                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteCanonical(a[i], sb);
                }

                sb.Append(']');
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
