using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage.Credentials;

/// <summary>
/// 数据主密钥的来源。默认(本机模式)是数据根下 0600 权限的 <c>credential.key</c> 文件;云端模式换成 <see cref="KmsMasterKeySource"/>。
/// 实现返回的必须是<b>副本</b>:调用方(<see cref="CredentialStore"/>)用完会把它清零。
/// </summary>
public interface IMasterKeySource
{
    /// <summary>读已有的主密钥;还没有抛 <see cref="FileNotFoundException"/>;解不开 / 长度不对抛 <see cref="CryptographicException"/>。</summary>
    byte[] Load();

    /// <summary>读主密钥;没有就生成(只有库里没有 NCK1 密文时才允许,否则抛异常——重新生成会让已有密文作废)。</summary>
    byte[] LoadOrCreate();
}

/// <summary>
/// KMS 抽象(docs/design/02 A6「密钥」、05 B8):包裹 / 解开<b>数据主密钥</b>(NCK1 的 32 字节 AES 密钥)。
/// <para>
/// ★ 本仓库<b>不含任何真实云厂商的 KMS 客户端</b>——这里只有接口和适配点:后端(编排服务 / 沙箱镜像)提供一个实现(AWS KMS / GCP KMS / Vault Transit / 编排服务自己的包裹端点……),
/// 在 <c>ncw-agent --mode cloud</c> 的启动装配处注入。凭据的加密格式(NCK1)完全不变,变的只是「主密钥在磁盘上以什么形态存在」。
/// </para>
/// <para>
/// <paramref name="context"/> 是加密上下文(AAD):包裹和解开必须给同一个值,KMS 据此保证「这个沙箱的密钥解不开别人的」。云端 Agent 用启动配置里的 <c>kms.context</c>(通常含账户 id 和沙箱 id)。
/// 实现要幂等、可重试;失败抛异常(<see cref="KeyWrapException"/> 表示 KMS 明确拒绝,其它异常表示暂时连不上),Agent 启动时据此以结构化错误退出。
/// </para>
/// </summary>
public interface IKeyWrap
{
    /// <summary>当前用于包裹的 KMS 密钥标识(写进包裹文件,轮换 / 排错用)。</summary>
    string KeyId { get; }

    ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken ct);

    ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrapped, string keyId, ReadOnlyMemory<byte> context, CancellationToken ct);
}

/// <summary>KMS 明确拒绝了这次包裹 / 解开(密钥被禁用、上下文不符、没有权限)。和「暂时连不上」(普通异常)区分开。</summary>
public sealed class KeyWrapException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>包裹文件(<c>credential.key.kms</c>)的内容。<see cref="Wrapped"/> 是 KMS 返回的不透明密文(base64)。</summary>
public sealed record WrappedKeyFile(int V, string KeyId, string Wrapped);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WrappedKeyFile))]
internal sealed partial class KeyWrapJsonContext : JsonSerializerContext;

/// <summary>
/// 云端模式的主密钥来源:磁盘上只有 KMS 包裹后的密文(<c>credential.key.kms</c>),明文主密钥只在进程内存里。
/// <list type="bullet">
/// <item><see cref="OpenAsync"/> 在启动时解开(或第一次启动时生成并包裹)并缓存;之后 <see cref="Load"/> 返回缓存的副本,不再访问 KMS(运行中 KMS 抖动不影响读写凭据)。</item>
/// <item>如果数据根里有明文 <c>credential.key</c>(例如用户把本机数据导入云端),启动时把它包裹成 KMS 形态,<b>然后删除明文文件</b>。</item>
/// <item>库里已有 NCK1 密文却找不到任何形态的主密钥:抛 <see cref="CryptographicException"/>,绝不生成新密钥(和本机模式同一条规则)。</item>
/// </list>
/// </summary>
public sealed class KmsMasterKeySource : IMasterKeySource, IDisposable
{
    public const string FileName = "credential.key.kms";

    private readonly object _gate = new();
    private byte[]? _key;

    private KmsMasterKeySource()
    {
    }

    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    /// <summary>
    /// 打开(必要时创建)云端主密钥。KMS 暂时不可用抛出底层异常,KMS 拒绝抛 <see cref="KeyWrapException"/>;调用方把它们映射成结构化的启动失败。
    /// </summary>
    public static async Task<KmsMasterKeySource> OpenAsync(string directory, IKeyWrap wrap, string context, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var source = new KmsMasterKeySource();
        var contextBytes = System.Text.Encoding.UTF8.GetBytes(context);
        var file = PathFor(directory);
        var plainFile = Path.Combine(directory, CredentialCrypto.MasterKeyFileName);

        if (File.Exists(file))
        {
            var saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), KeyWrapJsonContext.Default.WrappedKeyFile)
                ?? throw new CryptographicException("The wrapped master key file is damaged.");
            byte[] key;
            try
            {
                key = await wrap.UnwrapAsync(Convert.FromBase64String(saved.Wrapped), saved.KeyId, contextBytes, ct).ConfigureAwait(false);
            }
            catch (FormatException e)
            {
                throw new CryptographicException("The wrapped master key file is damaged.", e);
            }

            source.Adopt(key);
            return source;
        }

        byte[]? adopted = null;
        if (File.Exists(plainFile))
        {
            // 明文密钥(例如本机数据导入云端):包裹后删除明文。先写包裹文件再删明文,中途崩溃下一次启动还能再来一遍。
            adopted = CredentialCrypto.LoadMasterKey(directory);
        }
        else if (HasProgramRows(Path.Combine(directory, AgentDatabase.FileName)))
        {
            throw new CryptographicException("The credential store has encrypted rows but no master key exists in any form.");
        }

        var fresh = adopted ?? RandomNumberGenerator.GetBytes(32);
        var wrapped = await wrap.WrapAsync(fresh, contextBytes, ct).ConfigureAwait(false);
        var content = JsonSerializer.SerializeToUtf8Bytes(new WrappedKeyFile(1, wrap.KeyId, Convert.ToBase64String(wrapped)), KeyWrapJsonContext.Default.WrappedKeyFile);
        WriteAtomically(file, content);
        if (adopted is not null)
        {
            File.Delete(plainFile);
        }

        source.Adopt(fresh);
        return source;
    }

    private void Adopt(byte[] key)
    {
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException($"The unwrapped master key has the wrong length ({key.Length}, expected 32).");
        }

        lock (_gate)
        {
            _key = key;
        }
    }

    public byte[] Load()
    {
        lock (_gate)
        {
            return _key is null ? throw new FileNotFoundException("The master key has not been opened.", FileName) : [.. _key];
        }
    }

    public byte[] LoadOrCreate() => Load();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static bool HasProgramRows(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return false;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'credentials'";
        if (exists.ExecuteScalar() is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT blob FROM credentials";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is byte[] blob && CredentialCrypto.IsProgramEncrypted(blob))
            {
                return true;
            }
        }

        return false;
    }
}
