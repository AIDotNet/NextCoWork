using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Services;
using Ncw.Protocol.Security;

namespace Ncw.Desktop;

/// <summary>
/// 桌面端控制端身份的保存位置(docs/design/09 §4):私钥放系统钥匙串,不落明文。
/// macOS:钥匙串里的通用口令(服务「NextCoWork Controller」);Windows:DPAPI(当前用户)加密的文件;
/// Linux 的 libsecret 还没接上,先拒绝(配对时提示「暂时无法安全保存配对密钥」),绝不降级为明文。
/// </summary>
public sealed partial class ControllerKeyStore(string clientDirectory) : IControllerIdentityStore
{
    private const string Service = "NextCoWork Controller";
    private const string Account = "identity-v1";
    private readonly object _gate = new();
    private PeerIdentity? _cached;

    public PeerIdentity LoadOrCreate()
    {
        lock (_gate)
        {
            return _cached ??= LoadOrCreateCore();
        }
    }

    private PeerIdentity LoadOrCreateCore()
    {
        if (Read() is { } existing)
        {
            try
            {
                var file = JsonSerializer.Deserialize(existing, KeyJson.Default.StoredIdentity)!;
                return PeerIdentity.Load(Convert.FromBase64String(file.Certificate), Convert.FromBase64String(file.PrivateKey));
            }
            catch (Exception e) when (e is JsonException or FormatException or System.Security.Cryptography.CryptographicException or NullReferenceException)
            {
                // 存坏了:生成新的身份(之前配对过的设备需要重新配对)。
            }
        }

        var identity = PeerIdentity.Create($"NextCoWork {Environment.MachineName}");
        var json = JsonSerializer.Serialize(new StoredIdentity(Convert.ToBase64String(identity.Certificate), Convert.ToBase64String(identity.PrivateKey)),
            KeyJson.Default.StoredIdentity);
        Write(json);
        return identity;
    }

    private string? Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacKeychain.Find(Service, Account);
        }

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(clientDirectory, "controller-identity.dpapi");
            return File.Exists(path) ? Encoding.UTF8.GetString(WindowsDpapi.Unprotect(File.ReadAllBytes(path))) : null;
        }

        throw new PlatformNotSupportedException("No secure key store on this platform yet");
    }

    private void Write(string value)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacKeychain.Store(Service, Account, value);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(clientDirectory);
            File.WriteAllBytes(Path.Combine(clientDirectory, "controller-identity.dpapi"), WindowsDpapi.Protect(Encoding.UTF8.GetBytes(value)));
            return;
        }

        throw new PlatformNotSupportedException("No secure key store on this platform yet");
    }

    public sealed record StoredIdentity(string Certificate, string PrivateKey);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StoredIdentity))]
    internal sealed partial class KeyJson : JsonSerializerContext;

    /// <summary>macOS 钥匙串的通用口令(本应用自己创建的条目,读取不需要授权)。</summary>
    private static partial class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const int ItemNotFound = -25300;

        public static string? Find(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, IntPtr.Zero);
            if (status == ItemNotFound)
            {
                return null;
            }

            if (status != 0)
            {
                throw new PlatformNotSupportedException($"Keychain read failed ({status})");
            }

            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }

        public static void Store(string service, string account, string value)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var v = Encoding.UTF8.GetBytes(value);
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)v.Length, v, IntPtr.Zero);
            if (status != 0)
            {
                throw new PlatformNotSupportedException($"Keychain write failed ({status})");
            }
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
    }

    /// <summary>Windows DPAPI(当前用户作用域)。</summary>
    private static partial class WindowsDpapi
    {
        public static byte[] Protect(byte[] data) => Run(data, protect: true);

        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
                if (!ok)
                {
                    throw new PlatformNotSupportedException($"DPAPI failed ({Marshal.GetLastPInvokeError()})");
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
        private static partial bool CryptProtectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob dataOut);

        [LibraryImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob dataOut);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr memory);
    }
}
