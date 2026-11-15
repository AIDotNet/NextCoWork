using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>目录条目的排序(移植自 2.x <c>file-tree.test.ts</c> 的 <c>sortEntries</c>)。</summary>
public class FileSortTests
{
    private sealed record E(string Name, bool Dir = false, long? Mtime = null, long? Size = null);

    private static string[] Sorted(IEnumerable<E> list, FileSortBy by = FileSortBy.Name) =>
        [.. FileEntrySort.Sort(list, by, e => e.Name, e => e.Dir, e => e.Mtime, e => e.Size).Select(e => e.Name)];

    [Theory]
    [InlineData(FileSortBy.Name)]
    [InlineData(FileSortBy.Mtime)]
    [InlineData(FileSortBy.Size)]
    public void Directories_come_before_files_whatever_the_key(FileSortBy by)
    {
        var list = new[] { new E("a.ts"), new E("zzz", true), new E("b.ts"), new E("aaa", true) };
        var output = Sorted(list, by);
        Assert.Equal(["aaa", "zzz"], output.Take(2).Order().ToArray());
        Assert.Equal(["a.ts", "b.ts"], output.Skip(2).Order().ToArray());
    }

    [Fact]
    public void Numbers_in_names_compare_by_value_not_by_character()
    {
        Assert.Equal(["v1.ts", "v2.ts", "v10.ts"], Sorted([new("v10.ts"), new("v2.ts"), new("v1.ts")]));
    }

    [Fact]
    public void Case_does_not_affect_the_order()
    {
        Assert.Equal(["a.ts", "B.ts"], Sorted([new("B.ts"), new("a.ts")]));
    }

    [Fact]
    public void Chinese_names_sort_by_pinyin()
    {
        Assert.Equal(["北京", "广州", "上海"], Sorted([new("上海"), new("广州"), new("北京")]));
    }

    [Fact]
    public void Mtime_is_newest_first()
    {
        Assert.Equal(["new", "mid", "old"], Sorted([new("old", Mtime: 100), new("new", Mtime: 300), new("mid", Mtime: 200)], FileSortBy.Mtime));
    }

    [Fact]
    public void Size_is_largest_first()
    {
        Assert.Equal(["l", "m", "s"], Sorted([new("s", Size: 1), new("l", Size: 900), new("m", Size: 50)], FileSortBy.Size));
    }

    [Fact]
    public void Ties_on_time_or_size_fall_back_to_the_name()
    {
        Assert.Equal(["a", "b", "c"], Sorted([new("c", Mtime: 5), new("a", Mtime: 5), new("b", Mtime: 5)], FileSortBy.Mtime));
        Assert.Equal(["a", "b", "c"], Sorted([new("c", Size: 7), new("a", Size: 7), new("b", Size: 7)], FileSortBy.Size));
    }

    [Fact]
    public void Entries_without_mtime_or_size_sort_last_not_first()
    {
        Assert.Equal(["has", "none"], Sorted([new("none"), new("has", Mtime: 1)], FileSortBy.Mtime));
        Assert.Equal(["has", "none"], Sorted([new("none"), new("has", Size: 1)], FileSortBy.Size));
    }

    [Fact]
    public void The_input_is_never_modified()
    {
        var input = new List<E> { new("b"), new("a") };
        _ = FileEntrySort.Sort(input, FileSortBy.Name, e => e.Name, e => e.Dir, e => e.Mtime, e => e.Size);
        Assert.Equal(["b", "a"], input.Select(e => e.Name));
    }
}
