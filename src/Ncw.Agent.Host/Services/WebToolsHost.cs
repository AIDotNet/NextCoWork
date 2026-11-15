using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.WebTools.Fetch;
using Ncw.Agent.WebTools.Net;
using Ncw.Agent.WebTools.Search;
using Ncw.Agent.Host.Conversation;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 联网工具在 Host 里的装配(03 §13):HTTP 出口(<see cref="IWebHttp"/>,默认 <see cref="GuardedHttp"/>:解析 → 校验 → 钉死 IP)、WebFetch / web_search 两个工具、搜索提供方的配置
/// (库里 2.x 的 <c>search_providers</c> 表,一行 <c>id + priority + json</c>)与 API Key(NCK1 凭据库,ref <c>websearch:&lt;id&gt;</c>)、<c>websearch.*</c> 方法与 <c>websearch</c> 主题的状态。
/// 它同时是 <see cref="ISearchConfigSource"/>:<b>明文 Key 只有 <see cref="SearchService"/> 经它取</b>,工具和协议都拿不到。
/// </summary>
internal sealed class WebToolsHost : ISearchConfigSource, IDisposable
{
    private readonly SearchConfigTable _table;
    private readonly CredentialStore _credentials;
    private readonly Lock _gate = new();
    private long _version;
    private readonly GuardedHttp? _ownedHttp;

    public WebToolsHost(AgentDatabase database, string dataRoot, AgentServices services)
    {
        _table = new SearchConfigTable(database);
        _credentials = new CredentialStore(database, dataRoot, NoLegacySecrets.Instance);
        if (services.WebHttp is { } injected)
        {
            Http = injected;
        }
        else
        {
            _ownedHttp = new GuardedHttp(services.WebPolicy);
            Http = _ownedHttp;
        }

        Search = new SearchService(this, Http, services.WebSearch);
        FetchTool = new WebFetchTool(Http, services.WebFetch, services.WebPolicy);
        SearchTool = new WebSearchTool(Search);
    }

    public IWebHttp Http { get; }

    public SearchService Search { get; }

    public WebFetchTool FetchTool { get; }

    public WebSearchTool SearchTool { get; }

    /// <summary>状态变了(启停 / 顺序 / Key):Host 据此发布 <c>websearch</c> 主题。</summary>
    public event Action<WebSearchTopicState>? Changed;

    // ───────────────────────── ISearchConfigSource ─────────────────────────

    public ValueTask<IReadOnlyList<SearchProviderStatus>> StatusesAsync(CancellationToken ct) => ValueTask.FromResult(Statuses());

    public ValueTask<string?> ApiKeyAsync(string id, CancellationToken ct)
    {
        try
        {
            return ValueTask.FromResult(SearchProviders.IsKnown(id) ? _credentials.Get(SearchProviders.SecretRef(id)) : null);
        }
        catch (CryptographicException)
        {
            return ValueTask.FromResult<string?>(null); // 解不开:当没有,搜索链会如实记「Key 读不出来」
        }
    }

    // ───────────────────────── 方法 ─────────────────────────

    public WebSearchTopicState State() => new(Volatile.Read(ref _version), [.. Statuses().Select(ToDto)]);

    public WebSearchTopicState SetEnabled(string id, bool enabled)
    {
        Require(id);
        if (enabled && !SearchProviders.IsUsable(id))
        {
            // 标了 unavailable 的家不能被启用:界面已经把开关禁掉了,但这是渲染层之外的第二个入口——「界面禁掉了」不是约束,只是提示。放进来的话它会进搜索链、拿不到适配器、每次搜索白跑一圈
            throw ServiceMapping.Invalid("websearch.error.unavailable", "This search provider cannot be used right now, so it cannot be enabled.", ("id", id));
        }

        lock (_gate)
        {
            var current = _table.List();
            _table.PutAll([.. current.Select(c => c.Id == id ? c with { Enabled = enabled } : c)]);
        }

        return Bump();
    }

    public WebSearchTopicState Reorder(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            // 整批写、一个事务:半新半旧的 priority 会让「今天先试 A、明天先试 B」
            _table.PutAll(SearchProviders.ApplyOrder(_table.List(), ids));
        }

