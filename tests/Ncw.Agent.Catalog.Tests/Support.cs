using System.Net;
using System.Text;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>脚本化的上游:按顺序消费回复,记录收到的请求(头在请求对象释放前拷出来)。</summary>
public sealed class FakeHttp : IUpstreamHttp
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Fallback { get; set; }

    public List<Seen> Requests { get; } = [];

    public sealed record Seen(string Method, string Url, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());
    }

    public FakeHttp Reply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next)
    {
        _script.Enqueue(next);
        return this;
    }

    public FakeHttp Ok(string body, string? etag = null) => Reply((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body, etag)));

    public FakeHttp Status(HttpStatusCode status, string body = "") => Reply((_, _) => Task.FromResult(Json(status, body)));

    public FakeHttp Throw(Exception e) => Reply((_, _) => throw e);

    /// <summary>永远不回:只有取消才能结束它(超时测试用)。</summary>
    public FakeHttp Hang() => Reply(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), headers));
        if (_script.Count > 0)
        {
            return _script.Dequeue()(request, ct);
        }

        return Fallback?.Invoke(request, ct) ?? Task.FromResult(Json(HttpStatusCode.InternalServerError, "no script"));
    }
}

/// <summary>退避睡眠:记录请求的时长,由测试放行(不睡真的时间)。</summary>
public sealed class ManualDelayer : IDelayer
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

    /// <summary>等到有一个睡眠挂起,然后放行它。</summary>
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

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class Samples
{
    /// <summary>一份 models.dev 形状的数据:<paramref name="models"/> 个 anthropic 模型 + 若干 deepseek 模型。</summary>
    public static string ModelsDev(int anthropic, int deepseek = 0, int priceScale = 1)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"anthropic\":{\"id\":\"anthropic\",\"name\":\"Anthropic\",\"models\":{");
        for (var i = 0; i < anthropic; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($"\"claude-test-{i}\":{{\"id\":\"claude-test-{i}\",\"name\":\"Claude Test {i}\",\"reasoning\":true,\"tool_call\":true,"
                + $"\"modalities\":{{\"input\":[\"text\",\"image\"],\"output\":[\"text\"]}},"
                + $"\"cost\":{{\"input\":{3 * priceScale},\"output\":{15 * priceScale},\"cache_read\":0.3,\"cache_write\":3.75}},"
                + "\"limit\":{\"context\":200000,\"output\":64000}}");
        }

        sb.Append("}}");
        if (deepseek > 0)
        {
            sb.Append(",\"deepseek\":{\"id\":\"deepseek\",\"name\":\"DeepSeek\",\"api\":\"https://api.deepseek.com\",\"models\":{");
            for (var i = 0; i < deepseek; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append($"\"ds-{i}\":{{\"id\":\"ds-{i}\",\"name\":\"DS {i}\",\"tool_call\":true,\"modalities\":{{\"input\":[\"text\"],\"output\":[\"text\"]}},"
                    + "\"cost\":{\"input\":0.28,\"output\":0.42},\"limit\":{\"context\":128000,\"output\":8192}}");
            }

            sb.Append("}}");
        }

        return sb.Append('}').ToString();
    }
}
