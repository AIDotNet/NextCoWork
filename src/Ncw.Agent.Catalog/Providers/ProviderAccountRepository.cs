using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Catalog;

/// <summary>
/// <c>provider_accounts</c> 表里的一行(2.x 迁移 24)。没有 <c>auth</c> 字段:登录态摘要(邮箱、套餐、过没过期)要解密凭据才知道,
/// 存一份进这张表会和凭据里的真值慢慢分叉。<see cref="NeedsReauth"/> 是唯一的例外——从凭据里反范式出来的一个位,
/// 因为「这一刻用哪个账号」发生在请求热路径上、必须同步,而解密是异步的。
/// </summary>
public sealed record ProviderAccountRow(
    string Id,
    string ProviderId,
    string Issuer,
    string? Label,
    int Order,
    bool Enabled,
    bool Current,
    bool NeedsReauth,
    AccountLimit? Limit,
    QuotaSnapshot? Quota,
    long CreatedAt,
    long UpdatedAt)
{
    public ProviderAccount ToAccount() => new(Id, ProviderId, Issuer, Order, Enabled, Current, NeedsReauth, Limit, Quota);
}

/// <summary>
/// <c>provider_accounts</c> 表的读写 —— 多账号的持久化收口。不变式:<b>删账号 = 删行 + 删密文,在同一个事务里</b>
/// (只删行会留下永远读不到的孤儿 token,而用户以为自己已经退出登录了)。
/// 云同步的脏标记(2.x 的 <c>enqueueSyncMutation</c>)随账户配置同步模块移植,这里不做。
/// </summary>
public sealed class ProviderAccountRepository(AgentDatabase database, ICredentialVault credentials, TimeProvider? clock = null)
{
    private long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>按轮换顺序。<c>sort_order</c> 并列时按 id —— 顺序必须稳定,否则拖拽之后会抖。</summary>
    public IReadOnlyList<ProviderAccountRow> List(string? providerId = null)
    {
        lock (database.Gate)
        {
            using var command = database.Command(providerId is null
                ? "SELECT * FROM provider_accounts ORDER BY provider_id, sort_order, id"
                : "SELECT * FROM provider_accounts WHERE provider_id = $p ORDER BY sort_order, id");
            if (providerId is not null)
            {
                command.Parameters.AddWithValue("$p", providerId);
            }

            using var reader = command.ExecuteReader();
            var rows = new List<ProviderAccountRow>();
            while (reader.Read())
            {
                // 认不出 issuer 的行发不出请求也画不出界面:跳过而不是抛,一行坏数据不该让整家的账号列表读不出来。
                if (Read(reader) is { } row)
                {
                    rows.Add(row);
                }
            }

            return rows;
        }
    }

    public ProviderAccountRow? Get(string id) => Query("SELECT * FROM provider_accounts WHERE id = $id", ("$id", id));

