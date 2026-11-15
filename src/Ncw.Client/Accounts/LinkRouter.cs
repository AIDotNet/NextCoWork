namespace Ncw.Client.Accounts;

/// <summary>
/// <c>nextcowork://</c> 链接的路由(07 §2「跳转链接路由」白名单):系统把链接交给应用(macOS 的 OpenUri 激活、Windows / Linux 的启动参数、已运行实例收到的单实例转发)后都走这里。
/// <list type="bullet">
/// <item><c>auth/callback</c> → <see cref="AccountService.HandleLinkAsync"/>(state 校验、一次性)。</item>
/// <item><c>pair?invite=</c> → 只记下邀请并触发 <see cref="PairRequested"/>,<b>由界面让用户确认</b>后才配对(所有来自链接的操作都不能自动执行)。</item>
/// <item><c>open?…</c> 和其他一切 → 忽略(<c>open</c> 是后续版本)。</item>
/// </list>
/// </summary>
public sealed class LinkRouter(AccountService account)
{
    /// <summary>收到配对邀请(界面显示确认,用户同意才用 <see cref="PendingPairInvite"/> 去配对)。</summary>
    public event Action<string>? PairRequested;

    /// <summary>等用户确认的配对邀请(确认或取消后用 <see cref="ClearPendingPair"/> 清掉)。</summary>
    public string? PendingPairInvite { get; private set; }

    public async Task<LinkOutcome> HandleAsync(string? raw)
    {
        switch (NcwLinkParser.Parse(raw))
        {
            case AuthCallbackLink callback:
                return await account.HandleLinkAsync(callback).ConfigureAwait(false);
            case PairLink pair:
                PendingPairInvite = pair.Invite;
                PairRequested?.Invoke(pair.Invite);
                return LinkOutcome.Accepted;
            default:
                return LinkOutcome.Ignored; // open 路由(后续版本)与一切未知的链接
        }
    }

    public void ClearPendingPair() => PendingPairInvite = null;
}
