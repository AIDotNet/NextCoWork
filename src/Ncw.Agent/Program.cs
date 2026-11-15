using Ncw.Agent;
using Ncw.Agent.Cloud;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Cloud;
using Ncw.Protocol.Transport;

// ncw-agent --mode local --data-root <dir> [--endpoint <path|pipe>] [--parent-pid <pid>] [--lifeline stdin]
// 这些参数只给桌面端和云端编排调用,不是面向用户的命令行(AGENTS.md §1)。
// 本机令牌从环境变量 NCW_LOCAL_TOKEN 读取(不放在命令行上,避免出现在进程列表里)。
//   --parent-pid   父进程退出后自己退出(桌面端崩溃时不留下孤儿)。
//   --lifeline stdin   标准输入关闭(EOF)就优雅退出:桌面端用「关掉 stdin」做跨平台的优雅关闭,Windows 上也一样可用。
// 云端模式:ncw-agent --mode cloud。没有本机通道,也没有 NCW_LOCAL_TOKEN;启动配置由编排服务注入(环境变量 NCW_CLOUD_CONFIG 指向的配置文件 +
//   NCW_ACCOUNT_TOKEN / NCW_ACCOUNT_TOKEN_FILE 里的账户身份令牌),全部不在命令行上、不进日志。远程连接只经中继;休眠 / 升级时优雅退出(退出码 0)。详见 docs/design/02 A6。
// 退出码:0 正常结束(含云端休眠 / 升级);2 参数错误;65 身份 / 主密钥不符(云端:挂错了卷;本机:数据根里有加密数据却没有可用的 credential.key,绝不生成新密钥);69 依赖不可用(KMS、数据根);75 同一数据根已有另一个 Agent(单实例锁);78 启动配置不合法。
// 信号:SIGINT / SIGTERM 优雅关闭;SIGHUP 忽略(「关窗后继续运行」时,终端关闭不能带走它)。
if (IsCloudMode(args))
{
    return await RunCloudAsync(args);
}

var options = Parse(args, out var lifelineStdin);
if (options is null)
{
    Console.Error.WriteLine("usage: ncw-agent --mode local|cloud --data-root <dir> [--endpoint <path>] [--parent-pid <pid>] [--lifeline stdin]  (NCW_LOCAL_TOKEN in env)");
    return 2;
}

// 单实例:先拿锁,再创建 AgentHost(它会创建身份文件、监听端点)。
using var instanceLock = AgentInstanceLock.TryAcquire(options.DataRoot);
if (instanceLock is null)
{
    Console.Error.WriteLine($"ncw-agent already-running {options.DataRoot}");
    return AgentInstanceLock.AlreadyRunningExitCode;
}

var cts = new CancellationTokenSource(); // 不 using:进程退出回调可能在 Main 返回之后才触发
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cts.Cancel();
});
using var sighup = OperatingSystem.IsWindows()
    ? null
    : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, context => context.Cancel = true);
if (lifelineStdin)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await using var input = Console.OpenStandardInput();
            var buffer = new byte[256];
            while (await input.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (IOException)
        {
        }

        cts.Cancel();
    });
}

AgentHost host;
try
{
    host = new AgentHost(options);
}
catch (CloudStartupException e)
{
    // 本机模式的结构化启动失败(数据根里有加密数据,却没有可用的主密钥,见 LocalStartupCodes):一行,只有稳定 key 和字段名(密钥文件名),
    // 没有任何值、没有堆栈。绝不生成新密钥。桌面端的守护据此直接停在「失败」,不当崩溃去重启。只接这一种异常,其它未预期的异常保持原样。
    Console.Error.WriteLine($"ncw-agent startup-failed {e.Code} {e.Field ?? "-"}");
    Console.Error.Flush();
    return e.ExitCode;
}

// 只有端点真的可以连接之后才报告 ready;桌面端等这一行再连接。
await host.RunAsync(cts.Token, onListening: () =>
{
    Console.Out.WriteLine($"ncw-agent ready {host.DeviceId} {options.Endpoint}");
    Console.Out.Flush();
});
return 0;

