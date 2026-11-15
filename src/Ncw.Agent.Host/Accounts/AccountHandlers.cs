using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Accounts;

/// <summary>
/// <c>account.*</c> 的方法实现(方法表在 <see cref="AccountMethods"/>)。角色(owner)、承载(仅本机)、幂等键由路由统一检查;这里只校验令牌的形状并交给 <see cref="DelegatedAccount"/>。
/// 校验失败的消息里<b>不含令牌</b>。这些方法不依赖设备数据就绪:令牌只在内存里,登录可以早于 2.x 数据接管完成。
/// </summary>
internal static class AccountHandlers
{
    public static void Register(MethodRouter router, DelegatedAccount account, TimeProvider clock)
    {
        router.Handle(AccountMethods.AttachDelegatedToken, (p, _, _) =>
        {
            if (string.IsNullOrWhiteSpace(p.Token) || p.Token.Length > AccountLimits.MaxTokenLength || p.Token.Any(char.IsControl) || p.Token.Any(char.IsWhiteSpace))
            {
                throw Rejected(AccountErrorKeys.TokenInvalid, "The delegated token is empty, too long, or contains whitespace.");
            }

            if (string.IsNullOrWhiteSpace(p.AccountId) || p.AccountId.Length > 128 || (p.TeamId is { } team && (team.Length == 0 || team.Length > 128)))
            {
                throw Rejected(AccountErrorKeys.TokenInvalid, "The account or team id is not valid.");
            }

            if (p.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            {
                throw Rejected(AccountErrorKeys.TokenExpired, "The delegated token has already expired.");
            }

            account.Attach(p.Token, p.ExpiresAt, p.AccountId, p.TeamId);
            return ValueTask.FromResult(account.Status());
        });
        router.Handle(AccountMethods.DetachDelegatedToken, (_, _, _) =>
        {
            account.Detach();
            return ValueTask.FromResult(account.Status());
        });
        router.Handle(AccountMethods.GetStatus, (_, _, _) => ValueTask.FromResult(account.Status()));
    }

    private static ProtocolException Rejected(string key, string message) =>
        new(ErrorCodes.InvalidRequest, message, retryable: false,
            FrameCodec.EncodePayload(new ProviderErrorDto(key, []), PayloadShapes.GetShape<ProviderErrorDto>()));
}
