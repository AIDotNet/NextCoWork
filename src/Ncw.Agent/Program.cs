using Ncw.Agent.Host;
using Ncw.Protocol.Transport;

// ncw-agent --mode local --data-root <dir> [--endpoint <path|pipe>] [--parent-pid <pid>]
// 本机令牌从环境变量 NCW_LOCAL_TOKEN 读取(不放在命令行上,避免出现在进程列表里)。
var options = Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: ncw-agent --mode local|cloud --data-root <dir> [--endpoint <path>] [--parent-pid <pid>]  (NCW_LOCAL_TOKEN in env)");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var host = new AgentHost(options);
// 桌面端等这一行出现再连接(也可以直接重试连接)。
Console.Out.WriteLine($"ncw-agent ready {host.DeviceId} {options.Endpoint}");
Console.Out.Flush();
await host.RunAsync(cts.Token);
return 0;

static AgentOptions? Parse(string[] args)
{
    string? mode = null, root = null, endpoint = null, parent = null;
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--mode": mode = args[i + 1]; break;
            case "--data-root": root = args[i + 1]; break;
            case "--endpoint": endpoint = args[i + 1]; break;
            case "--parent-pid": parent = args[i + 1]; break;
            default: return null;
        }
    }

    var token = Environment.GetEnvironmentVariable("NCW_LOCAL_TOKEN");
    if (root is null || string.IsNullOrEmpty(token) || mode is not ("local" or "cloud"))
    {
        return null;
    }

    root = Path.GetFullPath(root);
    return new AgentOptions(
        mode == "cloud" ? AgentMode.Cloud : AgentMode.Local,
        root,
        endpoint ?? LocalTransport.DefaultEndpoint(root),
        token,
        int.TryParse(parent, out var pid) ? pid : null);
}
