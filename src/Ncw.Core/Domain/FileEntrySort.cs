using System.Globalization;

namespace Ncw.Core.Domain;

/// <summary>目录列表的排序键。</summary>
public enum FileSortBy
{
    /// <summary>名字(自然排序,不分大小写)。</summary>
    Name,

    /// <summary>修改时间,新的在前。</summary>
    Mtime,

    /// <summary>大小,大的在前。</summary>
    Size,
}

/// <summary>
/// 目录条目的排序(移植自 2.x <c>file-tree.ts</c> 的 <c>sortEntries</c>)。设备列目录、控制端画树都调这一个,顺序在这里定死一次,
/// 而不是在树组件里再排一遍(那样刷新前后的顺序会因为谁排的而不同)。
/// <list type="bullet">
/// <item><b>目录永远在文件前面</b>,与排序键无关;排序只作用在两组各自内部。</item>
/// <item>名字里的数字按值比较(<c>v2</c> 在 <c>v10</c> 前);大小写不影响顺序;中文按拼音(用 <c>zh-Hans-CN</c> 的排序规则)。</item>
/// <item>按时间 / 大小是倒序;<b>平局回落到名字</b>:一批文件同一秒写出来(<c>git checkout</c> 之后)是常态,没有这个回落每次刷新的顺序都可能不一样。缺 mtime / size 的当 0。</item>
/// </list>
/// 永不原地修改入参。
/// </summary>
public static class FileEntrySort
{
    private static readonly CompareInfo Collation = CollationFor();

    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering;

    private static CompareInfo CollationFor()
    {
        try
        {
            return CultureInfo.GetCultureInfo("zh-Hans-CN").CompareInfo;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.CompareInfo;
        }
    }

    /// <summary>名字比较(自然排序、不分大小写、中文按拼音)。</summary>
    public static int CompareNames(string a, string b) => Collation.Compare(a, b, Options);

    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> entries, FileSortBy by, Func<T, string> name, Func<T, bool> isDirectory, Func<T, long?> mtime, Func<T, long?> size)
    {
        var list = entries.ToList();
        // List.Sort 不稳定:用索引兜底,保证完全相同的两条保持原次序
        var indexed = list.Select(static (item, index) => (item, index)).ToList();
        indexed.Sort((a, b) =>
        {
            var dirA = isDirectory(a.item);
            var dirB = isDirectory(b.item);
            if (dirA != dirB)
            {
                return dirA ? -1 : 1;
            }

            var byNameResult = CompareNames(name(a.item), name(b.item));
            var primary = by switch
            {
                FileSortBy.Mtime => (mtime(b.item) ?? 0).CompareTo(mtime(a.item) ?? 0),
                FileSortBy.Size => (size(b.item) ?? 0).CompareTo(size(a.item) ?? 0),
                _ => 0,
            };
            if (primary != 0)
            {
                return primary;
            }

            return byNameResult != 0 ? byNameResult : a.index.CompareTo(b.index);
        });
        return [.. indexed.Select(static x => x.item)];
    }
}
