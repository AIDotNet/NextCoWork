using System.Diagnostics;

namespace Ncw.Agent.Terminal;

/// <summary>
/// POSIX 进程表快照(只读 ppid 关系),用来在终止终端时找出整棵后代树:
/// 作业控制的 shell 会给每个任务单独开一个进程组,只按终端会话的进程组杀会漏掉它们。
/// Linux 读 <c>/proc</c>,macOS 用 <c>ps</c>。读不到进程表时返回空(退化为只按进程组杀)。
/// </summary>
internal static class ProcessTable
{
    /// <summary>根进程之外的所有后代(广度优先)。</summary>
    public static IReadOnlyList<int> Descendants(int root)
    {
        var children = ReadChildren();
        var result = new List<int>();
        var queue = new Queue<int>();
        var seen = new HashSet<int> { root };
        queue.Enqueue(root);
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
            else if (OperatingSystem.IsMacOS())
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
            // 读不到进程表:退化为只杀进程组
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
}
