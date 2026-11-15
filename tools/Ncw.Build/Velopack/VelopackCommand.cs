using System.Diagnostics;
using Ncw.Build.Bundle;
using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Velopack;

/// <summary>velopack:用 vpk 把打包好的桌面应用做成安装包与更新包(全量 nupkg、可选增量、安装器、portable)。</summary>
internal sealed class VelopackCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("velopack",
        "Pack the bundled desktop app with Velopack (vpk from .config/dotnet-tools.json) into installers and update packages.",
        ["rid", "version", "channel", "artifacts", "bundle-dir", "out", "release-notes"],
        ["no-restore", "no-delta", "keep-output"],
        ["velopack --rid osx-arm64 --channel stable", "velopack --rid osx-arm64 --version 3.0.1 --out artifacts/feed --keep-output   (adds 3.0.1 to a feed that already has 3.0.0: vpk builds the delta)"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var version = CommonOptions.Version(args, ctx);
        var channel = args.Get("channel") ?? "stable";
        var layout = new BundleLayout(rid, args.Get("bundle-dir") ?? ctx.Repo.BundleDir(rid));
        if (!Directory.Exists(layout.AppRoot))
        {
            throw new BuildException($"No bundle at {layout.AppRoot} (run bundle first).");
        }

        var outDir = args.Get("out") ?? ctx.Repo.VelopackDir(rid, channel);
        // --keep-output:不清空输出目录。一个 feed 要累积各个版本(vpk 在输出目录里找上一个全量包来生成增量包,releases.<channel>.json 也要列出全部版本);
        // 默认仍然清空,保证单次打包的产物干净。
        if (Directory.Exists(outDir) && !args.Has("keep-output"))
        {
            Directory.Delete(outDir, true);
        }

        Directory.CreateDirectory(outDir);

        if (!args.Has("no-restore"))
        {
            ctx.Log.Step("dotnet tool restore (vpk)");
            var restore = await ctx.Runner.RunAsync(VelopackPlans.Restore(ctx.Repo), l => ctx.Log.Info(l), ct).ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                throw new BuildException("dotnet tool restore failed (needs access to a NuGet feed that has the vpk package).");
            }
        }

        string? entitlements = null;
        if (rid.Os == OsFamily.MacOs && ctx.EnvNonEmpty("NCW_MAC_SIGN_IDENTITY") is not null)
        {
            entitlements = Path.Combine(layout.OutputDir, Signing.SigningPlans.EntitlementsFileName);
            await File.WriteAllTextAsync(entitlements, Signing.SigningPlans.EntitlementsPlist + "\n", ct).ConfigureAwait(false);
        }

        var preSigned = File.Exists(layout.ManifestPath) && BundleBuilder.Read(layout.ManifestPath).Signed;
        ctx.Log.Step($"vpk pack {version} ({channel}) for {rid}");
        var spec = VelopackPlans.Pack(ctx.Repo, layout, version, channel, outDir, ctx.Host, ctx.Env, args.Get("release-notes"), args.Has("no-delta"), entitlements, preSigned);
        ctx.Log.Info(spec.Display());
        var clock = Stopwatch.StartNew();
        var result = await ctx.Runner.RunAsync(spec, l => ctx.Log.Info(l), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new BuildException($"vpk pack failed with exit code {result.ExitCode}.");
        }

        var files = Directory.EnumerateFiles(outDir).OrderBy(f => f, StringComparer.Ordinal).Select(f => new FileInfo(f)).ToArray();
        if (files.Length == 0)
        {
            throw new BuildException("vpk produced no files.");
        }

        ctx.Log.Info($"done in {clock.Elapsed.TotalSeconds:F0}s -> {outDir}");
        foreach (var f in files)
        {
            ctx.Log.Info($"{f.Name}  {f.Length / 1024.0 / 1024.0:F1} MiB");
        }

        return 0;
    }
}
