using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Ncw.Protocol.Transport;

/// <summary>
/// 本机承载(docs/design/09 §5):macOS / Linux 用 Unix socket(文件权限 0600),Windows 用命名管道(只允许当前用户)。
/// 上层只拿到一个双向 <see cref="Stream"/>,与以后的局域网 TCP、服务器中继一样。
/// </summary>
public static class LocalTransport
{
    /// <summary>Unix socket 路径的安全长度(macOS 上限 104 字节,Linux 108,留出余量)。</summary>
    private const int MaxSocketPathBytes = 100;

    /// <summary>
    /// 本机端点:Unix 上是 socket 文件路径,Windows 上是管道名。Agent 和桌面端用同一个数据根算出同一个端点。
    /// 数据根太深时 socket 放到只有当前用户能访问的短目录里 —— hello 里带着本机令牌,
    /// socket 绝不能放在别人能抢先占用的地方。
    /// </summary>
    public static string DefaultEndpoint(string dataRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"ncw-agent-{Environment.UserName}-{Fnv1a(dataRoot):x8}";
        }

        var preferred = Path.Combine(dataRoot, "run", "agent.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(preferred) <= MaxSocketPathBytes)
        {
            return preferred;
        }

        return Path.Combine(PrivateShortDirectory(), $"ncw-{Fnv1a(dataRoot):x8}.sock");
    }

    /// <summary>
    /// 当前用户私有的短目录:macOS 的 $TMPDIR 本来就是每用户 0700;Linux 优先 $XDG_RUNTIME_DIR,
    /// 否则 /tmp/ncw-&lt;uid&gt;,建成 0700 并核对属主,不是自己的就拒绝。
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string PrivateShortDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.GetTempPath();
        }

        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime))
        {
            return runtime;
        }

        var dir = Path.Combine("/tmp", $"ncw-{Environment.UserName}");
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var mode = File.GetUnixFileMode(dir);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException($"{dir} is accessible by other users; refusing to place the agent socket there");
        }

        return dir;
    }

    /// <summary>确定性的哈希(string.GetHashCode 每个进程不同,Agent 和桌面端会算出不同的管道名)。</summary>
    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    /// <summary>
    /// 立刻绑定监听(返回时端点已经可以连接),之后用 <see cref="LocalListener.AcceptAsync"/> 接受连接。
    /// 绑定和接受分开,调用方才能在「真的能连」之后再报告 ready。
    /// </summary>
    public static LocalListener Listen(string endpoint) => new(endpoint);

    /// <summary>绑定并接受连接的简写(测试用)。</summary>
    public static async IAsyncEnumerable<Stream> ListenAsync(string endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        using var listener = Listen(endpoint);
        await foreach (var stream in listener.AcceptAsync(ct).ConfigureAwait(false))
        {
            yield return stream;
        }
    }

    public static async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(ct).ConfigureAwait(false);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>已经绑定的本机监听器。释放时删除 socket 文件。</summary>
public sealed class LocalListener : IDisposable
{
    private readonly string _endpoint;
    private readonly Socket? _socket;

    internal LocalListener(string endpoint)
    {
        _endpoint = endpoint;
        if (OperatingSystem.IsWindows())
        {
            return; // 命名管道在 AcceptAsync 里逐个创建
        }

        Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
        if (File.Exists(endpoint))
        {
            File.Delete(endpoint); // 上次崩溃留下的 socket 文件
        }

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _socket.Bind(new UnixDomainSocketEndPoint(endpoint));
            File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _socket.Listen(16);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<Stream> AcceptAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(_endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    yield break;
                }

                yield return pipe;
            }

            yield break;
        }

        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return new NetworkStream(accepted, ownsSocket: true);
        }
    }

    public void Dispose()
    {
        if (_socket is null)
        {
            return;
        }

        _socket.Dispose();
        try
        {
            File.Delete(_endpoint);
        }
        catch (IOException)
        {
        }
    }
}
