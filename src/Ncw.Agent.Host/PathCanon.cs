using System.Runtime.InteropServices;

namespace Ncw.Agent.Host;

/// <summary>
/// 目录的规范路径。与 2.x 一样立刻取 realpath(macOS 上 /var → /private/var、符号链接展开),
/// 否则路径围栏和「同一个目录只登记一次」要面对两个都「正确」的根。
/// </summary>
public static partial class PathCanon
{
    public static string RealPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            return full;
        }

        var resolved = realpath(full, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return full;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? full;
        }
        finally
        {
            free(resolved);
        }
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr realpath(string path, IntPtr resolved);

    [LibraryImport("libc")]
    private static partial void free(IntPtr pointer);
}
