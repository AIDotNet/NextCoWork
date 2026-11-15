using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>
/// ssh-agent 协议客户端(draft-miller-ssh-agent):列出身份、签名请求。
/// 承载是一条双向字节流:Unix 上是 <c>SSH_AUTH_SOCK</c> 指向的 Unix 域套接字,Windows 上是 OpenSSH 的命名管道 <c>\\.\pipe\openssh-ssh-agent</c>。
/// <b>每次请求新开一条连接</b>(协议本身是一问一答):不持有长连接,所以 agent 重启、用户换了 <c>SSH_AUTH_SOCK</c> 都不会留下失效的句柄,
/// 也没有跨线程共享一条流的并发问题。私钥永远不离开 agent:这里只拿到公钥 blob 和签名。
/// </summary>
public sealed class SshAgentClient : ISshAgent
{
    /// <summary>OpenSSH 在 Windows 上的 agent 命名管道。</summary>
    public const string WindowsPipeName = "openssh-ssh-agent";

    // 协议常量
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>消息体上限(OpenSSH agent 自己的上限是 256 KiB):更大的一定是对端有问题,不分配。</summary>
    private const int MaxMessage = 256 * 1024;

    private const int MaxIdentities = 1024;

    /// <summary>RSA 签名用 SHA-2 的请求标志(<c>rsa-sha2-256</c> / <c>rsa-sha2-512</c>)。</summary>
    public const uint RsaSha2_256 = 2;

    public const uint RsaSha2_512 = 4;

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly TimeSpan _timeout;

    /// <param name="connect">打开一条到 agent 的新连接。</param>
    /// <param name="timeout">一次请求(连接 + 一问一答)的上限;签名可能要等用户在 agent 上确认(<c>ssh-add -c</c>),所以默认放宽。</param>
    public SshAgentClient(Func<CancellationToken, Task<Stream>> connect, TimeSpan? timeout = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>连到一个 Unix 域套接字(POSIX 上的 <c>SSH_AUTH_SOCK</c>)。</summary>
    public static SshAgentClient ForUnixSocket(string path, TimeSpan? timeout = null) => new(async ct =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }, timeout);

