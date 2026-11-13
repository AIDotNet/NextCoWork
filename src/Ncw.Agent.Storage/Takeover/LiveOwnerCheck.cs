using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ncw.Agent.Storage.Takeover;

/// <summary>
/// 判断 2.x 是否还在使用一份数据。规则来自 2.x 的 <c>assertNoLiveOwner</c>:
/// <list type="bullet">
/// <item>Chromium 的 <c>SingletonLock</c>(Unix 上是指向 <c>主机名-pid</c> 的符号链接)指向活进程 → 2.x 桌面端在运行。
///   与 2.x 不同,这里同时检查数据根和它的上一级(2.x 的 Electron profile 根是 <c>~/.next-cowork</c>,数据根是其下的 <c>data/</c>,
///   SingletonLock 落在 profile 根;2.x 只查数据根会漏掉)。Windows 上 Chromium 独占打开 <c>lockfile</c>,打不开即在运行。</item>
/// <item><c>service.lock</c>(2.x 独立设备服务)的 pid 还活着,或者文件读不懂 → 按「在运行」处理。</item>
/// <item>陈旧的锁(进程已经不在)不挡路。</item>
/// </list>
/// </summary>
public static partial class LiveOwnerCheck
{
    /// <summary>有活着的 2.x 进程在用 <paramref name="roots"/> 中任一目录时抛 <see cref="TakeoverException"/>。</summary>
    public static void AssertNone(IEnumerable<string> roots, Func<int, bool>? isAlive = null)
    {
        isAlive ??= ProcessAlive;
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            CheckSingletonLock(root, isAlive);
            CheckServiceLock(root, isAlive);
        }
    }

    /// <summary>数据根本身,以及当它是 2.x 布局的 <c>data/</c> 时的 profile 根。</summary>
    public static IReadOnlyList<string> RootsFor(string dataRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        return Path.GetFileName(full) == LegacyLayout.DataSubdirectory && Path.GetDirectoryName(full) is { } profile
            ? [full, profile]
            : [full];
    }

    private static void CheckSingletonLock(string root, Func<int, bool> isAlive)
    {
        if (OperatingSystem.IsWindows())
        {
            var lockfile = Path.Combine(root, "lockfile");
            if (!File.Exists(lockfile))
            {
                return;
            }

            try
            {
                using var probe = new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new TakeoverException(TakeoverFailure.LegacyAppRunning, "The 2.x desktop app is still running on this data root.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new TakeoverException(TakeoverFailure.OwnerUnknown, "Cannot determine whether the 2.x desktop app is running.");
            }

            return;
        }

        var path = Path.Combine(root, "SingletonLock");
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not { } target)
        {
            if (info.Exists || Directory.Exists(path))
            {
                // 存在但不是符号链接:不认识的格式,不抢。
                throw new TakeoverException(TakeoverFailure.OwnerUnknown, "Cannot determine whether the 2.x desktop app is running.");
            }

            return;
        }

        var dash = target.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(target.AsSpan(dash + 1), out var pid) && pid > 0 && isAlive(pid))
        {
            throw new TakeoverException(TakeoverFailure.LegacyAppRunning, "The 2.x desktop app is still running on this data root.");
        }
    }

    private static void CheckServiceLock(string root, Func<int, bool> isAlive)
    {
        var path = Path.Combine(root, "service.lock");
        if (!File.Exists(path))
        {
            return;
        }

        var pid = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("pid", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
            {
                pid = parsed;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // 读不懂按「忙」处理。
        }

        if (pid <= 0 || isAlive(pid))
        {
            throw new TakeoverException(TakeoverFailure.LegacyServiceRunning, "A 2.x device service is running on this data root.");
        }
    }

    public static bool ProcessAlive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // kill(pid, 0):成功或 EPERM(进程在,但不归我们)都算活着;只有 ESRCH 是不在。
        if (kill(pid, 0) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() != Esrch;
    }

    private const int Esrch = 3;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
