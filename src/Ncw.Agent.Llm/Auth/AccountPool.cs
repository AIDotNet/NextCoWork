namespace Ncw.Agent.Llm.Auth;

public enum AccountLimitSource
{
    /// <summary>上游 429 / 配额字样的 402/403。用户点「立即解除」是在赌我们判早了。</summary>
    Http429,

    /// <summary>额度快照显示已跑满,提前落闸到重置时刻。</summary>
    QuotaExhausted,

    /// <summary>用户自己关的:不会被下一次成功请求清掉,也不被 429 续期。</summary>
    Manual,
}

/// <summary><paramref name="Until"/> 是绝对毫秒时间戳。到期即自动为假,解除不需要任何人去「解」它。</summary>
public sealed record AccountLimit(long Until, long Since, AccountLimitSource Source, string Reason);

public sealed record QuotaWindow(double UsedPercent, int WindowMinutes, long ResetsAt);

public sealed record QuotaSnapshot(QuotaWindow? Primary, QuotaWindow? Secondary, long CapturedAt);

/// <summary>一家 OAuth 供应商下挂的一个登录身份。额度是按账号算的:一个账号被限流,另一个账号的额度还在。</summary>
public sealed record ProviderAccount(
    string Id,
    string ProviderId,
    string Issuer,
    int Order,
    bool Enabled = true,
    bool Current = false,
    bool NeedsReauth = false,
    AccountLimit? Limit = null,
    QuotaSnapshot? Quota = null)
{
    public string CredentialRef => CredentialRefs.ForAccount(ProviderId, Id);
}

/// <summary>
/// 「现在该用哪个账号」只能由这里的纯函数回答。界面的账号列表和路由读同一段代码,
/// 否则就是「界面说账号 A 可用,请求却发给了 B」,且零报错。
/// </summary>
public static class AccountSelection
{
    public static bool IsLimited(ProviderAccount account, long now) => account.Limit is { } limit && limit.Until > now;

    /// <summary>
    /// 三条判据每一条的恢复方式都不一样,所以不能合成一个布尔字段存库:停用只有用户能开;待重新登录只有重新登录能救;限流时间到了自己就好。
    /// 读的是账号行上反范式的 NeedsReauth(热路径上拿不到解密后的凭据)。
    /// </summary>
    public static bool IsUsable(ProviderAccount account, long now) => account.Enabled && !account.NeedsReauth && !IsLimited(account, now);

    /// <summary>轮换顺序:Order 升序,并列按 id 稳定排,否则拖拽之后顺序会随读取顺序抖。</summary>
    public static IReadOnlyList<ProviderAccount> Sorted(IEnumerable<ProviderAccount> accounts) =>
        accounts.OrderBy(a => a.Order).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 固定顺序 + 跳过不可用。不看 Current(当前账号是用户的显式选择,限流轮到别人头上时不该被悄悄改掉)。
    /// 轮换关掉时只认 Current,失败也不切——哪怕它正被限流也返回它,让那次请求带着真实的上游错误失败。
    /// </summary>
    public static ProviderAccount? Select(IEnumerable<ProviderAccount> accounts, long now, bool rotation = true)
    {
        var sorted = Sorted(accounts);
        if (sorted.Count == 0)
        {
            return null;
        }

        return rotation ? sorted.FirstOrDefault(a => IsUsable(a, now)) : sorted.FirstOrDefault(a => a.Current) ?? sorted[0];
    }

    /// <summary>当前这个不行了,下一个是谁。从整张表重新挑(被跳过的账号可能排在后面);excludeId 只是在它的闸门刚落下、数据还没回读时不把它选出来。</summary>
    public static ProviderAccount? NextUsable(IEnumerable<ProviderAccount> accounts, long now, string excludeId) =>
        Sorted(accounts).FirstOrDefault(a => a.Id != excludeId && IsUsable(a, now));

    /// <summary>全被限流时最早什么时候能再试;null = 没有任何账号在等时间(都停用了、或都要重新登录,等下去也不会好)。</summary>
    public static long? EarliestRecovery(IEnumerable<ProviderAccount> accounts, long now)
    {
        long? earliest = null;
        foreach (var account in accounts)
        {
            if (!account.Enabled || account.NeedsReauth || !IsLimited(account, now) || account.Limit is not { } limit)
            {
                continue;
            }

            if (earliest is null || limit.Until < earliest)
            {
                earliest = limit.Until;
            }
        }

        return earliest;
    }

    /// <summary>
    /// 两个闸门取更晚的那个:两条并发的流可能在同一秒各自吃到 429,拿到的 Retry-After 不一定一样,
    /// 后写的直接覆盖会把先落的「1 小时后恢复」盖成「5 分钟后恢复」,冷却期被越拖越长。手动停用不参与合并。
    /// </summary>
    public static AccountLimit Merge(AccountLimit? existing, AccountLimit incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        if (existing.Source == AccountLimitSource.Manual)
        {
            return existing;
        }

        return existing.Until >= incoming.Until ? existing : incoming;
    }
}

/// <summary>池子对外部世界的全部依赖,全部收在这里,于是一个假 Port 就能直测。</summary>
public interface IAccountPoolPort
{
    IReadOnlyList<ProviderAccount> List(string providerId);

