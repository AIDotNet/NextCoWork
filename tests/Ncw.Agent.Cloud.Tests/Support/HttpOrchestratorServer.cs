using System.Net;
using System.Net.Sockets;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud.Tests.Support;

/// <summary>
/// 把 <see cref="FakeOrchestrator"/> 放到真实的 HTTP 上(<see cref="HttpListener"/>,回环地址),路径和头按 <c>OrchestrationProtocol.Paths</c> 的契约:
/// 校验 <c>Authorization: Bearer</c> 和 <c>X-Ncw-Device</c>,把 <see cref="OrchestrationException"/> 映射成 HTTP 状态。
/// 给 <see cref="HttpOrchestrationChannel"/> 的测试和「真实 ncw-agent 进程」的冒烟测试用。
/// </summary>
public sealed class HttpOrchestratorServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly FakeOrchestrator _orchestrator;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public HttpOrchestratorServer(FakeOrchestrator orchestrator, string expectedToken)
    {
        _orchestrator = orchestrator;
        ExpectedToken = expectedToken;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUrl.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public Uri BaseUrl { get; }

    /// <summary>期望的 Bearer 令牌(测试里改它模拟令牌轮换)。</summary>
    public string ExpectedToken { get; set; }

    /// <summary>每个请求的(方法、路径、Authorization、X-Ncw-Device)。</summary>
    public List<(string Method, string Path, string? Authorization, string? Device)> Requests { get; } = [];

    /// <summary>非 null = 所有请求直接回这个状态码(模拟 5xx / 429)。</summary>
    public int? ForceStatus { get; set; }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            lock (Requests)
            {
                Requests.Add((request.HttpMethod, request.Url!.AbsolutePath, request.Headers["Authorization"], request.Headers[OrchestrationProtocol.DeviceHeader]));
            }

            if (ForceStatus is { } forced)
            {
                response.StatusCode = forced;
                return;
            }

            if (request.Headers["Authorization"] != $"Bearer {ExpectedToken}" || string.IsNullOrEmpty(request.Headers[OrchestrationProtocol.DeviceHeader]))
            {
                response.StatusCode = 401;
                return;
            }

            byte[] body;
            using (var memory = new MemoryStream())
            {
                await request.InputStream.CopyToAsync(memory).ConfigureAwait(false);
                body = memory.ToArray();
            }

            byte[] result;
            switch (request.Url!.AbsolutePath)
            {
                case OrchestrationProtocol.Paths.Hello:
                    result = OrchestrationWire.Encode(await _orchestrator.HelloAsync(OrchestrationWire.Decode(body, OrchestrationJson.Default.AgentHello)!, _stop.Token), OrchestrationJson.Default.HelloResponse);
                    break;
                case OrchestrationProtocol.Paths.Commands:
                {
                    var after = long.Parse(request.QueryString["after"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    var wait = TimeSpan.FromSeconds(double.Parse(request.QueryString["wait"] ?? "0", System.Globalization.CultureInfo.InvariantCulture));
                    result = OrchestrationWire.Encode(new CommandsResponse(await _orchestrator.PollCommandsAsync(after, wait, _stop.Token)), OrchestrationJson.Default.CommandsResponse);
                    break;
                }

                case OrchestrationProtocol.Paths.Ack:
                    await _orchestrator.AckAsync(OrchestrationWire.Decode(body, OrchestrationJson.Default.CommandAck)!, _stop.Token);
                    result = OrchestrationWire.Encode(new AckResponse(true), OrchestrationJson.Default.AckResponse);
                    break;
                case OrchestrationProtocol.Paths.Usage:
                    result = OrchestrationWire.Encode(await _orchestrator.ReportUsageAsync(OrchestrationWire.Decode(body, OrchestrationJson.Default.UsageBatch)!, _stop.Token), OrchestrationJson.Default.AckResponse);
                    break;
                case OrchestrationProtocol.Paths.Lifecycle:
                    await _orchestrator.NotifyLifecycleAsync(OrchestrationWire.Decode(body, OrchestrationJson.Default.LifecycleNotice)!, _stop.Token);
                    result = OrchestrationWire.Encode(new AckResponse(true), OrchestrationJson.Default.AckResponse);
                    break;
                default:
                    response.StatusCode = 404;
                    return;
            }

            response.ContentType = "application/json";
            await response.OutputStream.WriteAsync(result).ConfigureAwait(false);
        }
        catch (OrchestrationException e)
        {
            response.StatusCode = e.Status ?? 500;
        }
        catch (Exception e) when (e is HttpListenerException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
