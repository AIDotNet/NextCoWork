using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Session;

/// <summary>配对成功后控制端要保存的信任记录(docs/design/09 §3 第 5 步)。</summary>
/// <param name="DeviceFingerprint">设备公钥指纹,以后连接时钉扎。</param>
/// <param name="RouteToken">中继路由令牌(经中继连接时出示)。</param>
/// <param name="Relay">设备配对时配置的中继地址;没配置为 null。</param>
public sealed record PairedDevice(string DeviceId, byte[] DeviceFingerprint, string Role, string RouteToken, string Host, int Port, string? Relay = null);

/// <summary>配对失败的原因。</summary>
public enum PairingFailure
{
    /// <summary>配对码不对(或设备刚换了码)。</summary>
    WrongCode,

    /// <summary>设备的证明对不上:对方不是知道配对码的那台设备,或者连接被中间人转接。</summary>
    DeviceNotVerified,

    /// <summary>设备的远程开关关了。</summary>
    RemoteDisabled,

    /// <summary>二维码已过期(控制端本地按过期时间判断)。</summary>
    QrExpired,

    /// <summary>设备拒绝了二维码的密钥:过期或者已经用过。</summary>
    QrRejected,

    /// <summary>二维码里的地址都连不上(或者那里不是二维码里的设备)。</summary>
    Unreachable,
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

