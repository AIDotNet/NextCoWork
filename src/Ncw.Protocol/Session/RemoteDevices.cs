using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Session;

/// <summary>配对成功后控制端要保存的信任记录(docs/design/09 §3 第 5 步)。</summary>
/// <param name="DeviceFingerprint">设备公钥指纹,以后连接时钉扎。</param>
/// <param name="RouteToken">中继路由令牌(中继上线后使用)。</param>
public sealed record PairedDevice(string DeviceId, byte[] DeviceFingerprint, string Role, string RouteToken, string Host, int Port);

/// <summary>配对失败的原因。</summary>
public enum PairingFailure
{
    /// <summary>配对码不对(或设备刚换了码)。</summary>
    WrongCode,

    /// <summary>设备的证明对不上:对方不是知道配对码的那台设备,或者连接被中间人转接。</summary>
    DeviceNotVerified,

    /// <summary>设备的远程开关关了。</summary>
    RemoteDisabled,
}

public sealed class PairingException(PairingFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public PairingFailure Failure { get; } = failure;
}

/// <summary>控制端连接局域网设备:首次配对(IP:端口 + 配对码),以及之后按钉扎的设备指纹连接。</summary>
public static class RemoteDevices
{
    /// <summary>
    /// 首次配对。TLS 握手时先接受任何设备证书,随后:设备报的 deviceId 必须由它的公钥派生;SRP 用配对码证明双方知道同一个码;
    /// 双方的绑定证明把这次验证绑定到这条 TLS 会话和两把公钥上,经过中间人转接时对不上。
    /// </summary>
    public static async Task<PairedDevice> PairAsync(string host, int port, PeerIdentity controller, string code, string controllerName, CancellationToken ct = default)
    {
        var tcp = await LanTransport.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var channel = await SecureChannel.ConnectAsync(tcp, controller, pinnedDevice: null, ct).ConfigureAwait(false);
        await using var client = await DeviceClient.ConnectAsync(channel.Stream, new ClientHello(controllerName, ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);

        var deviceId = client.Welcome.DeviceId;
        if (deviceId != PeerIdentity.DeviceIdOf(channel.PeerFingerprint))
        {
            throw new PairingException(PairingFailure.DeviceNotVerified, "The device id does not match its key");
        }

        PairBeginResult begin;
        try
        {
            begin = await client.CallAsync(DeviceMethods.PairBegin, new PairBeginParams(controllerName), ct).ConfigureAwait(false);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.Unavailable)
        {
            throw new PairingException(PairingFailure.RemoteDisabled, e.Message, e);
        }

        var transcript = new PairingTranscript(channel.Exporter, channel.LocalFingerprint, channel.PeerFingerprint);
        var pairing = new PairingClient(deviceId, code);
        var (a, m1, proof) = pairing.Respond(begin.Salt, begin.B, transcript);
        PairFinishResult finish;
        try
        {
            finish = await client.CallAsync(DeviceMethods.PairFinish, new PairFinishParams(a, m1, proof), ct).ConfigureAwait(false);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.Unauthorized)
        {
            // 设备那边对不上:码错,或者(中间人转接时)绑定证明对不上。两者在设备上无法区分,也不该区分。
            throw new PairingException(PairingFailure.WrongCode, e.Message, e);
        }

        if (!pairing.Verify(finish.M2, finish.Proof, transcript))
        {
            throw new PairingException(PairingFailure.DeviceNotVerified, "The device could not prove it knows the pairing code");
        }

        return new PairedDevice(deviceId, channel.PeerFingerprint, finish.Role, finish.RouteToken, host, port);
    }

    /// <summary>连接已配对的设备:设备公钥必须与钉扎的指纹相同(地址变了没关系,身份看公钥)。返回可交给 <see cref="DeviceClient"/> 的字节流。</summary>
    public static async Task<Stream> ConnectAsync(string host, int port, PeerIdentity controller, byte[] deviceFingerprint, CancellationToken ct = default)
    {
        var tcp = await LanTransport.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var channel = await SecureChannel.ConnectAsync(tcp, controller, deviceFingerprint, ct).ConfigureAwait(false);
        return channel.Stream;
    }
}
