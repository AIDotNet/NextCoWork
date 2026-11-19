using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ncw.Agent.Environments;

public sealed class LocalEnvironmentOptions
{
    public string DisplayName { get; init; } = "Local";

    /// <summary>覆盖默认 shell 的路径(默认按系统探测)。</summary>
    public string? Shell { get; init; }

    /// <summary>
    /// 每次启动子进程时额外注入的环境变量(例如跟随系统代理)。合并次序:继承 ← 注入(不覆盖用户自己已有的同名变量)← 命令显式给的。
    /// 「注入不越过用户自己的配置」这条不变式只在这里有一份实现(对应 2.x 的 <c>mergeChildEnv</c>)。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? ChildEnvironment { get; init; }

    /// <summary>PTY 由 Ncw.Agent.Terminal(forkpty / ConPTY)提供;没有注入时 <see cref="LocalEnvironment.OpenTerminalAsync"/> 抛 Unsupported。</summary>
    public Func<TerminalSpec, EnvironmentFacts, CancellationToken, Task<ITerminal>>? TerminalFactory { get; init; }

    /// <summary>终止进程树时,从礼貌请求(SIGTERM)到强杀(SIGKILL)之间的宽限期。</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// 设备自己的文件系统和进程。<b>全项目唯一允许直接使用 <c>System.IO</c> / <c>Process</c> 的执行环境实现</b>(SSH 环境只读自己的密钥和配置)。
/// 工具、Hooks、插件宿主一律拿 <see cref="IExecutionEnvironment"/>,不要直接 new 它。
/// </summary>
public sealed class LocalEnvironment : IExecutionEnvironment
{
    private readonly LocalEnvironmentOptions _options;
    private readonly LocalFs _fs = new();
    private EnvironmentFacts _facts;

    public LocalEnvironment(string deviceId, LocalEnvironmentOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _options = options ?? new LocalEnvironmentOptions();
        Id = EnvironmentId.Local(deviceId);
        _facts = DetectFacts(_options.Shell);
        Paths = new PathGuard(_fs, LocalFs.Style);
    }

    /// <summary>
    /// 把这台机器上的原生路径(<c>Path.Combine</c>、<c>Directory.CreateDirectory(...).FullName</c> 得到的)包成本机风格的 <see cref="EnvPath"/>:
    /// Windows 上是 <see cref="PathStyle.Windows"/>,其它是 <see cref="PathStyle.Posix"/>。不要对原生路径用 <see cref="EnvPath.Posix"/>——
    /// 在 Windows 上本机文件系统会以 <see cref="EnvironmentErrorCode.InvalidPath"/> 拒绝它。
    /// </summary>
    public static EnvPath PathOf(string nativePath) => EnvPath.Parse(nativePath, LocalFs.Style);

    public EnvironmentId Id { get; }

    public EnvironmentKind Kind => EnvironmentKind.Local;

    public string DisplayName => _options.DisplayName;

    public int Generation => 0;

    public EnvironmentState State => EnvironmentState.Ready;

    // 本机永远 Ready,不会有状态变化
    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public EnvironmentFacts Facts => _facts;

    public PathGuard Paths { get; }

