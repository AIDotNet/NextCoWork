using System.Net;
using System.Text;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Host.Tests.Support;

/// <summary>没有网络:任何上游请求都立刻失败(测试不能依赖真实网络,也不能去碰 models.dev)。</summary>
public sealed class NoNetwork : IUpstreamHttp
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("no network in tests");
}

/// <summary>永远不醒的睡眠:目录同步的定时器在测试里不会自己触发(要触发的测试用 <see cref="ReleasableDelay"/>)。</summary>
public sealed class NeverDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}

/// <summary>记录每次睡眠的时长,由测试放行(不睡真的时间)。</summary>
public sealed class ReleasableDelay : IDelayer
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();

    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            Delays.Add(delay);
            _waiters.Enqueue(tcs);
        }

        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public async Task ReleaseNextAsync()
    {
        for (var i = 0; i < 500; i++)
        {
            lock (_gate)
            {
                if (_waiters.Count > 0)
                {
                    _waiters.Dequeue().TrySetResult();
                    return;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No delay was requested.");
    }
}

/// <summary>脚本化的上游:按「URL 包含的片段」路由,每条路由一个队列(用完后重复最后一条),记录收到的请求(含请求体)。没有路由 = 404。</summary>
public sealed class FakeUpstream : IUpstreamHttp
{
    private readonly object _gate = new();
    private readonly List<(string Fragment, List<Func<HttpResponseMessage>> Replies, int Next)> _routes = [];

    public sealed record Seen(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body);

    public List<Seen> Requests { get; } = [];

    public IReadOnlyList<Seen> To(string fragment)
    {
        lock (_gate)
        {
            return [.. Requests.Where(r => r.Url.Contains(fragment, StringComparison.Ordinal))];
        }
    }

    /// <summary>给一个 URL 片段排队一条回复(同一片段多次调用 = 依次消费;最后一条之后重复它)。</summary>
    public FakeUpstream On(string fragment, Func<HttpResponseMessage> reply)
    {
        lock (_gate)
        {
            var i = _routes.FindIndex(r => r.Fragment == fragment);
            if (i < 0)
            {
                _routes.Add((fragment, [reply], 0));
            }
            else
            {
                _routes[i].Replies.Add(reply);
            }
        }

        return this;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var url = request.RequestUri!.ToString();
        Func<HttpResponseMessage>? reply = null;
        lock (_gate)
        {
            Requests.Add(new Seen(request.Method.Method, url, headers, body));
            var i = _routes.FindIndex(r => url.Contains(r.Fragment, StringComparison.Ordinal));
            if (i >= 0)
            {
                var route = _routes[i];
                reply = route.Replies[Math.Min(route.Next, route.Replies.Count - 1)];
                _routes[i] = route with { Next = route.Next + 1 };
            }
        }

        return reply?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route") };
    }
}

public static class Replies
{
    public static HttpResponseMessage Json(string body, string? etag = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    public static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    /// <summary>OpenAI chat 的流式回复:一段文本 + 用量(<paramref name="promptTokens"/> / <paramref name="completionTokens"/>)。</summary>
    public static HttpResponseMessage ChatStream(string model, string text, int promptTokens, int completionTokens)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"model\":\"").Append(model).Append("\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"").Append(text).Append("\"}}]}\n\n");
        sb.Append("data: {\"model\":\"").Append(model).Append("\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        sb.Append("data: {\"model\":\"").Append(model).Append("\",\"choices\":[],\"usage\":{\"prompt_tokens\":").Append(promptTokens)
            .Append(",\"completion_tokens\":").Append(completionTokens).Append("}}\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    /// <summary>models.dev 形状的数据:<paramref name="anthropic"/> 个 anthropic 模型(<c>claude-test-N</c>,USD 价格:输入 3 / 输出 15 每百万)。</summary>
    public static string ModelsDev(int anthropic, int priceScale = 1)
    {
        var sb = new StringBuilder("{\"anthropic\":{\"id\":\"anthropic\",\"name\":\"Anthropic\",\"models\":{");
        for (var i = 0; i < anthropic; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($"\"claude-test-{i}\":{{\"id\":\"claude-test-{i}\",\"name\":\"Claude Test {i}\",\"reasoning\":true,\"tool_call\":true,"
                + "\"modalities\":{\"input\":[\"text\",\"image\"],\"output\":[\"text\"]},"
                + $"\"cost\":{{\"input\":{3 * priceScale},\"output\":{15 * priceScale},\"cache_read\":0.3,\"cache_write\":3.75}},"
                + "\"limit\":{\"context\":200000,\"output\":64000}}");
        }

        return sb.Append("}}}").ToString();
    }
}

/// <summary>包住连接的流,把设备发来的每个字节记下来:断言「凭据不出现在任何帧里」用整段线上字节扫描,不靠某个 DTO 的字段。</summary>
public sealed class TapStream(Stream inner) : Stream
{
    private readonly MemoryStream _seen = new();

    public byte[] Transcript
    {
        get
        {
            lock (_seen)
            {
                return _seen.ToArray();
            }
        }
    }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        Record(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        Record(buffer.Span[..n]);
        return n;
    }

    private void Record(ReadOnlySpan<byte> bytes)
    {
        lock (_seen)
        {
            _seen.Write(bytes);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
