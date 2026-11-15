using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host;

/// <summary>
/// 本机 Agent 持有的账户委托令牌(docs/design/07 §2)。<b>只放内存</b>:Agent 重启之后由控制端(账户会话的真源)重新交付;
/// 过期等同于没有。令牌只经 <see cref="AccessToken"/> 给「模型通道 / 配置同步 / 技能与插件市场」的 HTTP 调用用,
/// 永远不进任何 DTO、事件、诊断日志或磁盘(<see cref="Status"/> 只有有没有、属于谁、何时过期)。
/// </summary>
public sealed class DelegatedAccount(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private Held? _held;

    private sealed record Held(string Token, string AccountId, string? TeamId, long ExpiresAt);

    /// <summary>委托令牌的变化(交付 / 撤销 / 替换)。订阅者(模型通道、配置同步)据此重建自己的会话;参数里没有令牌。</summary>
    public event Action? Changed;

    /// <summary>当前可用的访问令牌;没有或已过期为 null(调用方当作「需要登录」)。</summary>
    public string? AccessToken
    {
        get
        {
            lock (_gate)
            {
                return _held is { } held && held.ExpiresAt > _clock.GetUtcNow().ToUnixTimeMilliseconds() ? held.Token : null;
            }
        }
    }

    public AccountStatusDto Status()
    {
        lock (_gate)
        {
            return _held is { } held && held.ExpiresAt > _clock.GetUtcNow().ToUnixTimeMilliseconds()
                ? new AccountStatusDto(true, held.AccountId, held.TeamId, held.ExpiresAt)
                : new AccountStatusDto(false, null, null, null);
        }
    }

    internal void Attach(string token, long expiresAt, string accountId, string? teamId)
    {
        lock (_gate)
        {
            _held = new Held(token, accountId, teamId, expiresAt);
        }

        Changed?.Invoke();
    }

    internal void Detach()
    {
        bool had;
        lock (_gate)
        {
            had = _held is not null;
            _held = null;
        }

        if (had)
        {
            Changed?.Invoke();
        }
    }
}
