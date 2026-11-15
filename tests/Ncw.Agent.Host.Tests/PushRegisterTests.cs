using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// <c>push.register</c>(docs/design/02 A4、09 §8.4):设备只存令牌不发推送。经真实路由:member 起、幂等键、校验、按控制端指纹覆盖、吊销后不再是发送目标。
/// </summary>
public sealed class PushRegisterTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private DeviceClient _owner = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<(DeviceClient Client, PeerIdentity Identity)> ConnectAsync(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        return (await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0")), identity);
    }

    private PushRegistrations Registrations()
    {
        var database = AgentDatabase.Open(_rig.DataRoot);
        return new PushRegistrations(database);
    }

    [Fact]
    public void The_method_is_declared_as_a_member_command_of_the_push_capability()
    {
        Assert.Equal(MethodKind.Command, PushMethods.Register.Kind);
        Assert.Equal(Role.Member, PushMethods.Register.MinRole);
        Assert.Equal(Capabilities.Push, PushMethods.Register.Capability);
        Assert.Equal(Transports.Any, PushMethods.Register.Allowed);
    }

    [Fact]
    public async Task A_member_registers_a_token_and_the_device_advertises_the_capability()
    {
        var (member, identity) = await ConnectAsync("phone", "member");
        await using var _ = member;

        Assert.Contains(Capabilities.Push, member.Welcome.Features);
        var result = await member.CallAsync(PushMethods.Register, new PushRegisterParams("apns", "token-1", "3.0.0"));

        Assert.Equal("apns", result.Platform);
        Assert.True(result.RegisteredAt > 0);
        var stored = Registrations().Get(identity.FingerprintHex);
        Assert.NotNull(stored);
        Assert.Equal("token-1", stored.Token);
        Assert.Equal("3.0.0", stored.AppVersion);
    }

    [Fact]
    public async Task Registering_again_replaces_the_token_and_an_idempotent_replay_returns_the_same_result()
    {
        var (member, identity) = await ConnectAsync("phone", "member");
        await using var _ = member;

        var first = await member.CallAsync(PushMethods.Register, new PushRegisterParams("fcm", "old"), idem: "reg-1");
        var replay = await member.CallAsync(PushMethods.Register, new PushRegisterParams("fcm", "old"), idem: "reg-1");
        Assert.Equal(first, replay);

        await member.CallAsync(PushMethods.Register, new PushRegisterParams("fcm", "new"));
        var all = Registrations().List();
        Assert.Single(all, r => r.ControllerId == identity.FingerprintHex);
        Assert.Equal("new", all.Single(r => r.ControllerId == identity.FingerprintHex).Token);
    }

    [Fact]
    public async Task A_viewer_cannot_register_and_bad_platforms_or_tokens_are_invalid_requests()
    {
        var (viewer, _) = await ConnectAsync("viewer", "viewer");
        await using var v = viewer;
        var denied = await Assert.ThrowsAsync<ProtocolException>(() => viewer.CallAsync(PushMethods.Register, new PushRegisterParams("apns", "t")));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);

        var (member, _) = await ConnectAsync("phone", "member");
        await using var m = member;
        var platform = await Assert.ThrowsAsync<ProtocolException>(() => member.CallAsync(PushMethods.Register, new PushRegisterParams("wns", "t")));
        Assert.Equal(ErrorCodes.InvalidRequest, platform.Code);
        var empty = await Assert.ThrowsAsync<ProtocolException>(() => member.CallAsync(PushMethods.Register, new PushRegisterParams("apns", "  ")));
        Assert.Equal(ErrorCodes.InvalidRequest, empty.Code);
        var huge = await Assert.ThrowsAsync<ProtocolException>(() => member.CallAsync(PushMethods.Register, new PushRegisterParams("apns", new string('x', PushMethods.MaxTokenLength + 1))));
        Assert.Equal(ErrorCodes.InvalidRequest, huge.Code);
    }

    [Fact]
    public async Task The_local_channel_may_register_but_is_never_a_send_target_and_a_revoked_controller_stops_being_one()
    {
        await _owner.CallAsync(PushMethods.Register, new PushRegisterParams("apns", "desktop-token"));
        var (member, identity) = await ConnectAsync("phone", "member");
        await using var _ = member;
        await member.CallAsync(PushMethods.Register, new PushRegisterParams("apns", "phone-token"));

        var registrations = Registrations();
        var trust = new ControllerTrustRepository(AgentDatabase.Open(_rig.DataRoot));
        Assert.Equal(["phone-token"], registrations.ActiveFor(trust).Select(r => r.Token));

        await _owner.CallAsync(DeviceMethods.TrustRevoke, new TrustRevokeParams(identity.FingerprintHex));
        Assert.Empty(registrations.ActiveFor(trust));
    }
}
