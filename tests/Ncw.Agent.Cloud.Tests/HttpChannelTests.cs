using Ncw.Agent.Cloud.Tests.Support;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Security;

namespace Ncw.Agent.Cloud.Tests;

/// <summary>
/// <see cref="HttpOrchestrationChannel"/>(Agent → 编排服务的 HTTPS + JSON 实现)对真实 HTTP 的测试:路径、头、令牌轮换、错误分类(可重试 / 不可重试)、长轮询。
/// 这些路径和头就是 docs/design/09 §13 的契约。
/// </summary>
public sealed class HttpChannelTests : IDisposable
{
    private const string Token = "bearer-token-1";
    private readonly Issuer _issuer = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly PeerIdentity _device = PeerIdentity.Create("http-device");
    private readonly FakeOrchestrator _orchestrator;
    private readonly HttpOrchestratorServer _server;
    private readonly HttpClient _http;
    private readonly AccountTokenHolder _token = new(Token);

    public HttpChannelTests()
    {
        _orchestrator = new FakeOrchestrator(_issuer, "wss://relay.example.com", _clock);
        _server = new HttpOrchestratorServer(_orchestrator, Token);
        _http = new HttpClient { BaseAddress = _server.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private HttpOrchestrationChannel Channel() => new(_http, () => _token.Current, () => _device.DeviceId);

    private AgentHello Hello(long lastSeq = 0)
    {
        var issuedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        return new AgentHello(
            1, _device.DeviceId, Convert.ToBase64String(Ncw.Protocol.Transport.RelayProtocol.PublicKeyInfo(_device)),
            Convert.ToBase64String(DeviceProof.Sign(_device, issuedAt, "boot")), issuedAt, "3.0", "boot", false, lastSeq);
    }

    [Fact]
    public async Task Hello_poll_ack_usage_and_lifecycle_travel_over_http_with_the_documented_paths_and_headers()
    {
        var channel = Channel();

        var hello = await channel.HelloAsync(Hello(), default);
        Assert.NotNull(hello.Binding);
        Assert.Empty(hello.Commands);

        var (token, seq) = _orchestrator.Grant(PeerIdentity.Create("phone"));
        var commands = await channel.PollCommandsAsync(0, TimeSpan.FromSeconds(2), default);
        Assert.Single(commands);
        await channel.AckAsync(new CommandAck(seq, AckStatuses.Applied), default);
        Assert.Equal(AckStatuses.Applied, _orchestrator.AckFor(seq)!.Status);
        Assert.NotEmpty(token);

        var batch = new UsageBatch(1, $"{_device.DeviceId}:1", _device.DeviceId, 1, 1, 2, 1, 10, 5, 0, 60);
        Assert.False((await channel.ReportUsageAsync(batch, default)).Duplicate);
        Assert.True((await channel.ReportUsageAsync(batch, default)).Duplicate); // 幂等:同一个 batchId 的重发
        await channel.NotifyLifecycleAsync(new LifecycleNotice(1, _device.DeviceId, LifecycleStates.Sleeping, "idle", 1), default);
        Assert.Single(_orchestrator.Notices);

        Assert.All(_server.Requests, r =>
        {
            Assert.Equal($"Bearer {Token}", r.Authorization);
            Assert.Equal(_device.DeviceId, r.Device);
        });
        Assert.Equal(
            [OrchestrationProtocol.Paths.Hello, OrchestrationProtocol.Paths.Commands, OrchestrationProtocol.Paths.Ack, OrchestrationProtocol.Paths.Usage, OrchestrationProtocol.Paths.Usage, OrchestrationProtocol.Paths.Lifecycle],
            _server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task A_long_poll_with_nothing_to_say_returns_empty_after_the_wait_and_wakes_up_early_when_a_command_arrives()
    {
        var channel = Channel();
        await channel.HelloAsync(Hello(), default);

        var empty = await channel.PollCommandsAsync(0, TimeSpan.FromMilliseconds(300), default);
        Assert.Empty(empty);

        var waiting = channel.PollCommandsAsync(0, TimeSpan.FromSeconds(20), default);
        await Task.Delay(100);
        _orchestrator.SetQuota(new QuotaLimits(DailyRuns: 1));
        var woken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(woken);
    }

    [Fact]
    public async Task The_token_is_read_per_request_so_a_rotation_takes_effect_immediately_and_a_stale_token_is_refused_for_good()
    {
        var channel = Channel();
        await channel.HelloAsync(Hello(), default);

        _server.ExpectedToken = "bearer-token-2"; // 编排服务轮换了令牌
        var stale = await Assert.ThrowsAsync<OrchestrationException>(() => channel.NotifyLifecycleAsync(new LifecycleNotice(1, "d", "awake", "x", 1), default));
        Assert.Equal((401, false), (stale.Status, stale.Retryable)); // 明确拒绝:重试没有意义

        _token.Current = "bearer-token-2";
        await channel.NotifyLifecycleAsync(new LifecycleNotice(1, "d", "awake", "x", 1), default);
        Assert.Equal("Bearer bearer-token-2", _server.Requests[^1].Authorization);
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(500, true)]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public async Task Server_errors_and_throttling_are_retryable_client_errors_are_not(int status, bool retryable)
    {
        _server.ForceStatus = status;

        var error = await Assert.ThrowsAsync<OrchestrationException>(() => Channel().HelloAsync(Hello(), default));

        Assert.Equal((status, retryable), (error.Status, error.Retryable));
    }

    [Fact]
    public async Task An_unreachable_orchestrator_and_a_garbage_response_are_retryable_failures()
    {
        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/") };
        var unreachable = await Assert.ThrowsAsync<OrchestrationException>(() => new HttpOrchestrationChannel(dead, () => Token, () => "d").HelloAsync(Hello(), default));
        Assert.True(unreachable.Retryable);

        // 假服务回了一段不是 JSON 的东西
        _server.ForceStatus = null;
        var garbage = new HttpClient(new GarbageHandler()) { BaseAddress = _server.BaseUrl };
        var invalid = await Assert.ThrowsAsync<OrchestrationException>(() => new HttpOrchestrationChannel(garbage, () => Token, () => "d").HelloAsync(Hello(), default));
        Assert.True(invalid.Retryable);
    }

    private sealed class GarbageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") });
    }
}

/// <summary>内网健康检查端点(可选;远程客户端的流量只走中继,它不是控制通道)。</summary>
public sealed class HealthEndpointTests
{
    private static async Task<string> GetAsync(int port, string path)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n"));
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Healthz_reports_ready_with_the_public_device_id_503_while_preparing_and_404_elsewhere()
    {
        var ready = true;
        using var endpoint = HealthEndpoint.Start("127.0.0.1", 0, () => (ready, "dev_abcdefghjkmnpqrstvwxyz0123"));

        var ok = await GetAsync(endpoint.Port, "/healthz");
        Assert.StartsWith("HTTP/1.1 200 OK", ok);
        Assert.Contains("\"deviceId\":\"dev_abcdefghjkmnpqrstvwxyz0123\"", ok);

        ready = false;
        Assert.StartsWith("HTTP/1.1 503", await GetAsync(endpoint.Port, "/healthz"));
        Assert.StartsWith("HTTP/1.1 404", await GetAsync(endpoint.Port, "/admin"));
        Assert.DoesNotContain("token", ok, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_bind_address_is_rejected_at_startup_not_silently_widened()
    {
        Assert.Throws<FormatException>(() => HealthEndpoint.Start("not-an-ip", 0, () => (true, "d")));
    }
}
