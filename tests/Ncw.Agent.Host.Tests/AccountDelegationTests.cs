using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Plugins;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// <c>account.attachDelegatedToken</c> 的端到端:真实 Agent(进程内)+ 真实 <see cref="DeviceClient"/> + 假的市场 HTTP(核对 <c>Authorization</c>)。
/// 覆盖:交付 / 状态 / 撤销;形状校验;远程通道调不了;技能与插件市场在令牌到位前是 <c>authRequired</c>、到位后恢复、撤销或过期后又回到 <c>authRequired</c>;
/// 令牌从不上线(控制端收到的每个字节、诊断日志、数据根下的所有文件里都扫不到)。
/// </summary>
public sealed class AccountDelegationTests : IAsyncLifetime
{
    private const string Token = "DELEGATED-TOKEN-7f3a9c1e5b";

    private readonly HostDiagnostics _log = new();
    private readonly FakeMarketHttp _market = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private int _port;

    public AccountDelegationTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            McpDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            Diagnostics = _log,
            MarketHttpClient = new HttpClient(_market),
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static long InMinutes(int minutes) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds();

    private Task<AccountStatusDto> AttachAsync(DeviceClient client, string token = Token, long? expiresAt = null, string accountId = "u1", string? teamId = "t1") =>
        client.CallAsync(AccountMethods.AttachDelegatedToken, new AccountAttachParams(token, expiresAt ?? InMinutes(30), accountId, teamId), idem: Guid.NewGuid().ToString("N"));

    private static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

