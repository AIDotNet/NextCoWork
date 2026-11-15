using System.Runtime.InteropServices;
using System.Text;

namespace Ncw.Desktop;

/// <summary>
/// 桌面端放小块机密的地方(控制端身份、账户刷新令牌),按平台用系统的安全存储,<b>绝不降级为明文文件</b>:
/// macOS:钥匙串里的通用口令(本应用自己创建的条目,读取不需要授权);Windows:DPAPI(当前用户)加密的文件;
/// Linux 的 libsecret 还没接上:抛 <see cref="PlatformNotSupportedException"/>,调用方拒绝保存并提示用户。
/// </summary>
public sealed partial class PlatformSecretStore(string clientDirectory)
{
    /// <param name="windowsFile">Windows 上放 DPAPI 密文的文件名(在客户端目录下)。</param>
    public string? Read(string service, string account, string windowsFile)
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacKeychain.Find(service, account);
        }

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(clientDirectory, windowsFile);
            return File.Exists(path) ? Encoding.UTF8.GetString(WindowsDpapi.Unprotect(File.ReadAllBytes(path))) : null;
        }

        throw new PlatformNotSupportedException("No secure key store on this platform yet");
    }

    /// <summary>写入;已有就覆盖。</summary>
    public void Write(string service, string account, string windowsFile, string value)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacKeychain.Store(service, account, value);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(clientDirectory);
            File.WriteAllBytes(Path.Combine(clientDirectory, windowsFile), WindowsDpapi.Protect(Encoding.UTF8.GetBytes(value)));
            return;
        }

        throw new PlatformNotSupportedException("No secure key store on this platform yet");
    }

    /// <summary>删除;本来就没有也成功。</summary>
    public void Delete(string service, string account, string windowsFile)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacKeychain.Remove(service, account);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(clientDirectory, windowsFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        throw new PlatformNotSupportedException("No secure key store on this platform yet");
    }

    private static partial class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
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

            // 已有条目就原地更新(刷新令牌会轮换,每次刷新都要写)
            var status = FindItem(s, a, out var item);
            if (status == 0)
            {
                try
                {
                    var update = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)v.Length, v);
                    if (update != 0)
                    {
                        throw new PlatformNotSupportedException($"Keychain update failed ({update})");
                    }
                }
                finally
                {
                    CFRelease(item);
                }

                return;
            }

            if (status != ItemNotFound)
            {
                throw new PlatformNotSupportedException($"Keychain lookup failed ({status})");
            }

            var add = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)v.Length, v, IntPtr.Zero);
            if (add != 0)
            {
                throw new PlatformNotSupportedException($"Keychain write failed ({add})");
            }
        }

        public static void Remove(string service, string account)
        {
            var status = FindItem(Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(account), out var item);
            if (status == ItemNotFound)
            {
                return;
            }

            if (status != 0)
            {
                throw new PlatformNotSupportedException($"Keychain lookup failed ({status})");
            }

            try
            {
                var delete = SecKeychainItemDelete(item);
                if (delete != 0)
                {
                    throw new PlatformNotSupportedException($"Keychain delete failed ({delete})");
                }
            }
            finally
            {
                CFRelease(item);
            }
        }

        private static int FindItem(byte[] service, byte[] account, out IntPtr item)
        {
            var status = SecKeychainFindGenericPasswordItem(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, IntPtr.Zero, IntPtr.Zero, out item);
            return status;
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

        [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
        private static partial int SecKeychainFindGenericPasswordItem(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport(CoreFoundation)]
        private static partial void CFRelease(IntPtr cf);
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
