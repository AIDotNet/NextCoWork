using System.Diagnostics;
using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Publish;

/// <summary>构造 <c>dotnet publish</c> 的参数(纯函数,单测覆盖)。</summary>
internal static class PublishPlans
{
    /// <summary>
    /// <paramref name="devKms"/>:把开发用的 <c>dev-env</c> KMS 提供者编进去(<c>-p:NcwDevKms=true</c>,只给冒烟测试;产物不得分发)。
    /// 默认显式写 <c>NcwDevKms=false</c>,不依赖项目里 Debug/Release 的默认值。
    /// </summary>
    public static ProcessSpec Agent(RepoLayout repo, Rid rid, string configuration, string outDir, string? versionOverride, bool devKms = false) =>
        new("dotnet", Common(repo.AgentProject, rid, configuration, outDir, versionOverride).Concat(
        [
            // 部署形态固定为 NativeAOT:Release + RID 时项目文件本来就开(Ncw.Agent.csproj),这里显式写出,不依赖项目里的条件。
            "-p:PublishAot=true",
            "-p:StripSymbols=true",
            $"-p:NcwDevKms={(devKms ? "true" : "false")}",
        ]).ToArray(), repo.Root);

    /// <summary>发布产物里不得出现的字符串:开发用 KMS 提供者的环境变量名。出现 = 它被编进了要分发的二进制。</summary>
    public const string DevKmsMarker = "NCW_DEV_KMS_KEY";

    /// <summary>环境变量:内置到桌面应用里的发布公钥(Ed25519),<c>kid:base64[,kid:base64…]</c>。公钥是公开信息;私钥永远不在这里。</summary>
    public const string ReleasePublicKeysEnv = "NCW_RELEASE_PUBLIC_KEYS";

    /// <summary>
    /// 桌面发布。<paramref name="releasePublicKeys"/> 非空时作为 <c>-p:NcwReleasePublicKeys=…</c> 写进程序集元数据(桌面端自更新的信任根,docs/design/04 §6)。
    /// 逗号和分号在 MSBuild 命令行属性里是分隔符,值里的要转义(<c>%2C</c> / <c>%3B</c>);没给就不加:这样的构建没有任何内置公钥,自更新拒绝一切清单。
    /// </summary>
    public static ProcessSpec Desktop(RepoLayout repo, Rid rid, string configuration, string outDir, string? versionOverride, bool readyToRun, string? releasePublicKeys = null)
    {
        var args = Common(repo.DesktopProject, rid, configuration, outDir, versionOverride).Concat(
        [
            "-p:PublishSingleFile=false",
            "-p:PublishTrimmed=false",
            $"-p:PublishReadyToRun={(readyToRun ? "true" : "false")}",
        ]).ToList();
        if (!string.IsNullOrWhiteSpace(releasePublicKeys))
        {
            args.Add("-p:NcwReleasePublicKeys=" + releasePublicKeys.Trim().Replace(",", "%2C", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal));
        }

        return new("dotnet", args.ToArray(), repo.Root);
    }

    /// <summary>
    /// 服务器中继(09 §9.3:不要求 AOT)。默认自包含(部署机不用装 .NET / ASP.NET 共享框架);<paramref name="frameworkDependent"/> 更小,但要求部署机有 ASP.NET Core 10 运行时;
    /// <paramref name="aot"/> = NativeAOT(零裁剪警告可用,见 09 §9.3 的评估),和框架依赖互斥。非 AOT 时显式不裁剪(<c>PublishTrimmed=false</c>):中继没有理由冒裁剪的险;AOT 本身隐含裁剪。
    /// </summary>
    public static ProcessSpec Relay(RepoLayout repo, Rid rid, string configuration, string outDir, string? versionOverride, bool aot, bool frameworkDependent)
    {
        if (aot && frameworkDependent)
        {
            throw new BuildException("--aot needs a self-contained publish; it cannot be combined with --framework-dependent.");
        }

        // NativeAOT 本身隐含裁剪,不能再写 PublishTrimmed=false(SDK 会报错);非 AOT 才显式关掉裁剪。
        string[] flavour = aot
            ? ["-p:PublishAot=true", "-p:StripSymbols=true"]
            : ["-p:PublishAot=false", "-p:PublishTrimmed=false"];
        return new("dotnet", Common(repo.RelayProject, rid, configuration, outDir, versionOverride, selfContained: !frameworkDependent)
            .Concat(flavour).Append("-p:PublishSingleFile=false").ToArray(), repo.Root);
    }

    private static IEnumerable<string> Common(string project, Rid rid, string configuration, string outDir, string? versionOverride, bool selfContained = true)
    {
        yield return "publish";
        yield return project;
        yield return "-c";
        yield return configuration;
        yield return "-r";
        yield return rid.Value;
        yield return selfContained ? "--self-contained" : "--no-self-contained";
        yield return "-o";
        yield return outDir;
        yield return "-nologo";
        if (versionOverride is not null)
        {
            yield return $"-p:Version={versionOverride}";
        }
    }
}

