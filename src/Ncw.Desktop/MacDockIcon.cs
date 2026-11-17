using System.Reflection;
using System.Runtime.InteropServices;

namespace Ncw.Desktop;

/// <summary>
/// macOS Dock / ⌘Tab 里的应用图标。打成 <c>.app</c> 的发布包由 <c>Info.plist</c> 的 <c>CFBundleIconFile</c> 指向 <c>Contents/Resources/AppIcon.icns</c>(tools/Ncw.Build 的 bundle 放进去),
/// 系统自己读,这里不动。**不是从 .app 里启动的**(<c>dotnet run</c>、IDE)没有包,进程是 <c>dotnet</c> 本身,Dock 显示的是 dotnet 的默认图标——
/// 这时把内嵌的 <c>AppIcon.png</c> 设成 <c>NSApplication.applicationIconImage</c>。只改 Dock 里这一份,不写任何文件。
/// 图标失败不是错误:任何一步出问题都静默放弃(留着默认图标)。
/// </summary>
internal static partial class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string ResourceName = "Ncw.Desktop.AppIcon.png";

    /// <summary>在 Avalonia 的平台初始化之后、主线程上调用(那时 <c>NSApplication</c> 已经存在)。</summary>
    public static void ApplyWhenUnbundled()
    {
        if (!OperatingSystem.IsMacOS() || IsInsideAppBundle(AppContext.BaseDirectory))
        {
            return;
        }

        try
        {
            using var stream = typeof(MacDockIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return;
            }

            var png = new byte[stream.Length];
            stream.ReadExactly(png);
            Apply(png);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or InvalidOperationException)
        {
            // 留着默认图标
        }
    }

    /// <summary>可执行文件在 <c>X.app/Contents/MacOS/</c> 里(<see cref="AppContext.BaseDirectory"/> 就是那个目录)。</summary>
    internal static bool IsInsideAppBundle(string baseDirectory) =>
        baseDirectory.Replace('\\', '/').TrimEnd('/').EndsWith(".app/Contents/MacOS", StringComparison.Ordinal);

    private static unsafe void Apply(byte[] png)
    {
        IntPtr data;
        fixed (byte* bytes = png)
        {
            data = Send(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)bytes, (nuint)png.Length);
        }

        if (data == IntPtr.Zero)
        {
            return;
        }

        var image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
        if (image == IntPtr.Zero)
        {
            return;
        }

        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        if (app != IntPtr.Zero)
        {
            Send(app, Sel("setApplicationIconImage:"), image);
        }
    }

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr Send(IntPtr receiver, IntPtr selector) => objc_msgSend(receiver, selector);

    private static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a) => objc_msgSend(receiver, selector, a);

    private static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, nuint b) => objc_msgSend(receiver, selector, a, b);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr a, nuint b);
}
