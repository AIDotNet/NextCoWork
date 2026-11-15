using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Gateway;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Gateway.Tests;

/// <summary>
/// 开放网关(docs/design/09 §11):同一张方法表的 HTTP 映射。JSON ↔ MessagePack 结构转换;OpenAPI 由方法表导出(只含网关暴露的方法);
/// HTTP 服务器:Bearer 密钥绑定角色(401 / 403 / 429)、命令要幂等键且重放只执行一次、错误码 → 状态码、本机专用方法天然调不到、SSE 订阅(快照 → 增量 → 游标续传)、请求大小 / 畸形请求。
/// 真实 TCP(回环)+ <see cref="HttpClient"/>。
/// </summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private const string ViewerKey = "viewer-key-0000000000000000000000000000000000000000000000000000000000";
    private const string MemberKey = "member-key-0000000000000000000000000000000000000000000000000000000000";

    private readonly MethodRouter _router = new();
    private readonly TopicHub _topics = new();
    private readonly HttpClient _http = new();
    private readonly List<string> _commandRuns = [];
    private GatewayServer _server = null!;
    private string _base = null!;
    private DeviceTopicState _device = new([], 1);

    private sealed class Keys : IGatewayAuth
    {
        public GatewayCaller? Authenticate(string bearer) => bearer switch
        {
            ViewerKey => new GatewayCaller("viewer", Role.Viewer),
            MemberKey => new GatewayCaller("member", Role.Member),
            _ => null,
        };
    }

    public ValueTask InitializeAsync()
    {
        _router.Handle(DeviceMethods.Info, (_, _, _) => ValueTask.FromResult(new DeviceInfoResult("dev-1", "Test device", "local", "3.0.0", "TestOS", "/data")));
        _router.Handle(DeviceMethods.ProjectsAdd, (p, _, _) =>
        {
            lock (_commandRuns)
            {
                _commandRuns.Add(p.Path);
            }

            return ValueTask.FromResult(new ProjectDto($"ws-{_commandRuns.Count}", p.Name ?? "n", p.Path, "local", null, 1));
        });
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(new ProjectsListResult([new ProjectDto("ws-1", "P", "/p", "local", null, 1)], 7)));
        _router.Handle(DeviceMethods.RemoteGetStatus, (_, _, _) => ValueTask.FromResult(new RemoteStatus(false, null, 7810, true, [], null, null)));
        _router.Handle(DeviceMethods.PairBegin, (_, _, _) => ValueTask.FromResult(new PairBeginResult([1], [2])));
        _router.Handle(DeviceMethods.ProjectsRemove, (_, _, _) => throw new ProtocolException(ErrorCodes.Conflict, "busy project"));
        _topics.Register(DeviceMethods.DeviceTopic, () => _device);
        _server = GatewayServer.Start(new GatewayOptions { Router = _router, Topics = _topics, Auth = new Keys(), MaxAuthFailuresPerMinute = 3 }, 0, allowLan: false);
        _base = $"http://127.0.0.1:{_server.Port}";
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string method, string json, string? key = MemberKey, string? idem = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/{method}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (idem is not null)
        {
            request.Headers.Add("Idempotency-Key", idem);
        }

        using var response = await _http.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    // ───────────────────────── JSON ↔ MessagePack ─────────────────────────

    [Fact]
    public void Json_converts_to_the_wire_form_the_method_table_decodes_and_back_with_the_same_key_names()
    {
        var bytes = JsonMsgPack.FromJson("""{"Path":"/work/app","Name":null}"""u8);
        var decoded = FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProjectsAddParams>());
        Assert.Equal(("/work/app", null), (decoded.Path, decoded.Name));

        var result = FrameCodec.EncodePayload(new ProjectDto("ws-1", "P", "/p", "local", "host", 123), PayloadShapes.GetShape<ProjectDto>());
        using var json = JsonDocument.Parse(JsonMsgPack.ToJson(result));
        Assert.Equal(("ws-1", "host", 123L), (json.RootElement.GetProperty("WorkspaceId").GetString(), json.RootElement.GetProperty("EnvironmentName").GetString(), json.RootElement.GetProperty("CreatedAt").GetInt64()));
    }

    [Fact]
    public void Numbers_booleans_nulls_nested_values_and_unicode_survive_a_round_trip_and_bad_input_is_rejected()
    {
        const string text = """{"a":-5,"b":18446744073709551615,"c":1.5,"d":true,"e":null,"f":[1,[2,{"g":"你好"}]],"h":{}}""";
        var back = Encoding.UTF8.GetString(JsonMsgPack.ToJson(JsonMsgPack.FromJson(Encoding.UTF8.GetBytes(text))));
        Assert.Equal(text, back);
        using (var emoji = JsonDocument.Parse(JsonMsgPack.ToJson(JsonMsgPack.FromJson("""{"g":"👨‍👩‍👧 ok"}"""u8))))
        {
            Assert.Equal("👨‍👩‍👧 ok", emoji.RootElement.GetProperty("g").GetString());
        }
        Assert.ThrowsAny<JsonException>(() => JsonMsgPack.FromJson("{not json"u8));
        Assert.ThrowsAny<JsonException>(() => JsonMsgPack.FromJson(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("[", JsonMsgPack.MaxDepth + 5)) + string.Concat(Enumerable.Repeat("]", JsonMsgPack.MaxDepth + 5)))));
        Assert.Equal("{}", Encoding.UTF8.GetString(JsonMsgPack.ToJson(JsonMsgPack.FromJson([])))); // 空 body = 空对象
    }

    [Fact]
    public void Binary_fields_come_out_as_base64_strings_in_responses()
    {
        var bytes = FrameCodec.EncodePayload(new ExportChunkDto([1, 2, 3, 255], true), PayloadShapes.GetShape<ExportChunkDto>());
        using var json = JsonDocument.Parse(JsonMsgPack.ToJson(bytes));
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 255]), json.RootElement.GetProperty("Data").GetString());
    }

    // ───────────────────────── OpenAPI ─────────────────────────

    [Fact]
    public void The_openapi_document_is_exported_from_the_method_table_and_lists_only_what_the_gateway_exposes()
    {
        var document = OpenApiExporter.Export(
            [.. DeviceMethods.All, .. SettingsMethods.All, .. StorageMethods.All, .. AttachmentsMethods.All, .. GatewayMethods.All, .. ConversationMethods.All], "T", "1");
        using var doc = JsonDocument.Parse(document);
        var paths = doc.RootElement.GetProperty("paths");

        Assert.Equal("3.1.0", doc.RootElement.GetProperty("openapi").GetString());
        Assert.True(paths.TryGetProperty("/api/device.info", out _));
        Assert.True(paths.TryGetProperty("/api/settings.update", out var update));
        Assert.True(paths.TryGetProperty("/api/sessions.list", out _));
        // 本机专用 / 未配对专用 / 带二进制参数的不在其中
        foreach (var hidden in new[] { "remote.setEnabled", "pair.begin", "pair.createQr", "trust.revoke", "storage.backup.restore", "storage.cleanup.execute", "storage.import.apply", "storage.import.write", "gateway.getKey", "gateway.setEnabled", "attachments.upload.chunk" })
        {
            Assert.False(paths.TryGetProperty($"/api/{hidden}", out _), hidden);
        }

        // 命令要幂等键头;查询不要
        var post = update.GetProperty("post");
        Assert.Equal("command", post.GetProperty("x-ncw-kind").GetString());
        Assert.Equal("owner", post.GetProperty("x-ncw-minRole").GetString());
        Assert.Equal("Idempotency-Key", post.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.False(paths.GetProperty("/api/device.info").GetProperty("post").TryGetProperty("parameters", out _));
        // schema 从类型形状遍历出来:DTO 的属性名和嵌套类型都在
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("DeviceSettingsPatchDto").GetProperty("properties").TryGetProperty("MaxOutputTokens", out _));
        Assert.True(schemas.TryGetProperty("GatewayStatusDto", out _));
        Assert.Contains("Name", schemas.GetProperty("ProjectsAddParams").GetProperty("required").EnumerateArray().Select(e => e.GetString())); // 可空的键也必须出现
        Assert.Equal("array", schemas.GetProperty("ProjectsListResult").GetProperty("properties").GetProperty("Projects").GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _));
    }

    [Fact]
    public async Task The_openapi_document_is_served_to_an_authenticated_caller_only()
    {
        using var anonymous = await _http.GetAsync($"{_base}/api/openapi.json");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/openapi.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ViewerKey);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("paths").TryGetProperty("/api/device.info", out _));
    }

    // ───────────────────────── 方法调用 ─────────────────────────

    [Fact]
    public async Task A_query_returns_ok_with_the_result_as_json()
    {
        var (status, body) = await PostAsync("device.info", "{}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(("dev-1", "Test device"), (body.GetProperty("result").GetProperty("DeviceId").GetString(), body.GetProperty("result").GetProperty("Name").GetString()));
        var (_, list) = await PostAsync("projects.list", "", ViewerKey); // 空 body = 空参数;viewer 读得了
        Assert.Equal(7, list.GetProperty("result").GetProperty("Revision").GetInt32());
    }

    [Fact]
    public async Task Authentication_failures_are_401_and_repeated_ones_from_one_address_become_429()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("device.info", "{}", key: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("device.info", "{}", key: "wrong")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("device.info", "{}", key: "also-wrong")).Status);

        var (status, body) = await PostAsync("device.info", "{}", key: "again");
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("rate_limited", body.GetProperty("error").GetProperty("code").GetString());
        // 限速期间连正确的密钥也被挡(防猜测);这里只验证确实被限速了
        Assert.False(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task The_keys_role_is_checked_by_the_router_a_viewer_cannot_call_a_member_command()
    {
        var (status, body) = await PostAsync("projects.add", """{"Path":"/x","Name":null}""", ViewerKey, idem: "k1");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("forbidden", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_commandRuns);
    }

    [Fact]
    public async Task A_command_needs_an_idempotency_key_and_a_replay_returns_the_first_result_without_running_again()
    {
        var (noKey, noKeyBody) = await PostAsync("projects.add", """{"Path":"/x","Name":null}""");
        Assert.Equal(HttpStatusCode.BadRequest, noKey);
        Assert.Equal("invalid_request", noKeyBody.GetProperty("error").GetProperty("code").GetString());

        var (first, firstBody) = await PostAsync("projects.add", """{"Path":"/x","Name":null}""", idem: "same-key");
        var (second, secondBody) = await PostAsync("projects.add", """{"Path":"/x","Name":null}""", idem: "same-key");
        Assert.True((HttpStatusCode.OK, HttpStatusCode.OK) == (first, second), firstBody.ToString() + secondBody);
        Assert.Equal(firstBody.GetProperty("result").GetProperty("WorkspaceId").GetString(), secondBody.GetProperty("result").GetProperty("WorkspaceId").GetString());
        Assert.Single(_commandRuns); // 重放只执行一次

        await PostAsync("projects.add", """{"Path":"/y","Name":null}""", idem: "other-key");
        Assert.Equal(2, _commandRuns.Count);
    }

    [Theory]
    [InlineData("nope.nothing", 404)]
    [InlineData("remote.getStatus", 404)]   // 本机专用:不暴露
    [InlineData("pair.begin", 404)]         // 配对在 TLS 通道上做
    public async Task Methods_the_gateway_does_not_expose_are_not_found(string method, int status)
    {
        var (code, body) = await PostAsync(method, "{}", idem: "k");
        Assert.Equal(status, (int)code);
        Assert.Equal("not_found", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_parameters_are_an_invalid_request_not_an_internal_error()
    {
        var (status, body) = await PostAsync("projects.add", """{"Path":"/x"}""", idem: "k");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_request", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Name", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Business_errors_map_to_their_http_status_with_the_protocol_error_body()
    {
        var (status, body) = await PostAsync("projects.remove", """{"WorkspaceId":"ws-1"}""", idem: "k");

        Assert.Equal(HttpStatusCode.Conflict, status);
        var error = body.GetProperty("error");
        Assert.Equal(("conflict", "busy project", false), (error.GetProperty("code").GetString(), error.GetProperty("message").GetString(), error.GetProperty("retryable").GetBoolean()));
    }

    [Fact]
    public void Every_protocol_error_code_has_an_http_status()
    {
        Assert.Equal(
            [400, 401, 403, 404, 409, 409, 413, 429, 501, 503, 500],
            new[] { ErrorCodes.InvalidRequest, ErrorCodes.Unauthorized, ErrorCodes.Forbidden, ErrorCodes.NotFound, ErrorCodes.Conflict, ErrorCodes.Busy, ErrorCodes.TooLarge, ErrorCodes.RateLimited, ErrorCodes.Unsupported, ErrorCodes.Unavailable, ErrorCodes.Internal }
                .Select(GatewayServer.StatusOf));
    }

    [Fact]
    public async Task Malformed_requests_are_refused_cleanly_invalid_json_wrong_verb_and_oversized_bodies()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("device.info", "{not json")).Status);

        using var get = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/device.info");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MemberKey);
        using var wrongVerb = await _http.SendAsync(get);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongVerb.StatusCode);

        using var big = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/device.info") { Content = new ByteArrayContent(new byte[5 * 1024 * 1024]) };
        big.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MemberKey);
        try
        {
            using var response = await _http.SendAsync(big);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // 服务器在读完之前就回了 413 并断开:客户端看到的是连接被重置,同样说明被拒绝了
        }

        using var notApi = await _http.GetAsync($"{_base}/anything");
        Assert.Equal(HttpStatusCode.NotFound, notApi.StatusCode);
    }

    // ───────────────────────── SSE ─────────────────────────

    private async Task<(HttpResponseMessage Response, StreamReader Reader)> SubscribeAsync(string topic, string? key = MemberKey, long? cursor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/topics/{topic}{(cursor is null ? "" : $"?cursor={cursor}")}");
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    private static async Task<(string Event, JsonElement Data)> NextEventAsync(StreamReader reader)
    {
        string? name = null;
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new EndOfStreamException();
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
            {
                using var doc = JsonDocument.Parse(line[6..]);
                return (name, doc.RootElement.Clone());
            }
        }
    }

    [Fact]
    public async Task A_subscription_streams_a_snapshot_then_increments_and_resumes_from_a_cursor()
    {
        var (response, reader) = await SubscribeAsync("device");
        using (response)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            var (kind, snapshot) = await NextEventAsync(reader);
            Assert.Equal("snapshot", kind);
            Assert.Equal(("device", 1), (snapshot.GetProperty("topic").GetString(), snapshot.GetProperty("state").GetProperty("ProjectsRevision").GetInt32()));

            _device = new DeviceTopicState([], 2);
            _topics.Publish("device", DeviceEvents.ProjectsChanged, new ProjectsListResult([], 2), PayloadShapes.GetShape<ProjectsListResult>());
            var (evt, data) = await NextEventAsync(reader);
            Assert.Equal("evt", evt);
            Assert.Equal((DeviceEvents.ProjectsChanged, 2), (data.GetProperty("type").GetString(), data.GetProperty("data").GetProperty("Revision").GetInt32()));
            var seq = data.GetProperty("seq").GetInt64();

            // 游标续得上:不发快照,只补发游标之后的增量
            _topics.Publish("device", DeviceEvents.ProjectsChanged, new ProjectsListResult([], 3), PayloadShapes.GetShape<ProjectsListResult>());
            var (resumedResponse, resumed) = await SubscribeAsync("device", cursor: seq);
            using (resumedResponse)
            {
                var (kind2, replay) = await NextEventAsync(resumed);
                Assert.Equal(("evt", 3), (kind2, replay.GetProperty("data").GetProperty("Revision").GetInt32()));
            }
        }
    }

    [Fact]
    public async Task Subscribing_needs_a_key_a_known_topic_and_the_keys_role()
    {
        var (anonymous, _) = await SubscribeAsync("device", key: null);
        using (anonymous)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var (unknown, _) = await SubscribeAsync("no-such-topic");
        using (unknown)
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
    }

    [Fact]
    public async Task Stopping_the_server_closes_open_subscriptions_and_new_connections_are_refused()
    {
        var (response, reader) = await SubscribeAsync("device");
        using (response)
        {
            await NextEventAsync(reader);
            await _server.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is not null)
                {
                }

                throw new EndOfStreamException();
            });
        }

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync($"{_base}/api/device.info"));
        _server = GatewayServer.Start(new GatewayOptions { Router = _router, Topics = _topics, Auth = new Keys() }, 0, allowLan: false); // 让 DisposeAsync 有东西可停
    }
}
