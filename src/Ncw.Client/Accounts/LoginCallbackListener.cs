using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ncw.Client.Accounts;

/// <summary>
/// 浏览器登录的回调接收端(docs/design/07 §2)。后端只认 <c>http://127.0.0.1:{随机端口}/callback</c>(<c>ClientOAuth.ValidRedirect</c>,与 2.x 相同),
/// 所以桌面登录要先在本机回环地址上起一个临时监听,把它的地址作为 <c>redirect_uri</c>。一次登录一份,登录结束就关。
/// </summary>
public interface ILoginCallbackListener
{
    /// <summary>
    /// 开始监听。只有 <c>state</c> 对得上的回调才会交给 <paramref name="onCallback"/>(对不上的回 400、继续等,别人构造的请求打断不了登录)。
    /// 监听起不来(没有可用端口)抛 <see cref="IOException"/> / <see cref="SocketException"/>。
    /// </summary>
    ILoginCallbackSession Start(string expectedState, Func<AuthCallbackLink, Task> onCallback);
}

/// <summary>一次登录的监听。<see cref="IDisposable.Dispose"/> 关掉监听(幂等)。</summary>
public interface ILoginCallbackSession : IDisposable
{
    /// <summary>授权请求和换码请求里要逐字使用的 <c>redirect_uri</c>。</summary>
    string RedirectUri { get; }
}

/// <summary>
/// 基于 <see cref="TcpListener"/> 的回环实现(不用 <c>HttpListener</c>:Windows 上它对 <c>127.0.0.1</c> 前缀要预留 URL ACL)。
/// 只收 <c>GET /callback</c>,只绑 <see cref="IPAddress.Loopback"/>,<c>Host</c> 头必须是 <c>127.0.0.1:{端口}</c>(挡 DNS 重绑定),请求头 ≤ 8 KB、5 秒读完;
/// 回调参数交给 <see cref="NcwLinkParser"/> 的同一套严格解析(重复参数、控制字符、非法编码一律拒绝);一次登录只接受一个匹配的回调。
/// 回给浏览器的页面不含任何外链、不带 Referrer;文字由调用方提供(<c>Ncw.Client</c> 不产出界面文字),缺省只显示 ✓ / ✕。
/// </summary>
public sealed class LoopbackLoginCallbackListener(Func<bool, string>? message = null) : ILoginCallbackListener
{
    private const string CallbackPath = "/callback";
    private const int MaxHeaderBytes = 8192;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public ILoginCallbackSession Start(string expectedState, Func<AuthCallbackLink, Task> onCallback) =>
        new Session(expectedState, onCallback, message);

    private sealed class Session : ILoginCallbackSession
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _state;
        private readonly Func<AuthCallbackLink, Task> _onCallback;
        private readonly Func<bool, string>? _message;
        private readonly string _host;
        private int _completed;

        public Session(string state, Func<AuthCallbackLink, Task> onCallback, Func<bool, string>? message)
        {
            _state = state;
            _onCallback = onCallback;
            _message = message;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(4);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _host = "127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RedirectUri = "http://" + _host + CallbackPath;
            _ = AcceptLoopAsync();
        }

        public string RedirectUri { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = HandleAsync(client);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 监听被关掉
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(ReadTimeout);
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                    if (head is null)
                    {
                        await RespondAsync(stream, 400, false, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    var (status, link) = Classify(head);
                    var ok = link is { Error: null };
                    await RespondAsync(stream, status, ok, timeout.Token).ConfigureAwait(false);
                    if (link is not null && status == 200 && Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
                    {
                        try
                        {
                            await _onCallback(link).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            // 登录状态由账户服务自己兜底
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // 浏览器断开 / 读超时 / 监听被关
                }
            }
        }

        /// <summary>返回 HTTP 状态和(状态为 200 时)解析出的回调。</summary>
        private (int Status, AuthCallbackLink? Link) Classify(string head)
        {
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');
            if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return (400, null);
            }

            var target = request[1];
            var question = target.IndexOf('?', StringComparison.Ordinal);
            var path = question < 0 ? target : target[..question];
            if (path != CallbackPath)
            {
                return (404, null);
            }

            if (request[0] != "GET")
            {
                return (405, null);
            }

            string? host = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host is not null)
                    {
                        return (400, null);
                    }

                    host = line[(colon + 1)..].Trim();
                }
            }

            if (!string.Equals(host, _host, StringComparison.OrdinalIgnoreCase))
            {
                return (400, null);
            }

            // 复用跳转链接的严格解析:参数个数 / 长度 / 重复 / 控制字符 / 非法百分号编码
            var query = question < 0 ? "" : target[question..];
            if (NcwLinkParser.Parse("nextcowork://auth/callback" + query) is not AuthCallbackLink link || !Pkce.StateEquals(_state, link.State))
            {
                return (400, null);
            }

            return (200, link);
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[MaxHeaderBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                length += read;
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    return Encoding.ASCII.GetString(buffer, 0, end);
                }
            }

            return null; // 请求头过大
        }

        private async Task RespondAsync(NetworkStream stream, int status, bool success, CancellationToken ct)
        {
            var reason = status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Bad Request" };
            var body = status == 404 ? "" : Page(status == 200 && success);
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\n"
                + "Referrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private string Page(bool success)
        {
            var text = WebUtility.HtmlEncode(_message?.Invoke(success) ?? "");
            return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>NextCoWork</title>"
                + "<style>body{font:16px system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0;text-align:center}.m{font-size:48px;margin:0}</style></head>"
                + $"<body><div><p class=\"m\">{(success ? "&#10003;" : "&#10007;")}</p><p>{text}</p></div></body></html>";
        }

        public void Dispose()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            _listener.Stop();
        }
    }
}
