using Ncw.Build.Bundle;
using Ncw.Build.Core;
using Ncw.Build.Signing;

namespace Ncw.Build.Velopack;

/// <summary>
/// Velopack 打包(docs/design/04 §3):调用 .NET 工具 <c>vpk</c>。工具版本只写在 <c>.config/dotnet-tools.json</c>(dotnet 工具清单,
/// 这是工具包唯一能固定版本的地方),运行时库的版本(M7 桌面端接入时)写在 <c>Directory.Packages.props</c>,两者必须一致,测试里核对。
/// </summary>
internal static class VelopackPlans
{
    public const string PackId = "NextCoWork";

    public static ProcessSpec Restore(RepoLayout repo) => new("dotnet", ["tool", "restore"], repo.Root);

    /// <summary>
    /// <c>vpk [os] pack …</c>。目标操作系统和宿主不同时带 <c>[os]</c> 前缀(vpk 的跨平台打包语法)。
    /// 签名参数只在对应的环境变量齐全时加入;密钥、口令不进命令行日志(<see cref="ProcessSpec.Redact"/>)。
    /// </summary>
    public static ProcessSpec Pack(
        RepoLayout repo, BundleLayout bundle, string version, string channel, string outDir, Rid host, Func<string, string?> env, string? releaseNotes, bool noDelta, string? entitlementsPath = null, bool preSigned = false)
    {
        var args = new List<string> { "tool", "run", "vpk", "--" };
        if (bundle.Rid.Os != host.Os)
        {
            args.Add(bundle.Rid.Os switch { OsFamily.MacOs => "[osx]", OsFamily.Windows => "[win]", _ => "[linux]" });
        }

        args.AddRange(["pack", "-u", PackId, "-v", version, "-p", bundle.PackDir, "-e", bundle.Rid.DesktopExecutable, "-o", outDir, "-c", channel,
            "--packTitle", BundleLayout.AppName, "--packAuthors", BundleLayout.AppName]);
        if (releaseNotes is not null)
        {
            args.AddRange(["--releaseNotes", releaseNotes]);
        }

        if (noDelta)
        {
            args.AddRange(["--delta", "none"]);
        }

        var redact = new List<string>();
        switch (bundle.Rid.Os)
        {
            case OsFamily.MacOs:
                // vpk 会往 .app 里加自己的文件(更新器、sq.version),这会让已有的签名失效(实测:「a sealed resource is missing or invalid」),
                // 所以应用的最终签名必须由 vpk 来做(--signAppIdentity),公证和 .pkg 签名也一样。sign 子命令先签过的话加 --signDisableDeep
                // (vpk 不再逐个重签内层文件,只补它自己加的)。
                if (env("NCW_MAC_SIGN_IDENTITY") is { Length: > 0 } app)
                {
                    args.AddRange(["--signAppIdentity", app]);
                    if (entitlementsPath is not null)
                    {
                        args.AddRange(["--signEntitlements", entitlementsPath]);
                    }

                    if (preSigned)
                    {
                        args.Add("--signDisableDeep");
                    }
                }

                if (env("NCW_MAC_INSTALLER_IDENTITY") is { Length: > 0 } installer)
                {
                    args.AddRange(["--signInstallIdentity", installer]);
                }

                if (env("NCW_NOTARY_PROFILE") is { Length: > 0 } profile)
                {
                    args.AddRange(["--notaryProfile", profile]);
                }

                if (env("NCW_MAC_KEYCHAIN") is { Length: > 0 } keychain)
                {
                    args.AddRange(["--keychain", keychain]);
                }

                break;
            case OsFamily.Windows:
                if (SigningPlans.WindowsSignParams(env) is { } sign)
                {
                    args.AddRange(["--signParams", sign.Params]);
                    redact.AddRange(sign.Secrets);
                }

                break;
        }

        return new ProcessSpec("dotnet", args, repo.Root, null, redact);
    }

    /// <summary>读 <c>.config/dotnet-tools.json</c> 里 vpk 的版本(没有则 null)。</summary>
    public static string? ToolVersion(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.TryGetProperty("tools", out var tools) && tools.TryGetProperty("vpk", out var vpk) && vpk.TryGetProperty("version", out var v)
            ? v.GetString()
            : null;
    }
}
