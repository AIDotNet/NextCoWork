using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Ncw.Desktop;

/// <summary>
/// 原生窗口的背景色(docs/design/03 §10.6):窗口出现的第一帧就必须是加载界面的底色,不能先白屏再变深色。
/// Avalonia 的 <c>Window.Background</c> 是它自己渲染的第一帧才有的;在那之前原生窗口显示的是系统给的默认底(macOS 的 NSWindow 随系统外观,浅色系统下是浅灰 / 白),
/// 所以这里在窗口显示之前把 NSWindow 的 <c>backgroundColor</c> 设成加载界面用的同一个 Arc token 解析出来的颜色(RGB 由 <c>NcwApp</c> 传进来,这里没有任何颜色值)。
/// <list type="bullet">
/// <item><b>macOS</b>:已实现并用 <see cref="TryRead"/> 读回核对(tools/StartupSmoke)。</item>
/// <item><b>Windows / Linux</b>:没有做,也没有机器可验证——Windows 上窗口类的背景画刷、Linux 上各合成器的首帧行为各不相同;没做的平台上第一帧是否闪色不下结论,见设计文档 §10.6。</item>
/// </list>
/// 任何失败都当作「做不到」,不影响启动。
/// </summary>
public static partial class NativeWindowBackground
{
    /// <summary>把窗口的原生背景色设成 (r, g, b)。</summary>
    public static void Apply(Window window, byte r, byte g, byte b)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && NativeWindow(window) is { } nsWindow)
            {
                MacSet(nsWindow, r / 255.0, g / 255.0, b / 255.0);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // 做不到就是做不到:窗口照常显示
        }
    }

    /// <summary>读回原生窗口当前的背景色(sRGB,0–255);读不到返回 null。验证用。</summary>
    public static (byte R, byte G, byte B)? TryRead(Window window)
    {
        try
        {
            return OperatingSystem.IsMacOS() && NativeWindow(window) is { } nsWindow ? MacRead(nsWindow) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IntPtr? NativeWindow(Window window) =>
        window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow", Handle: var handle } && handle != IntPtr.Zero ? handle : null;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static void MacSet(IntPtr nsWindow, double r, double g, double b)
    {
        if (!NativeLibrary.TryLoad(AppKit, out _))
        {
            return;
        }

        var color = objc_msgSend_color(objc_getClass("NSColor"), sel_registerName("colorWithSRGBRed:green:blue:alpha:"), r, g, b, 1.0);
        if (color != IntPtr.Zero)
        {
            objc_msgSend_void(nsWindow, sel_registerName("setBackgroundColor:"), color);
        }
    }

    private static (byte, byte, byte)? MacRead(IntPtr nsWindow)
    {
        var color = objc_msgSend_ptr(nsWindow, sel_registerName("backgroundColor"));
        if (color == IntPtr.Zero)
        {
            return null;
        }

        var srgb = objc_msgSend_ptr(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
        var converted = objc_msgSend_ptr_ptr(color, sel_registerName("colorUsingColorSpace:"), srgb);
        if (converted == IntPtr.Zero)
        {
            return null;
        }

        objc_msgSend_rgba(converted, sel_registerName("getRed:green:blue:alpha:"), out var r, out var g, out var b, out _);
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_color(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_rgba(IntPtr receiver, IntPtr selector, out double r, out double g, out double b, out double a);
}
