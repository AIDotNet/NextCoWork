using System.Security.Cryptography;
using Ncw.Agent.Cloud;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Host.Cloud;

/// <summary>
/// <c>--mode cloud</c> 的启动装配(进程入口 <c>Program.cs</c> 和测试共用):KMS 适配点与主密钥的打开。
/// </summary>
public static class CloudBoot
{
    public const string AllowDevKmsEnv = "NCW_CLOUD_ALLOW_DEV_KMS";
    public const string DevKmsKeyEnv = "NCW_DEV_KMS_KEY";

    /// <summary>
    /// 这个构建里有没有 <c>dev-env</c> KMS 提供者。<b>只在定义了 <c>NCW_DEV_KMS</c> 时才编译进去</b>(MSBuild 属性 <c>NcwDevKms</c>:Debug 默认开,Release 默认关),
    /// 所以发布给用户/沙箱镜像的二进制里根本没有这段代码,不能靠设置环境变量把它打开。用 <c>static readonly</c> 而不是 <c>const</c>,免得被内联进别的程序集。
    /// </summary>
    public static readonly bool DevKmsCompiledIn =
#if NCW_DEV_KMS
        true;
#else
        false;
#endif

    /// <summary>
    /// <b>KMS 适配点</b>:按启动配置的 <c>kms.provider</c> 造 <see cref="IKeyWrap"/>。真实云厂商的适配(AWS KMS / GCP KMS / Vault Transit / 编排服务自己的包裹端点……)是<b>后端</b>的工作(05 §6 B8):
    /// 在这里加一个 <c>case</c>,实现 <see cref="IKeyWrap"/> 即可,其余启动流程、凭据格式(NCK1)都不变。本仓库不含任何真实厂商的 KMS 客户端。
    /// <para>
    /// 唯一内置的 <c>dev-env</c> 只给开发和冒烟测试:用环境变量里的 AES-256 密钥(<see cref="DevKmsKeyEnv"/>)做 AES-GCM 包裹,
    /// 必须<b>同时</b>满足两个条件才可用:这个构建编译进了它(<see cref="DevKmsCompiledIn"/>,发布构建默认不带)和显式设置 <see cref="AllowDevKmsEnv"/>=1。
    /// 密钥就在同一个进程环境里,它没有任何 KMS 的保护价值——所以生产二进制里根本不该有它;<c>tools/Ncw.Build publish-agent</c> 会扫描发布产物,出现它就失败。
    /// </para>
    /// 未知的 provider、或缺少必要参数:<see cref="CloudStartupException"/>(<c>cloud.kms.unavailable</c>,退出码 69)。
    /// </summary>
    public static IKeyWrap CreateKeyWrap(CloudConfig config, Func<string, string?> env)
    {
        switch (config.Kms.Provider)
        {
#if NCW_DEV_KMS
            case "dev-env" when env(AllowDevKmsEnv) == "1":
            {
                var key = new byte[32];
                if (!Convert.TryFromBase64String(env(DevKmsKeyEnv) ?? "", key, out var written) || written != 32)
                {
                    throw new CloudStartupException(CloudStartupCodes.KmsUnavailable, DevKmsKeyEnv, CloudExitCodes.Dependency, "The development KMS key is missing or invalid.");
                }

                return new DevEnvKeyWrap(key, config.Kms.KeyId ?? "dev-env");
            }
#endif

            default:
                throw new CloudStartupException(CloudStartupCodes.KmsUnavailable, "kms.provider", CloudExitCodes.Dependency, "No KMS adapter is available for the configured provider.");
        }
    }

    /// <summary>
    /// 打开(第一次启动时创建)KMS 包裹的主密钥。失败映射成结构化的启动失败:KMS 拒绝(<see cref="KeyWrapException"/>)→ <c>cloud.kms.rejected</c>;暂时连不上 → <c>cloud.kms.unavailable</c>(都是退出码 69,
    /// 编排服务可以退避后重启);库里有加密凭据却没有任何形态的主密钥 → <c>cloud.kms.keyMissing</c>(退出码 65,重启也不会好)。
    /// </summary>
    public static async Task<KmsMasterKeySource> OpenKeySourceAsync(CloudConfig config, IKeyWrap wrap, CancellationToken ct)
    {
        try
        {
            return await KmsMasterKeySource.OpenAsync(config.DataRoot, wrap, config.Kms.Context, ct).ConfigureAwait(false);
        }
        catch (KeyWrapException e)
        {
            throw new CloudStartupException(CloudStartupCodes.KmsRejected, "kms", CloudExitCodes.Dependency, "The KMS refused to unwrap the master key.", e);
        }
        catch (CryptographicException e)
        {
            throw new CloudStartupException(CloudStartupCodes.KeyMissing, "kms", CloudExitCodes.Identity, "The master key is missing or damaged.", e);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new CloudStartupException(CloudStartupCodes.KmsUnavailable, "kms", CloudExitCodes.Dependency, "The KMS could not be reached.", e);
        }
    }

#if NCW_DEV_KMS
    /// <summary>开发用的包裹:AES-256-GCM,加密上下文作 AAD(和真实 KMS 的 EncryptionContext 同一个语义)。只在 <see cref="AllowDevKmsEnv"/> 下可用。</summary>
    private sealed class DevEnvKeyWrap(byte[] key, string keyId) : IKeyWrap
    {
        public string KeyId => keyId;

        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            var blob = new byte[12 + plaintext.Length + 16];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(blob.AsSpan(0, 12), plaintext.Span, blob.AsSpan(12, plaintext.Length), blob.AsSpan(blob.Length - 16), context.Span);
            return ValueTask.FromResult(blob);
        }

        public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrapped, string wrappedKeyId, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            var blob = wrapped.Span;
            if (wrappedKeyId != keyId || blob.Length < 12 + 16)
            {
                throw new KeyWrapException("The wrapped key was not produced by this KMS key.");
            }

            var plain = new byte[blob.Length - 12 - 16];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(blob[..12], blob[12..^16], blob[^16..], plain, context.Span);
            }
            catch (CryptographicException e)
            {
                throw new KeyWrapException("The KMS refused to unwrap the key (wrong key or encryption context).", e);
            }

            return ValueTask.FromResult(plain);
        }
    }
#endif
}
