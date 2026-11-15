using Ncw.Build.Bundle;
using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Signing;

/// <summary>
/// sign:macOS codesign(+ 公证)、Windows signtool 的封装(docs/design/04 §6、D12)。
/// 凭据只读环境变量(见 <see cref="SigningPlans"/>);缺失就跳过并明说,<c>--require</c> 时改为失败(CI 的正式发布任务用它,防止悄悄发出未签名的包)。
/// </summary>
internal sealed class SignCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("sign",
        "Sign the bundled app: macOS codesign (+ --notarize), Windows signtool. Skips with a clear message when credentials are absent.",
        ["rid", "artifacts", "bundle-dir"],
        ["require", "notarize"],
        ["sign --rid osx-arm64", "sign --rid osx-arm64 --require --notarize"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var layout = new BundleLayout(rid, args.Get("bundle-dir") ?? ctx.Repo.BundleDir(rid));
        if (!Directory.Exists(layout.AppRoot))
        {
            throw new BuildException($"No bundle at {layout.AppRoot} (run bundle first).");
        }

        var signed = rid.Os switch
        {
            OsFamily.MacOs => await SignMacAsync(ctx, args, layout, ct).ConfigureAwait(false),
            OsFamily.Windows => await SignWindowsAsync(ctx, args, layout, ct).ConfigureAwait(false),
            _ => Skip(ctx, args, "Linux has no platform signature step (AppImage / archive); the release manifest's Ed25519 signature covers integrity."),
        };

        if (signed && File.Exists(layout.ManifestPath))
        {
            var previous = BundleBuilder.Read(layout.ManifestPath);
            BundleBuilder.Refresh(layout, previous, signed: true);
            ctx.Log.Info("bundle.json refreshed (signing changed the binaries)");
        }

        return signed || !args.Has("require") ? 0 : 1;
    }

    private static bool Skip(BuildContext ctx, ParsedArgs args, string reason)
    {
        var message = $"SKIPPED signing: {reason}";
        if (args.Has("require"))
        {
            throw new BuildException(message + " (--require is set)");
        }

        ctx.Log.Warn(message);
        return false;
    }

    private async Task<bool> SignMacAsync(BuildContext ctx, ParsedArgs args, BundleLayout layout, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS() && ctx.Host.Os != OsFamily.MacOs)
        {
            return Skip(ctx, args, "codesign and notarytool only exist on macOS; run this step on a macOS runner.");
        }

        var entitlements = Path.Combine(layout.OutputDir, SigningPlans.EntitlementsFileName);
        var config = SigningPlans.MacConfig(ctx.Env, entitlements);
        if (config is null)
        {
            return Skip(ctx, args, "NCW_MAC_SIGN_IDENTITY is not set (Developer ID Application certificate; see SigningPlans for all variables).");
        }

        ctx.Log.Step($"codesign {layout.AppRoot} (identity: {(config.Identity == SigningPlans.AdHoc ? "ad-hoc, NOT a release signature" : config.Identity)})");
        await File.WriteAllTextAsync(entitlements, SigningPlans.EntitlementsPlist + "\n", ct).ConfigureAwait(false);
        await RunChecked(ctx, SigningPlans.MacClearXattrs(layout.AppRoot), ct).ConfigureAwait(false);
        var nested = SigningPlans.MacNestedCode(layout.AppRoot);
        foreach (var spec in SigningPlans.MacSignSequence(layout.AppRoot, nested, config))
        {
            await RunChecked(ctx, spec, ct).ConfigureAwait(false);
        }

        await RunChecked(ctx, SigningPlans.MacVerify(layout.AppRoot), ct).ConfigureAwait(false);
        ctx.Log.Info($"signed {nested.Count} nested file(s) + the app bundle");

        if (!args.Has("notarize"))
        {
            ctx.Log.Warn("notarization not requested (use --notarize); a Developer ID signature without notarization is still blocked by Gatekeeper on downloaded apps.");
            return true;
        }

        if (config.Identity == SigningPlans.AdHoc)
        {
            throw new BuildException("Cannot notarize an ad-hoc signed app. Set NCW_MAC_SIGN_IDENTITY to a Developer ID Application identity.");
        }

        if (SigningPlans.NotaryConfigFrom(ctx.Env) is not { } notary)
        {
            return Skip(ctx, args, "notarization credentials missing: set NCW_NOTARY_PROFILE, or NCW_NOTARY_APPLE_ID + NCW_NOTARY_TEAM_ID + NCW_NOTARY_PASSWORD.") || true;
        }

        var zip = Path.Combine(layout.OutputDir, "NextCoWork-notarize.zip");
        ctx.Log.Step("notarize (notarytool submit --wait)");
        await RunChecked(ctx, SigningPlans.MacZipForNotary(layout.AppRoot, zip), ct).ConfigureAwait(false);
        await RunChecked(ctx, SigningPlans.MacNotarize(zip, notary, config.Keychain), ct).ConfigureAwait(false);
        await RunChecked(ctx, SigningPlans.MacStaple(layout.AppRoot), ct).ConfigureAwait(false);
        File.Delete(zip);
        return true;
    }

    private async Task<bool> SignWindowsAsync(BuildContext ctx, ParsedArgs args, BundleLayout layout, CancellationToken ct)
    {
        var config = SigningPlans.WindowsConfig(ctx.Env);
        if (config is null)
        {
            return Skip(ctx, args, "no Authenticode certificate configured (set NCW_WIN_CERT_FILE + NCW_WIN_CERT_PASSWORD, or NCW_WIN_CERT_THUMBPRINT).");
        }

        if (!OperatingSystem.IsWindows() && ctx.Host.Os != OsFamily.Windows)
        {
            return Skip(ctx, args, "signtool only runs on Windows; run this step on a Windows runner.");
        }

        var files = SigningPlans.WindowsFiles(layout.AppRoot);
        ctx.Log.Step($"signtool: {files.Count} file(s)");
        foreach (var file in files)
        {
            await RunChecked(ctx, SigningPlans.WindowsSignFile(file, config), ct).ConfigureAwait(false);
        }

        foreach (var exe in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            await RunChecked(ctx, SigningPlans.WindowsVerify(exe, config), ct).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task RunChecked(BuildContext ctx, ProcessSpec spec, CancellationToken ct)
    {
        ctx.Log.Info(spec.Display());
        var result = await ctx.Runner.RunAsync(spec, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            foreach (var line in result.Lines.TakeLast(20))
            {
                ctx.Log.Error(line);
            }

            throw new BuildException($"'{spec.FileName}' failed with exit code {result.ExitCode}.");
        }
    }
}
