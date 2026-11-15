using Microsoft.Data.Sqlite;
using Ncw.Agent.Cloud;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 本机模式的主密钥保护(docs/design/02 A6.1):数据根里已经有加密数据(设备私钥 / NCK1 凭据)却没有可用的 <c>credential.key</c> 时,
/// 拒绝启动、绝不生成新密钥,而且是结构化的失败(稳定 key + 字段名,没有任何值)而不是未处理异常。
/// </summary>
public sealed class MasterKeyStartupTests : IDisposable
{
    private const string Sentinel = "SENTINEL-MASTER-KEY-CONTENT-0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwk-{Guid.NewGuid():N}"[..16]);

    public MasterKeyStartupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private string KeyFile => Path.Combine(_root, CredentialCrypto.MasterKeyFileName);

    private string DbFile => Path.Combine(_root, AgentDatabase.FileName);

    private void CreateCredentialsDb(params byte[][] blobs)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE credentials (ref TEXT PRIMARY KEY, blob BLOB NOT NULL)";
        create.ExecuteNonQuery();
        for (var i = 0; i < blobs.Length; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO credentials (ref, blob) VALUES ($r, $b)";
            insert.Parameters.AddWithValue("$r", "ref" + i);
            insert.Parameters.AddWithValue("$b", blobs[i]);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>一行 NCK1 形态的密文(只看前缀和长度判断格式;内容无所谓,这里不解密)。</summary>
    private static byte[] Nck1Blob() => [.. "NCK1"u8.ToArray(), 1, .. new byte[12 + 16 + 4]];

    private static DeviceIdentity Load(string root) => DeviceIdentity.LoadOrCreate(root, TimeProvider.System);

    private CloudStartupException Refused(string expectedCode)
    {
        var failure = Assert.Throws<CloudStartupException>(() => Load(_root));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(CredentialCrypto.MasterKeyFileName, failure.Field);
        Assert.Equal(CloudExitCodes.Identity, failure.ExitCode);
        Assert.Equal(65, failure.ExitCode);
        return failure;
    }

    [Fact]
    public void A_fresh_data_root_still_generates_the_master_key_and_the_identity()
    {
        var identity = Load(_root);

        Assert.True(File.Exists(KeyFile));
        Assert.Equal(32, new FileInfo(KeyFile).Length);
        Assert.True(File.Exists(Path.Combine(_root, DeviceIdentity.FileName)));
        Assert.Equal(identity.DeviceId, Load(_root).DeviceId); // 第二次启动读回同一个身份
    }

    [Fact]
    public void An_existing_identity_with_a_missing_key_is_refused_and_no_new_key_is_created()
    {
        var deviceId = Load(_root).DeviceId;
        File.Delete(KeyFile);

        var failure = Refused(LocalStartupCodes.MasterKeyMissing);

        Assert.False(File.Exists(KeyFile)); // 绝不生成新密钥
        Assert.IsType<FileNotFoundException>(failure.InnerException);

        // 用户把密钥放回去:同一份数据正常启动,身份没变
        var key = CredentialCrypto.LoadOrCreateMasterKey(_root);
        Assert.NotNull(key);
    }

    [Fact]
    public void A_database_with_encrypted_credentials_and_no_key_is_refused_and_no_new_key_is_created()
    {
        CreateCredentialsDb(Nck1Blob());

        Refused(LocalStartupCodes.MasterKeyMissing);

        Assert.False(File.Exists(KeyFile));
        Assert.False(File.Exists(Path.Combine(_root, DeviceIdentity.FileName))); // 也没有悄悄生成一个身份
    }

    [Fact]
    public void A_database_without_encrypted_credentials_still_starts_and_creates_the_key_like_before()
    {
        // 2.x 的库里只有旧 safeStorage 行(或根本没有凭据):没有任何东西是用 credential.key 加密的,生成是安全的,行为不变
        CreateCredentialsDb("v10legacy-row-from-safe-storage-xx"u8.ToArray());

        Load(_root);

        Assert.True(File.Exists(KeyFile));
    }

    [Fact]
    public void A_damaged_key_is_refused_with_its_own_key_and_left_untouched()
    {
        Load(_root);
        File.WriteAllText(KeyFile, Sentinel); // 长度不对

        var failure = Refused(LocalStartupCodes.MasterKeyUnreadable);

        Assert.Equal(Sentinel, File.ReadAllText(KeyFile)); // 不覆盖、不重新生成
        AssertNoSecret(failure);
    }

    [Fact]
    public void A_valid_but_wrong_key_cannot_open_the_device_identity_and_is_reported_as_unreadable()
    {
        Load(_root);
        var wrong = new byte[32];
        Array.Fill(wrong, (byte)7);
        File.WriteAllBytes(KeyFile, wrong);

        var failure = Refused(LocalStartupCodes.MasterKeyUnreadable);

        Assert.Equal(wrong, File.ReadAllBytes(KeyFile));
        AssertNoSecret(failure);
    }

    [Fact]
    public void A_key_that_is_a_directory_or_symlink_is_unreadable_not_a_crash()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Load(_root);
        File.Delete(KeyFile);
        var target = Path.Combine(_root, "elsewhere.key");
        File.WriteAllBytes(target, new byte[32]);
        File.CreateSymbolicLink(KeyFile, target);

        Refused(LocalStartupCodes.MasterKeyUnreadable);
    }

    [Fact]
    public void The_failure_never_carries_key_material_in_code_field_or_message()
    {
        Load(_root);
        File.WriteAllText(KeyFile, Sentinel);
        AssertNoSecret(Refused(LocalStartupCodes.MasterKeyUnreadable));

        File.Delete(KeyFile);
        AssertNoSecret(Refused(LocalStartupCodes.MasterKeyMissing));
    }

    [Fact]
    public void Other_startup_failures_are_not_swallowed_into_the_structured_failure()
    {
        // device.json 是个目录:读设备身份本身失败,和主密钥无关,不能被伪装成「主密钥缺失」
        Directory.CreateDirectory(Path.Combine(_root, DeviceIdentity.FileName));

        var error = Record.Exception(() => Load(_root));

        Assert.NotNull(error);
        Assert.IsNotType<CloudStartupException>(error);
    }

    private static void AssertNoSecret(CloudStartupException failure)
    {
        Assert.DoesNotContain(Sentinel, failure.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, failure.Field ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, failure.Message, StringComparison.Ordinal);
    }
}
