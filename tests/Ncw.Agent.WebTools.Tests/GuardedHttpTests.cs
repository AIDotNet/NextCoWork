using System.Net;
using System.Net.Sockets;
using System.Text;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>
/// 连接时的那一道(解析 → 校验 → 钉死 IP → 连接):用假解析器和假连接验证 DNS 重绑定的钉 IP 行为;用真的本机监听验证 <c>GuardedHttp</c> 整条链路
/// (Host 头保留原主机名、解析到内网的公网域名被拒、字面量内网地址被拒)。
/// </summary>
public sealed class GuardedHttpTests
{
    private static readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> NeverConnect = (_, _, _) => throw new InvalidOperationException("must not connect");

    private static WebPolicy Policy(bool allowPrivate = false) => new() { AllowPrivateAddresses = allowPrivate, ResolveTimeout = TimeSpan.FromMilliseconds(200), ConnectTimeout = TimeSpan.FromMilliseconds(500) };

    // ───────────────────────── PinnedConnector:假解析器、假连接 ─────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.100.100.200")]
    public async Task A_public_name_that_resolves_to_a_private_address_is_refused_before_any_connection(string resolved)
    {
        var resolver = new FakeResolver().Answer("evil.example", resolved);
        var connector = new PinnedConnector(resolver, Policy(), NeverConnect);

        var e = await Assert.ThrowsAsync<SsrfBlockedException>(async () => await connector.ConnectAsync("evil.example", 443, default));
        Assert.Equal("evil.example", e.Host);
        Assert.Equal(IPAddress.Parse(resolved), e.Address);
    }

    [Fact]
    public async Task One_private_answer_among_public_ones_rejects_the_whole_host()
    {
        // 不挑「好的那个」:攻击者控制的 DNS 可以给一个混合答案,等 happy-eyeballs 或重试落到内网那个上
        var resolver = new FakeResolver().Answer("mixed.example", "93.184.216.34", "127.0.0.1", "2606:4700::1111");
        var connector = new PinnedConnector(resolver, Policy(), NeverConnect);
        await Assert.ThrowsAsync<SsrfBlockedException>(async () => await connector.ConnectAsync("mixed.example", 80, default));
    }

    [Fact]
    public async Task The_connection_is_pinned_to_the_validated_ip_and_the_name_is_resolved_exactly_once()
    {
        // ★ DNS 重绑定:第一次解析给公网地址,第二次给 127.0.0.1。校验和连接之间不允许有第二次解析。
        var resolver = new FakeResolver().Answer("rebind.example", "93.184.216.34").Answer("rebind.example", "127.0.0.1");
        var connected = new List<IPAddress>();
        var connector = new PinnedConnector(resolver, Policy(), (ip, port, _) =>
        {
            connected.Add(ip);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });

        await connector.ConnectAsync("rebind.example", 443, default);

        Assert.Equal([IPAddress.Parse("93.184.216.34")], connected);
        Assert.Single(resolver.Lookups); // 只解析了一次
    }

    [Fact]
    public async Task Each_new_connection_is_resolved_and_validated_again_so_a_later_rebind_to_private_is_caught()
    {
        var resolver = new FakeResolver().Answer("rebind.example", "93.184.216.34").Answer("rebind.example", "127.0.0.1");
        var connector = new PinnedConnector(resolver, Policy(), (_, _, _) => ValueTask.FromResult<Stream>(new MemoryStream()));

        await connector.ConnectAsync("rebind.example", 443, default); // 第一条连接:公网
        await Assert.ThrowsAsync<SsrfBlockedException>(async () => await connector.ConnectAsync("rebind.example", 443, default)); // 第二条:已经重绑定成环回,被拒
    }

    [Fact]
    public async Task An_ip_literal_is_validated_without_a_lookup_and_private_literals_are_refused()
    {
        var resolver = new FakeResolver();
        var connected = new List<IPAddress>();
        var connector = new PinnedConnector(resolver, Policy(), (ip, _, _) =>
        {
            connected.Add(ip);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });

        await connector.ConnectAsync("93.184.216.34", 80, default);
        Assert.Equal([IPAddress.Parse("93.184.216.34")], connected);
        Assert.Empty(resolver.Lookups);

        foreach (var literal in new[] { "127.0.0.1", "2130706433", "0177.0.0.1", "[::1]", "169.254.169.254" })
        {
            await Assert.ThrowsAsync<SsrfBlockedException>(async () => await connector.ConnectAsync(literal, 80, default));
        }
    }

