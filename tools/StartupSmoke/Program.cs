using StartupSmoke;

// 用法见 StartupSmoke.csproj 的说明。
//   dotnet run --project tools/StartupSmoke -- <timeline|faults|takeover|window|all> [--agent <ncw-agent>] [--runs N] [--projects N] [--legacy-db <2.x 库的只读副本>] [--out <目录>]
// 所有数据(数据根、客户端目录、profile 根)都放在临时目录,不碰 ~/.next-cowork。
var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "timeline";

string? Option(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();

var work = Option("work") ?? Path.Combine(Path.GetTempPath(), "ncw-startup-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
Environment.SetEnvironmentVariable("NCW_CLIENT_DIR", Path.Combine(work, "client"));
Environment.SetEnvironmentVariable("NCW_PROFILE_ROOT", Path.Combine(work, "profile"));
Environment.SetEnvironmentVariable("NCW_AGENT_DATA_ROOT", Path.Combine(work, "window-data"));
Environment.SetEnvironmentVariable("NCW_ACCOUNT_ORIGIN", "http://127.0.0.1:1");   // 账户后端不存在:连不上也不影响本地启动

var agent = Option("agent") ?? Environment.GetEnvironmentVariable("NCW_AGENT_PATH");
if (agent is null || !File.Exists(agent))
{
    Console.WriteLine("SMOKE harness FAIL need a runnable ncw-agent: --agent <path> or NCW_AGENT_PATH (an AOT publish output, not the bin/<cfg>/<rid>/native intermediate)");
    return 1;
}

Console.WriteLine($"SMOKE note work dir {work}");
var failures = 0;
if (mode == "window")
{
    return WindowMode.Run(agent, work, Option("out") ?? Path.Combine(work, "shots"), args);
}

var scenarios = new Scenarios(agent, work);
try
{
    if (mode is "timeline" or "all")
    {
        await scenarios.TimelineAsync(int.TryParse(Option("runs"), out var runs) ? runs : 5, int.TryParse(Option("projects"), out var projects) ? projects : 3);
    }

    if (mode is "faults" or "all")
    {
        await scenarios.FaultsAsync(Option("only"));
    }

    if (mode is "takeover" or "all")
    {
        await scenarios.TakeoverAsync(Option("legacy-db"));
    }
}
catch (Exception e)
{
    Console.WriteLine($"SMOKE harness FAIL {e}");
    failures++;
}

failures += scenarios.Failures;
Console.WriteLine($"SMOKE done failures={failures}");
return failures;
