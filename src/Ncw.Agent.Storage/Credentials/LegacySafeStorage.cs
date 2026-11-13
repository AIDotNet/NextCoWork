using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Storage.Credentials;

/// <summary>解 2.x 用 Electron <c>safeStorage</c> 加密的旧凭据行。解不开返回 null,不抛(单行失败不能拦住整轮)。</summary>
public interface ILegacySecretDecryptor
{
    string? TryDecrypt(ReadOnlySpan<byte> blob);
}

/// <summary>没有可用的旧密钥(或者测试):所有旧行都解不开。</summary>
public sealed class NoLegacySecrets : ILegacySecretDecryptor
{
    public static readonly NoLegacySecrets Instance = new();

    public string? TryDecrypt(ReadOnlySpan<byte> blob) => null;
}

/// <summary>
/// Chromium <c>os_crypt</c> 的格式(Electron <c>safeStorage.encryptString</c> 用的就是它),按平台:
/// <list type="bullet">
/// <item>macOS:<c>v10</c> + AES-128-CBC;密钥 = PBKDF2-HMAC-SHA1(钥匙串里「&lt;App 名&gt; Safe Storage」的口令, "saltysalt", 1003 次, 16 字节),IV 是 16 个空格。</item>
/// <item>Linux:<c>v10</c> 同上,但口令固定为 "peanuts"、迭代 1 次;<c>v11</c> 的口令在 libsecret / KWallet 里,这里不支持(计为需要重新输入)。</item>
/// <item>Windows:<c>v10</c> + nonce(12) + 密文 + tag(16),AES-256-GCM;密钥是 <c>Local State</c> 里 <c>os_crypt.encrypted_key</c>
///   去掉 "DPAPI" 前缀后用 DPAPI 解开的 32 字节。</item>
/// </list>
/// 平台密钥只在第一次遇到旧行时才去取(macOS 上会弹钥匙串授权框),之后缓存在本对象里。
/// </summary>
public sealed partial class ChromiumOsCrypt : ILegacySecretDecryptor
{
    private static readonly byte[] V10 = "v10"u8.ToArray();
    private static readonly byte[] Salt = "saltysalt"u8.ToArray();
    private static readonly byte[] CbcIv = "                "u8.ToArray();

    private readonly Func<byte[]?> _loadKey;
    private readonly bool _gcm;
    private byte[]? _key;
    private bool _loaded;

    private ChromiumOsCrypt(Func<byte[]?> loadKey, bool gcm)
    {
        _loadKey = loadKey;
        _gcm = gcm;
    }

    /// <summary>
    /// 当前平台的解密器。<paramref name="profileRoot"/> 是 2.x 的 Electron userData(<c>~/.next-cowork</c>),
    /// Windows 的 <c>Local State</c> 在那里;<paramref name="appNames"/> 是 2.x 可能用过的 App 名(发行版 productName、开发时的 package name)。
    /// </summary>
    public static ILegacySecretDecryptor ForCurrentPlatform(string profileRoot, params string[] appNames)
    {
        if (OperatingSystem.IsMacOS())
        {
            return FromPassword(() => MacKeychain.FindPassword(appNames.Select(n => $"{n} Safe Storage")), iterations: 1003);
        }

        if (OperatingSystem.IsWindows())
        {
            return FromWindowsLocalState(Path.Combine(profileRoot, "Local State"));
        }

        return FromPassword(() => "peanuts"u8.ToArray(), iterations: 1);
    }

    /// <summary>macOS / Linux 的 CBC 形态。口令取不到时返回 null。</summary>
    public static ChromiumOsCrypt FromPassword(Func<byte[]?> password, int iterations) => new(() =>
    {
        var secret = password();
        if (secret is null)
        {
            return null;
        }

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secret, Salt, iterations, HashAlgorithmName.SHA1, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }, gcm: false);

    /// <summary>Windows 的 GCM 形态,密钥直接给出(测试用)或从 Local State 解出。</summary>
    public static ChromiumOsCrypt FromAesKey(Func<byte[]?> key) => new(key, gcm: true);

    private static ChromiumOsCrypt FromWindowsLocalState(string localState) => FromAesKey(() =>
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(localState));
            var encoded = document.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString();
            var wrapped = Convert.FromBase64String(encoded ?? "");
            return wrapped.AsSpan().StartsWith("DPAPI"u8) ? WindowsDpapi.Unprotect(wrapped[5..]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        {
            return null;
        }
    });

    public string? TryDecrypt(ReadOnlySpan<byte> blob)
    {
        if (!blob.StartsWith(V10))
        {
            return null; // v11(Linux 密钥环)和不认识的前缀
        }

        if (!_loaded)
        {
            _loaded = true;
            try
            {
                _key = _loadKey();
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                _key = null;
            }
        }

        if (_key is null)
        {
            return null;
        }

        try
        {
            return _gcm ? DecryptGcm(_key, blob[V10.Length..]) : DecryptCbc(_key, blob[V10.Length..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string DecryptCbc(byte[] key, ReadOnlySpan<byte> cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, CbcIv, PaddingMode.PKCS7));
    }

    private static string DecryptGcm(byte[] key, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12 + 16)
        {
            throw new CryptographicException("Truncated blob.");
        }

        var plain = new byte[payload.Length - 12 - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload[..12], payload[12..^16], payload[^16..], plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>macOS 钥匙串:按服务名找通用口令(不限账户名)。找不到返回 null。</summary>
    private static partial class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";

        public static byte[]? FindPassword(IEnumerable<string> services)
        {
            foreach (var service in services)
            {
                var name = Encoding.UTF8.GetBytes(service);
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)name.Length, name, 0, null, out var length, out var data, IntPtr.Zero);
                if (status != 0 || data == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var password = new byte[length];
                    Marshal.Copy(data, password, 0, (int)length);
                    return password;
                }
                finally
                {
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                }
            }

            return null;
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[]? accountName,
            out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
    }

    /// <summary>Windows DPAPI(当前用户)。</summary>
    private static partial class WindowsDpapi
    {
        public static byte[]? Unprotect(byte[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                {
                    return null;
                }

                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [LibraryImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob dataOut);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr memory);
    }
}
