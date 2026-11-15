namespace Ncw.Core.Domain;

/// <summary>
/// 「按行数增删计数」(移植自 2.x <c>shared/domain/line-diff.ts</c> 的 <c>countLineDiff</c>):改动集卡片上的 <c>+X −Y</c>。
/// 纯函数,Agent(封包改动集时算)和控制端共用同一个口径。
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// 超过这个格子数就不做精确 LCS,退化成按行计数的多重集差(数字可能略偏,但不会同步跑上几亿次比较)。
    /// </summary>
    private const long MaxLcsCells = 16_000_000;

    /// <summary>
    /// 两段文本的行级增删计数。<paramref name="before"/> 为 null 表示新建(所有行都算新增);<paramref name="after"/> 为 null 表示删除。
    /// 空文件 = 0 行;唯一的尾随空行不参与计数。
    /// </summary>
    public static (int Additions, int Deletions) Count(string? before, string? after)
    {
        var beforeLines = ToLines(before);
        var afterLines = ToLines(after);
        if (before is null)
        {
            return (afterLines.Count, 0);
        }

        if (after is null)
        {
            return (0, beforeLines.Count);
        }

        var common = LcsLength(beforeLines, afterLines);
        return (afterLines.Count - common, beforeLines.Count - common);
    }

    /// <summary>
    /// 只要计数的话不需要回溯:增删数 = 各自行数 − LCS 长度。先剥掉公共首尾行(它们一定在某个最长公共子序列里),
    /// 剩下的用两行滚动数组求长度,内存 O(m)。
    /// </summary>
    private static int LcsLength(List<string> a, List<string> b)
    {
        var start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start])
        {
            start++;
        }

        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        var shared = start + (a.Count - endA);
        var n = endA - start;
        var m = endB - start;
        if (n == 0 || m == 0)
        {
            return shared;
        }

        if ((long)n * m > MaxLcsCells)
        {
            return shared + MultisetOverlap(a, start, endA, b, start, endB);
        }

        var previous = new int[m + 1];
        var next = new int[m + 1];
        for (var i = 0; i < n; i++)
        {
            var line = a[start + i];
            for (var j = 0; j < m; j++)
            {
                next[j + 1] = line == b[start + j] ? previous[j] + 1 : Math.Max(previous[j + 1], next[j]);
            }

            (previous, next) = (next, previous);
        }

        return shared + previous[m];
    }

    /// <summary>两边都出现的行数(按出现次数取小)。是 LCS 长度的上界,用作超大输入时的近似。</summary>
    private static int MultisetOverlap(List<string> a, int fromA, int toA, List<string> b, int fromB, int toB)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = fromA; i < toA; i++)
        {
            counts[a[i]] = counts.GetValueOrDefault(a[i]) + 1;
        }

        var overlap = 0;
        for (var i = fromB; i < toB; i++)
        {
            if (counts.GetValueOrDefault(b[i]) > 0)
            {
                counts[b[i]]--;
                overlap++;
            }
        }

        return overlap;
    }

    /// <summary>空文件 → 0 行;否则按 <c>\n</c> 切分并剔除唯一的尾随空行。</summary>
    private static List<string> ToLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = new List<string>(text.Split('\n'));
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