static AgentOptions? Parse(string[] args, out bool lifelineStdin)
{
    lifelineStdin = false;
    string? mode = null, root = null, endpoint = null, parent = null, lifeline = null;
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--mode": mode = args[i + 1]; break;
            case "--data-root": root = args[i + 1]; break;
            case "--endpoint": endpoint = args[i + 1]; break;
            case "--parent-pid": parent = args[i + 1]; break;
            case "--lifeline": lifeline = args[i + 1]; break;
            default: return null;
        }
    }

    var token = Environment.GetEnvironmentVariable("NCW_LOCAL_TOKEN");
    if (root is null || string.IsNullOrEmpty(token) || mode is not ("local" or "cloud"))
    {
        return null;
    }

    if (lifeline is not null && lifeline != "stdin")
    {
        return null;
    }

    lifelineStdin = lifeline == "stdin";
    root = Path.GetFullPath(root);
    return new AgentOptions(
        mode == "cloud" ? AgentMode.Cloud : AgentMode.Local,
        root,
        endpoint ?? LocalTransport.DefaultEndpoint(root),
        token,
        int.TryParse(parent, out var pid) ? pid : null);
}

static bool IsCloudMode(string[] args)
{
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (args[i] == "--mode")
        {
            return args[i + 1] == "cloud";
        }
    }

    return false;
}

// 云端启动:配置 → 单实例锁 → KMS 主密钥 → 编排通道 → AgentHost(--mode cloud)。任何启动失败都是一行结构化输出(稳定 key + 字段名,没有任何值)和对应退出码。
static async Task<int> RunCloudAsync(string[] args)
{
    string? dataRootArg = null;
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--mode": break;
            case "--data-root": dataRootArg = args[i + 1]; break;
            default:
                Console.Error.WriteLine("usage: ncw-agent --mode cloud  (configuration comes from the environment: NCW_CLOUD_CONFIG, NCW_ACCOUNT_TOKEN)");
                return 2;
        }
    }

    var cts = new CancellationTokenSource(); // 不 using:进程退出回调可能在 Main 返回之后才触发(此时再 Cancel 一个已释放的 CTS 会让进程崩溃)
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cts.Cancel();
    });

    AgentHost? host = null;
    try
    {
        var config = CloudConfigLoader.LoadFromProcess();
        if (dataRootArg is not null && Path.GetFullPath(dataRootArg).TrimEnd(Path.DirectorySeparatorChar) != config.DataRoot)
        {
            throw new CloudStartupException(CloudStartupCodes.ConfigInvalid, "dataRoot", CloudExitCodes.Config, "--data-root disagrees with the startup configuration.");
        }

        var egress = HostAllowlistEgress.FromConfig(config.Egress)
            ?? throw new CloudStartupException(CloudStartupCodes.ConfigInvalid, "egress", CloudExitCodes.Config, "Unknown egress kind.");

        Directory.CreateDirectory(config.DataRoot);
        using var instanceLock = AgentInstanceLock.TryAcquire(config.DataRoot);
        if (instanceLock is null)
        {
            Console.Error.WriteLine($"ncw-agent already-running {config.DataRoot}");
            return AgentInstanceLock.AlreadyRunningExitCode;
        }

        var wrap = CloudBoot.CreateKeyWrap(config, Environment.GetEnvironmentVariable);
        var keySource = await CloudBoot.OpenKeySourceAsync(config, wrap, cts.Token);
        var token = new AccountTokenHolder(config.AccountToken);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = config.OrchestrationUrl,
            Timeout = TimeSpan.FromSeconds(60), // 长轮询(25 秒)之上留出余量
        };
        string deviceId = "";
        var channel = new HttpOrchestrationChannel(http, () => token.Current, () => deviceId);
        var cloud = new CloudHostOptions { Config = config, Channel = channel, Token = token, KeySource = keySource, Egress = egress };
        try
        {
            host = new AgentHost(new AgentOptions(AgentMode.Cloud, config.DataRoot, "", "", null), cloud: cloud);
        }
        catch
        {
            keySource.Dispose();
            throw;
        }

        deviceId = host.DeviceId;
        await host.RunAsync(cts.Token, onListening: () =>
        {
            Console.Out.WriteLine($"ncw-agent ready {host.DeviceId} cloud");
            Console.Out.Flush();
        });
        if (host.CloudExit is { } exit)
        {
            Console.Out.WriteLine($"ncw-agent exit {exit.Reason}");
            Console.Out.Flush();
            // 运行中发现身份绑定和设备不符(编排服务换了绑定):和启动时同一个退出码,重启也不会好
            return exit.Reason == "identity" ? CloudExitCodes.Identity : CloudExitCodes.Ok;
        }

        return CloudExitCodes.Ok;
    }
    catch (CloudStartupException e)
    {
        // 只有稳定 key 和字段名:值(可能是密钥)永远不打印
        Console.Error.WriteLine($"ncw-agent startup-failed {e.Code} {e.Field ?? "-"}");
        return e.ExitCode;
    }
    finally
    {
        host?.Dispose();
    }
}
