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
    private readonly ProjectRegistry _projects;
    private readonly DeviceIdentityFile _identity;
    private readonly AgentDatabase _database;

    public AgentHost(AgentOptions options, TimeProvider? clock = null)
    {
        _options = options;
        var time = clock ?? TimeProvider.System;
        Directory.CreateDirectory(options.DataRoot);
        _identity = LoadIdentity(options.DataRoot, time);
        _database = AgentDatabase.Open(options.DataRoot);
        _projects = new ProjectRegistry(new WorkspaceRepository(_database, time));

        _router.Handle(DeviceMethods.Info, (_, _, _) => ValueTask.FromResult(new DeviceInfoResult(
            _identity.DeviceId, _identity.Name, options.Mode == AgentMode.Cloud ? "cloud" : "local",
            Version, Environment.OSVersion.VersionString, options.DataRoot)));
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(_projects.List()));
        _router.Handle(DeviceMethods.ProjectsAdd, (p, _, _) => ValueTask.FromResult(_projects.Add(p.Path, p.Name)));
        _router.Handle(DeviceMethods.ProjectsRemove, (p, _, _) =>
        {
            _projects.Remove(p.WorkspaceId);
            return ValueTask.FromResult(Unit.Value);
        });

        // device 主题 = 权威广播:首帧和后续用同一个生成函数(AGENTS.md §3 规则 5)。
        _topics.Register(DeviceMethods.DeviceTopic, DeviceState);
        _projects.Changed += list =>
            _topics.Publish(DeviceMethods.DeviceTopic.Name, DeviceEvents.ProjectsChanged, list, PayloadShapes.GetShape<ProjectsListResult>());
    }

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string DeviceId => _identity.DeviceId;

    public void Dispose() => _database.Dispose();

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
            _database.Dispose();
        }
    }

    /// <summary>本机通道:核对一次性本机令牌(常数时间比较),通过即 owner。</summary>
    private Role? Authenticate(HelloFrame hello) =>
        hello.LocalToken is { } token
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(_options.LocalToken))
            ? Role.Owner
            : null;

    private DeviceTopicState DeviceState() => new([], _projects.Revision);

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
