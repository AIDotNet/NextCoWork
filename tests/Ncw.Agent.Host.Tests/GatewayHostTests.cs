using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Gateway;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 开放网关经真实 Agent 的端到端(docs/design/09 §11):默认关闭;开关 / 密钥 / 监听范围只有本机 owner;打开后 HTTP 的结果和协议调用同一个(同一张方法表);
/// 密钥绑定角色;换密钥旧的立刻失效;关闭后端口真的释放;会话忙时 <c>runs.start</c> 不排队(409)。真实 TCP(回环)。
/// </summary>
public sealed class GatewayHostTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private readonly HttpClient _http = new();
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _rig.DisposeAsync();
    }

    private async Task<GatewayStatusDto> EnableAsync()
    {
        var status = await _owner.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(true), idem: Guid.NewGuid().ToString("N"));
        Assert.True(status.Enabled);
        Assert.NotNull(status.Port);
        return status;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(int port, string method, string json, string? key, string? idem = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/{method}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

    private static string Json<T>(T value, PolyType.ITypeShape<T> shape) => Encoding.UTF8.GetString(JsonMsgPack.ToJson(FrameCodec.EncodePayload(value, shape)));

    // ───────────────────────── 开关与密钥 ─────────────────────────

    [Fact]
    public async Task The_gateway_is_off_by_default_and_has_no_address_until_it_is_turned_on_and_off_again_releases_the_port()
    {
        var off = await _owner.CallAsync(GatewayMethods.GetStatus, Unit.Value);
        Assert.Equal((false, (int?)null, false), (off.Enabled, off.Port, off.AllowRemote));
        Assert.Empty((await _owner.CallAsync(GatewayMethods.GetAddresses, Unit.Value)).Addresses);

        var on = await EnableAsync();
        Assert.Equal("127.0.0.1", on.Host); // 默认只绑回环
        Assert.Equal([$"http://127.0.0.1:{on.Port}"], (await _owner.CallAsync(GatewayMethods.GetAddresses, Unit.Value)).Addresses);
        var key = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(on.Port!.Value, "device.info", "{}", key)).Status);

        var off2 = await _owner.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(false), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal((false, (int?)null), (off2.Enabled, off2.Port));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PostAsync(on.Port.Value, "device.info", "{}", key));
        Assert.False((await _owner.CallAsync(SettingsMethods.Get, Unit.Value)).Settings.Gateway.Enabled); // 开关就是设置
    }

    [Fact]
    public async Task A_reconcile_returns_only_after_the_listener_is_up_even_when_a_later_one_queues_behind_it()
    {
        // 回归:对账以前带「版本号被后来的调用超过就提前返回」的优化。设置变更事件会 Task.Run 一次对账,setEnabled 的处理器自己又 await 一次,
        // 版本号较旧的那次拿到闸门后发现被取代,就在监听还没起来时返回,调用方读到 Port == null。
        // 做法:占住对账闸门,让 X(旧)和 Y(新)排队;放行后在 X「完成的那一刻」同步读状态。
        // X 释放闸门之后 Y 才能开始,所以旧逻辑里 X 提前返回时监听必然还没起来;现在 X 自己完成对账,读到的一定是端口。
        var gateway = _rig.Host.GatewayRuntime ?? throw new InvalidOperationException("device data is not ready");

        for (var round = 0; round < 6; round++)
        {
            var off = await _owner.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(false), idem: Guid.NewGuid().ToString("N"));
            Assert.Null(off.Port);

            await gateway.ReconcileGate.WaitAsync();
            var enable = _owner.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(true), idem: Guid.NewGuid().ToString("N"));
            // 先等「启用」真正写进设置:对账读的是拿到闸门那一刻的设置,慢机器上请求可能还没处理完,
            // 这时 X 合法地读到「关」而返回空端口,和被测的竞态无关。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!gateway.Status().Enabled)
            {
                Assert.True(DateTime.UtcNow < deadline, $"round {round}: the enable request never reached the settings");
                await Task.Delay(10);
            }

            await Task.Delay(100); // 让设置变更触发的对账和处理器自己的对账都排到闸门后面(没排上只会让这一轮变弱,不会让它出错)

            var x = gateway.ReconcileAsync();
            var portWhenXReturned = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = x.ContinueWith(_ => portWhenXReturned.TrySetResult(gateway.Status().Port), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var y = gateway.ReconcileAsync();
            gateway.ReconcileGate.Release();

            await Task.WhenAll(x, y, enable);
            Assert.True((await portWhenXReturned.Task) is not null, $"round {round}: the earlier reconcile returned before the listener was up");
            Assert.NotNull((await enable).Port);
        }
    }

    [Fact]
    public async Task Keys_are_per_role_stable_rotatable_and_a_rotated_key_stops_working_at_once()
    {
        var port = (await EnableAsync()).Port!.Value;
        var viewer = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key;
        var member = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("member"))).Key;

        Assert.NotEqual(viewer, member);
        Assert.Equal(64, viewer.Length);
        Assert.Equal(viewer, (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key); // 稳定
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(port, "device.info", "{}", viewer)).Status);

        var fresh = (await _owner.CallAsync(GatewayMethods.RotateKey, new GatewayKeyParams("viewer"), idem: Guid.NewGuid().ToString("N"))).Key;
        Assert.NotEqual(viewer, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(port, "device.info", "{}", viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(port, "device.info", "{}", fresh)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(port, "device.info", "{}", member)).Status); // 别的角色的密钥不受影响

        var bad = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("owner")));
        Assert.Equal(ErrorCodes.InvalidRequest, bad.Code); // 没有 owner 密钥:网关不给 owner 权限
    }

    [Fact]
    public async Task The_key_is_persisted_encrypted_and_never_appears_in_status_settings_or_the_database_in_the_clear()
    {
        await EnableAsync();
        var key = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("member"))).Key;

        Assert.DoesNotContain(key, Json(await _owner.CallAsync(GatewayMethods.GetStatus, Unit.Value), PayloadShapes.GetShape<GatewayStatusDto>()));
        var settings = await _owner.CallAsync(SettingsMethods.Get, Unit.Value);
        Assert.DoesNotContain(key, Json(settings, PayloadShapes.GetShape<DeviceSettingsState>()));
        foreach (var file in Directory.EnumerateFiles(_rig.DataRoot, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".sock")))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.False(memory.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(key)) >= 0, $"{file} holds the key in the clear");
        }
    }

    // ───────────────────────── 权限 ─────────────────────────

    [Fact]
    public async Task Only_a_local_owner_can_touch_the_gateway_a_remote_owner_cannot_open_the_port_or_read_a_key()
    {
        var remote = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
        var identity = PeerIdentity.Create("remote");
        var paired = await RemoteDevices.PairAsync("127.0.0.1", remote, identity, _rig.Host.Remote.PairingCode!, "remote");
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, "owner"));
        await using var far = await DeviceClient.ConnectAsync(await RemoteDevices.ConnectAsync("127.0.0.1", remote, identity, paired.DeviceFingerprint), new ClientHello("remote", "3.0.0"));

        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<ProtocolException>(() => far.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer")))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<ProtocolException>(() => far.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(true), idem: "k1"))).Code);
        // 绕过 gateway.* 直接改设置也不行
        var viaSettings = await Assert.ThrowsAsync<ProtocolException>(() =>
            far.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Gateway: new GatewaySettingsPatchDto(Enabled: true))), idem: "k2"));
        Assert.Equal(ErrorCodes.Forbidden, viaSettings.Code);
        Assert.False((await _owner.CallAsync(GatewayMethods.GetStatus, Unit.Value)).Enabled);
        // 远程 owner 能读状态(没有密钥),能重置健康
        Assert.False((await far.CallAsync(GatewayMethods.GetStatus, Unit.Value)).Enabled);
        Assert.NotNull(await far.CallAsync(GatewayMethods.ResetHealth, new GatewayResetHealthParams(null), idem: "k3"));
    }

    [Fact]
    public async Task A_keys_role_limits_what_it_can_call_and_dangerous_methods_are_unreachable_even_with_a_member_key()
    {
        var port = (await EnableAsync()).Port!.Value;
        var viewer = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key;
        var member = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("member"))).Key;

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(port, "projects.list", "{}", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(port, "projects.add", """{"Path":"/x","Name":null}""", viewer, "k1")).Status);
        // member 不是 owner:改设置要 owner
        var (forbidden, body) = await PostAsync(port, "settings.update", """{"Patch":{}}""", member, "k2");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
        Assert.Equal("forbidden", body.GetProperty("error").GetProperty("code").GetString());
        // 危险 / 本机专用的方法网关里根本没有
        foreach (var method in new[] { "storage.cleanup.execute", "storage.backup.restore", "gateway.getKey", "gateway.setEnabled", "remote.setEnabled", "trust.revoke" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(port, method, "{}", member, "k")).Status);
        }
    }

    // ───────────────────────── 同一张方法表 ─────────────────────────

    [Fact]
    public async Task An_http_call_returns_what_the_protocol_call_returns()
    {
        var port = (await EnableAsync()).Port!.Value;
        var viewer = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key;
        var session = await _rig.CreateSessionAsync(_owner, _project);

        var viaProtocol = await _owner.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(_project.WorkspaceId, false));
        var (status, body) = await PostAsync(port, "sessions.list", Json(new SessionsListParams(_project.WorkspaceId, false), PayloadShapes.GetShape<SessionsListParams>()), viewer);

        Assert.Equal(HttpStatusCode.OK, status);
        var sessions = body.GetProperty("result").GetProperty("Sessions").EnumerateArray().ToList();
        Assert.Equal(viaProtocol.Sessions.Select(s => s.Id), sessions.Select(s => s.GetProperty("Id").GetString()));
        Assert.Contains(session.Id, sessions.Select(s => s.GetProperty("Id").GetString()));

        var (_, projects) = await PostAsync(port, "projects.list", "{}", viewer);
        Assert.Equal(_project.WorkspaceId, projects.GetProperty("result").GetProperty("Projects")[0].GetProperty("WorkspaceId").GetString());
        // 业务错误经同一套错误码
        var (missing, error) = await PostAsync(port, "sessions.get", """{"SessionId":"no-such"}""", viewer);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_openapi_endpoint_describes_the_live_method_table_and_the_gateway_methods_are_in_it()
    {
        var on = await EnableAsync();
        var viewer = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("viewer"))).Key;

        var (status, body) = await PostAsync(on.Port!.Value, "gateway.openApi", "{}", viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body.GetProperty("result").GetProperty("Document").GetString()!);
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/runs.start", out _));
        Assert.True(paths.TryGetProperty("/api/gateway.openApi", out _));
        Assert.False(paths.TryGetProperty("/api/gateway.getKey", out _));
    }

    // ───────────────────────── Run ─────────────────────────

    [Fact]
    public async Task A_run_started_over_http_works_and_a_busy_session_is_refused_with_409_not_queued()
    {
        var port = (await EnableAsync()).Port!.Value;
        var member = (await _owner.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams("member"))).Key;
        var session = await _rig.CreateSessionAsync(_owner, _project);

        // 先用协议起一个会卡住的 Run
        using var release = new ManualResetEventSlim();
        _rig.Llm.Then(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return Up.Say("slow answer");
        });
        var first = await _rig.StartAsync(_owner, session.Id, "slow one");

        var request = new RunsStartParams(Guid.NewGuid().ToString("N"), session.Id, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, "via http")], new SendOptionsDto("sonnet", PermissionMode: "ask"));
        var (busy, body) = await PostAsync(port, "runs.start", Json(request, PayloadShapes.GetShape<RunsStartParams>()), member, "http-1");
        Assert.Equal(HttpStatusCode.Conflict, busy);
        Assert.Equal(("busy", true), (body.GetProperty("error").GetProperty("code").GetString(), body.GetProperty("error").GetProperty("retryable").GetBoolean()));

        release.Set();
        await (await RunWatch.OpenAsync(_owner, first.RunId)).SettledAsync();

        // 空闲了:同一个请求(换一个幂等键)成功,Run 真的跑完
        _rig.Llm.Then(Up.Say("http answer"));
        var (ok, started) = await PostAsync(port, "runs.start", Json(request with { RunId = Guid.NewGuid().ToString("N"), MessageId = Guid.NewGuid().ToString("N") }, PayloadShapes.GetShape<RunsStartParams>()), member, "http-2");
        Assert.Equal(HttpStatusCode.OK, ok);
        var runId = started.GetProperty("result").GetProperty("RunId").GetString()!;
        await (await RunWatch.OpenAsync(_owner, runId)).SettledAsync();
    }
}
