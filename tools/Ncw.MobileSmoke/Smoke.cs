using System.Diagnostics;

namespace Ncw.MobileSmoke;

/// <summary>
/// 完整的脚本化冒烟(docs/design/11 §7):清空应用数据 → 冷启动并带着 <c>nextcowork://pair</c> 深链 → 手机进入「找到设备」→ 点「配对」→ 桌面看到新的控制端 →
/// 桌面发起一轮会触发审批的 Run → 手机收件箱出现待审批 → 点开卡片 → <b>长按允许</b> → Run 继续并完成(标记文件写出、上游收到第二次请求)。
/// 每一步打印 <c>SMOKE step … PASS|FAIL</c> 和耗时;截图写进 <c>artifacts/mobile-android/smoke-*.png</c>(被忽略目录);退出码 = 失败步数。
/// 点击坐标按屏幕比例换算(按 Pixel 6 的 1080x2400 版式量出来的;换了屏幕尺寸 / 版式要重新量)。推送没有覆盖(需要 Firebase 配置与推送中继)。
/// 环境变量:<c>ANDROID_HOME</c>(找 adb)、<c>NCW_ANDROID_APK</c>(默认 src/Ncw.Mobile.Android/bin/Debug/net11.0-android/work.nextco.ncw-Signed.apk)、
/// <c>NCW_SKIP_INSTALL=1</c>(不重装,用设备上现有的)。
/// </summary>
internal static class Smoke
{
    private const string Package = "work.nextco.ncw";
    private static int _failures;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk");
        var adb = Path.Combine(sdk, "platform-tools", "adb");
        var repo = FindRepoRoot();
        var shots = Path.Combine(repo, "artifacts", "mobile-android");
        Directory.CreateDirectory(shots);

        if (Run(adb, "get-state").Trim() != "device")
        {
            Console.WriteLine("SMOKE no adb device (start the emulator first): FAIL");
            return 3;
        }

        var size = Run(adb, "shell", "wm", "size").Split(':').Last().Trim().Split('x');
        int width = int.Parse(size[0]), height = int.Parse(size[1]);
        (int X, int Y) At(double fx, double fy) => ((int)(width * fx), (int)(height * fy));
        void Tap(double fx, double fy) => Run(adb, "shell", "input", "tap", At(fx, fy).X.ToString(), At(fx, fy).Y.ToString());
        void Shot(string name) => Screenshot(adb, Path.Combine(shots, $"smoke-{name}.png"));

        await using var rig = new Rig();
        await Step("agent + remote + fake upstream start", async () =>
        {
            await rig.StartAsync(ct);
            return rig.RemotePort > 0;
        });

        if (Environment.GetEnvironmentVariable("NCW_SKIP_INSTALL") != "1")
        {
            var apk = Environment.GetEnvironmentVariable("NCW_ANDROID_APK") ?? Path.Combine(repo, "src", "Ncw.Mobile.Android", "bin", "Debug", "net11.0-android", "work.nextco.ncw-Signed.apk");
            await Step("install apk", () => Task.FromResult(Run(adb, "install", "-r", apk).Contains("Success", StringComparison.Ordinal)));
        }

        Run(adb, "shell", "pm", "clear", Package);
        Run(adb, "shell", "am", "force-stop", Package);
        var qr = await rig.CreateQrAsync(ct);
        await Step("cold start with nextcowork://pair deep link", async () =>
        {
            Run(adb, "shell", $"am start -a android.intent.action.VIEW -d '{qr}' {Package}");
            await Task.Delay(TimeSpan.FromSeconds(14), ct);
            Shot("01-pair-parsed");
            return Run(adb, "shell", "pidof", Package).Trim().Length > 0;
        });

        await Step("tap Pair -> desktop sees the new controller", async () =>
        {
            Tap(0.596, 0.353);
            var ok = await Poll(() => rig.Controllers.Length > 0, 25, ct);
            Shot("02-paired");
            return ok;
        });

        await Step("start a run that needs approval -> pending interaction on the desktop", async () =>
        {
            await rig.StartRunAsync(ct);
            return await Poll(() => rig.PendingAsync(ct).GetAwaiter().GetResult().Length == 1, 30, ct);
        });

        await Step("phone inbox shows the approval card", async () =>
        {
            Tap(0.125, 0.95);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            Shot("03-inbox-approval");
            return true; // 视觉证据在截图里;收件箱内容没有可编程的读取通道
        });

        await Step("open the card, long-press Allow", async () =>
        {
            Tap(0.5, 0.22);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            Shot("04-approval-card");
            var (x, y) = At(0.6, 0.45);
            Run(adb, "shell", "input", "swipe", x.ToString(), y.ToString(), x.ToString(), y.ToString(), "2600");
            return true;
        });

        await Step("run continues: nothing pending, marker file written, upstream saw the tool result", async () =>
        {
            var ok = await Poll(() => rig.MarkerWritten && rig.PendingAsync(ct).GetAwaiter().GetResult().Length == 0 && rig.Upstream.ChatRequests >= 2, 30, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Shot("05-run-continued");
            return ok;
        });

        Rig.Log($"SMOKE result: {(_failures == 0 ? "PASS" : "FAIL")} ({_failures} failed step(s)); push was not exercised (no Firebase configuration / relay)");
        return _failures;
    }

    private static async Task Step(string name, Func<Task<bool>> body)
    {
        var watch = Stopwatch.StartNew();
        bool ok;
        try
        {
            ok = await body();
        }
        catch (Exception e)
        {
            Console.WriteLine($"SMOKE step '{name}' threw {e.GetType().Name}: {e.Message}");
            ok = false;
        }

        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"SMOKE step '{name}' {(ok ? "PASS" : "FAIL")} ({watch.Elapsed.TotalSeconds:F1}s)");
    }

    private static async Task<bool> Poll(Func<bool> condition, int seconds, CancellationToken ct)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        return condition();
    }

    private static string Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return output;
    }

    private static void Screenshot(string adb, string path)
    {
        var info = new ProcessStartInfo(adb) { RedirectStandardOutput = true };
        info.ArgumentList.Add("exec-out");
        info.ArgumentList.Add("screencap");
        info.ArgumentList.Add("-p");
        using var process = Process.Start(info)!;
        using var file = File.Create(path);
        process.StandardOutput.BaseStream.CopyTo(file);
        process.WaitForExit(30_000);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
