using System.Runtime.InteropServices;

namespace Ncw.Desktop;

/// <summary>
/// 操作系统的「减少动态效果」探测(docs/design/03 §9.3)。Avalonia 12.1.5 的 <c>IPlatformSettings</c> 没有这个信息(只有主题与对比度),Arc 也不读,
/// 所以桌面宿主自己问系统,经 <c>NcwMotion.SystemReducesMotionProbe</c> 交给界面层;只在「外观 · 动效 = 跟随系统」时才用到。
/// <list type="bullet">
/// <item>macOS:<c>NSWorkspace.sharedWorkspace.accessibilityDisplayShouldReduceMotion</c>(「辅助功能 · 显示 · 减弱动态效果」),在 macOS 上实测能调用、返回当前值。</item>
/// <item>Windows:<c>SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)</c>(「设置 · 辅助功能 · 视觉效果 · 动画效果」关闭 = 要求减少)。<b>没有 Windows 机器可测</b>,同样的调用方式是系统文档里的标准用法。</item>
/// <item>Linux:读不到(GNOME 的 <c>enable-animations</c> 要读 gsettings,KDE 另有一套;没有机器可测,先不猜),返回 <c>null</c> → 按「完整」处理,外观页如实说明。</item>
/// </list>
/// 探测只在启动和偏好变化时调用一次,不轮询:系统设置在应用运行期间改了,要等下一次启动或在外观页重选才会跟上。任何失败都当作「读不到」。
/// </summary>
public static partial class SystemMotionProbe
{
    public static bool? ReducesMotion()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacReduceMotion();
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsReduceMotion();
            }
        }
        catch (Exception)
        {
            // 读不到就是读不到:不影响启动
        }

        return null;
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static bool? MacReduceMotion()
    {
        // NSWorkspace 在 AppKit 里;Avalonia 的原生层已经加载了它,这里再确认一次(幂等)
        if (!NativeLibrary.TryLoad(AppKit, out _))
        {
            return null;
        }

        var workspaceClass = objc_getClass("NSWorkspace");
        if (workspaceClass == IntPtr.Zero)
        {
            return null;
        }

        var shared = objc_msgSend_ptr(workspaceClass, sel_registerName("sharedWorkspace"));
        if (shared == IntPtr.Zero)
        {
            return null;
        }

        return objc_msgSend_bool(shared, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0;
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    private const uint SpiGetClientAreaAnimation = 0x1042;

    private static bool? WindowsReduceMotion() =>
        SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animations, 0) ? animations == 0 : null;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