        return new PairedDevice(deviceId, channel.PeerFingerprint, finish.Role, finish.RouteToken, host, port, finish.Relay);
    }

    /// <summary>
    /// 扫码配对。二维码是带外的可信通道:TLS 握手就按里面的设备指纹钉扎(中间人换了证书直接握手失败),
    /// 然后出示一次性密钥,不用配对码。依次尝试二维码里的地址,第一个连得上的胜出。
    /// </summary>
    public static async Task<PairedDevice> PairWithQrAsync(PairingQr qr, PeerIdentity controller, string controllerName, TimeProvider? clock = null, CancellationToken ct = default)
    {
        if ((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() >= qr.ExpiresAt)
        {
            throw new PairingException(PairingFailure.QrExpired, "The pairing QR code has expired");
        }

        Exception? last = null;
        foreach (var address in qr.Addresses)
        {
            if (!PairingQr.TrySplitAddress(address, out var host, out var port))
            {
                continue;
            }

            Stream tcp;
            SecureChannel channel;
            try
            {
                tcp = await LanTransport.ConnectAsync(host, port, ct).ConfigureAwait(false);
                channel = await SecureChannel.ConnectAsync(tcp, controller, qr.Fingerprint, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException or SecureChannelException)
            {
                last = e;
                continue; // 这个地址不通(或者不是二维码里的那台设备),试下一个
            }

            await using var client = await DeviceClient.ConnectAsync(channel.Stream, new ClientHello(controllerName, ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
            if (client.Welcome.DeviceId != qr.DeviceId)
            {
                throw new PairingException(PairingFailure.DeviceNotVerified, "The device id does not match the QR code");
            }

            try
            {
                var claim = await client.CallAsync(DeviceMethods.PairClaim, new PairClaimParams(controllerName, qr.Token), ct).ConfigureAwait(false);
                return new PairedDevice(qr.DeviceId, qr.Fingerprint, claim.Role, claim.RouteToken, host, port, claim.Relay);
            }
            catch (ProtocolException e) when (e.Code == ErrorCodes.Unauthorized)
            {
                throw new PairingException(PairingFailure.QrRejected, "The pairing QR code was rejected (expired or already used)", e);
            }
        }

        throw new PairingException(PairingFailure.Unreachable, "None of the addresses in the QR code could be reached", last);
    }

    /// <summary>
    /// 连接已配对的设备,尝试它所有已知的地址:局域网地址和中继路由(09 §4「并行尝试所有地址,先通的胜出,局域网优先」)。
    /// 所有局域网地址立刻开始;中继在 <paramref name="relayHeadStart"/>(默认 250 毫秒)之后才开始,或者在所有局域网地址都失败时立刻开始——
    /// 所以局域网通的时候中继根本不会被打扰,局域网慢或不通时中继能很快接上。第一条完成 TLS 握手(设备公钥必须与钉扎的指纹相同)的胜出,
    /// 其余的取消并关闭。身份由证书保证,所以地址可以随便试:中继或同一网段里的别的机器冒充设备,握手直接失败。
    /// 全部失败时抛出代表性的异常:中继明确说令牌无效(<see cref="RelayErrors.Unauthorized"/>)优先——那意味着设备已不再信任本控制端。
    /// </summary>
    public static async Task<RemoteConnection> ConnectAsync(string deviceId, DeviceAddresses addresses, PeerIdentity controller, byte[] deviceFingerprint, CancellationToken ct = default, TimeSpan? relayHeadStart = null)
    {
        if (addresses.Lan.Count == 0 && addresses.Relay is null)
        {
            throw new IOException("No known address for the device");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<RemoteConnection>>();
        foreach (var lan in addresses.Lan)
        {
            attempts.Add(TryLanAsync(lan, controller, deviceFingerprint, race.Token));
        }

        var pending = new List<Task>(attempts);
        Task? startRelay = null;
        if (addresses.Relay is not null)
        {
            var allLanSettled = Task.WhenAll(attempts.Select(a => a.ContinueWith(static _ => { }, TaskScheduler.Default)));
            startRelay = Task.WhenAny(Task.Delay(relayHeadStart ?? TimeSpan.FromMilliseconds(250), race.Token), allLanSettled);
            pending.Add(startRelay);
        }

        var errors = new List<Exception>();
        RemoteConnection? winner = null;
        while (pending.Count > 0 && winner is null)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done == startRelay)
            {
                if (!race.IsCancellationRequested && !attempts.Any(a => a.IsCompletedSuccessfully))
                {
                    var relay = TryRelayAsync(addresses.Relay!, deviceId, controller, deviceFingerprint, race.Token);
                    attempts.Add(relay);
                    pending.Add(relay);
                }

                continue;
            }

            var attempt = (Task<RemoteConnection>)done;
            if (attempt.IsCompletedSuccessfully)
            {
                winner = attempt.Result;
            }
            else if (attempt.Exception is { } failure)
            {
                errors.Add(failure.GetBaseException());
            }
        }

        // 其余的尝试:取消;已经连上的(同时完成的)关掉,不留下悬空连接。
        await race.CancelAsync().ConfigureAwait(false);
        foreach (var attempt in attempts)
        {
            if (winner is not null && attempt.IsCompletedSuccessfully && ReferenceEquals(attempt.Result, winner))
            {
                continue;
            }

            _ = attempt.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Stream.Dispose();
                }
                else
                {
                    _ = t.Exception; // 观察掉
                }
            }, TaskScheduler.Default);
        }

        if (winner is not null)
        {
            return winner;
        }

        ct.ThrowIfCancellationRequested();
        throw errors.OfType<RelayException>().FirstOrDefault(e => e.Code == RelayErrors.Unauthorized)
            ?? errors.FirstOrDefault()
            ?? new IOException("Could not reach the device");
    }

    private static async Task<RemoteConnection> TryLanAsync(LanAddress address, PeerIdentity controller, byte[] fingerprint, CancellationToken ct)
    {
        var tcp = await LanTransport.ConnectAsync(address.Host, address.Port, ct).ConfigureAwait(false);
        var channel = await SecureChannel.ConnectAsync(tcp, controller, fingerprint, ct).ConfigureAwait(false);
        return new RemoteConnection(channel.Stream, DeviceRoute.Lan);
    }

    private static async Task<RemoteConnection> TryRelayAsync(RelayRoute route, string deviceId, PeerIdentity controller, byte[] fingerprint, CancellationToken ct)
    {
        var stream = await RelayTransport.ConnectAsync(route, deviceId, ct).ConfigureAwait(false);
        var channel = await SecureChannel.ConnectAsync(stream, controller, fingerprint, ct).ConfigureAwait(false);
        return new RemoteConnection(channel.Stream, DeviceRoute.Relay);
    }

    /// <summary>连接已配对的设备:设备公钥必须与钉扎的指纹相同(地址变了没关系,身份看公钥)。返回可交给 <see cref="DeviceClient"/> 的字节流。</summary>
    public static async Task<Stream> ConnectAsync(string host, int port, PeerIdentity controller, byte[] deviceFingerprint, CancellationToken ct = default)
    {
        var tcp = await LanTransport.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var channel = await SecureChannel.ConnectAsync(tcp, controller, deviceFingerprint, ct).ConfigureAwait(false);
        return channel.Stream;
    }
}

/// <summary>设备的一个局域网地址。</summary>
public sealed record LanAddress(string Host, int Port);

/// <summary>控制端记录的设备地址(09 §4):<c>{ lan: […], relay?: route }</c>。</summary>
public sealed record DeviceAddresses(IReadOnlyList<LanAddress> Lan, RelayRoute? Relay = null);

/// <summary>连上设备走的哪条路。</summary>
public enum DeviceRoute
{
    Lan,
    Relay,
}

/// <summary>一条已经完成 TLS 握手、设备指纹已核对的连接,以及它走的路。</summary>
public sealed record RemoteConnection(Stream Stream, DeviceRoute Route);