        return Bump();
    }

    public WebSearchCredentialInfoDto SetCredential(string id, string apiKey)
    {
        Require(id);
        var trimmed = apiKey.Trim();
        if (trimmed.Length == 0)
        {
            throw ServiceMapping.Invalid("websearch.invalid.apiKey", "The API key must not be empty. To remove it, clear the credential.");
        }

        _credentials.Set(SearchProviders.SecretRef(id), trimmed);
        Bump();
        return new WebSearchCredentialInfoDto(true, SearchProviders.Last4(trimmed));
    }

    public WebSearchTopicState ClearCredential(string id)
    {
        Require(id);
        _credentials.Remove(SearchProviders.SecretRef(id));
        lock (_gate)
        {
            // 顺手把这家关掉:留着「已启用但没 Key」的状态,搜索链会把它滤掉,于是界面上开关是开的、搜索却永远不经过它
            var current = _table.List();
            _table.PutAll([.. current.Select(c => c.Id == id && c.Enabled ? c with { Enabled = false } : c)]);
        }

        return Bump();
    }

    public async Task<WebSearchTestResultDto> TestAsync(string id, CancellationToken ct)
    {
        Require(id);
        var r = await Search.TestAsync(id, ct).ConfigureAwait(false);
        return new WebSearchTestResultDto(r.Ok, r.LatencyMs, r.Key, r.Detail);
    }

    /// <summary>数据就绪 / 被动变化时重新发布(状态是全量,不是增量)。</summary>
    public void Publish() => Changed?.Invoke(State());

    // ───────────────────────── 内部 ─────────────────────────

    private WebSearchTopicState Bump()
    {
        Interlocked.Increment(ref _version);
        var state = State();
        Changed?.Invoke(state);
        return state;
    }

    private static void Require(string id)
    {
        if (!SearchProviders.IsKnown(id))
        {
            throw ServiceMapping.Invalid("websearch.invalid.provider", "There is no search provider with this id.", ("id", id));
        }
    }

    private IReadOnlyList<SearchProviderStatus> Statuses()
    {
        var merged = SearchProviders.Merge(_table.List());
        var list = new List<SearchProviderStatus>(merged.Count);
        foreach (var config in merged)
        {
            string? key = null;
            var hasKey = false;
            try
            {
                key = _credentials.Get(SearchProviders.SecretRef(config.Id));
                hasKey = !string.IsNullOrEmpty(key);
            }
            catch (CryptographicException)
            {
                hasKey = true; // 有这一行但解不开:列表上仍是「已配置」,真正用到时搜索链会如实报「Key 读不出来」
            }

            list.Add(new SearchProviderStatus(config, hasKey, SearchProviders.Last4(key)));
        }

        return list;
    }

    private static WebSearchProviderDto ToDto(SearchProviderStatus s)
    {
        var meta = SearchProviders.Find(s.Config.Id)!;
        return new WebSearchProviderDto(meta.Id, meta.Name, meta.KeyUrl, meta.UnavailableKey, s.Config.Enabled, s.Config.Priority, s.HasKey, s.Last4);
    }

    public void Dispose() => _ownedHttp?.Dispose();
}

/// <summary>库里的 <c>search_providers</c> 表(2.x 原样:<c>id</c> + <c>priority</c> 真列 + <c>json</c>,json 是 <c>{"id","enabled","priority"}</c>)。读不懂的行跳过。</summary>
internal sealed class SearchConfigTable(AgentDatabase database)
{
    public IReadOnlyList<SearchProviderConfig> List()
    {
        lock (database.Gate)
        {
            using var command = database.Command("SELECT json FROM search_providers");
            using var reader = command.ExecuteReader();
            var list = new List<SearchProviderConfig>();
            while (reader.Read())
            {
                if (Parse(reader.GetString(0)) is { } config)
                {
                    list.Add(config);
                }
            }

            return SearchProviders.Merge(list);
        }
    }

    /// <summary>整批写,一个事务。</summary>
    public void PutAll(IReadOnlyList<SearchProviderConfig> configs) =>
        database.InTransaction(() =>
        {
            foreach (var config in configs)
            {
                using var command = database.Command(
                    "INSERT INTO search_providers (id, priority, json) VALUES ($id, $priority, $json) ON CONFLICT (id) DO UPDATE SET priority = excluded.priority, json = excluded.json");
                command.Parameters.AddWithValue("$id", config.Id);
                command.Parameters.AddWithValue("$priority", config.Priority);
                command.Parameters.AddWithValue("$json", Write(config));
                command.ExecuteNonQuery();
            }

            return 0;
        });

    private static SearchProviderConfig? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var priority = root.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : int.MaxValue;
            return SearchProviders.IsKnown(id.GetString()!) ? new SearchProviderConfig(id.GetString()!, enabled, priority) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Write(SearchProviderConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", config.Id);
            w.WriteBoolean("enabled", config.Enabled);
            w.WriteNumber("priority", config.Priority);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

internal static class WebSearchHandlers
{
    public static void Register(MethodRouter router, Func<WebToolsHost> web)
    {
        router.Handle(WebSearchMethods.List, (_, _, _) => ServiceMapping.Guard(() => web().State()));
        router.Handle(WebSearchMethods.SetEnabled, (p, _, _) => ServiceMapping.Guard(() => web().SetEnabled(p.Id, p.Enabled)));
        router.Handle(WebSearchMethods.Reorder, (p, _, _) => ServiceMapping.Guard(() => web().Reorder(p.Ids)));
        router.Handle(WebSearchMethods.SetCredential, (p, _, _) => ServiceMapping.Guard(() => web().SetCredential(p.Id, p.ApiKey)));
        router.Handle(WebSearchMethods.ClearCredential, (p, _, _) => ServiceMapping.Guard(() => web().ClearCredential(p.Id)));
        router.Handle(WebSearchMethods.Test, (p, _, ct) => ServiceMapping.GuardAsync(() => web().TestAsync(p.Id, ct)));
    }
}