    public IEnvironmentFs Fs => _fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default)
    {
        _facts = await ToolProbe.ProbeAsync(this, _facts, ct).ConfigureAwait(false);
        return _facts;
    }

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            spec.Validate();
            return Task.FromResult<IEnvProcess>(Start(spec));
        }
        catch (Exception e)
        {
            return Task.FromException<IEnvProcess>(e);
        }
    }

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) =>
        _options.TerminalFactory is { } factory
            ? factory(spec, _facts, ct)
            : Task.FromException<ITerminal>(new EnvironmentException(EnvironmentErrorCode.Unsupported, "terminal"));

    public async Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            socket.Dispose();
            throw new EnvironmentException(EnvironmentErrorCode.ConnectionFailed, $"{host}:{port}", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default)
    {
        // 本机的回环端口本来就在设备上:不需要映射
        if (host is "localhost" or "127.0.0.1" or "::1")
        {
            return Task.FromResult<IPortForward>(new IdentityForward(port));
        }

        return Task.FromResult<IPortForward>(new TcpRelayForward(this, host, port));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private LocalProcess Start(CommandSpec spec)
    {
        ProcessStartInfo info;
        if (spec.ShellCommand is { } command)
        {
            var invocation = ShellCommands.Build(_facts.Shell, _facts.ShellDialect, command);
            info = new ProcessStartInfo(invocation.FileName);
            if (invocation.VerbatimArguments is { } verbatim)
            {
                info.Arguments = verbatim;
            }
            else
            {
                foreach (var arg in invocation.Args)
                {
                    info.ArgumentList.Add(arg);
                }
            }
        }
        else
        {
            info = new ProcessStartInfo(spec.Program!);
            foreach (var arg in spec.Args)
            {
                info.ArgumentList.Add(arg);
            }
        }

        if (spec.WorkingDirectory is { } cwd)
        {
            var native = LocalFs.Native(cwd);
            if (!Directory.Exists(native))
            {
                throw new EnvironmentException(File.Exists(native) ? EnvironmentErrorCode.NotDirectory : EnvironmentErrorCode.NotFound, native);
            }

            info.WorkingDirectory = native;
        }

        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        // 合并次序:继承(psi.Environment 已是当前进程环境)← 注入(不覆盖已有)← 命令显式给的
        if (_options.ChildEnvironment?.Invoke() is { } injected)
        {
            foreach (var (name, value) in injected)
            {
                if (!info.Environment.ContainsKey(name))
                {
                    info.Environment[name] = value;
                }
            }
        }

        if (spec.Environment is { } explicitEnvironment)
        {
            foreach (var (name, value) in explicitEnvironment)
            {
                info.Environment[name] = value;
            }
        }

        return LocalProcess.Start(Id, info, spec.Stdin, _options.KillGrace);
    }

    private static EnvironmentFacts DetectFacts(string? shellOverride) => new()
    {
        Os = OperatingSystem.IsWindows() ? EnvironmentOs.Windows : OperatingSystem.IsMacOS() ? EnvironmentOs.MacOS : EnvironmentOs.Linux,
        OsVersion = Environment.OSVersion.VersionString,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        Shell = shellOverride ?? DetectShell(),
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Hostname = Environment.MachineName,
        Username = Environment.UserName,
    };

    /// <summary>
    /// 默认 shell。POSIX:<c>$SHELL</c>(只认常见的 POSIX 兼容 shell,fish/nu 的语法不兼容,退到 bash 或 sh);
    /// Windows:pwsh、powershell,最后 <c>ComSpec</c>。
    /// </summary>
    internal static string DetectShell()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
            {
                if (FindOnPath(candidate) is { } found)
                {
                    return found;
                }
            }

            return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell) && File.Exists(shell)
            && Path.GetFileName(shell) is "bash" or "zsh" or "sh" or "dash" or "ksh" or "ash")
        {
            return shell;
        }

        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class IdentityForward(int port) : IPortForward
    {
        public string LocalHost => "127.0.0.1";

        public int LocalPort => port;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>非回环目标:在设备回环地址上开一个监听,把每条连接转发过去。</summary>
    private sealed class TcpRelayForward : IPortForward
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public TcpRelayForward(LocalEnvironment environment, string host, int port)
        {
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop(environment, host, port);
        }

        public string LocalHost => "127.0.0.1";

        public int LocalPort { get; }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }

        private async Task AcceptLoop(LocalEnvironment environment, string host, int port)
        {
            try
            {
                for (;;)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _ = Relay(environment, client, host, port);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 映射关闭
            }
        }

        private async Task Relay(LocalEnvironment environment, TcpClient client, string host, int port)
        {
            using (client)
            {
                try
                {
                    var upstream = await environment.OpenTcpAsync(host, port, _stop.Token).ConfigureAwait(false);
                    await using (upstream.ConfigureAwait(false))
                    {
                        var local = client.GetStream();
                        var a = local.CopyToAsync(upstream, _stop.Token);
                        var b = upstream.CopyToAsync(local, _stop.Token);
                        await Task.WhenAny(a, b).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException or EnvironmentException or OperationCanceledException or ObjectDisposedException)
                {
                    // 这条连接结束
                }
            }
        }
    }
}
