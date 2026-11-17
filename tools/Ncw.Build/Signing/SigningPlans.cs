using Ncw.Build.Core;

namespace Ncw.Build.Signing;

internal sealed record MacSignConfig(string Identity, string? Keychain, string? EntitlementsPath);

/// <summary>公证凭据:二选一 —— notarytool 的钥匙串配置名,或 Apple ID + 团队 ID + App 专用密码。</summary>
internal sealed record NotaryConfig(string? KeychainProfile, string? AppleId, string? TeamId, string? Password);

internal sealed record WindowsSignConfig(string SigntoolPath, string TimestampUrl, string? CertFile, string? CertPassword, string? Thumbprint);

/// <summary>
/// 签名命令的构造(纯函数,单测覆盖)。**所有凭据只来自环境变量**,缺失就由调用方跳过并说明;任何密钥都不进仓库、不进日志。
/// <code>
/// macOS    NCW_MAC_SIGN_IDENTITY   "Developer ID Application: … (TEAMID)";"-" = ad-hoc(只用来在本机演练流水线,不是正式签名)
///          NCW_MAC_KEYCHAIN        可选:钥匙串文件
///          NCW_NOTARY_PROFILE      notarytool store-credentials 保存的配置名(优先)
///          NCW_NOTARY_APPLE_ID + NCW_NOTARY_TEAM_ID + NCW_NOTARY_PASSWORD   (App 专用密码)
/// Windows  NCW_WIN_CERT_FILE + NCW_WIN_CERT_PASSWORD   .pfx
///          或 NCW_WIN_CERT_THUMBPRINT                  证书库里的证书
///          NCW_WIN_TIMESTAMP_URL   默认 http://timestamp.digicert.com
///          NCW_SIGNTOOL            signtool.exe 路径,默认走 PATH
/// </code>
/// </summary>
internal static class SigningPlans
{
    public const string AdHoc = "-";

    /// <summary>vpk 要求 entitlements 文件的扩展名是 .entitlements。</summary>
    public const string EntitlementsFileName = "NextCoWork.entitlements";

