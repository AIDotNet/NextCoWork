using System.Runtime.InteropServices;
using Avalonia.Threading;
using Ncw.UI.Controls;

namespace Ncw.Desktop;

/// <summary>
/// Windows 的系统托盘(docs/design/03 §11.2,issue #200):通知区域图标(<c>Shell_NotifyIcon</c>)+ 系统弹出菜单(<c>TrackPopupMenuEx</c>)。
/// <para>
/// 为什么不用 Avalonia 的 <c>TrayIcon</c>:它在 Windows 上右键时自己弹一个无边框、置顶、透明的 Avalonia 窗口当菜单(<c>TrayPopupRoot</c>),
/// 3.0.5 给菜单补了模板后,无界面测试里能画出来、能点,但用户在 Windows 真机上右键仍然什么都不出现。这里换成所有 Windows 程序都用的做法
/// (WinForms 的 NotifyIcon 也是这样):一个不显示的顶层窗口接托盘消息,右键时先把它设为前台窗口,再弹系统菜单,弹完补发一条 <c>WM_NULL</c>
/// (否则菜单第二次弹出时点外面关不掉,见 <c>TrackPopupMenu</c> 文档的说明)。系统菜单的样式跟随系统,不走 Arc token。
/// </para>
/// <para>菜单内容每次右键时向 <see cref="NcwAttention"/> 取快照;选中的命令和左键单击都投递回 UI 线程执行,不在窗口过程里直接跑。</para>
/// <para>图标取本程序 exe 内嵌的应用图标(<c>ApplicationIcon</c>,系统小图标尺寸)。资源管理器重启时(<c>TaskbarCreated</c>)重新添加图标。</para>
/// 没有 Windows 真机自动化;任何一步失败都返回 null,调用方退回 Avalonia 的托盘。
/// </summary>
public static unsafe partial class WindowsTray
{
    private const string WindowClass = "NextCoWork.TrayWindow";
    private const uint CallbackMessage = 0x8000 + 0x4E;   // WM_APP + n
    private const uint IconId = 1;

    private static Tray? _current;
    private static bool _classRegistered;
    private static uint _taskbarCreated;

    /// <summary><see cref="NativeTrayFactory"/>:只在 Windows 上建系统托盘,其他平台返回 null(用 Avalonia 的托盘)。</summary>
    public static INativeTray? Create(string toolTip, Func<IReadOnlyList<NativeTrayEntry>> menu, Action activate)
    {
        if (!OperatingSystem.IsWindows() || _current is not null)
        {
            return null;
        }

        var module = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            fixed (char* name = WindowClass)
            {
                var wc = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    WndProc = &WndProc,
                    Instance = module,
                    ClassName = name,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    return null;
                }
            }

