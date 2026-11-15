using System.Net;
using System.Net.Sockets;
using System.Text;
using Ncw.Client.Accounts;

namespace Ncw.Client.Tests.Accounts;

/// <summary>本机回环回调监听(真实的 TCP 套接字):后端只认 <c>http://127.0.0.1:{端口}/callback</c>,这里验证它的形状和防护。</summary>
public class LoopbackLoginCallbackListenerTests
{
    private const string State = "state-0123456789abcdef";

    private static (ILoginCallbackSession Session, List<AuthCallbackLink> Received) Start(Func<bool, string>? message = null)
    {
        var received = new List<AuthCallbackLink>();
        var session = new LoopbackLoginCallbackListener(message).Start(State, link =>
        {
            lock (received)
            {
                received.Add(link);
            }

            return Task.CompletedTask;
        });
        return (session, received);
    }

    /// <summary>发一个原始 HTTP 请求,读回完整响应。</summary>
    private static async Task<string> SendAsync(string redirectUri, string request)
    {
        var port = new Uri(redirectUri).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string Get(ILoginCallbackSession session, string target, string? host = null) =>
        $"GET {target} HTTP/1.1\r\nHost: {host ?? new Uri(session.RedirectUri).Authority}\r\nConnection: close\r\n\r\n";

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void The_redirect_uri_has_exactly_the_shape_the_backend_accepts()
    {
        var (session, _) = Start();
        using (session)
        {
            var uri = new Uri(session.RedirectUri);
            Assert.Equal("http", uri.Scheme);
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.True(uri.Port > 0);
            Assert.Equal("/callback", uri.AbsolutePath);
            Assert.Empty(uri.Query);
            Assert.Empty(uri.UserInfo);
        }
    }

    [Fact]
    public async Task A_matching_callback_is_delivered_once_and_the_browser_gets_a_page_without_links_or_referrer()
    {
        var (session, received) = Start(done => done ? "All done <b>" : "Nope");
        using (session)
        {
            var response = await SendAsync(session.RedirectUri, Get(session, $"/callback?code=abc%20def&state={State}"));
            Assert.StartsWith("HTTP/1.1 200", response);
            Assert.Contains("Referrer-Policy: no-referrer", response);
            Assert.Contains("Cache-Control: no-store", response);
            Assert.Contains("All done &lt;b&gt;", response); // 文字被转义
            Assert.DoesNotContain("href=", response);
            await WaitForAsync(() => received.Count == 1);
            var link = Assert.Single(received);
            Assert.Equal(("abc def", State, null), (link.Code, link.State, link.Error));

            // 同一个登录只接受一个匹配的回调:重放得到成功页,但不会再交给账户服务
            await SendAsync(session.RedirectUri, Get(session, $"/callback?code=other&state={State}"));
            await Task.Delay(100);
            Assert.Single(received);
        }
    }

    [Fact]
    public async Task A_denied_authorization_is_delivered_with_a_failure_page()
    {
        var (session, received) = Start(done => done ? "ok" : "failed");
        using (session)
        {
            var response = await SendAsync(session.RedirectUri, Get(session, $"/callback?error=access_denied&state={State}"));
            Assert.StartsWith("HTTP/1.1 200", response);
            Assert.Contains("failed", response);
            await WaitForAsync(() => received.Count == 1);
            Assert.Equal("access_denied", received[0].Error);
        }
    }

    [Fact]
    public async Task A_wrong_state_gets_400_and_does_not_end_the_wait()
    {
        var (session, received) = Start();
        using (session)
        {
            Assert.StartsWith("HTTP/1.1 400", await SendAsync(session.RedirectUri, Get(session, "/callback?code=EVIL&state=forged")));
            await Task.Delay(100);
            Assert.Empty(received);

            // 真正的回调仍然能到
            Assert.StartsWith("HTTP/1.1 200", await SendAsync(session.RedirectUri, Get(session, $"/callback?code=C&state={State}")));
            await WaitForAsync(() => received.Count == 1);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/favicon.ico")]
    [InlineData("/callback/extra")]
    [InlineData("/callbackX?code=c&state=" + State)]
    public async Task Any_other_path_is_404_and_changes_nothing(string target)
    {
        var (session, received) = Start();
        using (session)
        {
            Assert.StartsWith("HTTP/1.1 404", await SendAsync(session.RedirectUri, Get(session, target)));
            await Task.Delay(50);
            Assert.Empty(received);
        }
    }

    [Fact]
    public async Task Only_GET_is_accepted()
    {
        var (session, received) = Start();
        using (session)
        {
            var request = $"POST /callback?code=c&state={State} HTTP/1.1\r\nHost: {new Uri(session.RedirectUri).Authority}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            Assert.StartsWith("HTTP/1.1 405", await SendAsync(session.RedirectUri, request));
            await Task.Delay(50);
            Assert.Empty(received);
        }
    }

    [Theory]
    [InlineData("evil.test")]
    [InlineData("localhost:1")]
    [InlineData("127.0.0.1:1")]
    public async Task A_foreign_host_header_is_rejected_against_dns_rebinding(string host)
    {
        var (session, received) = Start();
        using (session)
        {
            Assert.StartsWith("HTTP/1.1 400", await SendAsync(session.RedirectUri, Get(session, $"/callback?code=c&state={State}", host)));
            await Task.Delay(50);
            Assert.Empty(received);
        }
    }

    [Theory]
    [InlineData("/callback?code=c&code=d&state=" + State)]   // 参数污染
    [InlineData("/callback?code=%zz&state=" + State)]        // 非法的百分号编码
    [InlineData("/callback?code=c")]                         // 缺 state
    [InlineData("/callback")]
    public async Task Malformed_callback_parameters_are_rejected(string target)
    {
        var (session, received) = Start();
        using (session)
        {
            Assert.StartsWith("HTTP/1.1 400", await SendAsync(session.RedirectUri, Get(session, target)));
            await Task.Delay(50);
            Assert.Empty(received);
        }
    }

    [Fact]
    public async Task An_oversized_request_head_is_refused()
    {
        var (session, received) = Start();
        using (session)
        {
            var padding = new string('a', 9000);
            var request = $"GET /callback?code=c&state={State} HTTP/1.1\r\nHost: {new Uri(session.RedirectUri).Authority}\r\nX-Pad: {padding}\r\n\r\n";
            try
            {
                // 服务端读满 8 KB 还没见到头结束就回 400 并断开,客户端可能先收到 400,也可能写到一半被断开
                var response = await SendAsync(session.RedirectUri, request);
                Assert.True(response.Length == 0 || response.StartsWith("HTTP/1.1 400", StringComparison.Ordinal));
            }
            catch (IOException)
            {
            }

            await Task.Delay(50);
            Assert.Empty(received);
        }
    }

    [Fact]
    public async Task Disposing_the_session_closes_the_port_and_is_idempotent()
    {
        var (session, _) = Start();
        var port = new Uri(session.RedirectUri).Port;
        session.Dispose();
        session.Dispose();
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public void Two_logins_get_two_different_ports()
    {
        var (first, _) = Start();
        var (second, _) = Start();
        using (first)
        using (second)
        {
            Assert.NotEqual(new Uri(first.RedirectUri).Port, new Uri(second.RedirectUri).Port);
        }
    }
}