    private ProviderAccountRow? Query(string sql, params (string, object)[] args)
    {
        lock (database.Gate)
        {
            using var command = database.Command(sql);
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public ProviderAccountRow Put(ProviderAccountRow row)
    {
        Execute(
            """
            INSERT INTO provider_accounts (
              id, provider_id, issuer, label, sort_order, enabled, is_current, needs_reauth,
              limit_until, limit_since, limit_source, limit_reason, quota_json, created_at, updated_at
            ) VALUES ($id, $p, $issuer, $label, $order, $enabled, $current, $reauth, $lu, $ls, $lsrc, $lr, $quota, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
              provider_id = excluded.provider_id, issuer = excluded.issuer, label = excluded.label, sort_order = excluded.sort_order,
              enabled = excluded.enabled, is_current = excluded.is_current, needs_reauth = excluded.needs_reauth,
              limit_until = excluded.limit_until, limit_since = excluded.limit_since, limit_source = excluded.limit_source,
              limit_reason = excluded.limit_reason, quota_json = excluded.quota_json, updated_at = excluded.updated_at
            """,
            ("$id", row.Id), ("$p", row.ProviderId), ("$issuer", row.Issuer), ("$label", row.Label), ("$order", row.Order),
            ("$enabled", row.Enabled ? 1 : 0), ("$current", row.Current ? 1 : 0), ("$reauth", row.NeedsReauth ? 1 : 0),
            ("$lu", row.Limit?.Until), ("$ls", row.Limit?.Since), ("$lsrc", row.Limit is null ? null : SourceName(row.Limit.Source)), ("$lr", row.Limit?.Reason),
            ("$quota", row.Quota is null ? null : JsonSerializer.Serialize(row.Quota, ProviderJson.Context.QuotaSnapshot)),
            ("$created", row.CreatedAt), ("$updated", row.UpdatedAt));
        return row;
    }

    /// <summary>删账号,连密文一起删,同一个事务。走 <see cref="ICredentialVault.Remove"/> 而不是写空串:后者要读主密钥,主密钥损坏时「退出登录」本身会失败。</summary>
    public void Remove(string id)
    {
        if (Get(id) is not { } account)
        {
            return;
        }

        database.InTransaction(() =>
        {
            Execute("DELETE FROM provider_accounts WHERE id = $id", ("$id", id));
            credentials.Remove(ProviderRefs.ForAccount(account.ProviderId, account.Id));
        });
    }

    /// <summary>删掉一家供应商的全部账号(供应商本身被删时)。孤儿密文同样一起收。</summary>
    public void RemoveFor(string providerId)
    {
        database.InTransaction(() =>
        {
            foreach (var account in List(providerId))
            {
                Execute("DELETE FROM provider_accounts WHERE id = $id", ("$id", account.Id));
                credentials.Remove(ProviderRefs.ForAccount(providerId, account.Id));
            }
        });
    }

    /// <summary>新账号排在最后。空表是 0。</summary>
    public int NextOrder(string providerId) => List(providerId).Aggregate(0, (max, row) => Math.Max(max, row.Order + 1));

    /// <summary>拖拽排序:整批写、一个事务(半新半旧的顺序会让「先用哪个账号」在两次读取之间变来变去)。</summary>
    public void SetOrder(string providerId, IReadOnlyList<string> accountIds)
    {
        var now = Now;
        database.InTransaction(() =>
        {
            for (var i = 0; i < accountIds.Count; i++)
            {
                Execute("UPDATE provider_accounts SET sort_order = $o, updated_at = $now WHERE id = $id AND provider_id = $p",
                    ("$o", i), ("$now", now), ("$id", accountIds[i]), ("$p", providerId));
            }
        });
    }

    public void SetEnabled(string id, bool enabled) =>
        Execute("UPDATE provider_accounts SET enabled = $e, updated_at = $now WHERE id = $id", ("$e", enabled ? 1 : 0), ("$now", Now), ("$id", id));

    /// <summary>值没变就不写:这条路径在每次 token 刷新后都会被调到,无谓的 UPDATE 会连带一次设置页整列表重渲。</summary>
    public void SetNeedsReauth(string id, bool needsReauth)
    {
        if (Get(id) is not { } account || account.NeedsReauth == needsReauth)
        {
            return;
        }

        Execute("UPDATE provider_accounts SET needs_reauth = $r, updated_at = $now WHERE id = $id", ("$r", needsReauth ? 1 : 0), ("$now", Now), ("$id", id));
    }

    /// <summary>备注名。空串 = 清掉备注(回落到邮箱 / 上游 id)。</summary>
    public void SetLabel(string id, string label)
    {
        var trimmed = label.Trim();
        Execute("UPDATE provider_accounts SET label = $l, updated_at = $now WHERE id = $id", ("$l", trimmed.Length == 0 ? null : trimmed), ("$now", Now), ("$id", id));
    }

    /// <summary>落闸 / 解闸(<c>null</c> = 解除)。限流四列同进同出。合并语义(取更晚的)不在这里,在 <see cref="AccountSelection.Merge"/>。</summary>
    public void SetLimit(string id, AccountLimit? limit) => Execute(
        "UPDATE provider_accounts SET limit_until = $u, limit_since = $s, limit_source = $src, limit_reason = $r, updated_at = $now WHERE id = $id",
        ("$u", limit?.Until), ("$s", limit?.Since), ("$src", limit is null ? null : SourceName(limit.Source)), ("$r", limit?.Reason), ("$now", Now), ("$id", id));

    public void SetQuota(string id, QuotaSnapshot quota) => Execute(
        "UPDATE provider_accounts SET quota_json = $q, updated_at = $now WHERE id = $id",
        ("$q", JsonSerializer.Serialize(quota, ProviderJson.Context.QuotaSnapshot)), ("$now", Now), ("$id", id));

    /// <summary>「当前账号」是每家唯一的一个:先全清再置一个,同一个事务(中间一刻「这家一个当前账号都没有」会让镜像槽读到空)。</summary>
    public void SetCurrent(string providerId, string accountId)
    {
        var now = Now;
        database.InTransaction(() =>
        {
            Execute("UPDATE provider_accounts SET is_current = 0, updated_at = $now WHERE provider_id = $p", ("$now", now), ("$p", providerId));
            Execute("UPDATE provider_accounts SET is_current = 1, updated_at = $now WHERE id = $id AND provider_id = $p", ("$now", now), ("$id", accountId), ("$p", providerId));
        });
    }

    /// <summary>这家的「当前账号」。都没标时取顺序最靠前的:删掉当前账号之后表里会短暂地一个 is_current 都没有,而镜像槽、界面的「当前」标都要有个答案。</summary>
    public ProviderAccountRow? CurrentOf(string providerId)
    {
        var rows = List(providerId);
        return rows.FirstOrDefault(r => r.Current) ?? rows.FirstOrDefault();
    }

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        lock (database.Gate)
        {
            using var command = database.Command(sql);
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    private static string SourceName(AccountLimitSource source) => source switch
    {
        AccountLimitSource.QuotaExhausted => "quota-exhausted",
        AccountLimitSource.Manual => "manual",
        _ => "http-429",
    };

    private static AccountLimitSource SourceOf(string? name) => name switch
    {
        "quota-exhausted" => AccountLimitSource.QuotaExhausted,
        "manual" => AccountLimitSource.Manual,
        _ => AccountLimitSource.Http429,
    };

    private static ProviderAccountRow? Read(SqliteDataReader r)
    {
        var issuer = Str(r, "issuer");
        if (!OAuthIssuers.IsKnown(issuer))
        {
            return null;
        }

        // 限流四列同进同出:limit_until 为 NULL 即「没被限流」,其余三列存着什么都不该被读出来(否则一条只剩 reason 的脏行会变成永不结束的倒计时)。
        AccountLimit? limit = null;
        if (Long(r, "limit_until") is { } until)
        {
            limit = new AccountLimit(until, Long(r, "limit_since") ?? until, SourceOf(Str(r, "limit_source")), Str(r, "limit_reason") ?? "");
        }

        // 额度快照解析失败一律当没有,不抛:这一列将来会改形状,一个解不开的快照绝不该让整个账号列表读不出来。
        QuotaSnapshot? quota = null;
        if (Str(r, "quota_json") is { } json)
        {
            try
            {
                quota = JsonSerializer.Deserialize(json, ProviderJson.Context.QuotaSnapshot);
            }
            catch (JsonException)
            {
                quota = null;
            }
        }

        return new ProviderAccountRow(
            Str(r, "id")!, Str(r, "provider_id")!, issuer!, Str(r, "label") is { Length: > 0 } label ? label : null,
            (int)(Long(r, "sort_order") ?? 0), (Long(r, "enabled") ?? 1) != 0, (Long(r, "is_current") ?? 0) != 0, (Long(r, "needs_reauth") ?? 0) != 0,
            limit, quota, Long(r, "created_at") ?? 0, Long(r, "updated_at") ?? 0);
    }

    private static string? Str(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? Long(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }
}

/// <summary>账号池对外部世界的全部依赖(<see cref="IAccountPoolPort"/>)接到 <see cref="ProviderAccountRepository"/> 上。</summary>
public sealed class AccountPoolPort(ProviderAccountRepository repository, TimeProvider clock, Func<bool> rotationEnabled) : IAccountPoolPort
{
    /// <summary>落闸 / 解闸 / 额度快照变化之后触发(参数是 providerId),供控制端刷新账号列表。</summary>
    public event Action<string>? Changed;

    public IReadOnlyList<ProviderAccount> List(string providerId) => repository.List(providerId).Select(r => r.ToAccount()).ToList();

    public void SetLimit(string accountId, AccountLimit? limit) => repository.SetLimit(accountId, limit);

    public void SetQuota(string accountId, QuotaSnapshot quota) => repository.SetQuota(accountId, quota);

    public long NowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public bool RotationEnabled() => rotationEnabled();

    /// <summary>挂在一次正在跑的上游请求后面:这条路径上不能抛。</summary>
    public void OnChanged(string providerId)
    {
        try
        {
            Changed?.Invoke(providerId);
        }
        catch (Exception)
        {
            // 广播失败不该让用户的对话断掉。
        }
    }
}
