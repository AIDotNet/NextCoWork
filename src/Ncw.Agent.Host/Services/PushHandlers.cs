using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// <c>push.register</c>:登记控制端的推送令牌(方法表在 <see cref="PushMethods"/>)。设备只存不发(docs/design/02 A4):
/// 令牌按控制端公钥指纹存进 <see cref="PushRegistrations"/>,发送由后端推送中继以后负责。角色、承载、幂等键由路由统一检查,这里只做业务。
/// </summary>
internal static class PushHandlers
{
    public static void Register(MethodRouter router, Func<PushRegistrations> registrations, TimeProvider clock) =>
        router.Handle(PushMethods.Register, (p, ctx, _) =>
        {
            var platform = p.Platform?.Trim().ToLowerInvariant();
            if (!PushPlatforms.IsKnown(platform))
            {
                throw ServiceMapping.Invalid("push.error.platform", "Unknown push platform", ("platform", p.Platform ?? ""));
            }

            var token = p.Token?.Trim() ?? "";
            if (token.Length == 0 || token.Length > PushMethods.MaxTokenLength)
            {
                throw ServiceMapping.Invalid("push.error.token", "The push token is empty or too long");
            }

            // 远程控制端用公钥指纹做 id;本机通道没有指纹
            var controller = ctx.Peer?.FingerprintHex ?? PushRegistrations.LocalController;
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var entry = registrations().Put(controller, platform!, token, p.AppVersion is { Length: > 0 } v ? v[..Math.Min(v.Length, 64)] : null, now);
            return ValueTask.FromResult(new PushRegisterResult(entry.Platform, entry.UpdatedAt));
        });
}
