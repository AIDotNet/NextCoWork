using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Ncw.Agent.Cloud.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Relay;

namespace Ncw.Agent.Cloud.Tests;

/// <summary>
/// 真实的 <c>ncw-agent --mode cloud</c> 进程的冒烟测试(进程内的假编排服务走真实 HTTP、进程内中继、开发用 KMS):启动失败的结构化退出码、
/// 启动 → 注册身份 → 信任下发 → 控制端经中继连接 → 休眠(退出码 0、通知编排服务)→ 唤醒 → SIGTERM 优雅退出。
/// 默认起 <c>dotnet ncw-agent.dll</c>(Debug 构建);设置环境变量 <c>NCW_AGENT_BINARY</c> 指向 NativeAOT 发布出来的可执行文件就用它(发布验证用)——这些冒烟用例要用开发 KMS 提供者,所以必须是 `publish-agent --dev-kms` 发布的、不可分发的冒烟二进制;正常的发布产物里没有它,会以 cloud.kms.unavailable 退出。
/// </summary>
public sealed class ProcessSmokeTests : IAsyncLifetime
{
    private const string Token = "smoke-account-token-SECRET";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncws-{Guid.NewGuid():N}"[..14]);
    private readonly Issuer _issuer = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly byte[] _kmsKey = RandomNumberGenerator.GetBytes(32);
    private readonly List<Process> _processes = [];
    private RelayServer _relay = null!;
    private FakeOrchestrator _orchestrator = null!;
    private HttpOrchestratorServer _http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _relay = await RelayServer.StartAsync(new RelayOptions
        {
            Urls = ["http://127.0.0.1:0"],
            IdleTimeout = TimeSpan.FromMinutes(2),
            MaxBytesPerSecondPerSession = 0,
            ConfigureLogging = logging => logging.ClearProviders(),
        });
        _orchestrator = new FakeOrchestrator(_issuer, RelayAddress.ToText(_relay.WebSocketUrl), _clock);
        _http = new HttpOrchestratorServer(_orchestrator, Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        _http.Dispose();
        await _relay.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Windows 上刚退出的 ncw-agent 可能还没放开 nextcowork.db;临时目录清不掉不影响测试结论
            }
        }
    }

    private sealed class Running(Process process, List<string> stdout, List<string> stderr)
    {
        public Process Process => process;

        public string Out => string.Join("\n", Snapshot(stdout));

        public string Err => string.Join("\n", Snapshot(stderr));

        private static string[] Snapshot(List<string> lines)
        {
            lock (lines)
            {
                return [.. lines];
            }
        }

        public async Task<string> WaitForOutAsync(string prefix, int timeoutMs = 60_000)
        {
            string? line = null;
            await CloudRig.WaitUntil(() => (line = Snapshot(stdout).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))) is not null, $"stdout line '{prefix}' (stdout: {Out} | stderr: {Err})", timeoutMs);
            return line!;
        }

        public async Task<int> WaitForExitAsync(int timeoutMs = 60_000)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return process.ExitCode;
        }
    }

    private string DataRoot => Path.Combine(_root, "data");

    private string WriteConfig(Action<JsonObject>? edit = null)
    {
        var doc = new JsonObject
        {
            ["dataRoot"] = DataRoot,
            ["workspacesRoot"] = Path.Combine(_root, "ws"),
            ["orchestrationUrl"] = _http.BaseUrl.ToString(),
            ["relayUrl"] = RelayAddress.ToText(_relay.WebSocketUrl),
            ["orchestratorKeys"] = new JsonArray(new JsonObject { ["kid"] = _issuer.Kid, ["key"] = _issuer.PublicKey }),
            ["kms"] = new JsonObject { ["provider"] = "dev-env", ["context"] = "acct-1/smoke" },
            ["accountId"] = "acct-1",
            ["usageReportSeconds"] = 5,
        };
        edit?.Invoke(doc);
        var path = Path.Combine(_root, "cloud.json");
        File.WriteAllText(path, doc.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static (string File, string[] Args) Command()
    {
        if (Environment.GetEnvironmentVariable("NCW_AGENT_BINARY") is { Length: > 0 } binary)
        {
            return (binary, ["--mode", "cloud"]);
        }

        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Ncw.Agent/bin/Debug/net11.0/ncw-agent.dll"));
        Assert.True(File.Exists(dll), $"build src/Ncw.Agent first ({dll})");
        return ("dotnet", [dll, "--mode", "cloud"]);
    }

    private Running Start(string? configPath, Action<Dictionary<string, string?>>? env = null, string[]? extraArgs = null)
    {
        var (file, args) = Command();
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args.Concat(extraArgs ?? []))
        {
            info.ArgumentList.Add(arg);
        }

        var variables = new Dictionary<string, string?>
        {
            ["NCW_CLOUD_CONFIG"] = configPath,
            ["NCW_ACCOUNT_TOKEN"] = Token,
            ["NCW_CLOUD_ALLOW_DEV_KMS"] = "1",
            ["NCW_DEV_KMS_KEY"] = Convert.ToBase64String(_kmsKey),
            ["DOTNET_gcServer"] = "0",
        };
        env?.Invoke(variables);
        foreach (var (name, value) in variables)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = info };
        List<string> stdout = [], stderr = [];
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.Add(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.Add(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        return new Running(process, stdout, stderr);
    }

    private static void Term(Process process)
    {
        using var kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!;
        kill.WaitForExit();
    }

    // ---- 启动失败 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_or_bad_configuration_exits_with_a_structured_line_and_the_config_exit_code()
    {
        var none = Start(null);
        Assert.Equal(CloudExitCodes.Config, await none.WaitForExitAsync());
        Assert.Contains("ncw-agent startup-failed cloud.config.missing NCW_CLOUD_CONFIG", none.Err);

        var noToken = Start(WriteConfig(), env => env["NCW_ACCOUNT_TOKEN"] = null);
        Assert.Equal(CloudExitCodes.Config, await noToken.WaitForExitAsync());
        Assert.Contains("cloud.secret.missing", noToken.Err);

        var unknownField = Start(WriteConfig(doc => doc["dataRooot"] = "/typo"));
        Assert.Equal(CloudExitCodes.Config, await unknownField.WaitForExitAsync());
        Assert.Contains("cloud.config.invalid", unknownField.Err);
        Assert.DoesNotContain("/typo", unknownField.Err); // 值不会被打印出来

        var badArgs = Start(WriteConfig(), extraArgs: ["--data-root", Path.Combine(_root, "other-root")]);
        Assert.Equal(CloudExitCodes.Config, await badArgs.WaitForExitAsync()); // --data-root 和启动配置不一致

        Assert.All(new[] { none, noToken, unknownField, badArgs }, r => Assert.DoesNotContain(Token, r.Out + r.Err));
    }

    [Fact]
    public async Task Without_the_development_kms_opt_in_the_agent_refuses_to_start_instead_of_falling_back_to_a_key_file()
    {
        var run = Start(WriteConfig(), env => env["NCW_CLOUD_ALLOW_DEV_KMS"] = null);

        Assert.Equal(CloudExitCodes.Dependency, await run.WaitForExitAsync());
        Assert.Contains("ncw-agent startup-failed cloud.kms.unavailable kms.provider", run.Err);
        Assert.False(File.Exists(Path.Combine(DataRoot, "credential.key"))); // 绝不退回到明文密钥文件
    }

    [Fact]
    public async Task A_second_agent_on_the_same_data_root_exits_with_the_already_running_code()
    {
        var first = Start(WriteConfig());
        await first.WaitForOutAsync("ncw-agent ready ");

        var second = Start(WriteConfig());

        Assert.Equal(CloudExitCodes.AlreadyRunning, await second.WaitForExitAsync());
        Assert.Contains("already-running", second.Err);
    }

    // ---- 全流程 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_real_process_starts_registers_serves_a_controller_through_the_relay_sleeps_with_exit_0_wakes_and_stops_on_sigterm()
    {
        var phone = PeerIdentity.Create("phone");
        var config = WriteConfig();

        // 启动:打印 ready、向编排服务注册身份(真实 HTTP)、向中继注册
        var coldStart = Stopwatch.StartNew();
        var first = Start(config);
        var ready = await first.WaitForOutAsync("ncw-agent ready ");
        var firstReadyAfter = coldStart.Elapsed;
        // 02 A6 的冷启动目标(Agent 自己的部分)。量的是进程到 ready 为止**用掉的 CPU 时间**,不是墙钟:墙钟包含了「这台机器当时有多忙」
        // (整套并行 / 内存吃紧换页时同一个进程冷启动实测过 10.6 秒墙钟,CPU 时间不变),那不是 Agent 的冷启动成本,也不是它能保证的东西。
        var cpuAtReady = first.Process.TotalProcessorTime;
        Assert.True(cpuAtReady < TimeSpan.FromSeconds(10), $"cold start to ready used {cpuAtReady} of CPU (wall clock {firstReadyAfter})");
        var deviceId = ready.Split(' ')[2];
        Assert.EndsWith(" cloud", ready);
        Assert.Equal(deviceId, _orchestrator.Hellos[0].DeviceId);
        await CloudRig.WaitUntil(() => _relay.IsOnline(deviceId), "device online at the relay");

        // 控制端把公钥注册到账户 API → 信任记录下发 → 经中继连接
        var (routeToken, seq) = _orchestrator.Grant(phone);
        await CloudRig.WaitUntil(() => _orchestrator.AckFor(seq) is { Status: AckStatuses.Applied }, "trust applied");
        await CloudRig.WaitUntil(() => _relay.TokenCount(deviceId) >= 1, "the relay to know the granted route token"); // 回执不等于中继已处理令牌(tokens 消息没有回执)
        var fingerprint = Convert.FromHexString(_orchestrator.Fingerprint!);
        var connection = await RemoteDevices.ConnectAsync(deviceId, new DeviceAddresses([], new RelayRoute(_relay.WebSocketUrl, routeToken)), phone, fingerprint);
        await using var client = await DeviceClient.ConnectAsync(connection.Stream, new ClientHello("phone", "3.0.0", FormFactor: "phone"));
        Assert.Equal("owner", client.Welcome.Role);
        Assert.Equal("cloud", (await client.CallAsync(DeviceMethods.Info, Unit.Value)).Kind);
        var status = await client.CallAsync(CloudMethods.GetStatus, Unit.Value);
        Assert.Equal("acct-1", status.AccountId);

        // 休眠:编排服务请求立即休眠 → 进程以 0 退出,先通知编排服务、设备已从中继注销
        _orchestrator.RequestSleep(SleepModes.Now, "smoke");
        Assert.Equal(0, await first.WaitForExitAsync());
        Assert.Contains("ncw-agent exit sleep", first.Out);
        Assert.Contains(_orchestrator.Notices, n => n.State == LifecycleStates.Sleeping && n.Reason == "smoke");
        Assert.False(_relay.IsOnline(deviceId));

        // 唤醒:同一数据根、同一(开发)KMS;控制端用旧凭据直接连上
        var second = Start(config);
        Assert.Equal(deviceId, (await second.WaitForOutAsync("ncw-agent ready ")).Split(' ')[2]);
        Assert.True(_orchestrator.Hellos[^1].Resumed);
        await CloudRig.WaitUntil(() => _relay.IsOnline(deviceId), "device back online");
        var woken = await RemoteDevices.ConnectAsync(deviceId, new DeviceAddresses([], new RelayRoute(_relay.WebSocketUrl, routeToken)), phone, fingerprint);
        await using var wokenClient = await DeviceClient.ConnectAsync(woken.Stream, new ClientHello("phone", "3.0.0", FormFactor: "phone"));
        Assert.True((await wokenClient.CallAsync(CloudMethods.GetStatus, Unit.Value)).ResumedFromSleep);

        // 磁盘上没有明文主密钥、令牌不在数据根里
        Assert.False(File.Exists(Path.Combine(DataRoot, "credential.key")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "credential.key.kms")));
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("cloud.json", StringComparison.Ordinal) && !f.EndsWith("agent.lock", StringComparison.Ordinal)))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            Assert.DoesNotContain(Token, await reader.ReadToEndAsync());
        }

        // SIGTERM:优雅退出(退出码 0)
        if (!OperatingSystem.IsWindows())
        {
            Term(second.Process);
            Assert.Equal(0, await second.WaitForExitAsync());
        }

        Assert.DoesNotContain(Token, first.Out + first.Err + second.Out + second.Err);
    }
}
