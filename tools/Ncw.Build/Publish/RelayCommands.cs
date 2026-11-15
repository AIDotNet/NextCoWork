using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ncw.Build.Cli;
using Ncw.Build.Core;
using Ncw.Build.Verify;

namespace Ncw.Build.Publish;

/// <summary>自检的结果(给日志和单测看)。</summary>
internal sealed record RelayCheckResult(TimeSpan ReadyAfter, int Port, bool MetricsOk, int NonWebSocketStatus, int ExitCode, TimeSpan ShutdownAfter);

/// <summary>
/// 产物自检:把发布出来的 <c>ncw-relay</c> 当真实进程跑一遍(启动 → <c>/healthz</c> → <c>/metrics</c> → 非 WebSocket 请求被 426 拒绝 → 关闭 stdin 优雅退出,退出码必须是 0)。
/// Unix 上再起一个实例发 SIGTERM(容器 / systemd 的停止方式),同样必须以 0 退出——这条抓出过「SIGTERM 之后进程不退出」的真实缺陷。
/// 配置只经环境变量(和部署一致):监听本机回环上的空闲端口;<c>NCW_RELAY_LIFELINE=stdin</c> 让「关 stdin」成为跨平台(含 Windows)的优雅退出信号。
/// </summary>
internal static class RelayCheck
{
    public static async Task<RelayCheckResult> RunAsync(string relayExe, IBuildLog log, TimeSpan timeout, CancellationToken ct, int? port = null)
    {
        var listenPort = port ?? FreePort();
        var url = $"http://127.0.0.1:{listenPort}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // 第一个实例:HTTP 面 + stdin 关闭的优雅退出
        using var first = Start(relayExe, listenPort, lifeline: true);
        var process = first.Process;
        try
        {
            log.Info($"started ncw-relay pid {process.Id} on {url}");
            var ready = await WaitHealthyAsync(http, url, process, timeout, ct).ConfigureAwait(false);
            log.Info($"/healthz 200 after {ready.TotalMilliseconds:F0} ms");

            using var metrics = await http.GetAsync(url + "/metrics", ct).ConfigureAwait(false);
            var metricsText = await metrics.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!metrics.IsSuccessStatusCode || metricsText.Length == 0)
            {
                throw new BuildException($"/metrics answered {(int)metrics.StatusCode} with {metricsText.Length} byte(s) from a loopback source; expected 200 with Prometheus text.");
            }

            log.Info($"/metrics 200 ({metricsText.Length} bytes)");

            // 路径是真的:非 WebSocket 请求访问控制端接入路径必须得到 426(Upgrade Required),不是 404 / 500。
            using var plain = await http.GetAsync(url + "/connect?d=dev_aaaaaaaaaaaaaaaaaaaaaaaaaa", ct).ConfigureAwait(false);
            if ((int)plain.StatusCode != 426)
            {
                throw new BuildException($"A plain HTTP GET to /connect answered {(int)plain.StatusCode}; expected 426 (WebSocket upgrade required).");
            }

            log.Info("plain GET /connect -> 426 (WebSocket upgrade required)");

            var stop = Stopwatch.StartNew();
            process.CloseStdin();
            await ExpectCleanExitAsync(process, "its stdin closed", timeout, ct).ConfigureAwait(false);
            var shutdown = stop.Elapsed;
            log.Info($"graceful exit OK after stdin closed (exit 0, {shutdown.TotalMilliseconds:F0} ms)");

            // 第二个实例(仅 Unix):SIGTERM。没有 lifeline,和真实部署一样只靠信号。
            if (!OperatingSystem.IsWindows())
            {
                using var second = Start(relayExe, FreePort(), lifeline: false);
                try
                {
                    var secondUrl = $"http://127.0.0.1:{second.Port}";
                    await WaitHealthyAsync(http, secondUrl, second.Process, timeout, ct).ConfigureAwait(false);
                    var term = Stopwatch.StartNew();
                    await SendSigtermAsync(second.Process.Id, ct).ConfigureAwait(false);
                    await ExpectCleanExitAsync(second.Process, "SIGTERM", timeout, ct).ConfigureAwait(false);
                    log.Info($"graceful exit OK after SIGTERM (exit 0, {term.Elapsed.TotalMilliseconds:F0} ms)");
                }
                finally
                {
                    second.Process.Kill();
                }
            }

            return new RelayCheckResult(ready, listenPort, true, 426, 0, shutdown);
        }
        finally
        {
            process.Kill();
        }
    }

    private sealed record Started(AgentProcess Process, int Port) : IDisposable
    {
        public int Id => Process.Id;

        public void Dispose() => Process.Dispose();
    }

    private static Started Start(string relayExe, int port, bool lifeline)
    {
        var env = new Dictionary<string, string> { ["NCW_RELAY_URLS"] = $"http://127.0.0.1:{port}" };
        if (lifeline)
        {
            env["NCW_RELAY_LIFELINE"] = "stdin";
        }

        return new Started(AgentProcess.Start(relayExe, [], env), port);
    }

    private static async Task<TimeSpan> WaitHealthyAsync(HttpClient http, string url, AgentProcess process, TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(url + "/healthz", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return started.Elapsed;
                }
            }
            catch (HttpRequestException)
            {
                // 还没监听上:继续等
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            if (process.HasExited)
            {
                throw new BuildException($"ncw-relay exited before /healthz answered.{process.Diagnostics()}");
            }

            if (started.Elapsed >= timeout)
            {
                throw new BuildException($"ncw-relay did not answer /healthz within {timeout.TotalSeconds:F0}s.{process.Diagnostics()}");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private static async Task ExpectCleanExitAsync(AgentProcess process, string trigger, TimeSpan timeout, CancellationToken ct)
    {
        var exit = await process.WaitForExitAsync(timeout, ct).ConfigureAwait(false);
        if (exit is null)
        {
            throw new BuildException($"ncw-relay did not exit within {timeout.TotalSeconds:F0}s after {trigger}.{process.Diagnostics()}");
        }

        if (exit != 0)
        {
            throw new BuildException($"ncw-relay exited with {exit} after {trigger}; expected 0.{process.Diagnostics()}");
        }
    }

    private static async Task SendSigtermAsync(int pid, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("kill", ["-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { UseShellExecute = false, CreateNoWindow = true };
        using var kill = Process.Start(psi) ?? throw new BuildException("Cannot start 'kill'.");
        await kill.WaitForExitAsync(ct).ConfigureAwait(false);
        if (kill.ExitCode != 0)
        {
            throw new BuildException($"kill -TERM {pid} failed with exit code {kill.ExitCode}.");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// build-relay:发布服务器中继 <c>ncw-relay</c>(服务端部署物,不是用户命令行),并对产物做自检。
/// 默认自包含、不裁剪、不 AOT(09 §9.3:中继不要求 AOT);<c>--aot</c> 切到 NativeAOT(同 publish-agent:任何 IL / 裁剪警告都当失败,
/// 不能跨操作系统编译);<c>--framework-dependent</c> 要求部署机有 ASP.NET Core 运行时。IL / 裁剪警告的处理对所有形态一致:有就失败,clang <c>.pcm</c> 环境警告白名单。
/// </summary>
internal sealed class BuildRelayCommand(Func<string, IBuildLog, TimeSpan, CancellationToken, Task<RelayCheckResult>>? check = null) : IBuildCommand
{
    private readonly Func<string, IBuildLog, TimeSpan, CancellationToken, Task<RelayCheckResult>> _check =
        check ?? ((exe, log, timeout, ct) => RelayCheck.RunAsync(exe, log, timeout, ct));

    public CommandSpec Spec { get; } = new("build-relay",
        "Publish the server relay (ncw-relay) for a RID and self-check the artifact (start, /healthz, graceful exit).",
        ["rid", "configuration", "version", "artifacts", "out", "timeout"],
        ["aot", "framework-dependent", "skip-check", "strict-warnings"],
        ["build-relay --rid linux-x64", "build-relay --rid osx-arm64 --aot", "build-relay --rid linux-x64 --framework-dependent"]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = CommonOptions.RidOrHost(args, ctx);
        var configuration = CommonOptions.Configuration(args);
        var aot = args.Has("aot");
        var frameworkDependent = args.Has("framework-dependent");
        if (aot && frameworkDependent)
        {
            throw new BuildException("--aot needs a self-contained publish; it cannot be combined with --framework-dependent.");
        }

        if (aot)
        {
            if (!string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildException("build-relay --aot only supports --configuration Release.");
            }

            if (rid.CannotNativeAotFrom(ctx.Host) is { } reason)
            {
                throw new BuildException(reason);
            }
        }

        var timeout = TimeSpan.FromSeconds(double.Parse(args.Get("timeout") ?? "30", System.Globalization.CultureInfo.InvariantCulture));
        var version = CommonOptions.Version(args, ctx);
        var outDir = args.Get("out") ?? ctx.Repo.PublishDir(rid, "relay");
        PublishAgentCommand.ResetDirectory(outDir);

        var flavour = aot ? "NativeAOT" : frameworkDependent ? "framework-dependent" : "self-contained";
        ctx.Log.Step($"publish ncw-relay {version} for {rid} ({flavour}, {configuration})");
        var spec = PublishPlans.Relay(ctx.Repo, rid, configuration, outDir, args.Get("version"), aot, frameworkDependent);
        ctx.Log.Info(spec.Display());
        var clock = Stopwatch.StartNew();
        var result = await ctx.Runner.RunAsync(spec, null, ct).ConfigureAwait(false);

        var report = WarningFilter.Analyze(result.Lines);
        PublishAgentCommand.Report(ctx.Log, report);
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
            throw new BuildException($"ncw-relay publish produced {report.Trim.Count()} IL/trim warning(s); the relay must publish with zero.");
        }

        if (args.Has("strict-warnings") && report.Other.Any())
        {
            throw new BuildException($"{report.Other.Count()} other warning(s) with --strict-warnings.");
        }

        var exe = Path.Combine(outDir, rid.RelayExecutable);
        if (!File.Exists(exe))
        {
            throw new BuildException($"Publish succeeded but {exe} does not exist.");
        }

        var moved = PublishAgentCommand.MoveSymbols(outDir, ctx.Repo.SymbolsDir(rid) + "-relay");
        var size = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        ctx.Log.Info($"ncw-relay: {new FileInfo(exe).Length / 1024.0 / 1024.0:F1} MiB, publish dir {size / 1024.0 / 1024.0:F1} MiB, {moved} symbol file(s) moved to {ctx.Repo.SymbolsDir(rid)}-relay");
        ctx.Log.Info($"published in {clock.Elapsed.TotalSeconds:F0}s, zero IL/trim warnings -> {outDir}");

        if (args.Has("skip-check"))
        {
            ctx.Log.Warn("--skip-check: the artifact was not started.");
            return 0;
        }

        if (rid.Os != ctx.Host.Os || rid.Arch != ctx.Host.Arch)
        {
            // 产物自检要运行它;和本机不同的平台没法启动(Rosetta 之类除外,不假设)。
            ctx.Log.Warn($"the artifact is for {rid} but this machine is {ctx.Host}: skipping the run check (run it on a {rid} machine).");
            return 0;
        }

        ctx.Log.Step($"self-check {exe}");
        var check = await _check(exe, ctx.Log, timeout, ct).ConfigureAwait(false);
        ctx.Log.Info($"PASS  ready in {check.ReadyAfter.TotalMilliseconds:F0} ms on port {check.Port}, /metrics ok, non-WebSocket -> {check.NonWebSocketStatus}, exit {check.ExitCode} after {check.ShutdownAfter.TotalMilliseconds:F0} ms");
        return 0;
    }
}