    /// <summary>hardened runtime 的 entitlements:沿用 2.x 的 build/entitlements.mac.plist(JIT、可执行内存、关闭库验证)—— 自包含 .NET 运行时需要。</summary>
    public const string EntitlementsPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
          <dict>
            <key>com.apple.security.cs.allow-jit</key>
            <true/>
            <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
            <true/>
            <key>com.apple.security.cs.disable-library-validation</key>
            <true/>
          </dict>
        </plist>
        """;

    public static MacSignConfig? MacConfig(Func<string, string?> env, string entitlementsPath) =>
        env("NCW_MAC_SIGN_IDENTITY") is { Length: > 0 } identity
            ? new MacSignConfig(identity, env("NCW_MAC_KEYCHAIN") is { Length: > 0 } k ? k : null, entitlementsPath)
            : null;

    public static NotaryConfig? NotaryConfigFrom(Func<string, string?> env)
    {
        if (env("NCW_NOTARY_PROFILE") is { Length: > 0 } profile)
        {
            return new NotaryConfig(profile, null, null, null);
        }

        return env("NCW_NOTARY_APPLE_ID") is { Length: > 0 } id
               && env("NCW_NOTARY_TEAM_ID") is { Length: > 0 } team
               && env("NCW_NOTARY_PASSWORD") is { Length: > 0 } password
            ? new NotaryConfig(null, id, team, password)
            : null;
    }

    public static WindowsSignConfig? WindowsConfig(Func<string, string?> env)
    {
        var file = env("NCW_WIN_CERT_FILE") is { Length: > 0 } f ? f : null;
        var thumbprint = env("NCW_WIN_CERT_THUMBPRINT") is { Length: > 0 } t ? t : null;
        if (file is null && thumbprint is null)
        {
            return null;
        }

        return new WindowsSignConfig(
            env("NCW_SIGNTOOL") is { Length: > 0 } tool ? tool : "signtool",
            env("NCW_WIN_TIMESTAMP_URL") is { Length: > 0 } url ? url : "http://timestamp.digicert.com",
            file,
            env("NCW_WIN_CERT_PASSWORD") is { Length: > 0 } p ? p : null,
            thumbprint);
    }

    /// <summary>Mach-O 文件:魔数 0xFEEDFACE/F、0xCEFAEDFE/CF(含 fat 的 0xCAFEBABE)。</summary>
    public static bool IsMachO(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            if (stream.Read(magic) < 4)
            {
                return false;
            }

            var value = (uint)(magic[0] << 24 | magic[1] << 16 | magic[2] << 8 | magic[3]);
            return value is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 应用包里需要单独签名的东西,按签名顺序(codesign 要由内到外,外层 bundle 由 <see cref="MacSignSequence"/> 最后签)。
    /// <c>Contents/MacOS</c> 下的**每个文件**都要签,包括托管 dll:.NET 应用把全部程序集放在 MacOS 里,
    /// 只签 Mach-O 的话,主程序签名时会报「In subcomponent: xxx.dll … code object is not signed at all」(Avalonia 的 macOS 发布指南也是逐个文件签)。
    /// 其余位置只签 Mach-O。
    /// <para>
    /// 嵌套的 .app(<c>agent/NextCoWork Agent.app</c>)自成一组、排在前面:它自己 <c>Contents/MacOS</c> 里的文件(主程序最后)→ 这个嵌套包目录本身;
    /// 它的 Info.plist、图标由签嵌套包时封进去,不单独签。然后才是外层包的文件,外层主程序(CFBundleExecutable)最后。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> MacNestedCode(string appDir)
    {
        appDir = Path.TrimEndingDirectorySeparator(appDir);
        var nestedApps = Directory.EnumerateDirectories(appDir, "*.app", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
        var files = Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).LinkTarget is null)
            .ToList();

        // 每个文件归属最内层的包
        string Owner(string file) =>
            nestedApps.FirstOrDefault(b => file.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal)) ?? appDir;

        var result = new List<string>();
        foreach (var bundle in nestedApps.Append(appDir))
        {
            // 外层包的主程序叫 NextCoWork(不管包目录叫什么);嵌套包的主程序和包同名
            var mainName = bundle == appDir ? Bundle.BundleLayout.AppName : Path.GetFileNameWithoutExtension(bundle);
            result.AddRange(BundleFiles(bundle, mainName, files.Where(f => Owner(f) == bundle)));
            if (bundle != appDir)
            {
                result.Add(bundle);
            }
        }

        return result;
    }

    private static IEnumerable<string> BundleFiles(string bundle, string mainName, IEnumerable<string> owned)
    {
        var macOs = Path.Combine(bundle, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        // 主程序(CFBundleExecutable)必须最后签:它签名时会检查同目录的其他文件是否都已签名。
        var main = macOs + mainName;
        return owned
            .Where(f => f.StartsWith(macOs, StringComparison.Ordinal) || IsMachO(f))
            .OrderBy(f => f == main ? 1 : 0)
            .ThenByDescending(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.Ordinal);
    }

    public static ProcessSpec MacSignFile(string path, MacSignConfig config, bool withEntitlements)
    {
        var args = new List<string> { "--force", "--options", "runtime" };
        // ad-hoc 没有可以打时间戳的证书。
        args.Add(config.Identity == AdHoc ? "--timestamp=none" : "--timestamp");
        if (withEntitlements && config.EntitlementsPath is not null)
        {
            args.AddRange(["--entitlements", config.EntitlementsPath]);
        }

        if (config.Keychain is not null)
        {
            args.AddRange(["--keychain", config.Keychain]);
        }

        args.AddRange(["--sign", config.Identity, path]);
        return new ProcessSpec("codesign", args);
    }

    /// <summary>
    /// 整个签名序列:内层文件 → .app。entitlements 只给 Mach-O 可执行文件和 .app(dylib、托管 dll 带 entitlements 没有意义)。
    /// </summary>
    public static IReadOnlyList<ProcessSpec> MacSignSequence(string appDir, IReadOnlyList<string> nested, MacSignConfig config)
    {
        var list = new List<ProcessSpec>();
        foreach (var file in nested)
        {
            // 嵌套的 .app 目录和可执行文件一样带 entitlements
            var isExecutable = Directory.Exists(file)
                || (IsMachO(file) && !file.EndsWith(".dylib", StringComparison.Ordinal) && !file.EndsWith(".so", StringComparison.Ordinal));
            list.Add(MacSignFile(file, config, withEntitlements: isExecutable));
        }

        list.Add(MacSignFile(appDir, config, withEntitlements: true));
        return list;
    }

    public static ProcessSpec MacVerify(string appDir) => new("codesign", ["--verify", "--deep", "--strict", "--verbose=2", appDir]);

    /// <summary>公证提交:zip 由 ditto 生成(保留 .app 的结构),notarytool 等待结果。</summary>
    public static ProcessSpec MacZipForNotary(string appDir, string zipPath) => new("ditto", ["-c", "-k", "--keepParent", appDir, zipPath]);

    public static ProcessSpec MacNotarize(string zipPath, NotaryConfig config, string? keychain)
    {
        var args = new List<string> { "notarytool", "submit", zipPath, "--wait" };
        var redact = new List<string>();
        if (config.KeychainProfile is not null)
        {
            args.AddRange(["--keychain-profile", config.KeychainProfile]);
        }
        else
        {
            args.AddRange(["--apple-id", config.AppleId!, "--team-id", config.TeamId!, "--password", config.Password!]);
            redact.Add(config.Password!);
        }

        if (keychain is not null)
        {
            args.AddRange(["--keychain", keychain]);
        }

        return new ProcessSpec("xcrun", args, null, null, redact);
    }

    public static ProcessSpec MacStaple(string appDir) => new("xcrun", ["stapler", "staple", appDir]);

    /// <summary>清掉扩展属性:位于 iCloud 同步目录里的构建输出会被盖上 com.apple.FinderInfo,codesign 拒绝(2.x 的 mac-adhoc-sign.mjs 踩过)。</summary>
    public static ProcessSpec MacClearXattrs(string appDir) => new("xattr", ["-cr", appDir]);

    /// <summary>signtool 的公共参数(给 vpk 的 --signParams 用)。</summary>
    public static (string Params, IReadOnlyList<string> Secrets)? WindowsSignParams(Func<string, string?> env)
    {
        if (WindowsConfig(env) is not { } c)
        {
            return null;
        }

        var parts = new List<string>();
        var secrets = new List<string>();
        if (c.CertFile is not null)
        {
            parts.Add($"/f \"{c.CertFile}\"");
            if (c.CertPassword is not null)
            {
                parts.Add($"/p \"{c.CertPassword}\"");
                secrets.Add(c.CertPassword);
            }
        }
        else
        {
            parts.Add($"/sha1 {c.Thumbprint}");
        }

        parts.AddRange(["/fd", "sha256", "/tr", c.TimestampUrl, "/td", "sha256"]);
        return (string.Join(' ', parts), secrets);
    }

    public static ProcessSpec WindowsSignFile(string path, WindowsSignConfig config)
    {
        var args = new List<string> { "sign" };
        var redact = new List<string>();
        if (config.CertFile is not null)
        {
            args.AddRange(["/f", config.CertFile]);
            if (config.CertPassword is not null)
            {
                args.AddRange(["/p", config.CertPassword]);
                redact.Add(config.CertPassword);
            }
        }
        else
        {
            args.AddRange(["/sha1", config.Thumbprint!]);
        }

        args.AddRange(["/fd", "sha256", "/tr", config.TimestampUrl, "/td", "sha256", path]);
        return new ProcessSpec(config.SigntoolPath, args, null, null, redact);
    }

    public static ProcessSpec WindowsVerify(string path, WindowsSignConfig config) => new(config.SigntoolPath, ["verify", "/pa", path]);

    /// <summary>
    /// Windows 上要签名的文件:所有 .exe,以及**原生** .dll(sidecar 的 e_sqlite3 等)。
    /// 托管程序集不逐个签(几百个第三方 dll,04 §6 只要求主程序、ncw-agent.exe 和原生库)。
    /// </summary>
    public static IReadOnlyList<string> WindowsFiles(string appDir) =>
        [.. Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !IsManagedAssembly(f)))
            .OrderBy(f => f, StringComparer.Ordinal)];

    /// <summary>PE 头里 CLR 描述符目录(第 15 项)非空即托管程序集。</summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            using var r = new BinaryReader(s);
            if (s.Length < 0x40 || r.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            s.Position = 0x3C;
            var pe = r.ReadInt32();
            s.Position = pe;
            if (r.ReadUInt32() != 0x00004550)
            {
                return false;
            }

            s.Position = pe + 24;
            var magic = r.ReadUInt16();
            var directories = pe + 24 + (magic == 0x20B ? 112 : 96);
            s.Position = directories + 14 * 8;
            _ = r.ReadUInt32();
            return r.ReadUInt32() != 0;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return false;
        }
    }
}
