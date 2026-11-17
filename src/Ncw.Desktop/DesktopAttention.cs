using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Ncw.UI.Controls;

namespace Ncw.Desktop;

/// <summary>
/// 操作系统的提醒能力(docs/design/03 §11.2)。任何一步失败都当作「做不到」,不影响使用。
/// <list type="bullet">
/// <item><b>macOS</b>:Dock 角标(<c>NSDockTile.badgeLabel</c>)、Dock 弹跳(<c>requestUserAttention:</c>)、系统通知(<c>NSUserNotificationCenter</c>;
/// 只有打成 .app、有 bundle identifier 时系统才给发,<c>dotnet run</c> 的开发进程没有,如实报告不支持)。点通知经委托方法回到应用,打开对应的那一条。</item>
/// <item><b>Windows</b>:窗口不在前台时任务栏按钮闪烁(<c>FlashWindowEx</c>);系统通知和角标没做(要注册 AUMID、走 WinRT toast),如实报告不支持。没有真机验证。</item>
/// <item><b>Linux</b>:有 <c>notify-send</c> 就用它发通知(点通知不能回到具体条目);没有角标。没有真机验证。</item>
/// </list>
/// </summary>
public static partial class DesktopAttention
{
    public static INativeAttention? Create()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return new MacAttention();
            }

            if (OperatingSystem.IsWindows())
            {
                return new WindowsAttention();
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxAttention();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
        }

        return null;
    }

    // ───────────────────────── macOS ─────────────────────────

    private sealed class MacAttention : INativeAttention
    {
        private const string UserInfoKey = "ncwKey";
        private static MacAttention? _current;
        private readonly IntPtr _center;

        public MacAttention()
        {
            NativeLibrary.TryLoad(AppKit, out _);
            NativeLibrary.TryLoad(Foundation, out _);
            var bundle = Msg(Class("NSBundle"), "mainBundle");
            var hasBundleId = bundle != IntPtr.Zero && Msg(bundle, "bundleIdentifier") != IntPtr.Zero;
            _center = hasBundleId ? Msg(Class("NSUserNotificationCenter"), "defaultUserNotificationCenter") : IntPtr.Zero;
            if (_center != IntPtr.Zero && EnsureDelegate() is var del && del != IntPtr.Zero)
            {
                MsgVoid(_center, "setDelegate:", del);
            }

            _current = this;
        }

        public bool SupportsNotifications => _center != IntPtr.Zero;

        public bool SupportsBadge => true;

        public event Action<string>? Activated;

        public void Notify(string title, string body, string key)
        {
            if (_center == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var notification = Msg(Msg(Class("NSUserNotification"), "alloc"), "init");
                MsgVoid(notification, "setTitle:", NsString(title));
                MsgVoid(notification, "setInformativeText:", NsString(body));
                var info = MsgPtrPtr(Class("NSDictionary"), "dictionaryWithObject:forKey:", NsString(key), NsString(UserInfoKey));
                MsgVoid(notification, "setUserInfo:", info);
                MsgVoid(_center, "deliverNotification:", notification);
                _ = Msg(notification, "release");
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
            }
        }

        public void SetBadge(int count)
        {
            var app = Msg(Class("NSApplication"), "sharedApplication");
            var tile = app == IntPtr.Zero ? IntPtr.Zero : Msg(app, "dockTile");
            if (tile != IntPtr.Zero)
            {
                MsgVoid(tile, "setBadgeLabel:", count > 0 ? NsString(count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : IntPtr.Zero);
            }
        }

        public void RequestAttention(Window window)
        {
            var app = Msg(Class("NSApplication"), "sharedApplication");
            if (app != IntPtr.Zero)
            {
                MsgLong(app, Sel("requestUserAttention:"), 10); // NSInformationalRequest:弹一下,不一直跳
            }
        }

        // 通知的委托:一个运行时创建的 NSObject 子类,两个方法——点了通知(打开那一条)、应用在前台时也显示横幅
        private static IntPtr _delegate;

        private static unsafe IntPtr EnsureDelegate()
        {
            if (_delegate != IntPtr.Zero)
            {
                return _delegate;
            }

            var cls = objc_getClass("NcwUserNotificationDelegate");
            if (cls == IntPtr.Zero)
            {
                cls = objc_allocateClassPair(Class("NSObject"), "NcwUserNotificationDelegate", 0);
                if (cls == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                class_addMethod(cls, Sel("userNotificationCenter:didActivateNotification:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidActivate, "v@:@@");
                class_addMethod(cls, Sel("userNotificationCenter:shouldPresentNotification:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte>)&ShouldPresent, "c@:@@");
                objc_registerClassPair(cls);
            }

            _delegate = Msg(Msg(cls, "alloc"), "init");
            return _delegate;
        }

        [UnmanagedCallersOnly]
        private static void DidActivate(IntPtr self, IntPtr cmd, IntPtr center, IntPtr notification)
        {
            try
            {
                var info = Msg(notification, "userInfo");
                var value = info == IntPtr.Zero ? IntPtr.Zero : MsgPtrPtr1(info, "objectForKey:", NsString(UserInfoKey));
                var utf8 = value == IntPtr.Zero ? IntPtr.Zero : Msg(value, "UTF8String");
                if (utf8 != IntPtr.Zero && Marshal.PtrToStringUTF8(utf8) is { Length: > 0 } key && _current is { } current)
                {
                    Dispatcher.UIThread.Post(() => current.Activated?.Invoke(key));
                }

                MsgVoid(center, "removeDeliveredNotification:", notification);
            }
            catch (Exception)
            {
                // 回调里不能让异常穿过原生边界
            }
        }

        [UnmanagedCallersOnly]
        private static byte ShouldPresent(IntPtr self, IntPtr cmd, IntPtr center, IntPtr notification) => 1;
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr Msg(IntPtr receiver, string selector) => objc_msgSend_ptr(receiver, Sel(selector));

    private static void MsgVoid(IntPtr receiver, string selector, IntPtr arg) => objc_msgSend_void(receiver, Sel(selector), arg);

    private static IntPtr MsgPtrPtr(IntPtr receiver, string selector, IntPtr a, IntPtr b) => objc_msgSend_ptr_ptr_ptr(receiver, Sel(selector), a, b);

    private static IntPtr MsgPtrPtr1(IntPtr receiver, string selector, IntPtr a) => objc_msgSend_ptr_ptr(receiver, Sel(selector), a);

    private static void MsgLong(IntPtr receiver, IntPtr selector, long value) => objc_msgSend_long(receiver, selector, value);

    /// <summary>autorelease 的 NSString(调用都在主线程的 run loop 里,随 autorelease pool 释放)。</summary>
    private static IntPtr NsString(string value) => objc_msgSend_str(Class("NSString"), Sel("stringWithUTF8String:"), value);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC)]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr imp, string types);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr_ptr(IntPtr receiver, IntPtr selector, IntPtr a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_ptr_ptr_ptr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial long objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_msgSend_str(IntPtr receiver, IntPtr selector, string arg);

    // ───────────────────────── Windows ─────────────────────────

    private sealed partial class WindowsAttention : INativeAttention
    {
        public bool SupportsNotifications => false;

        public bool SupportsBadge => false;

#pragma warning disable CS0067 // Windows 上没有系统通知,也就没有点击
        public event Action<string>? Activated;
#pragma warning restore CS0067

        public void Notify(string title, string body, string key)
        {
        }

        public void SetBadge(int count)
        {
        }

        public void RequestAttention(Window window)
        {
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var hwnd } && hwnd != IntPtr.Zero)
            {
                var info = new FlashInfo
                {
                    Size = (uint)Unsafe.SizeOf<FlashInfo>(),
                    Hwnd = hwnd,
                    Flags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG:闪到窗口回到前台
                    Count = uint.MaxValue,
                    Timeout = 0,
                };
                FlashWindowEx(ref info);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Hwnd;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlashWindowEx(ref FlashInfo info);
    }

    // ───────────────────────── Linux ─────────────────────────

    private sealed class LinuxAttention : INativeAttention
    {
        private readonly string? _notifySend = FindOnPath("notify-send");

        public bool SupportsNotifications => _notifySend is not null;

        public bool SupportsBadge => false;

#pragma warning disable CS0067 // notify-send 的点击回不到应用
        public event Action<string>? Activated;
#pragma warning restore CS0067

        public void Notify(string title, string body, string key)
        {
            if (_notifySend is null)
            {
                return;
            }

            try
            {
                var start = new ProcessStartInfo(_notifySend) { UseShellExecute = false };
                start.ArgumentList.Add("--app-name=NextCoWork");
                start.ArgumentList.Add(title);
                start.ArgumentList.Add(body);
                using var process = Process.Start(start);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        public void SetBadge(int count)
        {
        }

        public void RequestAttention(Window window)
        {
        }

        private static string? FindOnPath(string name) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, name))
                .FirstOrDefault(File.Exists);
    }
}
