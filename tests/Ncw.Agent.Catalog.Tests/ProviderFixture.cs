using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>供应商服务的测试夹具:临时目录里的真 SQLite 库 + 真 providers.json,上游和凭据库是假的,目录用内置种子。</summary>
public sealed class ProviderFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ncwprov-{Guid.NewGuid():N}");

    public AgentDatabase Db { get; }

    public ProviderFileStore File { get; }

    public MemoryCredentialVault Vault { get; } = new();

    public ProviderAccountRepository Accounts { get; }

    public FakeHttp Http { get; } = new();

    public ManualClock Clock { get; } = new();

    public ModelCatalogService Catalog { get; }

    public ProviderService Service { get; }

    public ProviderConfigSource Resolver { get; }

    public bool Failover { get; set; } = true;

    public ProviderFixture(ProviderServiceOptions? options = null, IProviderPresets? presets = null)
    {
        Directory.CreateDirectory(Root);
        Db = AgentDatabase.Open(Root);
        File = new ProviderFileStore(Path.Combine(Root, ProviderFileStore.FileName));
        Accounts = new ProviderAccountRepository(Db, Vault, Clock);
        Catalog = new ModelCatalogService(new FakeHttp(), new MemoryCatalogStore(), new MemoryOverrideStore(), Clock, new ManualDelayer());
        Service = new ProviderService(File, Vault, Accounts, Http, Catalog, Clock, presets, options: options);
        Resolver = new ProviderConfigSource(Service, Catalog, () => Failover);
    }

    public void Dispose()
    {
        File.StopWatching();
        Catalog.Dispose();
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }

    public ProviderRecord AddProvider(string id = "gw", WireProtocol protocol = WireProtocol.OpenAiChat, int priority = 50, bool enabled = true, string? baseUrl = null) =>
        Service.Upsert(new ProviderRecord(id, id.ToUpperInvariant(), protocol, baseUrl ?? $"https://{id}.example.com/v1", priority, enabled));

    public string ReadFile() => System.IO.File.ReadAllText(File.Path);

    public JsonObject FileEntry(string id) => (JsonObject)((JsonObject)JsonNode.Parse(ReadFile())!)["providers"]![id]!;
}
