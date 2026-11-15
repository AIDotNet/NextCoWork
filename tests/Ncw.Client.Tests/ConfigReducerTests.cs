using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>目录 / 供应商镜像的折叠规则(规则 5–7):全量状态只收不更旧的、旧快照不能盖掉新的、断开是未知、key 带 deviceId。</summary>
public class ConfigReducerTests
{
    private const string Dev = "dev-1";

    private static CatalogTopicState Catalog(long version, int models = 10, params ModelOverrideDto[] overrides) =>
        new(new CatalogStatusDto(version, "remote", 1, 1, null, models, 3, null), overrides);

    private static ProviderDto Provider(string id, bool key = true) => new(id, id, "openai-chat", "https://x", 50, true, "5m", null, key, null);

    private static ProvidersTopicState Providers(long version, params ProviderDto[] providers) => new(version, providers, [], [], []);

    [Fact]
    public void A_newer_catalog_replaces_the_state_and_an_older_replay_is_ignored()
    {
        var m = ConfigReducer.ApplyCatalog(CatalogMirror.Empty, Dev, Catalog(5, 10));
        m = ConfigReducer.ApplyCatalog(m, Dev, Catalog(7, 12, new ModelOverrideDto(null, "x")));
        var same = ConfigReducer.ApplyCatalog(m, Dev, Catalog(6, 99));

        Assert.Same(m, same); // 旧版本的重放:不变
        Assert.Equal((true, 7, 12), (m.Of(Dev).Known, (int)m.Of(Dev).Status.Version, m.Of(Dev).Status.ModelCount));
        Assert.Single(m.Of(Dev).Overrides);

        // 同一版本(只是状态 / 覆盖变了)照收
        m = ConfigReducer.ApplyCatalog(m, Dev, Catalog(7, 12));
        Assert.Empty(m.Of(Dev).Overrides);
    }

    [Fact]
    public void The_catalog_providers_list_is_only_kept_for_the_version_it_was_fetched_at()
    {
        var m = ConfigReducer.ApplyCatalog(CatalogMirror.Empty, Dev, Catalog(5));
        m = ConfigReducer.ApplyCatalogProviders(m, Dev, new CatalogProvidersResult(5, [new CatalogProviderDto("anthropic", "Anthropic", null, null, [], 3)]));
        Assert.Single(m.Of(Dev).Providers);

        // 目录版本变了:旧的列表作废(等重拉),拉取期间过期的结果不收
        m = ConfigReducer.ApplyCatalog(m, Dev, Catalog(8));
        Assert.Empty(m.Of(Dev).Providers);
        var stale = ConfigReducer.ApplyCatalogProviders(m, Dev, new CatalogProvidersResult(5, [new CatalogProviderDto("old", "Old", null, null, [], 1)]));
        Assert.Same(m, stale);
        m = ConfigReducer.ApplyCatalogProviders(m, Dev, new CatalogProvidersResult(8, [new CatalogProviderDto("anthropic", "Anthropic", null, null, [], 4)]));
        Assert.Equal(8, m.Of(Dev).ProvidersVersion);
    }

    [Fact]
    public void Providers_are_replaced_by_the_newer_snapshot_and_the_same_version_is_accepted_for_credential_changes()
    {
        var m = ConfigReducer.ApplyProviders(ProvidersMirror.Empty, Dev, Providers(3, Provider("a", key: false)));
        m = ConfigReducer.ApplyProviders(m, Dev, Providers(3, Provider("a", key: true))); // 设置了 key:版本不变,HasCredential 变了
        Assert.True(m.Of(Dev).Providers[0].HasCredential);

        var older = ConfigReducer.ApplyProviders(m, Dev, Providers(2));
        Assert.Same(m, older);
        m = ConfigReducer.ApplyProviders(m, Dev, Providers(4, Provider("a"), Provider("b")));
        Assert.Equal(2, m.Of(Dev).Providers.Length);
    }

    [Fact]
    public void Account_changes_replace_one_providers_list_and_options_remember_which_versions_they_were_computed_for()
    {
        var account = new ProviderAccountDto("a1", "codex", "chatgpt", null, 0, true, true, false, null, null, 1, 1, null);
        var m = ConfigReducer.ApplyProviders(ProvidersMirror.Empty, Dev, Providers(1, Provider("codex")));
        m = ConfigReducer.ApplyAccounts(m, Dev, new ProviderAccountsDto("codex", [account]));
        Assert.Single(m.Of(Dev).AccountsOf("codex"));
        Assert.Empty(m.Of(Dev).AccountsOf("other"));
        m = ConfigReducer.ApplyAccounts(m, Dev, new ProviderAccountsDto("codex", []));
        Assert.Empty(m.Of(Dev).AccountsOf("codex"));

        m = ConfigReducer.ApplyOptions(m, Dev, new ModelOptionsResult([]), 1, 5);
        Assert.Equal((1L, 5L), m.Of(Dev).OptionsFor);
    }

    [Fact]
    public void Oauth_progress_is_kept_only_while_waiting_and_a_disconnect_makes_everything_unknown()
    {
        var m = ConfigReducer.ApplyProviders(ProvidersMirror.Empty, Dev, Providers(1, Provider("codex")));
        m = ConfigReducer.ApplyOAuth(m, Dev, new OAuthProgressDto("codex", OAuthPhases.Waiting, "https://auth"));
        Assert.Equal("https://auth", m.Of(Dev).OAuth!.AuthorizeUrl);
        m = ConfigReducer.ApplyOAuth(m, Dev, new OAuthProgressDto("codex", OAuthPhases.Succeeded));
        Assert.Null(m.Of(Dev).OAuth);

        m = ConfigReducer.ApplyOAuth(m, Dev, new OAuthProgressDto("codex", OAuthPhases.Waiting, "https://auth"));
        m = ConfigReducer.MarkProvidersUnknown(m, Dev);
        Assert.Equal((false, null), (m.Of(Dev).Known, m.Of(Dev).OAuth));
        // 断开期间的旧值还在(界面可以灰显),但重连后第一份快照无条件采用(哪怕版本号更小:设备可能换过数据)
        Assert.Single(m.Of(Dev).Providers);
        m = ConfigReducer.ApplyProviders(m, Dev, Providers(0));
        Assert.Equal((true, 0), (m.Of(Dev).Known, m.Of(Dev).Providers.Length));

        var catalog = ConfigReducer.MarkCatalogUnknown(ConfigReducer.ApplyCatalog(CatalogMirror.Empty, Dev, Catalog(9)), Dev);
        Assert.False(catalog.Of(Dev).Known);
        Assert.Same(catalog, ConfigReducer.MarkCatalogUnknown(catalog, Dev)); // 已经是未知:不变
    }

    [Fact]
    public void Devices_are_keyed_apart_and_an_unseen_device_is_unknown()
    {
        var m = ConfigReducer.ApplyProviders(ProvidersMirror.Empty, "dev-a", Providers(1, Provider("a")));
        m = ConfigReducer.ApplyProviders(m, "dev-b", Providers(1, Provider("b1"), Provider("b2")));

        Assert.Single(m.Of("dev-a").Providers);
        Assert.Equal(2, m.Of("dev-b").Providers.Length);
        Assert.False(m.Of("dev-c").Known);
        Assert.False(CatalogMirror.Empty.Of("dev-c").Known);
    }
}
