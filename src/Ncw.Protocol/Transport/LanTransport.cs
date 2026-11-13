using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Ncw.Protocol.Transport;

/// <summary>
/// 局域网承载(docs/design/09 §3):设备在所有地址上监听 TCP,控制端按 <c>IP:端口</c> 连接。
/// 这一层只给字节流;TLS 和配对在 <see cref="Security.SecureChannel"/> 里做。
/// </summary>
public static class LanTransport
{
    public const int DefaultPort = 7810;

    /// <summary>在 <c>[::]</c>(双栈,同时接受 IPv4)上监听。端口被占用时依次往后试,最后让系统分配。</summary>
    public static LanListener Listen(int preferredPort, bool lanOnly, IPAddress? bind = null)
    {
        var candidates = Enumerable.Range(preferredPort, 20).Where(p => p is > 0 and < 65536).Append(0);
        SocketException? last = null;
        foreach (var port in candidates)
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            try
            {
                socket.Bind(new IPEndPoint(bind ?? IPAddress.IPv6Any, port));
                socket.Listen(32);
                return new LanListener(socket, lanOnly);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw last ?? new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    public static async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 「仅允许局域网地址」(09 §3):10/8、172.16/12、192.168/16、169.254/16、100.64/10(Tailscale 等)、fc00::/7、fe80::/10,
    /// 以及本机回环。IPv4 映射的 IPv6 地址按 IPv4 判断。
    /// </summary>
    public static bool IsLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
    }

    /// <summary>本机的局域网地址(界面上显示「192.168.1.23:7810」用)。</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (IsLanAddress(address) && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
                    {
                        list.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return [.. list.Distinct().OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
    }
}

/// <summary>已经绑定的局域网监听器。</summary>
public sealed class LanListener : IDisposable
{
    private readonly Socket _socket;
    private readonly bool _lanOnly;

    internal LanListener(Socket socket, bool lanOnly)
    {
        _socket = socket;
        _lanOnly = lanOnly;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>实际监听的端口。</summary>
    public int Port { get; }

    /// <summary>接受连接。打开「仅允许局域网地址」时,其他来源直接关掉。返回的是原始 TCP 流,接着要做 TLS。</summary>
    public async IAsyncEnumerable<(Stream Stream, IPEndPoint Remote)> AcceptAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }
            catch (SocketException)
            {
                continue;
            }

            var remote = (IPEndPoint)accepted.RemoteEndPoint!;
            if (_lanOnly && !LanTransport.IsLanAddress(remote.Address))
            {
                accepted.Dispose();
                continue;
            }

            accepted.NoDelay = true;
            yield return (new NetworkStream(accepted, ownsSocket: true), remote);
        }
    }

    public void Dispose() => _socket.Dispose();
}
