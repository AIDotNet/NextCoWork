using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ncw.Agent.Environments;

/// <summary>
/// 本机进程树终止。
/// <list type="bullet">
/// <item>Windows:<c>Process.Kill(entireProcessTree: true)</c>(等价于 <c>taskkill /T /F</c>)。</item>
/// <item>POSIX:先对整棵树的快照发 SIGTERM,宽限期内没退干净的再 SIGKILL。.NET 的 <see cref="Process"/> 没法让子进程自成进程组,
/// 所以不能像 2.x(<c>detached</c> + <c>kill(-pid)</c>)那样按进程组杀,改成按「后代快照」杀:先拍快照再发信号,
/// 根进程先死也不会漏掉它的子孙。代价:自己 <c>setsid</c>/双重 fork 脱离的守护进程杀不到(进程组方式同样杀不到 <c>setsid</c> 的)。</item>
/// </list>
/// </summary>
internal static partial class ProcessTree
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    /// <summary>根进程之外的所有后代(广度优先)。读不到进程表时返回空。</summary>
    public static IReadOnlyList<int> Descendants(int root)
    {
        var children = ReadChildren();
        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);
        var seen = new HashSet<int> { root };
        while (queue.TryDequeue(out var pid))
        {
            if (!children.TryGetValue(pid, out var list))
            {
                continue;
            }

            foreach (var child in list)
            {
                if (seen.Add(child))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    public static async Task TerminateAsync(Process process, Task exited, TimeSpan grace)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                // 已经退出;AggregateException = 根进程已经处理过、个别后代没杀掉(比如权限不同的沙箱子进程),调用方以根进程是否退出为准
            }

            return;
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var tree = new List<int> { pid };
        tree.AddRange(Descendants(pid));
        foreach (var target in tree)
        {
            Signal(target, SigTerm);
        }

        // 宽限期内等整棵树退干净(根进程以 exited 为准,其余按 kill(pid, 0) 探测)
        var deadline = Stopwatch.GetTimestamp() + (long)(grace.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline && (!exited.IsCompleted || tree.Skip(1).Any(IsAlive)))
        {
            await Task.WhenAny(exited, Task.Delay(30)).ConfigureAwait(false);
        }

        // 宽限期里新派生的后代也要带走
        if (!exited.IsCompleted)
        {
            tree.AddRange(Descendants(pid).Except(tree));
        }

        foreach (var target in tree)
        {
            if (target == pid ? !exited.IsCompleted : IsAlive(target))
            {
                Signal(target, SigKill);
            }
        }
    }

    private static bool IsAlive(int pid) => Signal(pid, 0);

    private static bool Signal(int pid, int signal) => kill(pid, signal) == 0;

    private static Dictionary<int, List<int>> ReadChildren()
    {
        var table = new Dictionary<int, List<int>>();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out var pid))
                    {
                        continue;
                    }

                    try
                    {
                        // /proc/<pid>/stat: "pid (comm) state ppid ..." —— comm 可能含空格和括号,从最后一个 ')' 往后解析
                        var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                        var close = stat.LastIndexOf(')');
                        var fields = stat[(close + 2)..].Split(' ');
                        if (fields.Length > 1 && int.TryParse(fields[1], out var parent))
                        {
                            Add(table, parent, pid);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                    {
                        // 进程在枚举过程中退出
                    }
                }
            }
            else
            {
                using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-A", "-o", "pid=,ppid="])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (ps is null)
                {
                    return table;
                }

                var output = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
                    {
                        Add(table, parent, pid);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // 读不到进程表:退化为只杀根进程
        }

        return table;
    }

    private static void Add(Dictionary<int, List<int>> table, int parent, int child)
    {
        if (!table.TryGetValue(parent, out var list))
        {
            table[parent] = list = [];
        }

        list.Add(child);
    }

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int kill(int pid, int signal);
}
