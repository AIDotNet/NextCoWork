using System.Reflection;
using System.Text.Json;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host;

/// <summary>
/// Agent 服务进程:装配方法表与主题,在本机承载上接受连接。一个进程可以同时服务多个控制端。
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentOptions _options;
    private readonly MethodRouter _router = new();
    private readonly TopicHub _topics = new();
    private readonly DeviceIdentityFile _identity;
    private readonly DataGate _data;
    private ProjectRegistry? _projects;

    public AgentHost(
        AgentOptions options,
        TimeProvider? clock = null,
        Ncw.Agent.Storage.Takeover.TakeoverOptions? takeover = null,
        Func<Ncw.Agent.Storage.Credentials.ILegacySecretDecryptor>? legacySecrets = null)
    {
        _options = options;
        var time = clock ?? TimeProvider.System;
        Directory.CreateDirectory(options.DataRoot);
        _identity = LoadIdentity(options.DataRoot, time);

        _data = new DataGate(options.DataRoot, _identity.DeviceId, time, takeover, legacySecrets);

        // 权威广播:首帧和后续用同一个生成函数(AGENTS.md §3 规则 5)。主题先注册,数据就绪时才能发布。
        _topics.Register(DeviceMethods.DeviceTopic, DeviceState);
        _topics.Register(DeviceMethods.DataTopic, () => _data.Status);
        _data.Ready += database =>
        {
            var projects = new ProjectRegistry(new WorkspaceRepository(database, time));
            projects.Changed += list =>
                _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, list, PayloadShapes.GetShape<ProjectsListResult>());
            Volatile.Write(ref _projects, projects);
            // 数据晚于连接就绪(接管完成)时,已经连着的控制端从这里拿到项目列表。
            _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, projects.List(), PayloadShapes.GetShape<ProjectsListResult>());
        };
        _data.Changed += status =>
            _topics.Publish(DeviceMethods.DataTopic.Name, DataEvents.Changed, status, PayloadShapes.GetShape<DataStatus>());
        _data.TryPrepareFast();

        _router.Handle(DeviceMethods.Info, (_, _, _) => ValueTask.FromResult(new DeviceInfoResult(
            _identity.DeviceId, _identity.Name, options.Mode == AgentMode.Cloud ? "cloud" : "local",
            Version, Environment.OSVersion.VersionString, options.DataRoot)));
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(Projects().List()));
        _router.Handle(DeviceMethods.ProjectsAdd, (p, _, _) => ValueTask.FromResult(Projects().Add(p.Path, p.Name)));
        _router.Handle(DeviceMethods.ProjectsRemove, (p, _, _) =>
        {
            Projects().Remove(p.WorkspaceId);
            return ValueTask.FromResult(Unit.Value);
        });
        _router.Handle(DeviceMethods.DataStatus, (_, _, _) => ValueTask.FromResult(_data.Status));
        _router.Handle(DeviceMethods.DataRetry, async (_, _, ct) => await _data.PrepareAsync(ct).ConfigureAwait(false));

    }

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string DeviceId => _identity.DeviceId;

    /// <summary>当前的数据状态(测试和启动日志用)。</summary>
    public DataStatus Data => _data.Status;

    public void Dispose() => _data.Dispose();

    /// <summary>运行直到取消或父进程退出。<paramref name="onListening"/> 在端点真正可以连接之后调用(用来报告 ready)。</summary>
    public async Task RunAsync(CancellationToken ct, Action? onListening = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.ParentPid is { } parent)
        {
            _ = WatchParentAsync(parent, lifetime);
        }

        var server = new ServerOptions(_identity.DeviceId, Version, Transports.Local, Authenticate);
        var sessions = new List<Task>();
        using var listener = LocalTransport.Listen(_options.Endpoint);
        onListening?.Invoke();
        if (_data.Status.State != DataStates.Ready)
        {
            // 接管 2.x 数据可能要几十秒:在后台进行,控制端通过 data 主题看进度。
            _ = _data.PrepareAsync(lifetime.Token);
        }

        try
        {
            await foreach (var stream in listener.AcceptAsync(lifetime.Token).ConfigureAwait(false))
            {
                sessions.Add(new ServerSession(new FrameStream(stream), _router, _topics, server).RunAsync(lifetime.Token));
                sessions.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(sessions).ConfigureAwait(false);
            _data.Dispose();
        }
    }

    /// <summary>本机通道:核对一次性本机令牌(常数时间比较),通过即 owner。</summary>
    private Role? Authenticate(HelloFrame hello) =>
        hello.LocalToken is { } token
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(_options.LocalToken))
            ? Role.Owner
            : null;

    private DeviceTopicState DeviceState() => new([], Volatile.Read(ref _projects)?.Revision ?? 0);

    /// <summary>依赖数据的方法:数据没就绪(正在接管或被挡住)时返回可重试的 unavailable。</summary>
    private ProjectRegistry Projects() =>
        Volatile.Read(ref _projects) ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);

    private static DeviceIdentityFile LoadIdentity(string dataRoot, TimeProvider clock)
    {
        var path = Path.Combine(dataRoot, "device.json");
        if (AtomicFile.Read(path, AgentJsonContext.Default.DeviceIdentityFile) is { } existing)
        {
            return existing;
        }

        var created = new DeviceIdentityFile($"dev_{Guid.NewGuid():N}"[..20], Environment.MachineName, clock.GetUtcNow().ToUnixTimeMilliseconds());
        AtomicFile.Write(path, s => JsonSerializer.Serialize(s, created, AgentJsonContext.Default.DeviceIdentityFile));
        return created;
    }

    private static async Task WatchParentAsync(int pid, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(pid);
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // 父进程已经不在了
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
    }
}
