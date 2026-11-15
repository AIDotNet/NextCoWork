using System.Diagnostics;
using System.Text;

namespace Ncw.Desktop;

/// <summary>
/// <c>nextcowork://</c> 的系统注册(docs/design/04、07 §2「各平台注册」)。
/// macOS 靠应用包的 Info.plist(<c>CFBundleURLTypes</c>,由 <c>tools/Ncw.Build</c> 的 bundle 写入),运行时什么都不用做;
/// Windows 写 <c>HKCU\Software\Classes\nextcowork</c>;Linux 写 <c>~/.local/share/applications</c> 下的 <c>.desktop</c> 文件并用 <c>xdg-mime</c> 设为默认处理程序(AppImage 也靠这一步)。
/// 注册只做「尽力而为」:失败不影响启动(用户可以用「改用登录码」登录)。开发时用 <c>dotnet</c> 主机启动、或设了 <c>NCW_REGISTER_URL_SCHEME=0</c> 都不会去改系统注册。
/// </summary>
public static class UrlSchemeRegistration
{
    public const string Scheme = "nextcowork";

    public const string DesktopFileName = "nextcowork-url-handler.desktop";

    /// <summary>Windows 注册表里要写的项:(子键,值名,值)。值名 null = 默认值。纯数据,单测直接断言。</summary>
    public static IReadOnlyList<(string SubKey, string? Name, string Value)> WindowsEntries(string exePath) =>
    [
        (@"Software\Classes\nextcowork", null, "URL:NextCoWork"),
        (@"Software\Classes\nextcowork", "URL Protocol", ""),
        (@"Software\Classes\nextcowork\DefaultIcon", null, $"\"{exePath}\",0"),
        (@"Software\Classes\nextcowork\shell\open\command", null, $"\"{exePath}\" \"%1\""),
    ];

    /// <summary>Linux 的 <c>.desktop</c> 内容。<paramref name="execPath"/> 里的空格 / 引号按 Desktop Entry 规范转义。</summary>
    public static string LinuxDesktopEntry(string execPath)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=NextCoWork\n");
        sb.Append("Comment=NextCoWork URL handler\n");
        sb.Append("Exec=").Append(QuoteExec(execPath)).Append(" %u\n");
        sb.Append("Terminal=false\n");
        sb.Append("NoDisplay=true\n");
        sb.Append("MimeType=x-scheme-handler/").Append(Scheme).Append(";\n");
        return sb.ToString();
    }

    /// <summary>Desktop Entry 的 Exec 值:带空白或特殊字符就整体加双引号,并转义 <c>" ` $ \</c>。</summary>
    public static string QuoteExec(string path)
    {
        if (path.AsSpan().IndexOfAny(" \t\n\"'\\><~|&;$*?#()`") < 0)
        {
            return path;
        }

        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    /// <summary>启动时调用:按平台确保注册(幂等、不抛)。返回做了什么,方便日志。</summary>
    public static string EnsureRegistered()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("NCW_REGISTER_URL_SCHEME") == "0")
            {
                return "disabled";
            }

            var exe = ExecutablePath();
            if (exe is null)
            {
                return "dotnet-host"; // 开发时用 dotnet 启动:不要把 dotnet 注册成处理程序
            }

            if (OperatingSystem.IsWindows())
            {
                return RegisterWindows(exe);
            }

            if (OperatingSystem.IsLinux())
            {
                return RegisterLinux(exe);
            }

            return "bundle"; // macOS:Info.plist
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "failed";
        }
    }

    /// <summary>应用自己的可执行文件(AppImage 用 <c>APPIMAGE</c> 指向的那个文件,因为挂载点每次不同)。<c>dotnet</c> 主机启动时为 null。</summary>
    private static string? ExecutablePath()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
        {
            return appImage;
        }

        var path = Environment.ProcessPath;
        return path is null || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string RegisterWindows(string exe)
    {
        var changed = false;
        foreach (var (subKey, name, value) in WindowsEntries(exe))
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
            if (!string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal))
            {
                key.SetValue(name, value);
                changed = true;
            }
        }

        return changed ? "registered" : "up-to-date";
    }

    private static string RegisterLinux(string exe)
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var applications = Path.Combine(data, "applications");
        return WriteLinuxEntry(applications, exe, runXdgMime: true) ? "registered" : "up-to-date";
    }

    /// <summary>写 <c>.desktop</c>(内容没变就不动),需要时调用 <c>xdg-mime</c>。返回是否有改动。</summary>
    public static bool WriteLinuxEntry(string applicationsDirectory, string execPath, bool runXdgMime)
    {
        Directory.CreateDirectory(applicationsDirectory);
        var path = Path.Combine(applicationsDirectory, DesktopFileName);
        var content = LinuxDesktopEntry(execPath);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        File.WriteAllText(path, content);
        if (runXdgMime)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("xdg-mime", ["default", DesktopFileName, "x-scheme-handler/" + Scheme])
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
                process?.WaitForExit(5000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 没有 xdg-mime:.desktop 文件已写好,用户可以用「改用登录码」
            }
        }

        return true;
    }
}