/// <summary>
/// publish-agent:<c>ncw-agent</c> 按 RID 做 NativeAOT 发布,并把输出里的 IL / 裁剪警告当失败(AGENTS.md §5)。
/// 调试符号移到 artifacts/symbols/(发布包里不带 .pdb / .dSYM,崩溃时再拿它符号化)。
/// </summary>
internal sealed class PublishAgentCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("publish-agent",
        "NativeAOT-publish ncw-agent for a RID; any IL/trim warning fails the step.",
        ["rid", "configuration", "version", "artifacts", "out"],
        ["strict-warnings", "dev-kms"],
        ["publish-agent --rid osx-arm64", "publish-agent --rid osx-arm64 --dev-kms   (smoke tests only; the output must not be distributed)"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var configuration = CommonOptions.Configuration(args);
        if (!string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            // Debug 配置不做 AOT(项目文件只在 Release 开 PublishAot),发出来的不是我们要分发的东西。
            throw new BuildException("publish-agent only supports --configuration Release (NativeAOT is a Release-only publish).");
        }

        if (rid.CannotNativeAotFrom(ctx.Host) is { } reason)
        {
            throw new BuildException(reason);
        }

        var version = CommonOptions.Version(args, ctx);
        var outDir = args.Get("out") ?? ctx.Repo.PublishDir(rid, "agent");
        ResetDirectory(outDir);

        ctx.Log.Step($"publish ncw-agent {version} for {rid} (NativeAOT, Release)");
        var devKms = args.Has("dev-kms");
        if (devKms)
        {
            ctx.Log.Warn("--dev-kms: the development KMS provider is compiled in. This binary is for smoke tests ONLY and must never be distributed.");
        }

        var spec = PublishPlans.Agent(ctx.Repo, rid, "Release", outDir, args.Get("version"), devKms);
        var clock = Stopwatch.StartNew();
        ctx.Log.Info(spec.Display());
        var result = await ctx.Runner.RunAsync(spec, null, ct).ConfigureAwait(false);
        var elapsed = clock.Elapsed;

        var report = WarningFilter.Analyze(result.Lines);
        Report(ctx.Log, report);
        if (result.ExitCode != 0)
        {
            foreach (var line in result.Lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(30))
            {
                ctx.Log.Error(line);
            }

            throw new BuildException($"dotnet publish failed with exit code {result.ExitCode}.");
        }

        if (!report.TrimClean)
        {
            throw new BuildException($"NativeAOT publish produced {report.Trim.Count()} IL/trim warning(s); the agent must publish with zero (AGENTS.md section 5).");
        }

        if (args.Has("strict-warnings") && report.Other.Any())
        {
            throw new BuildException($"{report.Other.Count()} other warning(s) with --strict-warnings.");
        }

        var exe = Path.Combine(outDir, rid.AgentExecutable);
        if (!File.Exists(exe))
        {
            throw new BuildException($"Publish succeeded but {exe} does not exist.");
        }

        if (!devKms && BinaryContains(exe, PublishPlans.DevKmsMarker))
        {
            throw new BuildException($"The published ncw-agent contains the development KMS provider ('{PublishPlans.DevKmsMarker}'). A distributable build must not include it; check -p:NcwDevKms.");
        }

        var moved = MoveSymbols(outDir, ctx.Repo.SymbolsDir(rid));
        ctx.Log.Info($"ncw-agent: {new FileInfo(exe).Length / 1024.0 / 1024.0:F1} MiB, publish dir {DirectorySize(outDir) / 1024.0 / 1024.0:F1} MiB, {moved} symbol file(s) moved to {ctx.Repo.SymbolsDir(rid)}");
        ctx.Log.Info($"done in {elapsed.TotalSeconds:F0}s, zero trim warnings -> {outDir}");
        return 0;
    }

    internal static void Report(IBuildLog log, WarningReport report)
    {
        foreach (var w in report.Trim)
        {
            log.Error(w.Line);
        }

        var env = report.AllowedEnvironment.Count();
        if (env > 0)
        {
            log.Info($"{env} whitelisted environment warning(s) ignored (clang module cache .pcm; unrelated to our code):");
            foreach (var w in report.AllowedEnvironment.Take(3))
            {
                log.Info("  " + (w.Line.Length > 200 ? w.Line[..200] + "..." : w.Line));
            }
        }

        foreach (var w in report.Other)
        {
            log.Warn(w.Line);
        }
    }

    /// <summary>
    /// 流式扫描文件里是否含某个字符串。NativeAOT 的字符串字面量是 UTF-16,但<b>紧凑排布时最后一个字符的高位 0x00 字节会被下一条记录的头占掉</b>
    /// (实测:<c>N\0C\0W\0…K\0E\0Y</c> 后面直接是下一条记录,没有结尾的 0x00),所以按完整字符串的 UTF-16 编码去找会永远找不到。
    /// 这里搜「去掉最后一个字符的 UTF-16LE」和完整的 ASCII 两种形式;<paramref name="text"/> 至少要有 8 个字符,前缀才足够特异。
    /// </summary>
    internal static bool BinaryContains(string path, string text)
    {
        if (text.Length < 8)
        {
            throw new ArgumentException("The marker must have at least 8 characters.", nameof(text));
        }

        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        var utf16Packed = System.Text.Encoding.Unicode.GetBytes(text[..^1]);
        var overlap = Math.Max(ascii.Length, utf16Packed.Length) - 1;
        var buffer = new byte[1 << 20];
        var carry = 0;
        using var stream = File.OpenRead(path);
        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var span = buffer.AsSpan(0, carry + read);
            if (span.IndexOf(ascii) >= 0 || span.IndexOf(utf16Packed) >= 0)
            {
                return true;
            }

            carry = Math.Min(overlap, span.Length);
            span[^carry..].CopyTo(buffer);
        }

        return false;
    }

    internal static void ResetDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
    }

    /// <summary>把 .pdb、.dSYM、.dbg 移出发布目录。返回移动的条目数。</summary>
    internal static int MoveSymbols(string publishDir, string symbolsDir)
    {
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(publishDir).ToArray())
        {
            var name = Path.GetFileName(entry);
            if (!(name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Directory.CreateDirectory(symbolsDir);
            var target = Path.Combine(symbolsDir, name);
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                Directory.Move(entry, target);
            }
            else
            {
                File.Move(entry, target, overwrite: true);
            }

            count++;
        }

        return count;
    }

    private static long DirectorySize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}

