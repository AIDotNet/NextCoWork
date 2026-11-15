using System.Diagnostics;
using System.Globalization;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.MobileSmoke;

/// <summary>隔离的桌面一侧:真实 ncw-agent 进程(临时数据根)+ 本机通道上的 owner 连接 + 假上游。</summary>
internal sealed class Rig : IAsyncDisposable
{
    private const string Token = "ncw-mobile-smoke-token";
    private Process? _agent;
    private DeviceClient? _owner;
    private IAsyncDisposable? _remoteSubscription;
    private TrustedControllerDto[] _controllers = [];

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "ncw-mobile-smoke-" + Guid.NewGuid().ToString("N")[..8]);

    public string ProjectDir { get; } = Path.Combine(Path.GetTempPath(), "ncw-mobile-smoke-proj-" + Guid.NewGuid().ToString("N")[..8]);

    public FakeUpstream Upstream { get; } = new();

    public string WorkspaceId { get; private set; } = "";

    public int RemotePort { get; private set; }

    public TrustedControllerDto[] Controllers => _controllers;

    public static string Log(string text)
    {
        var line = $"SMOKE {DateTime.Now:HH:mm:ss.fff} {text}";
        Console.WriteLine(line);
        return line;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ProjectDir);
        var dll = Environment.GetEnvironmentVariable("NCW_AGENT_DLL") ?? LocateAgentDll();
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--mode");
        info.ArgumentList.Add("local");
        info.ArgumentList.Add("--data-root");
        info.ArgumentList.Add(DataRoot);
        info.ArgumentList.Add("--lifeline");
        info.ArgumentList.Add("stdin");
        info.Environment["NCW_LOCAL_TOKEN"] = Token;
        _agent = Process.Start(info) ?? throw new InvalidOperationException("cannot start ncw-agent");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agent.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                Log("agent: " + line);
                if (line.StartsWith("ncw-agent ready", StringComparison.Ordinal))
                {
                    ready.TrySetResult();
                }
            }
        };
        _agent.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                Log("agent-err: " + line);
            }
        };
        _agent.BeginOutputReadLine();
        _agent.BeginErrorReadLine();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);

        var endpoint = LocalTransport.DefaultEndpoint(DataRoot);
        var stream = await LocalTransport.ConnectAsync(endpoint, ct);
        _owner = await DeviceClient.ConnectAsync(stream, new ClientHello("mobile-smoke-owner", "3.0.0", LocalToken: Token), ct);
        _remoteSubscription = await _owner.SubscribeAsync(DeviceMethods.RemoteTopic, (state, _) => _controllers = state.Controllers,
            evt => _controllers = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RemoteState>()).Controllers, ct);

        // 远程开关:只放行局域网地址(默认;模拟器经 NAT 过来的来源是回环 / 10.0.2.x,都在放行范围内)
        var status = await _owner.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(Port: FreePort(), LanOnly: true), ct);
        status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true), ct, idem: Guid.NewGuid().ToString("N"));
        RemotePort = status.Port ?? throw new InvalidOperationException("remote did not start: " + status.Error);
        Log($"remote enabled port={RemotePort} lanOnly={status.LanOnly} addresses=[{string.Join(", ", status.Addresses)}]");

        // 供应商 + 假上游 + 项目
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("smoke", "Smoke upstream", "openai-chat", Upstream.BaseUrl), ct, idem: Guid.NewGuid().ToString("N"));
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("smoke", "sk-smoke"), ct, idem: Guid.NewGuid().ToString("N"));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("smoke", [FakeUpstream.Model]), ct, idem: Guid.NewGuid().ToString("N"));
        var project = await _owner.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(ProjectDir, "smoke-project"), ct, idem: Guid.NewGuid().ToString("N"));
        WorkspaceId = project.WorkspaceId;
        Log($"project={WorkspaceId} dir={ProjectDir} upstream={Upstream.BaseUrl}");
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string LocateAgentDll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ncw.Agent", "bin", "Debug", "net10.0", "ncw-agent.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("ncw-agent.dll not found; build src/Ncw.Agent first or set NCW_AGENT_DLL");
    }

    /// <summary>新二维码内容。地址改写成模拟器能到的宿主地址(10.0.2.2:端口);指纹钉扎和一次性密钥不变,所以安全性和原二维码一样。</summary>
    public async Task<string> CreateQrAsync(CancellationToken ct, bool emulatorAddress = true)
    {
        var qr = await _owner!.CallAsync(DeviceMethods.PairCreateQr, Unit.Value, ct, idem: Guid.NewGuid().ToString("N"));
        if (!emulatorAddress)
        {
            return qr.Uri;
        }

        var uri = new Uri(qr.Uri);
        var parts = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i][0] == "a")
            {
                parts[i][1] = Uri.EscapeDataString(string.Create(CultureInfo.InvariantCulture, $"10.0.2.2:{RemotePort}"));
            }
        }

        return "nextcowork://pair?" + string.Join('&', parts.Select(p => p[0] + "=" + p[1]));
    }

    public async Task<string> StartRunAsync(CancellationToken ct)
    {
        var session = await _owner!.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(WorkspaceId, Title: "Mobile smoke", Model: FakeUpstream.Model), ct, idem: Guid.NewGuid().ToString("N"));
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        var result = await _owner.CallAsync(ConversationMethods.RunsStart, new RunsStartParams(runId, session.Id, "msg-" + Guid.NewGuid().ToString("N")[..8],
            [new InputPartDto(InputPartKinds.Text, "write the marker file")], new SendOptionsDto(FakeUpstream.Model, PermissionMode: "ask")), ct, idem: Guid.NewGuid().ToString("N"));
        Log($"run {runId} outcome={result.Outcome} session={session.Id}");
        return session.Id;
    }

    public async Task<InteractionDto[]> PendingAsync(CancellationToken ct) => (await _owner!.CallAsync(ConversationMethods.InteractionsList, Unit.Value, ct)).Pending;

    public bool MarkerWritten => File.Exists(Path.Combine(ProjectDir, "mobile-marker.txt"));

    public async ValueTask DisposeAsync()
    {
        if (_remoteSubscription is not null)
        {
            await _remoteSubscription.DisposeAsync();
        }

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }

        if (_agent is not null)
        {
            try
            {
                _agent.StandardInput.Close(); // --lifeline stdin:关 stdin 就优雅退出
                if (!_agent.WaitForExit(8000))
                {
                    _agent.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }
        }

        Upstream.Dispose();
        foreach (var dir in new[] { DataRoot, ProjectDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
