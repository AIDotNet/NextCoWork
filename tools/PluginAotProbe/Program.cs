using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Plugins;

// 在原生二进制里跑一遍插件系统:写一个插件目录 → 目录安装 → 授权 → 启用 → 命令 / 工具 / 异步 → 死循环被预算终止且不影响宿主。
var root = Path.Combine(Path.GetTempPath(), "ncw-aot-probe-" + Guid.NewGuid().ToString("N")[..8]);
var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok)
    {
        failures.Add(what);
    }
}

try
{
    var source = Path.Combine(root, "src", "acme.probe");
    Directory.CreateDirectory(Path.Combine(source, "dist"));
    File.WriteAllText(Path.Combine(source, "package.json"), """
        {
          "name": "probe", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./dist/extension.js",
          "permissions": ["storage"],
          "contributes": {
            "commands": [{ "command": "acme.probe.go", "title": "%go%" }, { "command": "acme.probe.spin", "title": "%spin%" }],
            "tools": [{ "name": "echo", "title": "%echo%" }]
          }
        }
        """);
    File.WriteAllText(Path.Combine(source, "dist", "extension.js"), """
        import * as ncw from 'nextcowork'
        const boot = await Promise.resolve('booted')
        export function activate(context) {
          context.subscriptions.push(
            ncw.commands.registerCommand('acme.probe.go', async () => {
              await new Promise((resolve) => setTimeout(resolve, 10))
              await ncw.storage.global.set('result', boot + ':' + JSON.stringify(await ncw.configuration.get()))
            }),
            ncw.commands.registerCommand('acme.probe.spin', async () => { while (true) {} }),
            ncw.tools.registerTool('echo', {
              description: 'echoes its input', inputSchema: { type: 'object', properties: { text: { type: 'string' } } }, readOnly: true,
              async invoke({ input }) { return { content: [{ text: 'echo:' + input.text }], card: { kind: 'declarative', blocks: [{ type: 'text', value: input.text }] } } }
            })
          )
        }
        """);

    var env = new LocalEnvironment("probe");
    var kv = new Kv();
    var registry = new ToolRegistry();
    await using var manager = new PluginManager(new PluginManagerOptions
    {
        Environment = env,
        Root = EnvPath.Posix(Path.Combine(root, "plugins")),
        Kv = kv,
        Tools = registry,
        Limits = new PluginRuntimeLimits { MaxInstructionsPerCall = 2_000_000, CommandTimeout = TimeSpan.FromSeconds(5), ToolTimeout = TimeSpan.FromSeconds(5) },
    });

    var installed = await manager.InstallDirectoryAsync(env, EnvPath.Posix(source));
    Check(installed.Status == PluginStatus.Disabled, "installed from a directory (disabled until granted)");
    await manager.GrantAsync(installed.Id, installed.Manifest.Permissions);
    var enabled = await manager.SetEnabledAsync(installed.Id, true);
    Check(enabled.Status == PluginStatus.Active, $"activated under Hardened Jaunt (status {enabled.Status})");
    Check(registry.Find("plugin__acme_probe__echo") is not null, "the tool is registered under source plugin:acme.probe");

    await manager.RunCommandAsync("acme.probe", "acme.probe.go", null, null);
    Check(kv.Get("plugins.kv.acme.probe")?.Contains("booted:{}", StringComparison.Ordinal) == true, "async command (top-level await, timers, host calls) ran");

    var tool = registry.Find("plugin__acme_probe__echo")!;
    var ctx = new Ncw.Agent.Kernel.Tools.ToolContext(new Ncw.Agent.Kernel.RunId("r"), "c", "s", 0, null, new Progress<Ncw.Agent.Kernel.Tools.ToolProgress>(), new Ncw.Agent.Kernel.Tools.FileMutationQueue());
    using var input = System.Text.Json.JsonDocument.Parse("""{"text":"hi"}""");
    var result = await tool.Tool.ExecuteAsync(new Ncw.Agent.Kernel.Tools.ToolInvocation("c", tool.ExternalName, tool.Tool, input.RootElement.Clone()), ctx, default);
    var text = string.Concat(result.Content.OfType<Ncw.Agent.Llm.TextPart>().Select(p => p.Text));
    Check(text == "echo:hi" && result.Details is not null, "the tool ran and returned text plus a UI-only card");

    var spin = false;
    try
    {
        await manager.RunCommandAsync("acme.probe", "acme.probe.spin", null, null);
    }
    catch (PluginException e) when (e.Key == PluginErrorKeys.Faulted)
    {
        spin = true;
    }

    Check(spin && manager.Snapshot().Plugins.Single().Status == PluginStatus.Faulted, "a busy loop was stopped by the instruction budget; the plugin is faulted, the host is fine");
}
catch (Exception e)
{
    Console.WriteLine("FAIL unexpected: " + e);
    failures.Add("unexpected");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
    }
}

Console.WriteLine(failures.Count == 0 ? "ALL OK" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;

internal sealed class Kv : IPluginKv
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string json) => _values[key] = json;
}
