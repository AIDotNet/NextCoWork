using Microsoft.Data.Sqlite;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>搜索提供方在库里的存放(2.x 的 <c>search_providers</c> 表 + NCK1 凭据 <c>websearch:&lt;id&gt;</c>):往返、2.x 形状的行、整批写、Key 加密落库且不出现在任何状态里。</summary>
public sealed class WebSearchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncw-wss-" + Guid.NewGuid().ToString("N")[..10]);

    public WebSearchStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WebToolsHost Host(AgentDatabase db, string root) => new(db, root, new AgentServices());

    [Fact]
    public void An_empty_table_still_lists_the_whole_catalog_all_disabled_with_no_keys()
    {
        using var db = AgentDatabase.Open(_root);
        using var web = Host(db, _root);
        var state = web.State();
        Assert.Equal(8, state.Providers.Length);
        Assert.Equal(["tavily", "exa", "brave", "serper", "serpapi", "metaso", "doubao", "bing"], state.Providers.Select(static p => p.Id));
        Assert.All(state.Providers, static p => Assert.False(p.Enabled || p.HasKey));
        Assert.Equal("websearch.unavailable.doubao", state.Providers.Single(static p => p.Id == "doubao").UnavailableKey);
    }

    [Fact]
    public async Task Configuration_survives_a_restart_and_the_key_is_encrypted_at_rest_and_never_in_the_state()
    {
        const string secret = "tvly-0123456789-SECRET";
        using (var db = AgentDatabase.Open(_root))
        using (var web = Host(db, _root))
        {
            web.SetCredential("tavily", secret);
            web.SetEnabled("tavily", true);
            web.Reorder(["serper", "tavily"]);
        }

        using (var db = AgentDatabase.Open(_root))
        using (var web = Host(db, _root))
        {
            var state = web.State();
            Assert.Equal(["serper", "tavily", "exa", "brave", "serpapi", "metaso", "doubao", "bing"], state.Providers.Select(static p => p.Id));
            var tavily = state.Providers.Single(static p => p.Id == "tavily");
            Assert.True(tavily.Enabled);
            Assert.True(tavily.HasKey);
            Assert.Equal(secret[^4..], tavily.Last4);
            Assert.DoesNotContain(secret, string.Join('|', state.Providers.Select(static p => p.ToString())));
            // 明文 Key 只有搜索服务能取
            Assert.Equal(secret, await web.ApiKeyAsync("tavily", default));
        }

        // 落库的是 NCK1 密文,ref 是 2.x 同一个 websearch:<id>
        var path = Directory.EnumerateFiles(_root, "*.db").First();
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT blob FROM credentials WHERE ref = 'websearch:tavily'";
        var blob = (byte[])cmd.ExecuteScalar()!;
        Assert.True(CredentialCrypto.IsProgramEncrypted(blob));
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(blob));
        using var rows = conn.CreateCommand();
        rows.CommandText = "SELECT id, priority, json FROM search_providers ORDER BY priority";
        using var reader = rows.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("serper", reader.GetString(0));
        Assert.Equal("""{"id":"serper","enabled":false,"priority":0}""", reader.GetString(2)); // 2.x 的 JSON 形状
    }

    [Fact]
    public void A_row_written_by_2x_is_read_and_an_existing_2x_credential_is_usable()
    {
        using (var db = AgentDatabase.Open(_root))
        {
            lock (db.Gate)
            {
                using var insert = db.Command("INSERT INTO search_providers (id, priority, json) VALUES ('brave', 0, $json)");
                insert.Parameters.AddWithValue("$json", """{"id":"brave","enabled":true,"priority":0}""");
                insert.ExecuteNonQuery();
            }

            lock (db.Gate)
            {
                using var other = db.Command("INSERT INTO search_providers (id, priority, json) VALUES ('tavily', 5, $json)");
                other.Parameters.AddWithValue("$json", """{"id":"tavily","enabled":false,"priority":5}""");
                other.ExecuteNonQuery();
            }

            new CredentialStore(db, _root, NoLegacySecrets.Instance).Set("websearch:brave", "BSA-from-2x-1234");
        }

        using var db2 = AgentDatabase.Open(_root);
        using var web = Host(db2, _root);
        var brave = web.State().Providers.Single(static p => p.Id == "brave");
        Assert.True(brave.Enabled);
        Assert.True(brave.HasKey);
        Assert.Equal("1234", brave.Last4);
        Assert.Equal("brave", web.State().Providers[0].Id); // priority 0
    }

    [Fact]
    public void Rows_that_cannot_be_understood_are_skipped_and_unknown_providers_are_ignored()
    {
        using var db = AgentDatabase.Open(_root);
        lock (db.Gate)
        {
            foreach (var (id, json) in new[] { ("exa", "not json"), ("ghost", """{"id":"ghost","enabled":true,"priority":0}"""), ("brave", """{"enabled":true}""") })
            {
                using var insert = db.Command("INSERT INTO search_providers (id, priority, json) VALUES ($id, 0, $json)");
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$json", json);
                insert.ExecuteNonQuery();
            }
        }

        using var web = Host(db, _root);
        Assert.Equal(8, web.State().Providers.Length);
        Assert.All(web.State().Providers, static p => Assert.False(p.Enabled));
    }

    [Fact]
    public void Enabling_an_unavailable_provider_and_unknown_ids_are_refused_with_stable_keys()
    {
        using var db = AgentDatabase.Open(_root);
        using var web = Host(db, _root);
        var bing = Assert.Throws<ProtocolException>(() => web.SetEnabled("bing", true));
        Assert.Equal(ErrorCodes.InvalidRequest, bing.Code);
        Assert.Throws<ProtocolException>(() => web.SetEnabled("ghost", true));
        Assert.Throws<ProtocolException>(() => web.SetCredential("ghost", "key-123456789"));
        Assert.Throws<ProtocolException>(() => web.SetCredential("tavily", "   "));
        // 关掉用不了的家是允许的(幂等)
        web.SetEnabled("bing", false);
    }

    [Fact]
    public async Task Clearing_the_key_disables_the_provider_and_every_change_bumps_the_version_and_notifies()
    {
        using var db = AgentDatabase.Open(_root);
        using var web = Host(db, _root);
        var seen = new List<WebSearchTopicState>();
        web.Changed += seen.Add;

        web.SetCredential("exa", "exa-key-abcdef");
        web.SetEnabled("exa", true);
        var cleared = web.ClearCredential("exa");

        Assert.False(cleared.Providers.Single(static p => p.Id == "exa").HasKey);
        Assert.False(cleared.Providers.Single(static p => p.Id == "exa").Enabled);
        Assert.Equal(3, seen.Count);
        Assert.Equal(seen.Select(static s => s.Version).Order(), seen.Select(static s => s.Version)); // 单调
        Assert.Equal(3, seen.Select(static s => s.Version).Distinct().Count());
        Assert.Null(await web.ApiKeyAsync("exa", default));
    }
}
