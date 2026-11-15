using System.Text.Json.Nodes;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// 密钥与测试连接。移植自 2.x 的 <c>mcp-methods.test.ts</c>(真的 stdio 服务器:没有 FIX_TOKEN 就启动失败,所以「已连接」本身证明密钥被送进了子进程环境;
/// 值既不在任何响应里也不在落盘的数据里;整张 map 替换并丢弃未声明的键;测试 / 停用 / 删除连同密钥)。
/// 2.x 那份用桌面的 IPC + 真 Node 子进程;这里在服务层用假进程(协议层同一个)。端到端(真实 Agent + DeviceClient + 线上字节扫描)在 Ncw.Agent.Host.Tests。
/// </summary>
public sealed class SecretsTests
{
    private const string Token = "tok-SECRET-123456";

    /// <summary>没有 FIX_TOKEN 就启动失败的服务器(退出码 3,stderr 回显路径和 token)。</summary>
    private static McpRig TokenRig()
    {
        var rig = new McpRig();
        rig.Local.ProcessFactory = spec =>
        {
            if (spec.Environment?.ContainsKey("FIX_TOKEN") != true)
            {
                var dead = new FakeProcess(rig.Local.Id, null);
                _ = dead.WriteStderrAsync("missing token at /secret/path\n");
                dead.ExitNow(3);
                return dead;
            }

            return new FakeProcess(rig.Local.Id, ServiceTests.ThreeTools());
        };
        return rig;
    }

    private static McpServerConfig Fix() => McpRig.Stdio("fix") with { Name = "Fixture", EnvNames = ["FIX_TOKEN"] };

