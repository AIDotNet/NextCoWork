using System.Net;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>假的 HTTP 出口:按请求给响应,记录每个请求(含头)。</summary>
public sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : IWebHttp
{
    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public List<(HttpMethod Method, Uri Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(static h => h.Key.ToLowerInvariant(), static h => string.Join(",", h.Value));
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!, body, headers));
        }

        return await handler(request, ct);
    }

    public static HttpResponseMessage Ok(string body, string contentType = "text/html; charset=utf-8", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] body, string? contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        return response;
    }
}

public sealed class FakeResolver : IHostResolver
{
    private readonly Dictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress[]> _last = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Lookups { get; } = [];

    /// <summary>给一个主机名依次排答案(每次解析消费一个,最后一个答案一直用下去)。</summary>
    public FakeResolver Answer(string host, params string[] addresses)
    {
        if (!_answers.TryGetValue(host, out var queue))
        {
            _answers[host] = queue = new Queue<IPAddress[]>();
        }

        queue.Enqueue([.. addresses.Select(IPAddress.Parse)]);
        return this;
    }

    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        lock (Lookups)
        {
            Lookups.Add(host);
        }

        if (!_answers.TryGetValue(host, out var queue))
        {
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }

        var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        _last[host] = answer;
        return ValueTask.FromResult<IReadOnlyList<IPAddress>>(answer);
    }
}

/// <summary>工具的执行上下文(本机环境,无项目根)。</summary>
public sealed class ToolHarness
{
    private sealed class NoProgress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }

    public ToolHarness()
    {
        Env = new LocalEnvironment("dev-1");
        Runtime = new ToolRuntime { Environment = Env };
    }

    public LocalEnvironment Env { get; }

    public ToolRuntime Runtime { get; }

    public ExecutionToolContext Ctx(int depth = 0) =>
        new(new ToolContext(new RunId("run-1"), "call-1", "s1", depth, new EnvironmentTag("dev-1", "local"), new NoProgress(), new FileMutationQueue()), Runtime);

    public async Task<ToolResult> Run(IAgentTool tool, string json, CancellationToken ct = default)
    {
        var check = tool.PrepareArguments(JsonDocument.Parse(json).RootElement.Clone());
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        return await tool.ExecuteAsync(new ToolInvocation("call-1", tool.Info.InternalId, tool, check.Input), Ctx(), ct);
    }

    public static string TextOf(ToolResult result) => string.Concat(result.Content.OfType<TextPart>().Select(static p => p.Text));
}