/// <summary>publish-desktop:Avalonia 桌面按 RID 自包含发布(不裁剪:Avalonia / Arc 的 XAML 与反射式资源加载不适合裁剪,见 docs/design/04)。</summary>
internal sealed class PublishDesktopCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("publish-desktop",
        "Publish the Avalonia desktop app for a RID (self-contained, untrimmed).",
        ["rid", "configuration", "version", "artifacts", "out"],
        ["ready-to-run"],
        ["publish-desktop --rid osx-arm64"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var version = CommonOptions.Version(args, ctx);
        var outDir = args.Get("out") ?? ctx.Repo.PublishDir(rid, "desktop");
        PublishAgentCommand.ResetDirectory(outDir);

        ctx.Log.Step($"publish desktop {version} for {rid}");
        var keys = ctx.EnvNonEmpty(PublishPlans.ReleasePublicKeysEnv);
        if (keys is null)
        {
            ctx.Log.Warn($"{PublishPlans.ReleasePublicKeysEnv} is not set: this build has no release public key built in, so its self-update will refuse every manifest (04 section 6). Set it for builds that ship.");
        }
        else
        {
            ctx.Log.Info($"embedding {keys.Split(',').Length} release public key(s) for self-update verification");
        }

        var spec = PublishPlans.Desktop(ctx.Repo, rid, CommonOptions.Configuration(args), outDir, args.Get("version"), args.Has("ready-to-run"), keys);
        ctx.Log.Info(spec.Display());
        var clock = Stopwatch.StartNew();
        var result = await ctx.Runner.RunAsync(spec, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            foreach (var line in result.Lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(30))
            {
                ctx.Log.Error(line);
            }

            throw new BuildException($"dotnet publish failed with exit code {result.ExitCode}.");
        }

        var report = WarningFilter.Analyze(result.Lines);
        PublishAgentCommand.Report(ctx.Log, report);
        var exe = Path.Combine(outDir, rid.DesktopExecutable);
        if (!File.Exists(exe))
        {
            throw new BuildException($"Publish succeeded but {exe} does not exist.");
        }

        // 开发构建会把 ncw-agent 复制到 <输出>/agent/(Ncw.Desktop.csproj 的 CopyAgentSidecar):发布目录里不该有,sidecar 由 bundle 放进去。
        var stray = Path.Combine(outDir, "agent");
        if (Directory.Exists(stray))
        {
            ctx.Log.Warn("publish output unexpectedly contains agent/; removing it (bundle places the AOT sidecar there).");
            Directory.Delete(stray, true);
        }

        PublishAgentCommand.MoveSymbols(outDir, ctx.Repo.SymbolsDir(rid) + "-desktop");
        ctx.Log.Info($"done in {clock.Elapsed.TotalSeconds:F0}s -> {outDir}");
        return 0;
    }
}
