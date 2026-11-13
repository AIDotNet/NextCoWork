using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using BcCertificateRequest = Org.BouncyCastle.Tls.CertificateRequest;

namespace Ncw.Protocol.Security;

/// <summary>远程连接的安全层(L1)握手失败:对方证书不是钉扎的那一张、协议不对、超时等。</summary>
public sealed class SecureChannelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 一条建立好的 TLS 1.3 双向认证连接(docs/design/09 §2 L1、§4)。用 BouncyCastle 的纯托管实现:
/// 各平台行为一致(macOS / iOS 的 SslStream 没有 TLS 1.3),并且能导出 exporter 值给配对绑定用。
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public const string Alpn = "ncw/1";
    public const string ExporterLabel = "EXPORTER-ncw-pair";

    private static readonly SignatureAndHashAlgorithm EcdsaSha256 = new(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);

    private SecureChannel(Stream stream, byte[] peerFingerprint, byte[] localFingerprint, byte[] exporter)
    {
        Stream = stream;
        PeerFingerprint = peerFingerprint;
        LocalFingerprint = localFingerprint;
        Exporter = exporter;
    }

    /// <summary>加密后的双向字节流(读写可以在不同线程上并发)。</summary>
    public Stream Stream { get; }

    /// <summary>对方公钥指纹(TLS 握手已证明对方持有对应私钥)。</summary>
    public byte[] PeerFingerprint { get; }

    public byte[] LocalFingerprint { get; }

    /// <summary>TLS exporter 值(<see cref="ExporterLabel"/>,32 字节),两端相同;经中间人时两端不同。</summary>
    public byte[] Exporter { get; }

    /// <summary>设备端:接受一条连接。任何出示了证书的控制端都能完成握手,是否信任由上层按指纹决定。</summary>
    public static Task<SecureChannel> AcceptAsync(Stream transport, PeerIdentity identity, CancellationToken ct) =>
        HandshakeAsync(transport, ct, () =>
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var server = new Server(crypto, identity);
            var protocol = new TlsServerProtocol(transport);
            protocol.Accept(server);
            return new SecureChannel(new BlockingStreamAdapter(protocol.Stream, transport), server.Peer!, identity.Fingerprint, server.Exporter!);
        });

    /// <summary>
    /// 控制端:连接设备。<paramref name="pinnedDevice"/> 不为 null 时,设备公钥指纹必须与它相同,否则握手失败;
    /// 为 null(首次配对)时接受任何设备证书,由配对流程验证。
    /// </summary>
    public static Task<SecureChannel> ConnectAsync(Stream transport, PeerIdentity identity, byte[]? pinnedDevice, CancellationToken ct) =>
        HandshakeAsync(transport, ct, () =>
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new Client(crypto, identity, pinnedDevice);
            var protocol = new TlsClientProtocol(transport);
            protocol.Connect(client);
            return new SecureChannel(new BlockingStreamAdapter(protocol.Stream, transport), client.Peer!, identity.Fingerprint, client.Exporter!);
        });

    private static async Task<SecureChannel> HandshakeAsync(Stream transport, CancellationToken ct, Func<SecureChannel> handshake)
    {
        // BouncyCastle 的握手是阻塞的:放到线程池上,超时或取消时关掉底层连接让它退出。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var abort = timeout.Token.Register(transport.Dispose).ConfigureAwait(false);
        try
        {
            return await Task.Run(handshake, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TlsException or IOException or ObjectDisposedException or CryptographicException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new SecureChannelException(timeout.IsCancellationRequested ? "TLS handshake timed out" : $"TLS handshake failed: {e.Message}", e);
        }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    private static (BcCertificate Chain, AsymmetricKeyParameter Key) Credentials(TlsCrypto crypto, PeerIdentity identity) =>
        (new BcCertificate(TlsUtilities.EmptyBytes, [new CertificateEntry(crypto.CreateCertificate(identity.Certificate), null)]),
         PrivateKeyFactory.CreateKey(identity.PrivateKey));

    private static byte[] FingerprintOf(BcCertificate certificate)
    {
        if (certificate.IsEmpty)
        {
            throw new TlsFatalAlert(AlertDescription.certificate_required);
        }

        try
        {
            return PeerIdentity.FingerprintOf(certificate.GetCertificateAt(0).GetEncoded());
        }
        catch (CryptographicException e)
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate, e);
        }
    }

    private sealed class Server(BcTlsCrypto crypto, PeerIdentity identity) : DefaultTlsServer(crypto)
    {
        public byte[]? Peer { get; private set; }

        public byte[]? Exporter { get; private set; }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

        protected override IList<ProtocolName> GetProtocolNames() => [ProtocolName.AsUtf8Encoding(Alpn)];

        public override BcCertificateRequest GetCertificateRequest() => new(TlsUtilities.EmptyBytes, [EcdsaSha256], null, null);

        public override void NotifyClientCertificate(BcCertificate clientCertificate) => Peer = FingerprintOf(clientCertificate);

        public override TlsCredentials GetCredentials()
        {
            var (chain, key) = Credentials(Crypto, identity);
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto, key, chain, EcdsaSha256);
        }

        public override void NotifyHandshakeComplete()
        {
            base.NotifyHandshakeComplete();
            if (m_context.SecurityParameters.ApplicationProtocol?.GetUtf8Decoding() != Alpn || Peer is null)
            {
                throw new TlsFatalAlert(AlertDescription.no_application_protocol);
            }

            Exporter = m_context.ExportKeyingMaterial(ExporterLabel, null, 32);
        }
    }

    private sealed class Client(BcTlsCrypto crypto, PeerIdentity identity, byte[]? pinned) : DefaultTlsClient(crypto)
    {
        private readonly PeerIdentity _identity = identity;
        private readonly byte[]? _pinned = pinned;

        public byte[]? Peer { get; private set; }

        public byte[]? Exporter { get; private set; }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

        protected override IList<ProtocolName> GetProtocolNames() => [ProtocolName.AsUtf8Encoding(Alpn)];

        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        public override void NotifyHandshakeComplete()
        {
            base.NotifyHandshakeComplete();
            if (m_context.SecurityParameters.ApplicationProtocol?.GetUtf8Decoding() != Alpn)
            {
                throw new TlsFatalAlert(AlertDescription.no_application_protocol);
            }

            Exporter = m_context.ExportKeyingMaterial(ExporterLabel, null, 32);
        }

        private sealed class Authentication(Client owner) : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                var fingerprint = FingerprintOf(serverCertificate.Certificate);
                if (owner._pinned is not null && !CryptographicOperations.FixedTimeEquals(fingerprint, owner._pinned))
                {
                    throw new TlsFatalAlert(AlertDescription.bad_certificate);
                }

                owner.Peer = fingerprint;
            }

            public TlsCredentials GetClientCredentials(BcCertificateRequest certificateRequest)
            {
                var (chain, key) = Credentials(owner.Crypto, owner._identity);
                return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(owner.m_context), (BcTlsCrypto)owner.Crypto, key, chain, EcdsaSha256);
            }
        }
    }

    /// <summary>
    /// 把 BouncyCastle 的阻塞流包装成异步流。不能用 <see cref="System.IO.Stream"/> 基类的异步实现:它让读和写共用一把信号量,
    /// 读阻塞时写永远排不上(读循环一直挂着读,心跳和响应就发不出去)。这里读写各自在线程池上执行,互不等待。
    /// 释放时同时关掉底层连接,让阻塞中的读退出。
    /// </summary>
    private sealed class BlockingStreamAdapter(Stream tls, Stream transport) : Stream
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => tls.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count)
        {
            tls.Write(buffer, offset, count);
            tls.Flush();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // 取消只放弃等待;底层的阻塞读在连接关闭时结束(调用方取消读就意味着要关连接)。
            var read = Task.Run(() =>
            {
                var array = new byte[buffer.Length];
                var n = tls.Read(array, 0, array.Length);
                array.AsSpan(0, n).CopyTo(buffer.Span);
                return n;
            }, CancellationToken.None);
            return new ValueTask<int>(read.WaitAsync(ct));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var copy = buffer.ToArray();
                await Task.Run(() =>
                {
                    tls.Write(copy, 0, copy.Length);
                    tls.Flush();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public override void Flush() => tls.Flush();

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask; // 每次写都已经 flush

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    tls.Dispose();
                }
                catch (Exception e) when (e is IOException or TlsException or ObjectDisposedException)
                {
                }

                transport.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
