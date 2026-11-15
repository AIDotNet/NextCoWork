using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp;

/// <summary>一次 HTTP 请求(MCP 远程传输用)。</summary>
public sealed record McpHttpRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[]? Body = null, string? ContentType = null);

/// <summary>
/// HTTP 响应:收到响应头就返回,<see cref="Body"/> 是流(SSE 响应可以一直开着)。释放它即释放连接。
/// <b>不跟随重定向</b>:3xx 原样交给调用方,由它当错误(一个会把 <c>Authorization</c> 带去别的主机的重定向,是凭据泄漏的标准路径)。
/// </summary>
public sealed class McpHttpResponse(int status, IReadOnlyDictionary<string, string> headers, Stream body, IDisposable? owner = null) : IAsyncDisposable
{
    public int Status { get; } = status;

    /// <summary>响应头(键不区分大小写;同名多值用逗号连接)。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public Stream Body { get; } = body;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string? MediaType
    {
        get
        {
            var type = Header("Content-Type");
            var semi = type?.IndexOf(';') ?? -1;
            return (semi >= 0 ? type![..semi] : type)?.Trim().ToLowerInvariant();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync().ConfigureAwait(false);
        owner?.Dispose();
    }
}

/// <summary>HTTP 端口(可注入:测试用真实本机回环服务器 + 生产实现;SSH 环境里的服务器经环境的 TCP 通道)。</summary>
public interface IMcpHttp : IDisposable
{
    Task<McpHttpResponse> SendAsync(McpHttpRequest request, CancellationToken ct);
}

/// <summary>按服务器所在的环境造 HTTP 端口。每个连接一个(释放连接时一起释放)。</summary>
public interface IMcpHttpFactory
{
    IMcpHttp Create(McpRuntimeTarget target, Uri origin);
}

/// <summary>
/// 默认实现:<see cref="HttpClient"/>(<see cref="SocketsHttpHandler"/>,不跟随重定向、自动解压、不带 cookie、无总超时——超时由调用方的取消令牌管)。
/// 服务器在 SSH 环境里时,TCP 连接经 <see cref="IExecutionEnvironment.OpenTcpAsync"/>(<c>direct-tcpip</c>),TLS 仍由 <c>SocketsHttpHandler</c> 在其上做(默认证书校验,不自定义回调),
/// 并且只允许打到配置里的那个源(2.x 的 <c>environmentFetch</c>:跨源请求拒绝)。
/// </summary>
public sealed class DefaultMcpHttpFactory : IMcpHttpFactory
{
    public IMcpHttp Create(McpRuntimeTarget target, Uri origin) =>
        new HttpClientMcp(target.Environment.Kind == EnvironmentKind.Local ? null : target.Environment, origin);
}

/// <summary><see cref="IMcpHttp"/> 的 <see cref="HttpClient"/> 实现。</summary>
public sealed class HttpClientMcp : IMcpHttp
{
    private readonly HttpClient _client;
    private readonly Uri? _origin;

    /// <param name="environment">非 null = 连接经这个环境的 TCP 通道,并锁定到 <paramref name="origin"/> 这个源。</param>
    public HttpClientMcp(IExecutionEnvironment? environment = null, Uri? origin = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            UseProxy = environment is null,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (environment is not null)
        {
            _origin = origin;
            handler.ConnectCallback = async (context, ct) =>
            {
                var stream = await environment.OpenTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return stream;
            };
        }

        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<McpHttpResponse> SendAsync(McpHttpRequest request, CancellationToken ct)
    {
        if (_origin is not null && !SameOrigin(_origin, request.Url))
        {
            throw McpException.With(McpErrorKeys.Http, "The MCP endpoint changed origin; the request was refused.", ("reason", "crossOrigin"));
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        foreach (var (name, value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "application/json");
        }

        try
        {
            var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new McpHttpResponse((int)response.StatusCode, headers, stream, response);
        }
        catch (HttpRequestException e)
        {
            throw new McpException(McpErrorKeys.Network, $"The MCP server could not be reached: {e.Message}", inner: e);
        }
        catch (IOException e)
        {
            throw new McpException(McpErrorKeys.Network, $"The connection to the MCP server failed: {e.Message}", inner: e);
        }
        catch (SocketException e)
        {
            throw new McpException(McpErrorKeys.Network, $"The MCP server could not be reached: {e.Message}", inner: e);
        }
        catch (EnvironmentException e)
        {
            throw new McpException(McpErrorKeys.EnvironmentUnavailable, $"The execution environment could not open the connection ({e.Code}).", inner: e);
        }
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public void Dispose() => _client.Dispose();
}
