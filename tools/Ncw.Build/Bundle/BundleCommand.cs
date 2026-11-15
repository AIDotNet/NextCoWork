using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Bundle;

/// <summary>bundle:把 ncw-agent 作为 sidecar 放进桌面应用的约定目录,写入版本与校验清单。</summary>
internal sealed class BundleCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("bundle",
        "Place the ncw-agent sidecar into the desktop app layout (.app on macOS, folder elsewhere) and write bundle.json.",
        ["rid", "version", "artifacts", "desktop-dir", "agent-dir", "out"],
        [],
        ["bundle --rid osx-arm64"]);

    public Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var version = CommonOptions.Version(args, ctx);
        var layout = new BundleLayout(rid, args.Get("out") ?? ctx.Repo.BundleDir(rid));
        var desktopDir = args.Get("desktop-dir") ?? ctx.Repo.PublishDir(rid, "desktop");
        var agentDir = args.Get("agent-dir") ?? ctx.Repo.PublishDir(rid, "agent");

        ctx.Log.Step($"bundle {version} for {rid}");
        var manifest = BundleBuilder.Build(layout, desktopDir, agentDir, version, ctx.Clock);
        var agent = manifest.Files.Where(f => f.Path.Contains("/agent/", StringComparison.Ordinal) || f.Path.StartsWith("agent/", StringComparison.Ordinal)).ToArray();
        ctx.Log.Info($"app:      {layout.AppRoot}");
        ctx.Log.Info($"sidecar:  {layout.AgentExe} ({agent.Length} files, {agent.Sum(f => f.Size) / 1024.0 / 1024.0:F1} MiB)");
        ctx.Log.Info($"manifest: {layout.ManifestPath} ({manifest.Files.Length} files, {manifest.Files.Sum(f => f.Size) / 1024.0 / 1024.0:F1} MiB total)");
        return Task.FromResult(0);
    }
}
