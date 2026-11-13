using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// NCK1 部分移植自 2.x 的 <c>src/main/secrets/__tests__/credential-crypto.test.ts</c>;
/// safeStorage 的向量由 openssl 独立生成(不依赖本实现):PBKDF2 用 Python hashlib,AES-CBC 用 <c>openssl enc</c>。
/// </summary>
public sealed class CredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncwc-{Guid.NewGuid():N}");

    public CredentialTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // ---- NCK1 格式 ----

    [Fact]
    public void Round_trips_utf8_including_oauth_json()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        foreach (var plain in new[]
        {
            "sk-plain-api-key",
            """{"kind":"oauth","issuer":"chatgpt","accessToken":"at","refreshToken":"rt","expiresAt":null,"accountId":"acc"}""",
            "中文密钥/emoji 🔑",
            "",
        })
        {
            var blob = CredentialCrypto.Encrypt(key, plain);
            Assert.True(CredentialCrypto.IsProgramEncrypted(blob));
            Assert.Equal(plain, CredentialCrypto.Decrypt(key, blob));
        }
    }

    [Fact]
    public void Fresh_nonce_per_encryption()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(CredentialCrypto.Encrypt(key, "same"), CredentialCrypto.Encrypt(key, "same"));
    }

    [Fact]
    public void Only_full_length_nck1_blobs_are_program_encrypted()
    {
        Assert.False(CredentialCrypto.IsProgramEncrypted("""{"kind":"api-key"}"""u8));
        Assert.False(CredentialCrypto.IsProgramEncrypted("NCK1"u8));
        Assert.False(CredentialCrypto.IsProgramEncrypted([]));
        Assert.True(CredentialCrypto.IsProgramEncrypted(CredentialCrypto.Encrypt(RandomNumberGenerator.GetBytes(32), "")));
    }

    [Fact]
    public void Throws_on_wrong_key_tampering_unknown_version_and_foreign_blobs()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = CredentialCrypto.Encrypt(key, "sk-secret");

        Assert.ThrowsAny<CryptographicException>(() => CredentialCrypto.Decrypt(RandomNumberGenerator.GetBytes(32), blob));
        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 0xff;
        Assert.ThrowsAny<CryptographicException>(() => CredentialCrypto.Decrypt(key, tampered));
        var reversioned = (byte[])blob.Clone();
        reversioned[4] = 99;
        Assert.Contains("version", Assert.ThrowsAny<CryptographicException>(() => CredentialCrypto.Decrypt(key, reversioned)).Message);
        Assert.Contains("NCK1", Assert.ThrowsAny<CryptographicException>(() => CredentialCrypto.Decrypt(key, "not-ours"u8.ToArray())).Message);
    }

    [Fact]
    public void Decrypts_a_blob_written_by_2x_layout()
    {
        // 按 2.x 的布局手工拼一个 blob:magic + version + nonce + ciphertext + tag。
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = "sk-from-2x"u8.ToArray();
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        byte[] blob = [.. "NCK1"u8, 1, .. nonce, .. cipher, .. tag];

        Assert.Equal("sk-from-2x", CredentialCrypto.Decrypt(key, blob));
    }

    // ---- 主密钥文件 ----

    [Fact]
    public void Creates_a_32_byte_0600_key_and_reuses_it()
    {
        var first = CredentialCrypto.LoadOrCreateMasterKey(_dir);
        var file = Path.Combine(_dir, CredentialCrypto.MasterKeyFileName);

        Assert.Equal(32, first.Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(first, CredentialCrypto.LoadOrCreateMasterKey(_dir));
    }

    [Fact]
    public void Tightens_existing_key_and_rejects_a_symlinked_key()
    {
        if (OperatingSystem.IsWindows()) return;

        var file = Path.Combine(_dir, CredentialCrypto.MasterKeyFileName);
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(32));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        CredentialCrypto.LoadOrCreateMasterKey(_dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var linked = Path.Combine(_dir, "linked");
        Directory.CreateDirectory(linked);
        File.WriteAllBytes(Path.Combine(linked, "real-key"), RandomNumberGenerator.GetBytes(32));
        File.CreateSymbolicLink(Path.Combine(linked, CredentialCrypto.MasterKeyFileName), Path.Combine(linked, "real-key"));
        Assert.Throws<CryptographicException>(() => CredentialCrypto.LoadOrCreateMasterKey(linked));
    }

    [Fact]
    public void Refuses_to_regenerate_a_damaged_key()
    {
        File.WriteAllBytes(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName), new byte[7]);

        Assert.Contains("damaged", Assert.Throws<CryptographicException>(() => CredentialCrypto.LoadOrCreateMasterKey(_dir)).Message);
    }

    // ---- 旧行迁移编排 ----

    [Fact]
    public void Migration_rewrites_legacy_rows_and_leaves_program_and_undecryptable_rows()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var legacy = "legacy-safe-storage-bytes"u8.ToArray();
        var rows = new[]
        {
            new CredentialRow("provider:a", legacy),
            new CredentialRow("provider:b", CredentialCrypto.Encrypt(key, "already-migrated")),
            new CredentialRow("provider:c", "corrupt-legacy-row"u8.ToArray()),
        };
        var written = new List<(string Ref, byte[] Blob)>();

        var result = CredentialStore.MigrateRows(rows, (r, b) => written.Add((r, b)),
            blob => blob.AsSpan().SequenceEqual(legacy) ? "sk-legacy-plain" : null,
            plain => CredentialCrypto.Encrypt(key, plain));

        Assert.Equal(new CredentialMigrationResult(1, 1, 1), result);
        var (reference, written0) = Assert.Single(written);
        Assert.Equal("provider:a", reference);
        Assert.Equal("sk-legacy-plain", CredentialCrypto.Decrypt(key, written0));
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var writes = 0;

        var result = CredentialStore.MigrateRows([new CredentialRow("provider:a", CredentialCrypto.Encrypt(key, "sk-x"))],
            (_, _) => writes++, _ => null, plain => CredentialCrypto.Encrypt(key, plain));

        Assert.Equal(new CredentialMigrationResult(0, 1, 0), result);
        Assert.Equal(0, writes);
    }

    // ---- Chromium os_crypt(safeStorage)----

    [Fact]
    public void Mac_v10_blob_decrypts_with_keychain_password()
    {
        var crypt = ChromiumOsCrypt.FromPassword(() => "test-keychain-password"u8.ToArray(), iterations: 1003);

        Assert.Equal("sk-legacy-plain", crypt.TryDecrypt(Hex("763130aca81c4005be0deb1b55b7cd465564aa")));
    }

    [Fact]
    public void Linux_v10_blob_decrypts_with_the_fixed_password()
    {
        var crypt = ChromiumOsCrypt.FromPassword(() => "peanuts"u8.ToArray(), iterations: 1);

        Assert.Equal("sk-legacy-plain", crypt.TryDecrypt(Hex("763130f3d0a2ec43ee45b1e0f764749c6f0552")));
    }

    [Fact]
    public void Windows_v10_blob_decrypts_with_the_local_state_key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = "sk-legacy-plain"u8.ToArray();
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);

        var crypt = ChromiumOsCrypt.FromAesKey(() => key);

        Assert.Equal("sk-legacy-plain", crypt.TryDecrypt([.. "v10"u8, .. nonce, .. cipher, .. tag]));
    }

    [Fact]
    public void Wrong_password_missing_key_and_unknown_prefix_give_null_and_key_is_fetched_once()
    {
        var fetches = 0;
        var wrong = ChromiumOsCrypt.FromPassword(() => { fetches++; return "nope"u8.ToArray(); }, iterations: 1003);
        Assert.Null(wrong.TryDecrypt(Hex("763130aca81c4005be0deb1b55b7cd465564aa")));
        Assert.Null(wrong.TryDecrypt(Hex("763130aca81c4005be0deb1b55b7cd465564aa")));
        Assert.Equal(1, fetches); // macOS 上每取一次就可能弹一次授权框

        Assert.Null(ChromiumOsCrypt.FromPassword(() => null, 1003).TryDecrypt(Hex("763130aca81c4005be0deb1b55b7cd465564aa")));
        Assert.Null(ChromiumOsCrypt.FromPassword(() => "peanuts"u8.ToArray(), 1).TryDecrypt("v11whatever"u8));

        var untouched = 0;
        Assert.Null(ChromiumOsCrypt.FromPassword(() => { untouched++; return null; }, 1).TryDecrypt("plain-bytes"u8));
        Assert.Equal(0, untouched); // 不是 v10 就不去取平台密钥
    }

    // ---- 凭据表 ----

    private sealed class FakeLegacy(string plain) : ILegacySecretDecryptor
    {
        public string? TryDecrypt(ReadOnlySpan<byte> blob) => blob.StartsWith("v10"u8) ? plain : null;
    }

    private static void PutRaw(AgentDatabase db, string reference, byte[] blob)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = "INSERT INTO credentials (ref, blob) VALUES ($r, $b)";
        command.Parameters.AddWithValue("$r", reference);
        command.Parameters.Add("$b", SqliteType.Blob).Value = blob;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Store_sets_gets_and_reads_legacy_rows_through_the_fallback()
    {
        using var db = AgentDatabase.Open(_dir);
        var store = new CredentialStore(db, _dir, new FakeLegacy("sk-old"));
        PutRaw(db, "provider:old", "v10-legacy"u8.ToArray());

        store.Set("provider:new", "sk-new");

        Assert.Equal("sk-new", store.Get("provider:new"));
        Assert.Equal("sk-old", store.Get("provider:old"));
        Assert.Null(store.Get("provider:none"));
        Assert.True(File.Exists(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName)));
    }

    [Fact]
    public void Store_refuses_to_write_with_a_key_that_does_not_match_existing_rows()
    {
        using var db = AgentDatabase.Open(_dir);
        var store = new CredentialStore(db, _dir, NoLegacySecrets.Instance);
        store.Set("provider:a", "sk-a");

        File.Delete(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName));
        Assert.Throws<FileNotFoundException>(() => store.Set("provider:b", "sk-b")); // 不能悄悄生成第二把密钥

        File.WriteAllBytes(Path.Combine(_dir, CredentialCrypto.MasterKeyFileName), RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => store.Set("provider:b", "sk-b"));
        Assert.Null(store.Get("provider:b")); // 没有写入
    }

    [Fact]
    public void Store_migrates_legacy_rows_once_and_counts_failures()
    {
        using var db = AgentDatabase.Open(_dir);
        PutRaw(db, "provider:ok", "v10-legacy"u8.ToArray());
        PutRaw(db, "provider:v11", "v11-linux-keyring"u8.ToArray());
        var store = new CredentialStore(db, _dir, new FakeLegacy("sk-old"));
        Assert.Equal(2, store.LegacyCount());

        Assert.Equal(new CredentialMigrationResult(1, 0, 1), store.MigrateLegacy());
        Assert.Equal(new CredentialMigrationResult(0, 1, 1), store.MigrateLegacy());
        Assert.Equal(1, store.LegacyCount());
        // 迁移后的行是 NCK1,不再需要旧解密器。
        Assert.Equal("sk-old", new CredentialStore(db, _dir, NoLegacySecrets.Instance).Get("provider:ok"));
    }
}
