using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Stores;
using Ncw.Client.Updates;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Desktop.Updates;

/// <summary>
/// <b>仅开发:</b>无界面的自更新演练(docs/design/04 §7)。<b>不是面向用户的入口</b>:没有命令行参数,只认环境变量 <c>NCW_DEV_UPDATE_REHEARSAL=&lt;工作目录&gt;</c>
/// (用户环境里不会有它;开发在终端里临时设)。作用:在<b>真的装出来的</b>应用里,用它自己的 <c>UpdateManager</c>、真实的 feed 与真实的 ncw-agent 走一遍
/// 「检查 → 下载 → 重启并更新 → 新版本起来后验证」,不需要有人点界面(macOS 上自动点 GUI 不方便)。
/// <list type="number">
/// <item><b>阶段 1</b>(工作目录里没有 <c>state.json</c>):起 sidecar、握手(版本 = 本应用版本)、经协议写一条设置、读回;
/// <c>NCW_DEV_UPDATE_MODE=check</c> 只检查并报告(负面路径:签名被篡改、feed 回滚),否则下载并交给更新器(同 <see cref="UpdateService.RestartAndApplyAsync"/>:先让更新器就位、再收尾 sidecar、再退出)。</item>
/// <item><b>阶段 2</b>(更新器重启了新版本之后,<c>state.json</c> 在):起 sidecar、握手(版本 = 清单宣布的新版本)、读回阶段 1 写的那条数据,写 <c>report.json</c>。</item>
/// </list>
/// 日志在 <c>rehearsal.log</c>(带耗时),结论在 <c>report.json</c> / <c>check-result.json</c>。feed 用 <c>NCW_UPDATE_FEED</c>,数据根用 <c>NCW_AGENT_DATA_ROOT</c>(阶段 2 以 state 里记的为准)。
/// </summary>
public static class DevUpdateRehearsal
{
    public const string DirEnv = "NCW_DEV_UPDATE_REHEARSAL";

    public const string ModeEnv = "NCW_DEV_UPDATE_MODE";

    public static bool IsRequested(Func<string, string?> env) => env(DirEnv) is { Length: > 0 };

