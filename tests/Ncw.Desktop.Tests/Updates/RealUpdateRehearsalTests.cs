using System.Diagnostics;
using System.Text.Json;

namespace Ncw.Desktop.Tests.Updates;

/// <summary>
/// <b>可选(默认跳过)</b>的本机真实演练(docs/design/04 §7.3):用<b>真的装出来的</b>应用、真的 Velopack 更新器、真的 ncw-agent、真的本地 feed,
/// 走一遍「3.0.0 → 3.0.1」自更新,再走两条负面路径。只在 macOS 上、设了 <c>NCW_REHEARSAL_ROOT</c> 时运行。
/// <para>
/// <c>NCW_REHEARSAL_ROOT</c> 指向一个已经准备好的目录(准备步骤见 04 §7.3,全部用 <c>tools/Ncw.Build</c> 的子命令):
/// <c>install/NextCoWork.app</c>(3.0.0 解出来的应用)、<c>feed/</c>(3.0.0 + 3.0.1 的 Velopack 产物与签名的清单)、<c>feed-3.0.0-only/</c>(只有 3.0.0 的 feed,回滚用)。
/// 这些包是用开发用的 Ed25519 密钥签的、构建时把对应公钥内置进了应用(<c>NCW_RELEASE_PUBLIC_KEYS</c>),密钥不在仓库里。
/// </para>
/// 演练程序是应用内的 <see cref="Ncw.Desktop.Updates.DevUpdateRehearsal"/>(只认环境变量,没有任何用户可见的命令行)。
/// </summary>
public class RealUpdateRehearsalTests
{
    private static string? Root => Environment.GetEnvironmentVariable("NCW_REHEARSAL_ROOT");

    private static void RequireRehearsal()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The rehearsal drives the macOS .app bundle (Windows and Linux are not covered).");
        Assert.SkipUnless(Root is { Length: > 0 } root && Directory.Exists(Path.Combine(root, "install", "NextCoWork.app")), "Set NCW_REHEARSAL_ROOT to a prepared rehearsal directory (docs/design/04 section 7.3).");
    }

    private static string CopyApp(string source, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        Run("/usr/bin/ditto", source, Path.Combine(destinationDir, "NextCoWork.app"));
        return Path.Combine(destinationDir, "NextCoWork.app");
    }

    private static void Run(string fileName, params string[] args)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{fileName} failed: {process.StandardError.ReadToEnd()}");
    }

    private static ProcessStartInfo Launch(string app, string work, string feed, string? mode = null)
    {
        var info = new ProcessStartInfo(Path.Combine(app, "Contents", "MacOS", "NextCoWork")) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["NCW_DEV_UPDATE_REHEARSAL"] = Path.Combine(work, "driver");
        info.Environment["NCW_UPDATE_FEED"] = feed;
        info.Environment["NCW_AGENT_DATA_ROOT"] = Path.Combine(work, "data");
        info.Environment["NCW_CLIENT_DIR"] = Path.Combine(work, "client");
        info.Environment["NCW_PROFILE_ROOT"] = Path.Combine(work, "profile");
        if (mode is not null)
        {
            info.Environment["NCW_DEV_UPDATE_MODE"] = mode;
        }

        return info;
    }

    private static async Task<(int ExitCode, string Output)> RunApp(ProcessStartInfo info, TimeSpan timeout)
    {
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await output + await error);
    }

    private static async Task<JsonElement> WaitForJson(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path))
            {
                try
                {
                    return JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // 还没写完
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"{path} did not appear in {timeout.TotalSeconds:F0}s");
    }

    [Fact]
    public async Task Update_3_0_0_to_3_0_1_keeps_the_data_then_a_tampered_manifest_is_refused_and_an_older_feed_does_not_downgrade()
    {
        RequireRehearsal();
        var root = Root!;
        var temp = Path.Combine(Path.GetTempPath(), "ncw-rehearsal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            // Velopack 把下载的包放在用户缓存目录,和别的运行共用:清掉上一次的残留,免得「已经下载过」盖住这次的下载
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "velopack", "NextCoWork");
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }

            var app = CopyApp(Path.Combine(root, "install", "NextCoWork.app"), Path.Combine(temp, "app"));
            var work = Path.Combine(temp, "work");

            // 1. 3.0.0:检查 → 下载 → 交给更新器并退出;更新器替换应用并重新拉起 3.0.1,3.0.1 验证数据并写 report.json
            var (code, output) = await RunApp(Launch(app, work, Path.Combine(root, "feed")), TimeSpan.FromSeconds(90));
            Assert.True(code == 0, "phase 1 failed:\n" + output);
            var report = await WaitForJson(Path.Combine(work, "driver", "report.json"), TimeSpan.FromSeconds(120));
            Assert.True(report.GetProperty("ok").GetBoolean(), report.ToString());
            Assert.Equal("3.0.0", report.GetProperty("fromVersion").GetString());
            Assert.Equal("3.0.1", report.GetProperty("toVersion").GetString());
            Assert.Equal("3.0.1", report.GetProperty("agentVersion").GetString());
            Assert.Equal(report.GetProperty("marker").GetString(), report.GetProperty("markerAfter").GetString());
            Assert.Contains("CFBundleShortVersionString", File.ReadAllText(Path.Combine(app, "Contents", "Info.plist")));
            Assert.Contains("<version>3.0.1</version>", File.ReadAllText(Path.Combine(app, "Contents", "MacOS", "sq.version")));

            // 2. 负面:清单被改了一个字节(现在装着的是 3.0.1,所以换一个 3.0.2 的假清单也行;这里直接改 3.0.1 的清单,签名对不上)
            var tampered = Path.Combine(temp, "feed-tampered");
            Run("/bin/cp", "-R", Path.Combine(root, "feed"), tampered);
            var manifest = Path.Combine(tampered, "release-manifest.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"mandatory\": false", "\"mandatory\": true", StringComparison.Ordinal));
            var fresh = CopyApp(Path.Combine(root, "install", "NextCoWork.app"), Path.Combine(temp, "app-fresh"));
            (code, output) = await RunApp(Launch(fresh, Path.Combine(temp, "work-tampered"), tampered, "check"), TimeSpan.FromSeconds(60));
            Assert.True(code == 0, output);
            var check = await WaitForJson(Path.Combine(temp, "work-tampered", "driver", "check-result.json"), TimeSpan.FromSeconds(5));
            Assert.Equal("Failed", check.GetProperty("phase").GetString());
            Assert.Equal("SignatureInvalid", check.GetProperty("failure").GetString());
            Assert.Equal(JsonValueKind.Null, check.GetProperty("releaseVersion").ValueKind);

            // 3. 负面:feed 里只有更低的版本(回滚)。已经是 3.0.1 的应用(第 1 步更新后的那份)不降级
            (code, output) = await RunApp(Launch(app, Path.Combine(temp, "work-rollback"), Path.Combine(root, "feed-3.0.0-only"), "check"), TimeSpan.FromSeconds(60));
            Assert.True(code == 0, output);
            check = await WaitForJson(Path.Combine(temp, "work-rollback", "driver", "check-result.json"), TimeSpan.FromSeconds(5));
            Assert.Equal("UpToDate", check.GetProperty("phase").GetString());
            Assert.Equal("3.0.1", check.GetProperty("currentVersion").GetString());
            Assert.Equal(JsonValueKind.Null, check.GetProperty("releaseVersion").ValueKind);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
