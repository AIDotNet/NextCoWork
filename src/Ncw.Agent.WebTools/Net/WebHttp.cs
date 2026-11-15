using System.Net;
using System.Net.Sockets;

namespace Ncw.Agent.WebTools.Net;

/// <summary>
/// 联网工具的唯一 HTTP 出口(可注入):一次发一跳、<b>不跟随重定向</b>(重定向由调用方逐跳校验)、响应头到达即返回(正文由调用方按上限流式读)。
/// 测试里换成假的;生产是 <see cref="GuardedHttp"/>。
/// </summary>
public interface IWebHttp
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>名字解析的接缝:生产用系统解析器,测试用假的(验证「解析到内网」「重绑定」)。</summary>
public interface IHostResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemHostResolver : IHostResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
}

/// <summary>连接目标被 SSRF 防护拒绝(解析出了环回 / 内网 / 元数据地址)。<see cref="Address"/> 是被拒的那个地址(解析失败时为 null)。</summary>
public sealed class SsrfBlockedException(string host, IPAddress? address, string message) : Exception(message)
{
    public string Host { get; } = host;

    public IPAddress? Address { get; } = address;
}

/// <summary>
/// <b>解析 → 校验 → 钉死 IP → 连接</b>,全部在同一次连接里完成(防 DNS 重绑定):名字只解析一次,校验这次解析出来的<b>全部</b>地址
/// (只要有一个是内网就整台主机拒绝,不挑「好的那个」),然后<b>直接用 IP 建立 TCP 连接</b>。TLS 的 SNI / 证书校验和 <c>Host</c> 头仍然用原来的主机名
/// (<c>SocketsHttpHandler</c> 在连接回调返回的流之上按请求的主机名做握手),所以证书校验不受影响。
/// 校验和连接之间没有第二次解析,攻击者控制的 DNS 在两次查询里给不同答案也没有用。每条新连接都重新走一遍。
/// </summary>
public sealed class PinnedConnector(IHostResolver resolver, WebPolicy policy, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
{
    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> _connect = connect ?? ConnectTcpAsync;

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> addresses;
        if (AddressPolicy.TryParseLiteral(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(policy.ResolveTimeout);
            try
            {
                addresses = await resolver.ResolveAsync(host, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException($"Resolving \"{host}\" timed out.");
            }
            catch (SocketException e)
            {
                throw new HttpRequestException($"Could not resolve \"{host}\" ({e.SocketErrorCode}).", e);
            }
        }

        if (addresses.Count == 0)
        {
            throw new HttpRequestException($"\"{host}\" did not resolve to any address.");
        }

        if (!policy.AllowPrivateAddresses)
        {
            foreach (var address in addresses)
            {
                if (AddressPolicy.IsBlocked(address))
                {
                    throw new SsrfBlockedException(host, address,
                        $"Refusing to reach \"{host}\": it resolves to a loopback or private-network address ({address}), and network tools may only reach the public internet.");
                }
            }
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(policy.ConnectTimeout);
            try
            {
                return await _connect(address, port, attempt.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }

        throw new HttpRequestException($"Could not connect to \"{host}\" ({last?.Message}).", last);
    }

    private static async ValueTask<Stream> ConnectTcpAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// 默认的 <see cref="IWebHttp"/>:<see cref="SocketsHttpHandler"/> + <see cref="PinnedConnector"/>。不跟随重定向、自动解压、不用代理(代理会让连接回调连到代理而不是目标,
/// SSRF 校验和 IP 钉死就落空;设置页里的代理对联网工具暂不生效,见设计文档 03 §13)、不带 Cookie。
/// </summary>
public sealed class GuardedHttp : IWebHttp, IDisposable
{
    private readonly HttpClient _client;

    public GuardedHttp(WebPolicy? policy = null, IHostResolver? resolver = null, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        var connector = new PinnedConnector(resolver ?? new SystemHostResolver(), policy ?? WebPolicy.Default, connect);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), // 连接按生命周期换新:每条新连接重新解析、重新校验
            MaxResponseHeadersLength = 64, // KiB
            ConnectCallback = async (context, ct) => await connector.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (FindBlocked(e) is { } blocked)
        {
            throw blocked;
        }
    }

    private static SsrfBlockedException? FindBlocked(Exception e)
    {
        for (var cursor = e.InnerException; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor is SsrfBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
