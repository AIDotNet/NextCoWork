using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>
/// <c>@</c> 文件选择器的排序(移植自 2.x <c>fuzzy-path.test.ts</c>)。断言的是<b>相对次序</b>而不是具体分数:分档的绝对值随时可以调,
/// 而「敲 chatv 第一条必须是 ChatView.tsx」是不能变的。
/// </summary>
public class FuzzyPathTests
{
    private sealed record F(string Path)
    {
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    }

    private static readonly F[] Files =
    [
        new("src/renderer/src/views/chat/ChatView.tsx"),
        new("src/renderer/src/views/chat/Composer.tsx"),
        new("docs/chat-view-notes.md"),
        new("src/shared/domain/attachment.ts"),
        new("README.md"),
        new("src/main/ipc/attachment.ts"),
    ];

    private static string[] Paths(string query, int limit = 10) =>
        [.. FuzzyPath.Rank(Files, query, limit, f => f.Path, f => f.Name).Select(f => f.Path)];

    private static int? Score(string path, string query) => FuzzyPath.Score(path, path[(path.LastIndexOf('/') + 1)..], query);

    [Fact]
    public void A_miss_is_null_not_zero()
    {
        Assert.Null(Score("a/b.ts", "zzzz"));
        Assert.NotNull(Score("a/b.ts", ""));
    }

    [Fact]
    public void Name_prefix_beats_name_contains_beats_path_contains()
    {
        var prefix = Score("x/chat.ts", "chat")!.Value;
        var contains = Score("x/mychat.ts", "chat")!.Value;
        var inPath = Score("chat/x.ts", "chat")!.Value;
        Assert.True(prefix > contains);
        Assert.True(contains > inPath);
    }

    [Fact]
    public void A_deep_path_penalty_cannot_cross_a_tier()
    {
        var deep = Score("a/b/c/d/e/f/g/h/i/j/k/chat.ts", "chat")!.Value;
        var shallowLowerTier = Score("chat/x.ts", "chat")!.Value;
        Assert.True(deep > shallowLowerTier);
    }

    [Fact]
    public void Within_a_tier_the_shallow_one_comes_first()
    {
        Assert.True(Score("a.ts", "a")!.Value > Score("x/y/z/a.ts", "a")!.Value);
    }

    [Fact]
    public void Typing_chatv_ranks_ChatView_first_not_the_note_with_the_same_words()
    {
        Assert.Equal("src/renderer/src/views/chat/ChatView.tsx", Paths("chatv")[0]);
    }

    [Fact]
    public void A_slashed_query_works()
    {
        Assert.Equal(["src/renderer/src/views/chat/Composer.tsx"], Paths("chat/comp"));
    }

    [Fact]
    public void Abbreviations_use_the_subsequence_tier()
    {
        Assert.Contains("src/renderer/src/views/chat/ChatView.tsx", Paths("chv"));
    }

    [Fact]
    public void A_real_substring_always_ranks_above_subsequence_hits()
    {
        var top = Paths("attachment").Take(2).Order().ToArray();
        Assert.Equal(["src/main/ipc/attachment.ts", "src/shared/domain/attachment.ts"], top);
    }

    [Fact]
    public void An_empty_query_gives_everything_shallow_first()
    {
        Assert.Equal("README.md", Paths("")[0]);
        Assert.Equal(Files.Length, Paths("").Length);
    }

    [Fact]
    public void The_limit_truncates()
    {
        Assert.Equal(2, Paths("", 2).Length);
    }

    [Fact]
    public void The_sort_is_stable_for_equal_scores()
    {
        F[] same = [new("a/x.ts"), new("b/x.ts"), new("c/x.ts")];
        Assert.Equal(["a/x.ts", "b/x.ts", "c/x.ts"], FuzzyPath.Rank(same, "x.ts", 10, f => f.Path, f => f.Name).Select(f => f.Path));
    }

    [Fact]
    public void No_match_returns_an_empty_list()
    {
        Assert.Empty(Paths("zzzzzz"));
    }

    [Fact]
    public void Subsequence_matches_in_order_and_may_skip_characters()
    {
        Assert.False(FuzzyPath.IsSubsequence("chatview", "ccv"));
        Assert.True(FuzzyPath.IsSubsequence("chatview", "chv"));
        Assert.False(FuzzyPath.IsSubsequence("chatview", "vc"));
        Assert.True(FuzzyPath.IsSubsequence("abc", ""));
        Assert.True(FuzzyPath.IsSubsequence("", ""));
        Assert.False(FuzzyPath.IsSubsequence("", "a"));
    }

    [Fact]
    public void Both_sides_walk_by_code_unit_so_an_emoji_does_not_misalign_the_match()
    {
        Assert.True(FuzzyPath.IsSubsequence("a🎉b", "ab"));
        Assert.True(FuzzyPath.IsSubsequence("a🎉b", "🎉"));
    }
}
