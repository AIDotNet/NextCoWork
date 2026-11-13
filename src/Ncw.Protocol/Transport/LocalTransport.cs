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

    public static async IAsyncEnumerable<Stream> ListenAsync(string endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
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

        Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
        if (File.Exists(endpoint))
        {
            File.Delete(endpoint); // 上次崩溃留下的 socket 文件
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                yield return new NetworkStream(accepted, ownsSocket: true);
            }
        }
        finally
        {
            try
            {
                File.Delete(endpoint);
            }
            catch (IOException)
            {
            }
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
