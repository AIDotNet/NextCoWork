using Ncw.Build.Bundle;
using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Verify;

/// <summary>verify:检查打包产物(清单校验值、sidecar 位置),再启动里面的 ncw-agent 做冒烟,最后干净退出。</summary>
internal sealed class VerifyCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("verify",
        "Check the bundle against bundle.json and smoke-test the bundled ncw-agent (start, local-channel handshake, version, clean exit).",
        ["rid", "artifacts", "bundle-dir", "agent", "timeout"],
        ["skip-hash"],
        ["verify --rid osx-arm64", "verify --agent /path/to/ncw-agent"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(double.Parse(args.Get("timeout") ?? "30", System.Globalization.CultureInfo.InvariantCulture));
        string agentExe;
        string? expectedVersion = null;

        if (args.Get("agent") is { } explicitAgent)
        {
            agentExe = explicitAgent;
        }
        else
        {
            var rid = CommonOptions.RidOrHost(args, ctx);
            var layout = new BundleLayout(rid, args.Get("bundle-dir") ?? ctx.Repo.BundleDir(rid));
            if (!File.Exists(layout.ManifestPath))
            {
                throw new BuildException($"No {layout.ManifestPath} (run bundle first).");
            }

            ctx.Log.Step($"verify bundle {layout.AppRoot}");
            var manifest = BundleBuilder.Read(layout.ManifestPath);
            expectedVersion = manifest.Version;
            if (manifest.Rid != rid.Value)
            {
                throw new BuildException($"bundle.json is for {manifest.Rid}, not {rid}.");
            }

            if (!args.Has("skip-hash"))
            {
                var problems = manifest.Verify(layout.AppRoot);
                if (problems.Count > 0)
                {
                    foreach (var p in problems.Take(20))
                    {
                        ctx.Log.Error(p);
                    }

                    throw new BuildException($"{problems.Count} file(s) differ from bundle.json (changed after bundling? run sign, which refreshes it).");
                }

                ctx.Log.Info($"{manifest.Files.Length} file(s) match bundle.json ({(manifest.Signed ? "signed" : "unsigned")} bundle, version {manifest.Version})");
            }

            agentExe = layout.AgentExe;
            if (!File.Exists(agentExe))
            {
                throw new BuildException($"sidecar not found at the agreed location: {agentExe}");
            }

            // 同目录的 e_sqlite3 原生库(NativeAOT 可执行文件不自带,AGENTS.md §5 / 04 §3)
            var nativeLib = Directory.EnumerateFiles(layout.AgentDir, "*e_sqlite3*").FirstOrDefault();
            ctx.Log.Info(nativeLib is null ? "WARN no e_sqlite3 native library next to ncw-agent" : $"native library: {Path.GetFileName(nativeLib)}");
            if (nativeLib is null)
            {
                throw new BuildException("The e_sqlite3 native library is missing next to ncw-agent; the agent cannot open its database.");
            }
        }

        ctx.Log.Step($"smoke {agentExe}");
        var result = await AgentSmoke.RunAsync(agentExe, expectedVersion, ctx.Log, timeout, ct).ConfigureAwait(false);
        ctx.Log.Info($"PASS  version {result.AgentVersion}, protocol {result.ProtocolVersion}, ready in {result.ReadyAfter.TotalMilliseconds:F0} ms");
        return 0;
    }
}
