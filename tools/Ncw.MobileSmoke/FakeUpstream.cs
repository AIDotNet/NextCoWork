using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ncw.MobileSmoke;

/// <summary>
/// 本机假上游(OpenAI Chat 协议):<c>GET /v1/models</c> 给一个模型;<c>POST /v1/chat/completions</c> 的请求里还没有工具结果时,
/// 回复一个需要审批的 Bash 工具调用(在工作目录写一个标记文件),有了工具结果就回复一句文字。
/// </summary>
internal sealed class FakeUpstream : IDisposable
{
    public const string Model = "fake-model";
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeUpstream()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    /// <summary>收到的聊天请求数(诊断用)。</summary>
    public int ChatRequests { get; private set; }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path.EndsWith("/models", StringComparison.Ordinal))
            {
                await WriteAsync(context, "application/json", $"{{\"object\":\"list\",\"data\":[{{\"id\":\"{Model}\",\"object\":\"model\"}}]}}");
                return;
            }

            if (context.Request.HttpMethod == "POST" && path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                ChatRequests++;
                var hasToolResult = body.Contains("\"role\":\"tool\"", StringComparison.Ordinal);
                await WriteAsync(context, "text/event-stream", hasToolResult ? FinalAnswer() : ToolCall());
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string Chunk(string json) => "data: " + json + "\n\n";

    private static string ToolCall() =>
        Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"I will write a marker file.\"},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"Bash\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"command\\\":\\\"echo ncw-mobile-ok > mobile-marker.txt\\\",\\\"description\\\":\\\"write marker\\\"}\"}}]},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}")
        + Chunk("{\"id\":\"c1\",\"model\":\"m\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}")
        + "data: [DONE]\n\n";

    private static string FinalAnswer() =>
        Chunk("{\"id\":\"c2\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c2\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Done: the marker file was written.\"},\"finish_reason\":null}]}")
        + Chunk("{\"id\":\"c2\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}")
        + Chunk("{\"id\":\"c2\",\"model\":\"m\",\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":7}}")
        + "data: [DONE]\n\n";

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