    void SetLimit(string accountId, AccountLimit? limit);

    void SetQuota(string accountId, QuotaSnapshot quota);

    long NowMs();

    /// <summary>账号轮换开关(设置 › 模型)。每次读,改完立刻对下一条请求生效。</summary>
    bool RotationEnabled();

    void OnChanged(string providerId);
}

/// <summary>
/// 「现在该用这家的哪个账号」唯一的回答者。不发请求、不读密文;不算退避时长以外的重试策略
/// (「这条流还要不要重试」归路由;这里只回答「这个账号还能不能用」,对所有并发流同时生效)。
/// </summary>
public sealed class AccountPool(IAccountPoolPort port)
{
    /// <summary>上游没给恢复时间时账号被停用多久。刻意不复用路由的 4 秒退避:那是「这条流退多久再发」(秒),这是「账号停用多久」(分钟)。</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 这次失败是不是「这个账号的额度没了」:429,或 402/403 含配额字样(上游对「订阅额度跑完」的回法并不统一)。
    /// <b>401 不算</b>:一个真正失效的 token 被认成「限流」后,界面会说「1 小时后恢复」,而它永远不会恢复。
    /// </summary>
    public static bool IsQuotaFailure(AgentError error)
    {
        if (error.Code == AgentErrorCode.RateLimit)
        {
            return true;
        }

        if (error.Status is not (402 or 403))
        {
            return false;
        }

        var message = error.Message.ToLowerInvariant();
        return QuotaPhrases.Any(phrase => message.Contains(phrase, StringComparison.Ordinal));
    }

    private static readonly string[] QuotaPhrases =
        ["quota", "insufficient_quota", "usage limit", "usage_limit", "rate limit", "exceeded your current", "out of credits", "credit balance"];

    /// <summary>这一刻该用哪个账号。null = 这家没有账号表(纯 API Key / 还没登录),或全部账号都不可用;用 <see cref="HasAccounts"/> 区分。</summary>
    public ProviderAccount? Select(string providerId) => AccountSelection.Select(port.List(providerId), port.NowMs(), port.RotationEnabled());

    public bool HasAccounts(string providerId) => port.List(providerId).Count > 0;

    /// <summary>
    /// 这次失败构不构成「换个账号」。构成就落闸并返回下一个可用账号。落闸发生在「这条流还重不重试」之前:
    /// 这次尝试可能是本条流的最后一次,但对其它并发流这个账号的额度照样是空的。
    /// 关掉轮换时落闸照落(用户关掉的是自动换号,不是知情权)但绝不返回下一个账号——这一行还挡着一个死循环:
    /// 路由把「换到了新账号」当成「这次不算重试」,而轮换关掉时 Select 恒返回当前那一个。
    /// </summary>
    public ProviderAccount? ReportFailure(ProviderAccount account, AgentError error)
    {
        if (!IsQuotaFailure(error))
        {
            return null;
        }

        var now = port.NowMs();
        var until = now + (error.RetryAfterMs ?? (long)DefaultLimit.TotalMilliseconds);
        ApplyLimit(account, new AccountLimit(until, now, AccountLimitSource.Http429, error.Message));
        return port.RotationEnabled() ? AccountSelection.NextUsable(port.List(account.ProviderId), now, account.Id) : null;
    }

    /// <summary>成功 = 配额确实回来了,清闸(手动停用的不清:用户自己关的,只有用户能开)。</summary>
    public void ReportSuccess(ProviderAccount account)
    {
        if (account.Limit is null || account.Limit.Source == AccountLimitSource.Manual)
        {
            return;
        }

        port.SetLimit(account.Id, null);
        port.OnChanged(account.ProviderId);
    }

    /// <summary>额度快照回来了:跑满就提前落闸到重置时刻(不等下一次必然失败的 429);两个窗口都跑满时取更晚的。</summary>
    public void ReportQuota(ProviderAccount account, QuotaSnapshot quota)
    {
        port.SetQuota(account.Id, quota);
        var now = port.NowMs();
        long? exhausted = null;
        foreach (var window in new[] { quota.Primary, quota.Secondary })
        {
            if (window is { UsedPercent: >= 100 } w && w.ResetsAt > now)
            {
                exhausted = Math.Max(exhausted ?? 0, w.ResetsAt);
            }
        }

        if (exhausted is null)
        {
            port.OnChanged(account.ProviderId);
            return;
        }

        ApplyLimit(account, new AccountLimit(exhausted.Value, now, AccountLimitSource.QuotaExhausted, ""));
    }

    public long? EarliestRecoveryAt(string providerId) => AccountSelection.EarliestRecovery(port.List(providerId), port.NowMs());

    private void ApplyLimit(ProviderAccount account, AccountLimit incoming)
    {
        var merged = AccountSelection.Merge(account.Limit, incoming);
        // 合并之后没变(旧闸门更晚)就不写库:省的不是一次 UPDATE,是一次会让设置页整列表重渲的广播。
        if (account.Limit is not null && ReferenceEquals(merged, account.Limit))
        {
            return;
        }

        port.SetLimit(account.Id, merged);
        port.OnChanged(account.ProviderId);
    }
}