            _classRegistered = true;
        }

        // 不显示的顶层窗口(不能是 HWND_MESSAGE:弹菜单前要把它设为前台窗口)
        var hwnd = CreateWindowExW(0, WindowClass, "NextCoWork", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, module, 0);
        if (hwnd == 0)
        {
            return null;
        }

        // 以管理员身份运行时,资源管理器(普通权限)发来的 TaskbarCreated 默认会被 UIPI 挡掉
        _ = ChangeWindowMessageFilterEx(hwnd, _taskbarCreated, 1 /* MSGFLT_ALLOW */, 0);
        var tray = new Tray(hwnd, LoadAppIcon(), toolTip, menu, activate);
        if (!tray.Add())
        {
            tray.Destroy();
            return null;
        }

        _current = tray;
        return tray;
    }

    /// <summary>菜单文字里的 <c>&amp;</c> 在系统菜单中是助记符前缀,原样显示要写成 <c>&amp;&amp;</c>。</summary>
    internal static string MenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private static nint LoadAppIcon()
    {
        nint small = 0;
        if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, null, &small, 1) > 0 && small != 0)
        {
            return small;
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // 异常不能穿过非托管边界(会直接结束进程):托盘出问题只影响托盘
        try
        {
            if (_current is { } tray && tray.Hwnd == hwnd)
            {
                if (msg == CallbackMessage)
                {
                    tray.OnIconMessage((uint)(lParam & 0xFFFF));
                    return 0;
                }

                if (msg == _taskbarCreated && _taskbarCreated != 0)
                {
                    tray.Add();
                    return 0;
                }
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"tray: {e}");
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private sealed class Tray(nint hwnd, nint icon, string toolTip, Func<IReadOnlyList<NativeTrayEntry>> menu, Action activate) : INativeTray
    {
        private const uint WmNull = 0x0000;
        private const uint WmContextMenu = 0x007B;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmRButtonUp = 0x0205;

        private string _toolTip = toolTip;
        private bool _menuOpen;

        public nint Hwnd { get; private set; } = hwnd;

        public bool Add() => Notify(0 /* NIM_ADD */) || Notify(1 /* NIM_MODIFY:资源管理器里还留着同 id 的图标时 */);

        public void SetToolTip(string text)
        {
            _toolTip = text;
            Notify(1 /* NIM_MODIFY */);
        }

        public void OnIconMessage(uint mouse)
        {
            switch (mouse)
            {
                case WmLButtonUp:
                    Dispatcher.UIThread.Post(activate);
                    break;
                case WmRButtonUp or WmContextMenu:
                    ShowMenu();
                    break;
            }
        }

        private void ShowMenu()
        {
            if (_menuOpen)
            {
                return;
            }

            var entries = menu();
            if (entries.Count == 0)
            {
                return;
            }

            var popup = CreatePopupMenu();
            if (popup == 0)
            {
                return;
            }

            _menuOpen = true;
            try
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry.IsSeparator)
                    {
                        AppendMenuW(popup, 0x0800 /* MF_SEPARATOR */, 0, null);
                    }
                    else
                    {
                        // 命令 id 从 1 开始:TrackPopupMenuEx 返回 0 表示没选
                        AppendMenuW(popup, entry.IsEnabled ? 0u : 0x0001 /* MF_GRAYED */, (nuint)(i + 1), MenuText(entry.Text));
                    }
                }

                GetCursorPos(out var point);
                SetForegroundWindow(Hwnd);
                var align = GetSystemMetrics(40 /* SM_MENUDROPALIGNMENT */) != 0 ? 0x0008u /* TPM_RIGHTALIGN */ : 0u;
                var flags = 0x0100u /* TPM_RETURNCMD */ | 0x0080 /* TPM_NONOTIFY */ | 0x0002 /* TPM_RIGHTBUTTON */ | 0x0020 /* TPM_BOTTOMALIGN */ | align;
                var chosen = TrackPopupMenuEx(popup, flags, point.X, point.Y, Hwnd, 0);
                PostMessageW(Hwnd, WmNull, 0, 0);
                if (chosen > 0 && chosen <= entries.Count && entries[chosen - 1] is { IsEnabled: true, Invoke: { } invoke })
                {
                    Dispatcher.UIThread.Post(invoke);
                }
            }
            finally
            {
                DestroyMenu(popup);
                _menuOpen = false;
            }
        }

        public void Dispose()
        {
            if (Hwnd == 0)
            {
                return;
            }

            Notify(2 /* NIM_DELETE */);
            Destroy();
        }

        public void Destroy()
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }

            if (Hwnd != 0)
            {
                DestroyWindow(Hwnd);
                Hwnd = 0;
            }

            if (icon != 0)
            {
                DestroyIcon(icon);
            }
        }

        private bool Notify(uint message)
        {
            var data = new NotifyIconData
            {
                Size = (uint)sizeof(NotifyIconData),
                Hwnd = Hwnd,
                Id = IconId,
            };
            if (message != 2)
            {
                data.Flags = 0x1 /* NIF_MESSAGE */ | 0x4 /* NIF_TIP */ | (icon != 0 ? 0x2u /* NIF_ICON */ : 0);
                data.CallbackMessage = CallbackMessage;
                data.Icon = icon;
                var tip = _toolTip.AsSpan(0, Math.Min(_toolTip.Length, 127));   // szTip 最多 128 个字符(含结尾的 0)
                tip.CopyTo(new Span<char>(data.Tip, 128));
            }

            return Shell_NotifyIconW(message, &data) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WndClassEx* wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
