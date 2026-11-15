using Microsoft.Data.Sqlite;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>db/__tests__/provider-accounts.test.ts</c>(账号行、限流四列、额度快照、当前账号);「存量迁移」一组在 ProviderServiceTests 里。纯选号规则的测试在 Ncw.Agent.Llm.Tests(AuthTests)。</summary>
public sealed class ProviderAccountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwacct-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly MemoryCredentialVault _vault = new();
    private readonly ProviderAccountRepository _repo;

    public ProviderAccountTests()
    {
        _db = AgentDatabase.Open(_root);
        _repo = new ProviderAccountRepository(_db, _vault);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static ProviderAccountRow Row(string id, string provider = "codex", int order = 0, bool current = false) =>
        new(id, provider, "chatgpt", null, order, true, current, false, null, null, 1000, 1000);

    [Fact]
    public void Rows_come_back_by_sort_order_and_ties_break_by_id()
    {
        _repo.Put(Row("b", order: 1));
        _repo.Put(Row("c", order: 0));
        _repo.Put(Row("a", order: 1));
        Assert.Equal(["c", "a", "b"], _repo.List("codex").Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Deleting_an_account_deletes_its_ciphertext_in_the_same_transaction()
    {
        // 只删行会留下永远读不到的孤儿 token,而用户以为自己已经退出登录了。
        _repo.Put(Row("a"));
        _vault.Seed(ProviderRefs.ForAccount("codex", "a"), "secret");

        _repo.Remove("a");

        Assert.Null(_repo.Get("a"));
        Assert.Empty(_vault.Values);
    }

    [Fact]
    public void A_failing_credential_delete_rolls_the_row_deletion_back()
    {
        var failing = new ThrowingVault();
        var repo = new ProviderAccountRepository(_db, failing);
        repo.Put(Row("a"));
        Assert.Throws<InvalidOperationException>(() => repo.Remove("a"));
        Assert.NotNull(repo.Get("a"));
    }

    private sealed class ThrowingVault : ICredentialVault
    {
        public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) => ValueTask.FromResult<string?>(null);

        public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(string credentialRef, CancellationToken ct) => throw new InvalidOperationException("boom");

        public void Remove(string credentialRef) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Removing_a_provider_takes_its_accounts_and_ciphertext_with_it()
    {
        // provider_accounts 上没有外键管得着这件事。
        _repo.Put(Row("a"));
        _repo.Put(Row("b", order: 1));
        _repo.Put(Row("z", provider: "other"));
        _vault.Seed(ProviderRefs.ForAccount("codex", "a"), "s1");
        _vault.Seed(ProviderRefs.ForAccount("codex", "b"), "s2");
        _vault.Seed(ProviderRefs.ForAccount("other", "z"), "s3");

        _repo.RemoveFor("codex");

        Assert.Empty(_repo.List("codex"));
        Assert.Single(_repo.List("other"));
        Assert.Equal(["provider:other#z"], _vault.Values.Keys.ToArray());
    }

    [Fact]
    public void A_new_account_goes_last_and_an_empty_table_starts_at_zero()
    {
        Assert.Equal(0, _repo.NextOrder("codex"));
        _repo.Put(Row("a", order: 0));
        _repo.Put(Row("b", order: 4));
        Assert.Equal(5, _repo.NextOrder("codex"));
    }

    [Fact]
    public void The_whole_order_is_written_at_once()
    {
        _repo.Put(Row("a", order: 0));
        _repo.Put(Row("b", order: 1));
        _repo.Put(Row("c", order: 2));
        _repo.SetOrder("codex", ["c", "a", "b"]);
        Assert.Equal(["c", "a", "b"], _repo.List("codex").Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Disabling_only_changes_enabled_and_leaves_the_limit_columns_alone()
    {
        var limit = new AccountLimit(5000, 1000, AccountLimitSource.Http429, "429");
        _repo.Put(Row("a") with { Limit = limit });
        _repo.SetEnabled("a", false);
        var row = _repo.Get("a")!;
        Assert.False(row.Enabled);
        Assert.Equal(limit, row.Limit);
    }

    [Fact]
    public void NeedsReauth_is_a_denormalised_column_that_is_not_rewritten_when_unchanged()
    {
        _repo.Put(Row("a"));
        Assert.False(_repo.Get("a")!.NeedsReauth);
        _repo.SetNeedsReauth("a", true);
        var first = _repo.Get("a")!;
        Assert.True(first.NeedsReauth);

        // 值没变就不写:这条路径在每次 token 刷新后都会被调到。
        _db.Connection.CreateCommand().Also(c => { c.CommandText = "UPDATE provider_accounts SET updated_at = 1"; c.ExecuteNonQuery(); });
        _repo.SetNeedsReauth("a", true);
        Assert.Equal(1, _repo.Get("a")!.UpdatedAt);
        _repo.SetNeedsReauth("a", false);
        Assert.False(_repo.Get("a")!.NeedsReauth);
    }

    [Fact]
    public void A_label_is_trimmed_and_an_empty_one_falls_back_to_null()
    {
        _repo.Put(Row("a"));
        _repo.SetLabel("a", "  work  ");
        Assert.Equal("work", _repo.Get("a")!.Label);
        _repo.SetLabel("a", "   ");
        Assert.Null(_repo.Get("a")!.Label);
    }

    [Fact]
    public void The_four_limit_columns_go_in_and_out_together()
    {
        _repo.Put(Row("a"));
        var limit = new AccountLimit(9000, 1000, AccountLimitSource.QuotaExhausted, "weekly window used up");
        _repo.SetLimit("a", limit);
        Assert.Equal(limit, _repo.Get("a")!.Limit);
        _repo.SetLimit("a", null);
        Assert.Null(_repo.Get("a")!.Limit);

        // limit_until 为 NULL 时其余三列存着什么都不该被读出来(否则是永不结束的倒计时)。
        _db.Connection.CreateCommand().Also(c => { c.CommandText = "UPDATE provider_accounts SET limit_reason = 'stale', limit_source = 'manual'"; c.ExecuteNonQuery(); });
        Assert.Null(_repo.Get("a")!.Limit);
    }

    [Fact]
    public void All_three_limit_sources_round_trip()
    {
        foreach (var source in Enum.GetValues<AccountLimitSource>())
        {
            _repo.Put(Row($"a-{source}") with { Limit = new AccountLimit(10, 1, source, "r") });
            Assert.Equal(source, _repo.Get($"a-{source}")!.Limit!.Source);
        }
    }

    [Fact]
    public void A_quota_snapshot_round_trips_and_an_unreadable_one_is_treated_as_absent()
    {
        _repo.Put(Row("a"));
        var quota = new QuotaSnapshot(new QuotaWindow(42.5, 300, 7000), new QuotaWindow(10, 10080, 9000), 1234);
        _repo.SetQuota("a", quota);
        Assert.Equal(quota, _repo.Get("a")!.Quota);

        // 一个解不开的快照绝不该让整个账号列表读不出来。
        _db.Connection.CreateCommand().Also(c => { c.CommandText = "UPDATE provider_accounts SET quota_json = '{not json'"; c.ExecuteNonQuery(); });
        Assert.Null(_repo.Get("a")!.Quota);
        Assert.Single(_repo.List("codex"));
    }

    [Fact]
    public void Rows_with_an_unknown_issuer_are_skipped_instead_of_breaking_the_list()
    {
        _repo.Put(Row("a"));
        _repo.Put(Row("b", order: 1) with { Issuer = "mystery" });
        Assert.Equal(["a"], _repo.List("codex").Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Making_one_account_current_clears_the_marker_on_the_others_of_that_provider_only()
    {
        _repo.Put(Row("a", current: true));
        _repo.Put(Row("b", order: 1));
        _repo.Put(Row("x", provider: "other", current: true));
        _repo.SetCurrent("codex", "b");
        Assert.Equal(["b"], _repo.List("codex").Where(r => r.Current).Select(r => r.Id).ToArray());
        Assert.True(_repo.Get("x")!.Current);
    }

    [Fact]
    public void When_none_is_marked_current_the_first_in_order_is_the_answer()
    {
        // 否则镜像槽会被清空:用户明明还有账号,回退旧版本却是未登录。
        _repo.Put(Row("b", order: 1));
        _repo.Put(Row("a", order: 0));
        Assert.Equal("a", _repo.CurrentOf("codex")!.Id);
        Assert.Null(_repo.CurrentOf("nobody"));
    }

    [Fact]
    public void The_rows_a_legacy_2x_database_holds_are_readable()
    {
        // 2.x 写的行:limit_source 是 'http-429' 形态,时间戳是毫秒,没有额度。
        _db.Connection.CreateCommand().Also(c =>
        {
            c.CommandText = "INSERT INTO provider_accounts (id, provider_id, issuer, label, sort_order, enabled, is_current, needs_reauth, limit_until, limit_since, limit_source, limit_reason, created_at, updated_at) "
                + "VALUES ('legacy','codex','kimi-code','Work',2,1,1,0,1800000000000,1799999999000,'http-429','too many',1700000000000,1700000000000)";
            c.ExecuteNonQuery();
        });
        var row = _repo.Get("legacy")!;
        Assert.Equal(("kimi-code", "Work", 2, true), (row.Issuer, row.Label, row.Order, row.Current));
        Assert.Equal(new AccountLimit(1_800_000_000_000, 1_799_999_999_000, AccountLimitSource.Http429, "too many"), row.Limit);
    }

    [Fact]
    public void The_pool_port_reads_and_writes_through_the_repository()
    {
        _repo.Put(Row("a", order: 0));
        _repo.Put(Row("b", order: 1));
        var clock = new ManualClock();
        var port = new AccountPoolPort(_repo, clock, () => true);
        var pool = new AccountPool(port);
        var changed = new List<string>();
        port.Changed += changed.Add;

        var first = pool.Select("codex")!;
        Assert.Equal("a", first.Id);
        var next = pool.ReportFailure(first, AgentError.Create(AgentErrorCode.RateLimit, "429", status: 429, retryAfterMs: 60_000));

        Assert.Equal("b", next!.Id);
        Assert.Equal(AccountLimitSource.Http429, _repo.Get("a")!.Limit!.Source);
        Assert.Equal(["codex"], changed);
        Assert.Equal("b", pool.Select("codex")!.Id);

        // 一个出错的订阅者不能让请求路径抛异常。
        port.Changed += _ => throw new InvalidOperationException("boom");
        pool.ReportSuccess(_repo.Get("a")!.ToAccount());
        Assert.Null(_repo.Get("a")!.Limit);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action) where T : IDisposable
    {
        using (value)
        {
            action(value);
        }

        return value;
    }
}
