using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud;

/// <summary>编排通道失败。<see cref="Retryable"/>:网络问题 / 5xx / 429(退避后重试);false = 编排服务明确拒绝(401 / 403 / 4xx),重试没有意义。</summary>
public sealed class OrchestrationException(string message, bool retryable, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;

    public int? Status { get; } = status;
}

/// <summary>
/// Agent → 编排服务的通道(02 A6 的「编排控制通道」)。Agent 永远是发起方(沙箱不需要入站端口):
/// 打招呼 / 注册身份、长轮询拉指令、回执、批量上报用量、生命周期通知。所有方法在失败时抛 <see cref="OrchestrationException"/>。
/// 契约见 docs/design/09 §13;实现有 <see cref="HttpOrchestrationChannel"/>,测试用假编排服务。
/// </summary>
public interface IOrchestrationChannel
{
    /// <summary>每次启动 / 唤醒调用一次;第一次也是设备身份的注册(编排服务验证「持有私钥证明」后签发身份绑定)。</summary>
    Task<HelloResponse> HelloAsync(AgentHello hello, CancellationToken ct);

    /// <summary>拉取序号大于 <paramref name="afterSeq"/> 的指令;没有时最多等 <paramref name="wait"/>(长轮询),返回空数组。</summary>
    Task<SignedEnvelope[]> PollCommandsAsync(long afterSeq, TimeSpan wait, CancellationToken ct);

    Task AckAsync(CommandAck ack, CancellationToken ct);

    /// <summary>上报一批用量。编排服务按 <see cref="UsageBatch.BatchId"/> 去重:重复的批次回 <see cref="AckResponse.Duplicate"/>(Agent 当作成功)。</summary>
    Task<AckResponse> ReportUsageAsync(UsageBatch batch, CancellationToken ct);

    /// <summary>生命周期通知(休眠 / 升级前的最后一步,以及醒来)。</summary>
    Task NotifyLifecycleAsync(LifecycleNotice notice, CancellationToken ct);
}

/// <summary>
/// HTTPS + JSON 实现:路径见 <see cref="OrchestrationProtocol.Paths"/>,<c>Authorization: Bearer &lt;账户身份令牌&gt;</c> + <c>X-Ncw-Device: &lt;deviceId&gt;</c>。
/// 令牌由 <paramref name="token"/> 每次现取(轮换之后立即生效)。零反射(源生成 JSON),NativeAOT 可用。
/// </summary>
public sealed class HttpOrchestrationChannel(HttpClient http, Func<string?> token, Func<string> deviceId) : IOrchestrationChannel
{
    public Task<HelloResponse> HelloAsync(AgentHello hello, CancellationToken ct) =>
        PostAsync(OrchestrationProtocol.Paths.Hello, hello, OrchestrationJson.Default.AgentHello, OrchestrationJson.Default.HelloResponse, ct);

    public async Task<SignedEnvelope[]> PollCommandsAsync(long afterSeq, TimeSpan wait, CancellationToken ct)
    {
        var path = $"{OrchestrationProtocol.Paths.Commands}?after={afterSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}&wait={(int)wait.TotalSeconds}";
        using var request = Create(HttpMethod.Get, path);
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadAsync(response, OrchestrationJson.Default.CommandsResponse, ct).ConfigureAwait(false);
        return body.Commands ?? [];
    }

    public async Task AckAsync(CommandAck ack, CancellationToken ct)
    {
        _ = await PostAsync(OrchestrationProtocol.Paths.Ack, ack, OrchestrationJson.Default.CommandAck, OrchestrationJson.Default.AckResponse, ct).ConfigureAwait(false);
    }

    public Task<AckResponse> ReportUsageAsync(UsageBatch batch, CancellationToken ct) =>
        PostAsync(OrchestrationProtocol.Paths.Usage, batch, OrchestrationJson.Default.UsageBatch, OrchestrationJson.Default.AckResponse, ct);

    public async Task NotifyLifecycleAsync(LifecycleNotice notice, CancellationToken ct)
    {
        _ = await PostAsync(OrchestrationProtocol.Paths.Lifecycle, notice, OrchestrationJson.Default.LifecycleNotice, OrchestrationJson.Default.AckResponse, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage Create(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (token() is { Length: > 0 } t)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        }

        request.Headers.TryAddWithoutValidation(OrchestrationProtocol.DeviceHeader, deviceId());
        return request;
    }

    private async Task<TResponse> PostAsync<TBody, TResponse>(
        string path, TBody body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TBody> bodyInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseInfo, CancellationToken ct)
        where TResponse : class
    {
        using var request = Create(HttpMethod.Post, path);
        request.Content = new ByteArrayContent(OrchestrationWire.Encode(body, bodyInfo));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, responseInfo, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new OrchestrationException("The orchestration service could not be reached.", retryable: true, inner: e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OrchestrationException("The orchestration service timed out.", retryable: true);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        response.Dispose();
        // 429 / 408 / 5xx 可以退避重试;其余 4xx(令牌无效、设备未知……)是明确拒绝
        var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || status >= 500;
        throw new OrchestrationException($"The orchestration service answered {status}.", retryable, status);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct) where T : class
    {
        using (response)
        {
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync(stream, info, ct).ConfigureAwait(false)
                    ?? throw new OrchestrationException("The orchestration service sent an empty response.", retryable: true);
            }
            catch (JsonException e)
            {
                throw new OrchestrationException("The orchestration service sent an invalid response.", retryable: true, inner: e);
            }
            catch (HttpRequestException e)
            {
                throw new OrchestrationException("The orchestration connection was lost.", retryable: true, inner: e);
            }
        }
    }
}
