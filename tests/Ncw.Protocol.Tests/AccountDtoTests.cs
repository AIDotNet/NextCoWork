using PolyType;
using PolyType.Abstractions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 账户委托的线上形状:往返、方法表声明(owner、仅本机通道、command 带幂等键)、冻结向量(<see cref="GoldenV1Account"/>),
/// 以及结构性保证:令牌只存在于 attach 的请求参数里(方向是控制端 → Agent),结果和状态的类型树里没有任何可能装令牌的字段。
/// </summary>
public class AccountDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    [Fact]
    public void Account_payloads_round_trip_and_keep_their_fields()
    {
        var attach = RoundTrip(new AccountAttachParams("tok-1", 1_800_000_000_000, "u1", "t1"));
        Assert.Equal(("tok-1", 1_800_000_000_000L, "u1", "t1"), (attach.Token, attach.ExpiresAt, attach.AccountId, attach.TeamId));
        Assert.Null(RoundTrip(new AccountAttachParams("tok-1", 1, "u1", null)).TeamId);
        var status = RoundTrip(new AccountStatusDto(true, "u1", "t1", 1_800_000_000_000));
        Assert.Equal((true, "u1", "t1", 1_800_000_000_000L), (status.Attached, status.AccountId, status.TeamId, status.ExpiresAt));
        Assert.False(RoundTrip(new AccountStatusDto(false, null, null, null)).Attached);
    }

    [Fact]
    public void Every_account_method_is_an_owner_method_on_the_local_channel_only_and_commands_carry_idempotency()
    {
        Assert.Equal(3, AccountMethods.All.Count);
        foreach (var method in AccountMethods.All)
        {
            Assert.Equal(Capabilities.Account, method.Capability);
            Assert.Equal(Transports.Local, method.Allowed); // 远程控制端不能替本机换账户
            Assert.Equal(Role.Owner, method.MinRole);
            Assert.StartsWith("account.", method.Name);
        }

        Assert.Equal(MethodKind.Command, AccountMethods.AttachDelegatedToken.Kind);
        Assert.Equal(MethodKind.Command, AccountMethods.DetachDelegatedToken.Kind);
        Assert.Equal(MethodKind.Query, AccountMethods.GetStatus.Kind);
        Assert.Equal("account.attachDelegatedToken", AccountMethods.AttachDelegatedToken.Name);
        Assert.NotEqual(Capabilities.Providers, Capabilities.Account);
    }

    [Fact]
    public void Golden_vectors_v1_account()
    {
        Assert.Equal(GoldenV1Account.AttachParams, Hex(new AccountAttachParams("tok-1", 1_800_000_000_000, "u1", "t1")));
        Assert.Equal(GoldenV1Account.StatusAttached, Hex(new AccountStatusDto(true, "u1", "t1", 1_800_000_000_000)));
        Assert.Equal(GoldenV1Account.StatusDetached, Hex(new AccountStatusDto(false, null, null, null)));

        Assert.Equal(new AccountAttachParams("tok-1", 1_800_000_000_000, "u1", "t1"), FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Account.AttachParams), PayloadShapes.GetShape<AccountAttachParams>()));
        Assert.Equal(new AccountStatusDto(false, null, null, null), FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Account.StatusDetached), PayloadShapes.GetShape<AccountStatusDto>()));
    }

    private static void Walk(ITypeShape shape, HashSet<Type> seen, List<string> names)
    {
        if (!seen.Add(shape.Type))
        {
            return;
        }

        if (shape is IObjectTypeShape obj)
        {
            foreach (var property in obj.Properties)
            {
                names.Add(property.Name);
                Walk(property.PropertyType, seen, names);
            }
        }
    }

    [Fact]
    public void The_status_type_cannot_carry_a_token_but_the_attach_request_can()
    {
        var status = new List<string>();
        Walk(PayloadShapes.GetShape<AccountStatusDto>(), [], status);
        Assert.Equal(["Attached", "AccountId", "TeamId", "ExpiresAt"], status); // 没有任何名字里带令牌的字段

        var request = new List<string>();
        Walk(PayloadShapes.GetShape<AccountAttachParams>(), [], request);
        Assert.Contains("Token", request); // 方向是控制端 → Agent,和 providers.credential.set 的密钥一样
    }
}