    [Fact]
    public async Task Connecting_falls_through_the_addresses_in_order_when_one_is_unreachable()
    {
        var resolver = new FakeResolver().Answer("multi.example", "93.184.216.34", "93.184.216.35");
        var attempts = new List<IPAddress>();
        var connector = new PinnedConnector(resolver, Policy(), (ip, _, _) =>
        {
            attempts.Add(ip);
            return ip.ToString().EndsWith(".34", StringComparison.Ordinal) ? throw new SocketException((int)SocketError.ConnectionRefused) : ValueTask.FromResult<Stream>(new MemoryStream());
        });

        await connector.ConnectAsync("multi.example", 443, default);
        Assert.Equal([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35")], attempts);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_plain_request_failure_not_an_ssrf_block()
    {
        var connector = new PinnedConnector(new FakeResolver(), Policy(), NeverConnect);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await connector.ConnectAsync("nowhere.example", 443, default));
    }

    [Fact]
    public async Task A_resolver_that_hangs_is_cut_off_by_the_resolve_timeout()
    {
        var connector = new PinnedConnector(new HangingResolver(), Policy(), NeverConnect);
        var e = await Assert.ThrowsAsync<HttpRequestException>(async () => await connector.ConnectAsync("slow.example", 443, default));
        Assert.Contains("timed out", e.Message);
    }

    [Fact]
    public async Task Cancelling_the_run_cancels_the_connection_attempt()
    {
        using var cts = new CancellationTokenSource();
        var resolver = new FakeResolver().Answer("a.example", "93.184.216.34");
        var connector = new PinnedConnector(resolver, Policy(), async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new MemoryStream();
        });
        var task = connector.ConnectAsync("a.example", 443, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task The_test_only_policy_lets_private_resolutions_through()
    {
        var resolver = new FakeResolver().Answer("local.example", "127.0.0.1");
        var connected = new List<IPAddress>();
        var connector = new PinnedConnector(resolver, Policy(allowPrivate: true), (ip, _, _) =>
        {
            connected.Add(ip);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });
        await connector.ConnectAsync("local.example", 80, default);
        Assert.Equal([IPAddress.Loopback], connected);
    }

    private sealed class HangingResolver : IHostResolver
    {
        public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    // ───────────────────────── GuardedHttp:真的本机监听 ─────────────────────────

    /// <summary>一个最小的 HTTP/1.1 服务:记录每个请求的 Host 头,回固定正文。</summary>
    private sealed class MiniServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public MiniServer()
        {
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Hosts { get; } = [];

        private async Task Loop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                foreach (var line in text.Split("\r\n"))
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (Hosts)
                        {
                            Hosts.Add(line[5..].Trim());
                        }
                    }
                }

                var body = "hello from the pinned address";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task End_to_end_the_request_goes_to_the_pinned_ip_with_the_original_host_header()
    {
        using var server = new MiniServer();
        var resolver = new FakeResolver().Answer("pinned.test", "127.0.0.1");
        using var http = new GuardedHttp(Policy(allowPrivate: true), resolver);

        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://pinned.test:{server.Port}/page"), default);

        Assert.Equal("hello from the pinned address", await response.Content.ReadAsStringAsync());
        Assert.Equal($"pinned.test:{server.Port}", Assert.Single(server.Hosts)); // Host 头是原主机名,不是 IP
    }

    [Fact]
    public async Task End_to_end_a_public_name_that_resolves_to_loopback_is_refused_and_never_reaches_the_server()
    {
        using var server = new MiniServer();
        var resolver = new FakeResolver().Answer("evil.test", "127.0.0.1");
        using var http = new GuardedHttp(Policy(), resolver);

        var e = await Assert.ThrowsAsync<SsrfBlockedException>(async () => await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://evil.test:{server.Port}/"), default));

        Assert.Equal(IPAddress.Loopback, e.Address);
        Assert.Empty(server.Hosts); // 一个字节也没发到服务器
    }

    [Fact]
    public async Task End_to_end_a_literal_loopback_url_is_refused_by_the_connect_gate_even_if_the_url_check_was_skipped()
    {
        using var server = new MiniServer();
        using var http = new GuardedHttp(Policy(), new FakeResolver());
        await Assert.ThrowsAsync<SsrfBlockedException>(async () => await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/"), default));
        await Assert.ThrowsAsync<SsrfBlockedException>(async () => await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://2130706433:{server.Port}/"), default));
        Assert.Empty(server.Hosts);
    }

    [Fact]
    public async Task Redirects_are_not_followed_by_the_http_layer()
    {
        // 服务器回 302:GuardedHttp 原样交回,由工具逐跳校验
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var http = new GuardedHttp(Policy(allowPrivate: true), new FakeResolver().Answer("r.test", "127.0.0.1"));
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://r.test:{port}/"), default);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("http://169.254.169.254/", response.Headers.Location?.ToString());
        listener.Stop();
    }
}