    [Fact]
    public async Task The_server_connects_only_once_its_secret_is_supplied_and_the_value_never_comes_back()
    {
        await using var rig = TokenRig();

        var first = await rig.Service.UpsertAsync(Fix());
        Assert.Equal(McpState.Failed, first.State);
        Assert.Equal(McpErrorKeys.ProcessExited, first.ErrorKey);
        Assert.DoesNotContain("/secret/path", Everything(rig)); // 错误原文(含路径)不出状态

        var info = await rig.Service.SetSecretsAsync("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = Token });
        Assert.Equal([new McpSecretState("FIX_TOKEN", true)], info.Entries);
        await rig.WaitForAsync("fix", McpState.Connected);

        Assert.Equal(Token, rig.Local.Spawned[^1].Environment!["FIX_TOKEN"]); // 密钥确实送进了子进程的环境
        Assert.DoesNotContain(Token, Everything(rig));
        Assert.DoesNotContain(Token, rig.Configs.Dump());
        Assert.DoesNotContain(Token, McpRig.TextOf(await McpRig.CallAsync(rig.Tool("mcp__fix__add"), """{"a":1,"b":1}""")));
        // 凭据库里是 NCK1 之前的明文 JSON(内存替身);重要的是它只在密钥库里
        Assert.Contains(Token, rig.Secrets.Get("mcp:fix:env"));
    }

    /// <summary>状态、配置、日志、秘密信息里能出现的一切文字(断言值不在其中)。</summary>
    private static string Everything(McpRig rig) =>
        string.Join("\n", rig.Service.Snapshot().Servers.Select(s => $"{s.ErrorKey} {string.Concat(s.ErrorParams.Values)} {string.Join(',', s.Tools)} {McpConfigJson.Write(s.Config)}"))
        + "\n" + rig.Log.All + "\n" + rig.Configs.Dump()
        + "\n" + string.Join("\n", rig.Service.Configs().SelectMany(c => rig.Service.SecretsInfo(c.Id).Entries.Select(e => $"{e.Name}={e.IsSet}")));

    [Fact]
    public async Task Secrets_info_has_names_and_whether_they_are_set_never_values()
    {
        await using var rig = TokenRig();
        await rig.Service.UpsertAsync(Fix() with { EnvNames = ["FIX_TOKEN", "OTHER"] });

        Assert.All(rig.Service.SecretsInfo("fix").Entries, e => Assert.False(e.IsSet));
        await rig.Service.SetSecretsAsync("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = Token });

        var info = rig.Service.SecretsInfo("fix");
        Assert.Equal("env", info.Kind);
        Assert.Equal([new McpSecretState("FIX_TOKEN", true), new McpSecretState("OTHER", false)], info.Entries);
    }

    [Fact]
    public async Task Writing_secrets_replaces_the_whole_map_and_drops_names_the_config_never_declared()
    {
        await using var rig = TokenRig();
        await rig.Service.UpsertAsync(Fix());

        var written = await rig.Service.SetSecretsAsync("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = Token, ["UNDECLARED"] = "x" });
        Assert.Equal(["FIX_TOKEN"], written.Entries.Where(e => e.IsSet).Select(e => e.Name));
        Assert.DoesNotContain("UNDECLARED", rig.Secrets.Get("mcp:fix:env"));

        // 整张 map 替换:不带 FIX_TOKEN 的写入把它删掉(而不是累加保留);删掉之后服务器又起不来了
        var cleared = await rig.Service.SetSecretsAsync("fix", new Dictionary<string, string> { ["UNDECLARED"] = "y" });
        Assert.All(cleared.Entries, e => Assert.False(e.IsSet));
        await rig.WaitForAsync("fix", McpState.Failed);
    }

    [Fact]
    public async Task Writing_secrets_for_an_unknown_server_is_not_found()
    {
        await using var rig = TokenRig();

        var e = await Assert.ThrowsAsync<McpException>(() => rig.Service.SetSecretsAsync("nope", new Dictionary<string, string>()));

        Assert.Equal(McpErrorKeys.NotFound, e.Key);
        Assert.Throws<McpException>(() => rig.Service.SecretsInfo("nope"));
    }

    [Fact]
    public async Task Secrets_written_for_a_disabled_server_do_not_start_it()
    {
        await using var rig = TokenRig();
        await rig.Service.UpsertAsync(Fix() with { Enabled = false });

        await rig.Service.SetSecretsAsync("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = Token });

        Assert.Empty(rig.Local.Spawned);
        Assert.Equal(McpState.Disabled, rig.Service.StatusOf("fix")!.State);
    }

    [Fact]
    public async Task A_credential_store_that_cannot_be_read_fails_the_connection_without_leaking_anything()
    {
        var rig = new McpRig();
        await using var _ = rig;
        rig.Local.ProcessFactory = _ => new FakeProcess(rig.Local.Id, ServiceTests.ThreeTools());
        var broken = new McpRigBrokenSecrets();
        var service = new McpService(new McpServiceDeps
        {
            Configs = rig.Configs,
            Secrets = broken,
            Environments = new TestEnvironments(rig.Local),
            Tools = rig.Tools,
            Delayer = rig.Delayer,
            Log = rig.Log,
            Options = rig.Options,
        });
        await using var __ = service;
        rig.Configs.Put(Fix());

        var status = await service.ReconnectAsync("fix");

        Assert.Equal(McpState.Failed, status.State);
        Assert.Equal(McpErrorKeys.Failed, status.ErrorKey);
    }

    private sealed class McpRigBrokenSecrets : IMcpSecretStore
    {
        public string? Get(string reference) => throw new System.Security.Cryptography.CryptographicException("master key is gone");

        public void Set(string reference, string value) => throw new System.Security.Cryptography.CryptographicException("master key is gone");

        public bool Remove(string reference) => false;
    }

    // ───────────────────────── 脱敏 ─────────────────────────

    [Fact]
    public async Task A_server_that_echoes_the_secret_in_its_error_or_its_output_has_it_masked_before_it_reaches_us()
    {
        var rig = new McpRig();
        await using var _ = rig;
        rig.Secrets.Set("mcp:fix:env", $$"""{"FIX_TOKEN":"{{Token}}"}""");
        var server = new FakeMcpServer();
        server.Tools.Add(new FakeTool("leaky", (_, _) => Task.FromResult<JsonNode?>(FakeTool.Text($"auth failed for {Token}", isError: true))));
        server.Tools.Add(new FakeTool("leaky_ok", (_, _) => Task.FromResult<JsonNode?>(FakeTool.Text($"echo {Token}"))));
        rig.ServeStdio(() => server);
        await rig.Service.UpsertAsync(Fix());

        var failed = McpRig.TextOf(await McpRig.CallAsync(rig.Tool("mcp__fix__leaky")));
        var ok = McpRig.TextOf(await McpRig.CallAsync(rig.Tool("mcp__fix__leaky_ok")));

        Assert.Equal("auth failed for ***", failed);
        Assert.Equal("echo ***", ok);
    }

    // ───────────────────────── 测试连接 ─────────────────────────

    [Fact]
    public async Task Test_connection_connects_once_lists_the_tools_and_leaves_nothing_behind()
    {
        await using var rig = new McpRig();
        rig.ServeStdio(ServiceTests.ThreeTools);
        rig.Configs.Put(McpRig.Stdio() with { Enabled = false });
        await rig.Service.StartAsync();

        var result = await rig.Service.TestConnectionAsync("demo");

        Assert.True(result.Ok);
        Assert.Equal(["add", "unknown_risk", "always_fails"], result.Tools);
        Assert.Equal("fake-server", result.ServerName);
        Assert.Equal("1.2.3", result.ServerVersion);
        Assert.Equal("2025-06-18", result.ProtocolVersion);
        Assert.Empty(rig.Tools.List());                                        // 不碰工具注册表
        Assert.Equal(McpState.Disabled, rig.Service.StatusOf("demo")!.State);  // 不改状态
        await ServiceTests.Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the temporary connection to be closed");
    }

    [Fact]
    public async Task Test_connection_reports_failure_as_a_result_with_a_key_not_as_an_exception()
    {
        await using var rig = new McpRig();
        rig.Local.StartFailure = new Ncw.Agent.Environments.EnvironmentException(Ncw.Agent.Environments.EnvironmentErrorCode.ProcessStartFailed, "ENOENT");
        rig.Configs.Put(McpRig.Stdio());

        var result = await rig.Service.TestConnectionAsync("demo");

        Assert.False(result.Ok);
        Assert.Equal(McpErrorKeys.CommandNotFound, result.ErrorKey);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task Test_connection_can_try_an_unsaved_draft_with_the_secrets_stored_for_the_same_id()
    {
        await using var rig = TokenRig();
        rig.Secrets.Set("mcp:fix:env", $$"""{"FIX_TOKEN":"{{Token}}"}""");

        var result = await rig.Service.TestConnectionAsync("fix", Fix());

        Assert.True(result.Ok);
        Assert.Empty(rig.Configs.List()); // 草稿没有被保存
    }

    [Fact]
    public async Task Test_connection_rejects_an_invalid_draft_and_an_unknown_id()
    {
        await using var rig = new McpRig();

        var bad = await Assert.ThrowsAsync<McpException>(() => rig.Service.TestConnectionAsync("x", McpRig.Stdio("../x")));
        var missing = await Assert.ThrowsAsync<McpException>(() => rig.Service.TestConnectionAsync("nope"));

        Assert.Equal(McpErrorKeys.InvalidId, bad.Key);
        Assert.Equal(McpErrorKeys.NotFound, missing.Key);
    }

    [Fact]
    public async Task Test_connection_can_be_cancelled_by_the_caller()
    {
        await using var rig = new McpRig();
        rig.Local.ProcessFactory = _ => new FakeProcess(rig.Local.Id, null); // 永远不回话
        rig.Configs.Put(McpRig.Stdio());
        using var stop = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.TestConnectionAsync("demo", ct: stop.Token));

        await ServiceTests.Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the half-open process to be released");
    }
}
