using Ncw.Mobile;

namespace Ncw.Mobile.Shared.Tests;

/// <summary>身份文件的封存:磁盘上只有密文;同一把包裹密钥能读回同一个身份;密钥丢了 / 文件坏了就换新身份;封存失败绝不降级为明文。</summary>
public sealed class SealedIdentityStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncw-sealed-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "identity.bin");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_same_wrapping_key_reads_back_the_same_identity_after_a_restart()
    {
        var key = new byte[32];
        new Random(1).NextBytes(key);
        var first = new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate();

        var second = new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate();

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
        Assert.Equal(first.Certificate, second.Certificate);
    }

    [Fact]
    public void The_file_on_disk_never_contains_the_private_key_in_the_clear()
    {
        var identity = new SealedIdentityStore(FilePath, new InMemorySecretBox(), "phone").LoadOrCreate();

        var bytes = File.ReadAllBytes(FilePath);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.True(SealedIdentityStore.LooksSealed(bytes));
        Assert.False(bytes.AsSpan().IndexOf(identity.PrivateKey) >= 0);
        Assert.DoesNotContain(Convert.ToBase64String(identity.PrivateKey), text, StringComparison.Ordinal);
        Assert.DoesNotContain("privateKey", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_file_is_owner_read_write_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new SealedIdentityStore(FilePath, new InMemorySecretBox(), "phone").LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void A_lost_wrapping_key_means_a_fresh_identity_not_a_crash()
    {
        var before = new SealedIdentityStore(FilePath, new InMemorySecretBox(), "phone").LoadOrCreate();

        var after = new SealedIdentityStore(FilePath, new InMemorySecretBox(), "phone").LoadOrCreate();

        Assert.NotEqual(before.DeviceId, after.DeviceId);
        // 新身份已经覆盖旧文件:用新的盒子(同一把)再读,得到的是新身份
    }

    [Fact]
    public void A_tampered_or_truncated_file_is_replaced_by_a_fresh_identity()
    {
        var key = new byte[32];
        var before = new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate();
        var bytes = File.ReadAllBytes(FilePath);
        bytes[^3] ^= 0x40;
        File.WriteAllBytes(FilePath, bytes);
        Assert.NotEqual(before.DeviceId, new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate().DeviceId);

        File.WriteAllBytes(FilePath, bytes[..10]);
        var again = new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate();
        Assert.NotNull(again);

        File.WriteAllText(FilePath, "{\"privateKey\":\"AAAA\"}"); // 旧的明文格式不被接受
        Assert.NotNull(new SealedIdentityStore(FilePath, new InMemorySecretBox(key), "phone").LoadOrCreate());
        Assert.True(SealedIdentityStore.LooksSealed(File.ReadAllBytes(FilePath)));
    }

    [Fact]
    public void The_associated_data_binds_the_ciphertext_to_its_purpose()
    {
        var box = new InMemorySecretBox();
        var sealedData = box.Seal([1, 2, 3], "a"u8);

        Assert.Equal([1, 2, 3], box.Open(sealedData, "a"u8));
        Assert.Throws<SecretBoxException>(() => box.Open(sealedData, "b"u8));
    }

    [Fact]
    public void When_the_platform_cannot_seal_nothing_is_written_and_the_failure_surfaces()
    {
        var store = new SealedIdentityStore(FilePath, new ThrowingBox(), "phone");

        Assert.Throws<PlatformNotSupportedException>(store.LoadOrCreate);
        Assert.False(File.Exists(FilePath));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public void The_identity_is_cached_in_memory_after_the_first_load()
    {
        var store = new SealedIdentityStore(FilePath, new InMemorySecretBox(), "phone");

        Assert.Same(store.LoadOrCreate(), store.LoadOrCreate());
    }

    private sealed class ThrowingBox : ISecretBox
    {
        public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData) => throw new PlatformNotSupportedException("no keystore");

        public byte[] Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData) => throw new SecretBoxException("no keystore");
    }
}