    public static async Task<int> RunAsync(Func<string, string?> env, TextWriter output)
    {
        var dir = Path.GetFullPath(env(DirEnv)!);
        Directory.CreateDirectory(dir);
        var log = new Log(Path.Combine(dir, "rehearsal.log"), output);
        try
        {
            var statePath = Path.Combine(dir, "state.json");
            var state = File.Exists(statePath) ? JsonSerializer.Deserialize(File.ReadAllText(statePath), RehearsalJson.Default.RehearsalState) : null;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            return state is { Phase: 1 }
                ? await PhaseTwoAsync(dir, statePath, state, log, cts.Token).ConfigureAwait(false)
                : await PhaseOneAsync(env, dir, statePath, log, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.Write($"FAILED: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    // ───────────────────────── 阶段 1 ─────────────────────────

    private static async Task<int> PhaseOneAsync(Func<string, string?> env, string dir, string statePath, Log log, CancellationToken ct)
    {
        var current = new VelopackUpdateEngine(UpdateConfiguration.FromEnvironment(env), log: log.Write);
        log.Write($"phase 1: running {current.CurrentVersion}, installed={current.IsInstalled}, platform {PlatformId.Current}");
        if (!current.IsInstalled)
        {
            log.Write("FAILED: this is not a Velopack-installed app; the rehearsal needs the installed .app");
            return 1;
        }

        var dataRoot = LocalAgentSidecar.DefaultDataRoot;
        var marker = "rehearsal-" + Guid.NewGuid().ToString("N")[..8];
        var sidecar = new LocalAgentSidecar(dataRoot);
        var clock = Stopwatch.StartNew();
        sidecar.Start();
        await sidecar.WaitReadyAsync(WithTimeout(ct, 40)).ConfigureAwait(false);
        var before = await Smoke(sidecar, current.CurrentVersion, writeMarker: marker, ct).ConfigureAwait(false);
        log.Write($"sidecar ready and handshake OK in {clock.ElapsedMilliseconds} ms: agent {before.AgentVersion}; wrote marker '{marker}' and read it back");

        var stores = new ClientStores();
        var host = new RehearsalHost(sidecar, dataRoot, log);
        var service = new UpdateService(stores, current, host, new MemoryUpdateMetaStore(), null, null, new UpdateOptions { AutoCheck = false }, log.Write);
        await service.InitializeAsync(ct).ConfigureAwait(false);
        clock.Restart();
        await service.CheckAsync(ct).ConfigureAwait(false);
        var state = stores.Updates.State;
        log.Write($"check: {state.Phase} release={state.Release?.Version ?? "-"} failure={state.Failure?.ToString() ?? "-"} ({clock.ElapsedMilliseconds} ms)");
        await File.WriteAllTextAsync(Path.Combine(dir, "check-result.json"),
            JsonSerializer.Serialize(new CheckResult(state.Phase.ToString(), state.Release?.Version, state.Failure?.ToString(), state.CurrentVersion), RehearsalJson.Default.CheckResult), ct).ConfigureAwait(false);

        if (string.Equals(env(ModeEnv), "check", StringComparison.OrdinalIgnoreCase) || state.Phase != UpdatePhase.Available)
        {
            await sidecar.StopForUpdateAsync().ConfigureAwait(false);
            return state.Phase is UpdatePhase.Available or UpdatePhase.UpToDate or UpdatePhase.Failed ? 0 : 1;
        }

        // 写下阶段 1 的事实,再交给更新器:新版本起来后由阶段 2 对照
        var s1 = new RehearsalState(1, current.CurrentVersion, state.Release!.Version, marker, dataRoot, []);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(s1, RehearsalJson.Default.RehearsalState), ct).ConfigureAwait(false);
        host.OnPrepared = files => File.WriteAllText(statePath, JsonSerializer.Serialize(s1 with { DataFiles = files }, RehearsalJson.Default.RehearsalState));

        clock.Restart();
        await service.DownloadAsync(ct).ConfigureAwait(false);
        log.Write($"download: {stores.Updates.State.Phase} ({clock.ElapsedMilliseconds} ms) failure={stores.Updates.State.Failure?.ToString() ?? "-"}");
        if (stores.Updates.State.Phase != UpdatePhase.Ready)
        {
            File.Delete(statePath);
            await sidecar.StopForUpdateAsync().ConfigureAwait(false);
            return 1;
        }

        clock.Restart();
        var started = await service.RestartAndApplyAsync(ct).ConfigureAwait(false);
        log.Write($"restart-and-apply handed over={started} ({clock.ElapsedMilliseconds} ms incl. stopping the sidecar); exiting so the updater can replace the app");
        if (!started)
        {
            File.Delete(statePath);
            return 1;
        }

        return 0; // 进程退出 → Velopack 的更新器接手:替换 .app、重新拉起
    }

    // ───────────────────────── 阶段 2 ─────────────────────────

    private static async Task<int> PhaseTwoAsync(string dir, string statePath, RehearsalState state, Log log, CancellationToken ct)
    {
        var engine = new VelopackUpdateEngine(new UpdateConfiguration("", Ncw.Client.Updates.ReleaseKeyRing.Empty, PlatformId.Current));
        var now = engine.CurrentVersion;
        log.Write($"phase 2: running {now} (was {state.FromVersion}, expected {state.ToVersion})");
        var problems = new List<string>();
        if (now != state.ToVersion)
        {
            problems.Add($"the app reports {now}, expected {state.ToVersion}");
        }

        var files = state.DataFiles.ToDictionary(f => f.Name, f => f.Size);
        var clock = Stopwatch.StartNew();
        var sidecar = new LocalAgentSidecar(state.DataRoot);
        sidecar.Start();
        await sidecar.WaitReadyAsync(WithTimeout(ct, 40)).ConfigureAwait(false);
        var ready = clock.ElapsedMilliseconds;
        var after = await Smoke(sidecar, now, writeMarker: null, ct).ConfigureAwait(false);
        log.Write($"sidecar re-launched by the new app: ready in {ready} ms, agent version {after.AgentVersion}, data survived: marker read back = '{after.Marker}'");
        if (after.AgentVersion != state.ToVersion)
        {
            problems.Add($"the agent reports {after.AgentVersion}, expected {state.ToVersion}");
        }

        if (after.Marker != state.Marker)
        {
            problems.Add($"the marker written before the update reads back as '{after.Marker}', expected '{state.Marker}'");
        }

        await sidecar.StopForUpdateAsync().ConfigureAwait(false);
        var nowFiles = DataFiles(state.DataRoot);
        foreach (var (name, size) in files)
        {
            var match = nowFiles.FirstOrDefault(f => f.Name == name);
            if (match is null)
            {
                problems.Add($"data file {name} is gone");
            }
            else if (size > 0 && match.Size == 0)
            {
                problems.Add($"data file {name} was emptied");
            }
        }

        var report = new Report(problems.Count == 0, state.FromVersion, now, after.AgentVersion, state.Marker, after.Marker, ready, [.. problems]);
        await File.WriteAllTextAsync(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, RehearsalJson.Default.Report), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state with { Phase = 2 }, RehearsalJson.Default.RehearsalState), ct).ConfigureAwait(false);
        log.Write(report.Ok ? "phase 2: PASS" : "phase 2: FAIL: " + string.Join("; ", problems));
        return report.Ok ? 0 : 1;
    }

    // ───────────────────────── 共用 ─────────────────────────

    private sealed record SmokeOutcome(string AgentVersion, string? Marker);

    /// <summary>本机通道 + 本机令牌握手,版本必须等于 <paramref name="expectedVersion"/>;可选写一条设置,再读回(设置存在 Agent 的库里)。</summary>
    private static async Task<SmokeOutcome> Smoke(LocalAgentSidecar sidecar, string expectedVersion, string? writeMarker, CancellationToken ct)
    {
        var endpoint = LocalTransport.DefaultEndpoint(sidecar.DataRoot);
        var stream = await LocalTransport.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello("ncw-dev-update-rehearsal", expectedVersion, LocalToken: sidecar.Token), ct).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            var info = await client.CallAsync(DeviceMethods.Info, Unit.Value, ct).ConfigureAwait(false);
            if (info.AgentVersion != expectedVersion)
            {
                throw new InvalidOperationException($"the agent reports version {info.AgentVersion}; the app is {expectedVersion}");
            }

            if (writeMarker is not null)
            {
                await client.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Personalization: new PersonalizationPatchDto(Name: writeMarker))),
                    ct, idem: "rehearsal:" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
            }

            var settings = await client.CallAsync(SettingsMethods.Get, Unit.Value, ct).ConfigureAwait(false);
            var marker = settings.Settings.Personalization.Name;
            if (writeMarker is not null && marker != writeMarker)
            {
                throw new InvalidOperationException($"wrote '{writeMarker}' but read back '{marker}'");
            }

            return new SmokeOutcome(info.AgentVersion, marker);
        }
    }

    private static CancellationToken WithTimeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts.Token;
    }

    /// <summary>数据根里的文件(不含 <c>run/</c> 与 <c>logs/</c>:每次启动都会变)。库文件 = 用户数据。</summary>
    internal static DataFile[] DataFiles(string dataRoot) =>
        !Directory.Exists(dataRoot)
            ? []
            : [.. Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories)
                .Select(f => (Rel: Path.GetRelativePath(dataRoot, f).Replace('\\', '/'), Info: new FileInfo(f)))
                .Where(f => !f.Rel.StartsWith("run/", StringComparison.Ordinal) && !f.Rel.StartsWith("logs/", StringComparison.Ordinal))
                .Select(f => new DataFile(f.Rel, f.Info.Length))
                .OrderBy(f => f.Name, StringComparer.Ordinal)];

    private sealed class RehearsalHost(LocalAgentSidecar sidecar, string dataRoot, Log log) : IUpdateHost
    {
        public Action<DataFile[]>? OnPrepared { get; set; }

        public async Task PrepareForRestartAsync(CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            await sidecar.StopForUpdateAsync().ConfigureAwait(false);
            var files = DataFiles(dataRoot);
            OnPrepared?.Invoke(files);
            log.Write($"sidecar stopped gracefully in {clock.ElapsedMilliseconds} ms; data files before the update: {string.Join(", ", files.Select(f => $"{f.Name} ({f.Size} B)"))}");
        }

        public void RequestExit()
        {
            // 进程退出由 RunAsync 返回完成(不需要 Avalonia)
        }
    }

    private sealed class Log(string path, TextWriter output)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Write(string line)
        {
            var text = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{_clock.Elapsed.TotalSeconds,6:F1}s] {line}";
            output.WriteLine(text);
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }
}

public sealed record DataFile(string Name, long Size);

public sealed record RehearsalState(int Phase, string FromVersion, string ToVersion, string Marker, string DataRoot, DataFile[] DataFiles);

public sealed record CheckResult(string Phase, string? ReleaseVersion, string? Failure, string CurrentVersion);

public sealed record Report(bool Ok, string FromVersion, string ToVersion, string AgentVersion, string Marker, string? MarkerAfter, long SidecarReadyMs, string[] Problems);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RehearsalState))]
[JsonSerializable(typeof(CheckResult))]
[JsonSerializable(typeof(Report))]
internal sealed partial class RehearsalJson : JsonSerializerContext;
