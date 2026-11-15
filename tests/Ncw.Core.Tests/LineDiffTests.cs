using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>移植自 2.x 的 <c>shared/domain/__tests__/line-diff.test.ts</c>(<c>countLineDiff</c>)。</summary>
public class LineDiffTests
{
    [Fact]
    public void A_new_file_counts_every_line_as_added() => Assert.Equal((3, 0), LineDiff.Count(null, "a\nb\nc"));

    [Fact]
    public void A_deleted_file_counts_every_line_as_deleted() => Assert.Equal((0, 2), LineDiff.Count("a\nb", null));

    [Fact]
    public void Changing_one_line_is_one_added_and_one_deleted() => Assert.Equal((1, 1), LineDiff.Count("a\nb\nc", "a\nB\nc"));

    [Fact]
    public void A_pure_insertion_only_adds() => Assert.Equal((1, 0), LineDiff.Count("a\nc", "a\nb\nc"));

    [Fact]
    public void A_trailing_newline_does_not_disturb_the_count() => Assert.Equal((1, 0), LineDiff.Count("a\n", "a\nb\n"));

    [Fact]
    public void Identical_text_has_no_changes() => Assert.Equal((0, 0), LineDiff.Count("a\nb", "a\nb"));

    [Fact]
    public void Empty_file_is_zero_lines() => Assert.Equal((2, 0), LineDiff.Count("", "a\nb"));

    [Fact]
    public void Matches_a_full_lcs_backtrack_including_shared_head_and_tail()
    {
        long seed = 7;
        double Random()
        {
            seed = (seed * 1103515245 + 12345) % 2147483648;
            return seed / 2147483648d;
        }

        string Text(int length) => string.Join('\n', Enumerable.Range(0, length).Select(_ => "abcde"[(int)Math.Floor(Random() * 5)]));
        for (var round = 0; round < 200; round++)
        {
            var shared = Text((int)Math.Floor(Random() * 4));
            var before = string.Join('\n', [shared, Text((int)Math.Floor(Random() * 12)), shared]);
            var after = string.Join('\n', [shared, Text((int)Math.Floor(Random() * 12)), shared]);
            var (a, b) = (Lines(before), Lines(after));
            var common = FullLcs(a, b);
            Assert.Equal((b.Length - common, a.Length - common), LineDiff.Count(before, after));
        }

        static string[] Lines(string value) => value.Length == 0 ? [] : (value.EndsWith('\n') ? value[..^1] : value).Split('\n');
    }

    [Fact]
    public void A_big_file_with_one_changed_line_does_not_compare_the_whole_file()
    {
        var lines = Enumerable.Range(0, 50_000).Select(i => $"line {i}").ToArray();
        var changed = (string[])lines.Clone();
        changed[25_000] = "changed";
        Assert.Equal((1, 1), LineDiff.Count(string.Join('\n', lines), string.Join('\n', changed)));
    }

    private static int FullLcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        return dp[0, 0];
    }
}
