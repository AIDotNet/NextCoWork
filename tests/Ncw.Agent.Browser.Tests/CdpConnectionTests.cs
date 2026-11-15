using System.Text.Json;
using Ncw.Agent.Browser.Cdp;

namespace Ncw.Agent.Browser.Tests;

/// <summary>CDP 客户端的协议级测试:真 WebSocket、假服务。</summary>
public sealed class CdpConnectionTests
{
    [Fact]
    public async Task A_command_gets_its_own_result_by_id_even_when_answered_out_of_order()
    {
        await using var server = new FakeCdpServer();
        var release = new TaskCompletionSource();
        server.Handle("Slow.one", _ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(5));
            return "{\"which\":\"slow\"}";
        });
        server.Handle("Fast.one", "{\"which\":\"fast\"}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var slow = conn.SendAsync("Slow.one");
        var fast = await conn.SendAsync("Fast.one");
        Assert.Equal("fast", fast.GetProperty("which").GetString());
        release.SetResult();
        Assert.Equal("slow", (await slow).GetProperty("which").GetString());
    }

    [Fact]
    public async Task Params_and_session_id_go_on_the_wire_as_written()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Page.navigate", "{}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);

        await conn.SendAsync("Page.navigate", w =>
        {
            w.WriteString("url", "https://example.com/?q=\"quoted\"\n");
            w.WriteNumber("n", 3);
        }, "SESSION-1");

        var request = Assert.Single(server.Of("Page.navigate"));
        Assert.Equal("SESSION-1", request.SessionId);
        Assert.Equal("https://example.com/?q=\"quoted\"\n", request.Str("url"));
        Assert.Equal(3, request.Num("n"));
    }

    [Fact]
    public async Task An_error_reply_becomes_a_CdpException_with_code_and_message()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Bad.call", _ => throw new CdpFailure(-32000, "Cannot find context with specified id"));
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var e = await Assert.ThrowsAsync<CdpException>(() => conn.SendAsync("Bad.call"));
        Assert.Equal(-32000, e.Code);
        Assert.Contains("Cannot find context", e.Message);
        // 一次失败不影响后续命令
        server.Handle("Good.call", "{\"ok\":true}");
        Assert.True((await conn.SendAsync("Good.call")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_method_is_an_error_not_a_hang()
    {
        await using var server = new FakeCdpServer();
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        await Assert.ThrowsAsync<CdpException>(() => conn.SendAsync("Nope.nothing"));
    }

    [Fact]
    public async Task Events_reach_subscribers_and_expectations_registered_before_the_command()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Fire.event", r =>
        {
            _ = server.PushAsync("Thing.happened", "{\"n\":7}", "S1");
            return "{}";
        });
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        var seen = new List<CdpEvent>();
        conn.EventReceived += seen.Add;

        using var waiter = conn.Expect("Thing.happened", "S1", p => p.GetProperty("n").GetInt32() == 7);
        await conn.SendAsync("Fire.event");
        var payload = await waiter.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(7, payload.GetProperty("n").GetInt32());
        Assert.Contains(seen, e => e.Method == "Thing.happened" && e.SessionId == "S1");
    }

    [Fact]
    public async Task An_expectation_ignores_other_sessions_and_failing_predicates_and_times_out()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Ping", "{}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        using var waiter = conn.Expect("Thing.happened", "S1");
        await conn.SendAsync("Ping");
        await server.PushAsync("Thing.happened", "{}", "OTHER");
        await server.PushAsync("Other.event", "{}", "S1");

        await Assert.ThrowsAsync<TimeoutException>(() => waiter.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task Losing_the_connection_fails_every_in_flight_command_and_completes_Closed()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Hang", _ =>
        {
            Thread.Sleep(50);
            return "{}";
        });
        server.Handle("Ping", "{}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        await conn.SendAsync("Ping");
        using var waiter = conn.Expect("Never.arrives");

        var pending = conn.SendAsync("Hang", timeout: TimeSpan.FromSeconds(10));
        await server.DropConnectionsAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        await Assert.ThrowsAsync<CdpDisconnectedException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(conn.IsOpen);
        await Assert.ThrowsAsync<CdpDisconnectedException>(() => conn.SendAsync("Ping"));
    }

    [Fact]
    public async Task A_command_the_browser_never_answers_times_out_without_killing_the_connection()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Stall", _ =>
        {
            Thread.Sleep(600);
            return "{}";
        });
        server.Handle("Ping", "{\"pong\":1}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => conn.SendAsync("Stall", timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, (await conn.SendAsync("Ping")).GetProperty("pong").GetInt32());
    }

    [Fact]
    public async Task Cancellation_propagates_as_cancellation_not_as_a_timeout()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Stall", _ =>
        {
            Thread.Sleep(500);
            return "{}";
        });
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conn.SendAsync("Stall", ct: cts.Token));
    }

    [Fact]
    public async Task A_broken_json_message_from_the_browser_is_ignored()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Ping", "{\"pong\":1}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);
        await conn.SendAsync("Ping");

        await server.SendRawAsync("{not json");
        await server.SendRawAsync("[1,2,3]");

        Assert.Equal(1, (await conn.SendAsync("Ping")).GetProperty("pong").GetInt32());
        Assert.True(conn.IsOpen);
    }

    [Fact]
    public async Task A_result_can_be_read_after_the_message_buffer_is_reused()
    {
        await using var server = new FakeCdpServer();
        server.Handle("Echo", r => $"{{\"text\":\"{r.Str("text")}\"}}");
        await using var conn = await CdpConnection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var first = await conn.SendAsync("Echo", w => w.WriteString("text", "first"));
        await conn.SendAsync("Echo", w => w.WriteString("text", "second-and-longer"));
        Assert.Equal("first", first.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Connecting_to_nothing_fails_instead_of_hanging()
    {
        var closed = new FakeCdpServer();
        var endpoint = closed.Endpoint;
        await closed.DisposeAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => CdpConnection.ConnectAsync(endpoint, cts.Token));
    }
}