    /// <summary>连到一个命名管道(Windows 的 OpenSSH agent)。</summary>
    public static SshAgentClient ForNamedPipe(string pipeName = WindowsPipeName, TimeSpan? timeout = null) => new(async ct =>
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }, timeout);

    /// <summary>
    /// 按当前环境找 agent:Unix 看 <c>SSH_AUTH_SOCK</c>(指向的套接字文件存在才算);Windows 看 OpenSSH 的命名管道是否存在。没有返回 null。
    /// <paramref name="socketPath"/> 非空时用它代替环境变量(测试和 ssh_config 的 <c>IdentityAgent</c> 用)。
    /// </summary>
    public static SshAgentClient? Discover(string? socketPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            if (socketPath is { Length: > 0 } pipe && pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            {
                return ForNamedPipe(pipe[@"\\.\pipe\".Length..]);
            }

            try
            {
                return File.Exists(@"\\.\pipe\" + WindowsPipeName) ? ForNamedPipe() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        var path = socketPath ?? Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : ForUnixSocket(path);
    }

    public async ValueTask<IReadOnlyList<SshAgentIdentity>> ListIdentitiesAsync(CancellationToken ct = default)
    {
        var reply = await RoundTripAsync(RequestIdentities, [], ct).ConfigureAwait(false);
        if (reply.Type != IdentitiesAnswer)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ssh-agent refused to list identities");
        }

        var reader = new WireReader(reply.Body);
        var count = reader.UInt32();
        if (count > MaxIdentities)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ssh-agent returned too many identities");
        }

        var identities = new List<SshAgentIdentity>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var blob = reader.String();
            var comment = Encoding.UTF8.GetString(reader.String());
            identities.Add(new SshAgentIdentity(blob, comment));
        }

        return identities;
    }

    public async ValueTask<byte[]> SignAsync(byte[] publicKeyBlob, byte[] data, uint flags, CancellationToken ct = default)
    {
        var body = new WireWriter();
        body.String(publicKeyBlob);
        body.String(data);
        body.UInt32(flags);
        var reply = await RoundTripAsync(SignRequest, body.ToArray(), ct).ConfigureAwait(false);
        if (reply.Type != SignResponse)
        {
            // agent 拒绝(用户在确认框里点了拒绝、key 被锁定、不支持这个算法……):按「没有这把签名」处理,认证会换下一把
            throw new EnvironmentException(EnvironmentErrorCode.Authentication, "ssh-agent refused to sign");
        }

        return new WireReader(reply.Body).String();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(byte Type, byte[] Body)> RoundTripAsync(byte type, byte[] body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var stream = await _connect(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var frame = new byte[4 + 1 + body.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(1 + body.Length));
                frame[4] = type;
                body.CopyTo(frame, 5);
                await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

                var header = new byte[4];
                await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length is 0 or > MaxMessage)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ssh-agent sent a malformed reply");
                }

                var reply = new byte[length];
                await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                return (reply[0], reply[1..]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Timeout, "ssh-agent");
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or TimeoutException or EndOfStreamException)
        {
            throw new EnvironmentException(EnvironmentErrorCode.ConnectionFailed, "ssh-agent", e);
        }
    }

    private ref struct WireReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public uint UInt32()
        {
            if (_data.Length < 4)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ssh-agent sent a malformed reply");
            }

            var value = BinaryPrimitives.ReadUInt32BigEndian(_data);
            _data = _data[4..];
            return value;
        }

        public byte[] String()
        {
            var length = UInt32();
            if (length > _data.Length)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ssh-agent sent a malformed reply");
            }

            var value = _data[..(int)length].ToArray();
            _data = _data[(int)length..];
            return value;
        }
    }

    private sealed class WireWriter
    {
        private readonly MemoryStream _stream = new();

        public void UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void String(byte[] value)
        {
            UInt32((uint)value.Length);
            _stream.Write(value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}

/// <summary>
/// 把 ssh-agent 里的一把身份接到 SSH.NET 的公钥认证上:SSH.NET 的 <see cref="PrivateKeyAuthenticationMethod"/> 只认 <see cref="IPrivateKeySource"/>,
/// 但它拿私钥做的事只有两件——报公钥 blob(<see cref="HostAlgorithm.Data"/>)和对认证数据签名(<see cref="HostAlgorithm.Sign"/>)。
/// 这里实现的 <see cref="HostAlgorithm"/> 把「签名」转给 agent,私钥永远不进本进程。SSH.NET 的签名回调是同步的,所以在线程池线程上阻塞等 agent 的回答
/// (此时握手线程本来就在等这一步)。
/// </summary>
internal sealed class AgentHostAlgorithm(string name, byte[] publicKeyBlob, ISshAgent agent, uint flags) : HostAlgorithm(name)
{
    public override byte[] Data => publicKeyBlob;

    public override byte[] Sign(byte[] data) =>
        Task.Run(() => agent.SignAsync(publicKeyBlob, data, flags, CancellationToken.None).AsTask()).GetAwaiter().GetResult();

    public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException("ssh-agent keys only sign");
}

/// <summary>ssh-agent 里的一把身份,以 SSH.NET 的 <see cref="IPrivateKeySource"/> 形式出现。</summary>
internal sealed class AgentKeySource : IPrivateKeySource
{
    public AgentKeySource(SshAgentIdentity identity, ISshAgent agent)
    {
        Identity = identity;
        HostKeyAlgorithms = Algorithms(identity, agent);
    }

    public SshAgentIdentity Identity { get; }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    /// <summary>公钥 blob 开头的算法名(<c>ssh-ed25519</c>、<c>ecdsa-sha2-nistp256</c>、<c>ssh-rsa</c>……)。解析不出来返回 null。</summary>
    internal static string? KeyType(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        return length is 0 or > 64 || length > blob.Length - 4 ? null : Encoding.ASCII.GetString(blob, 4, (int)length);
    }

    private static HostAlgorithm[] Algorithms(SshAgentIdentity identity, ISshAgent agent)
    {
        var type = KeyType(identity.PublicKeyBlob);
        return type switch
        {
            null => [],
            // RSA:服务器要的是 rsa-sha2-*(现代 OpenSSH 默认不再接受 SHA-1 的 ssh-rsa),公钥 blob 仍是 ssh-rsa 的;优先 SHA-512
            "ssh-rsa" =>
            [
                new AgentHostAlgorithm("rsa-sha2-512", identity.PublicKeyBlob, agent, SshAgentClient.RsaSha2_512),
                new AgentHostAlgorithm("rsa-sha2-256", identity.PublicKeyBlob, agent, SshAgentClient.RsaSha2_256),
                new AgentHostAlgorithm("ssh-rsa", identity.PublicKeyBlob, agent, 0),
            ],
            // 证书类身份(ssh-*-cert-v01@openssh.com)SSH.NET 的认证不支持:不提供
            _ when type.Contains("-cert-", StringComparison.Ordinal) => [],
            _ => [new AgentHostAlgorithm(type, identity.PublicKeyBlob, agent, 0)],
        };
    }
}