    private static async Task<ProtocolException> Fails<T>(Func<Task<T>> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    // ───────────────────────── 假市场 ─────────────────────────

    private sealed class FakeMarketHttp : HttpMessageHandler
    {
        public static readonly byte[] PluginZip = BuildPluginZip();
        public static readonly byte[] SkillZip = BuildSkillZip();

        public List<(string Method, string Path, string? Authorization)> Requests { get; } = [];

        public List<string> GrantAuthorizations { get { lock (Requests) { return [.. Requests.Where(r => r.Path.EndsWith("/install", StringComparison.Ordinal)).Select(r => r.Authorization ?? "(none)")]; } } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var auth = request.Headers.Authorization?.ToString();
            lock (Requests)
            {
                Requests.Add((request.Method.Method, path, auth));
            }

            if (path.EndsWith("/install", StringComparison.Ordinal))
            {
                // 授权:没有令牌 / 令牌不对 = 401;对了给权威摘要
                if (auth != $"Bearer {Token}")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                var isPlugin = path.Contains("/plugins/", StringComparison.Ordinal);
                var sha = Convert.ToHexStringLower(SHA256.HashData(isPlugin ? PluginZip : SkillZip));
                return Task.FromResult(Json("{\"data\":{\"version\":\"1.0.0\",\"sha256\":\"" + sha + "\"}}"));
            }

            return Task.FromResult(path switch
            {
                "/api/plugins/hello" => Json("""{"data":{"plugin":{"slug":"hello","publisher":"acme","name":"hello","displayName":"Hello","version":"1.0.0"},"versions":[{"version":"1.0.0"}]}}"""),
                "/api/plugins/hello/versions/1.0.0/download" => Bytes(PluginZip),
                "/api/plugins" => Json("""{"data":{"items":[{"slug":"hello","publisher":"acme","name":"hello","displayName":"Hello","version":"1.0.0","permissions":["storage"]}],"hasNext":false}}"""),
                "/api/skills/pdf-tools" => Json("""{"data":{"skill":{"slug":"pdf-tools","name":"pdf-tools","version":"1.0.0"},"versions":[{"version":"1.0.0"}]}}"""),
                "/api/skills/pdf-tools/versions/1.0.0/download" => Bytes(SkillZip),
                "/api/skills" => Json("""{"data":{"items":[{"slug":"pdf-tools","name":"pdf-tools","displayName":"PDF tools","version":"1.0.0"}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        private static byte[] BuildPluginZip()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string path, string content)
                {
                    using var w = new StreamWriter(zip.CreateEntry($"acme.hello/{path}").Open(), new UTF8Encoding(false));
                    w.Write(content);
                }

                Add("package.json", """{ "name": "hello", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./main.js", "permissions": ["storage"] }""");
                Add("main.js", "export function activate() {}");
            }

            return memory.ToArray();
        }

        private static byte[] BuildSkillZip()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var w = new StreamWriter(zip.CreateEntry("pdf-tools/SKILL.md").Open(), new UTF8Encoding(false));
                w.Write("---\nname: pdf-tools\ndescription: Work with PDF files.\n---\n\nUse the pdf tools.\n");
            }

            return memory.ToArray();
        }
    }

    // ───────────────────────── 交付 / 状态 / 撤销 ─────────────────────────

    [Fact]
    public async Task The_token_is_attached_reported_without_itself_and_detached()
    {
        var before = await _owner.CallAsync(AccountMethods.GetStatus, Unit.Value);
        Assert.Equal(new AccountStatusDto(false, null, null, null), before);

        var expires = InMinutes(30);
        var attached = await AttachAsync(_owner, expiresAt: expires);
        Assert.Equal(new AccountStatusDto(true, "u1", "t1", expires), attached);
        Assert.Equal(attached, await _owner.CallAsync(AccountMethods.GetStatus, Unit.Value));

        // 换团队 = 再交付一次(替换)
        Assert.Equal("t2", (await AttachAsync(_owner, teamId: "t2")).TeamId);

        var detached = await _owner.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"));
        Assert.False(detached.Attached);
        Assert.False((await _owner.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"))).Attached); // 幂等
    }

    [Fact]
    public async Task Malformed_or_expired_tokens_are_refused_with_stable_keys_and_nothing_is_attached()
    {
        foreach (var bad in new[] { "", "   ", "has space", "tab\there", "line\nbreak", new string('x', AccountLimits.MaxTokenLength + 1) })
        {
            var e = await Fails(() => AttachAsync(_owner, bad));
            Assert.Equal((ErrorCodes.InvalidRequest, AccountErrorKeys.TokenInvalid), (e.Code, KeyOf(e)));
            Assert.DoesNotContain("has space", e.Message); // 消息里没有令牌
        }

        var expired = await Fails(() => AttachAsync(_owner, expiresAt: InMinutes(-1)));
        Assert.Equal((ErrorCodes.InvalidRequest, AccountErrorKeys.TokenExpired), (expired.Code, KeyOf(expired)));
        var noAccount = await Fails(() => AttachAsync(_owner, accountId: " "));
        Assert.Equal(AccountErrorKeys.TokenInvalid, KeyOf(noAccount));
        var emptyTeam = await Fails(() => AttachAsync(_owner, teamId: ""));
        Assert.Equal(AccountErrorKeys.TokenInvalid, KeyOf(emptyTeam));

        Assert.False((await _owner.CallAsync(AccountMethods.GetStatus, Unit.Value)).Attached);
    }

    [Fact]
    public async Task A_paired_remote_controller_cannot_use_the_account_methods_even_as_owner()
    {
        var identity = PeerIdentity.Create("remote-owner");
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, "remote-owner");
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, "owner"));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        await using var remote = await DeviceClient.ConnectAsync(stream, new ClientHello("remote-owner", "3.0.0"));
        Assert.Equal("owner", remote.Welcome.Role);

        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => AttachAsync(remote))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => remote.CallAsync(AccountMethods.GetStatus, Unit.Value))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => remote.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: "x"))).Code);
        Assert.False((await _owner.CallAsync(AccountMethods.GetStatus, Unit.Value)).Attached);
        Assert.Contains(Capabilities.Account, _owner.Welcome.Features); // 本机通道声明了这个能力
    }

    [Fact]
    public async Task The_delegated_account_expires_by_the_clock_and_never_reports_an_expired_token_as_present()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
        var account = new DelegatedAccount(clock);
        Assert.Null(account.AccessToken);
        account.Attach(Token, clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(), "u1", null);
        Assert.Equal(Token, account.AccessToken);
        Assert.True(account.Status().Attached);

        var changes = 0;
        account.Changed += () => changes++;
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(account.AccessToken);
        Assert.Equal(new AccountStatusDto(false, null, null, null), account.Status());
        account.Attach("OTHER", clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds(), "u2", "t9");
        Assert.Equal("OTHER", account.AccessToken);
        account.Detach();
        account.Detach(); // 第二次什么都没有,不触发变化
        Assert.Equal(2, changes);
        Assert.Null(account.AccessToken);
    }

    // ───────────────────────── 市场:令牌到位后恢复 ─────────────────────────

    [Fact]
    public async Task Plugin_market_install_says_auth_required_until_the_token_arrives_then_works_and_stops_after_detach()
    {
        // 浏览照常,安装需要登录
        Assert.Single((await _owner.CallAsync(PluginsMethods.MarketList, new PluginsMarketListParams(null, null, 1))).Items);
        var before = await Fails(() => _owner.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.Unauthorized, PluginErrorKeys.AuthRequired), (before.Code, KeyOf(before)));
        Assert.Empty(_market.GrantAuthorizations); // 没有令牌:连授权请求都没发

        await AttachAsync(_owner);
        var installed = await _owner.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal("acme.hello", installed.Installed.Id);
        Assert.Equal([$"Bearer {Token}"], _market.GrantAuthorizations); // 市场收到的是委托令牌,不用重启

        await _owner.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams("acme.hello"), idem: Guid.NewGuid().ToString("N"));
        await _owner.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"));
        var after = await Fails(() => _owner.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal(PluginErrorKeys.AuthRequired, KeyOf(after)); // 撤销之后又是 authRequired
        Assert.Single(_market.GrantAuthorizations);
    }

    [Fact]
    public async Task Skill_market_install_follows_the_same_token_lifecycle()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var before = await Fails(() => _owner.CallAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams("pdf-tools", null, "global"), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.Unauthorized, "skills.authRequired"), (before.Code, KeyOf(before)));

        await AttachAsync(_owner);
        var installed = await _owner.CallAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams("pdf-tools", null, "global"), idem: Guid.NewGuid().ToString("N"));
        Assert.Contains(installed.Skills, s => s.Name == "pdf-tools");
        Assert.Equal([$"Bearer {Token}"], _market.GrantAuthorizations);
        _ = project;
    }

    [Fact]
    public async Task A_token_the_market_rejects_is_still_auth_required_not_a_crash()
    {
        await AttachAsync(_owner, token: "REVOKED-ON-SERVER-TOKEN");
        var e = await Fails(() => _owner.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.Unauthorized, PluginErrorKeys.AuthRequired), (e.Code, KeyOf(e)));
    }

    // ───────────────────────── 令牌不上线 ─────────────────────────

    [Fact]
    public async Task The_token_never_appears_in_any_response_event_log_or_file_on_the_device()
    {
        var (tapped, tap) = await _rig.ConnectTappedAsync("recorded-owner");
        var plugins = await Watch<PluginsTopicState>.OpenAsync(tapped, PluginsMethods.Topic);
        var providers = await Watch<ProvidersTopicState>.OpenAsync(tapped, ProvidersMethods.Topic);
        var device = await Watch<DeviceTopicState>.OpenAsync(tapped, DeviceMethods.DeviceTopic);
        var interactions = await Watch<InteractionsDto>.OpenAsync(tapped, ConversationMethods.InteractionsTopic);
        var settings = await Watch<DeviceSettingsState>.OpenAsync(tapped, SettingsMethods.Topic);

        await AttachAsync(tapped);
        await tapped.CallAsync(AccountMethods.GetStatus, Unit.Value);
        await tapped.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N"));
        await tapped.CallAsync(PluginsMethods.List, Unit.Value);
        await tapped.CallAsync(PluginsMethods.Activity, new PluginsActivityParams(null));
        await tapped.CallAsync(ProvidersMethods.List, Unit.Value);
        await tapped.CallAsync(SettingsMethods.Get, Unit.Value);
        // 失败路径也不能带出令牌:撤销之后再装一次(authRequired),以及一条不合法的交付
        await tapped.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"));
        await Fails(() => tapped.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N")));
        await Fails(() => AttachAsync(tapped, token: Token + " with space"));
        await Task.Delay(150);

        var wire = Encoding.Latin1.GetString(tap.Transcript);
        Assert.DoesNotContain("DELEGATED-TOKEN", wire);
        Assert.DoesNotContain("7f3a9c1e5b", wire);
        Assert.DoesNotContain("DELEGATED-TOKEN", string.Join('\n', _log.Lines));

        // 重新交付一次,再扫数据根下的每个文件(数据库、WAL、providers.json、日志……):令牌只在内存里
        await AttachAsync(tapped);
        await tapped.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("hello", null), idem: Guid.NewGuid().ToString("N"));
        var needle = Encoding.UTF8.GetBytes("7f3a9c1e5b");
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_rig.DataRoot, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains(".sock", StringComparison.Ordinal))
            {
                continue; // 本机通道的 socket 文件不是数据文件
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.False(memory.ToArray().AsSpan().IndexOf(needle) >= 0, $"the token was written to {Path.GetRelativePath(_rig.DataRoot, file)}");
            scanned++;
        }

        Assert.True(scanned > 0);
        Assert.Contains("DELEGATED-TOKEN", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(Token))); // 对照:扫描的字符串确实是令牌的一部分
        _ = plugins; _ = providers; _ = device; _ = interactions; _ = settings;
    }
}
