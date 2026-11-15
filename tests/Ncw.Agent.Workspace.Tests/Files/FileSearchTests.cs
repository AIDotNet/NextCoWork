using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>文件名建议(移植自 2.x <c>workspace-search.ts</c>:带 TTL 的索引、复用遍历闸门)和内容搜索。</summary>
public sealed class FileSearchTests
{
    private static Task<SuggestResult> Suggest(FilesRig rig, string query, int limit = 0) => rig.Service.SuggestAsync(rig.Fp, query, limit).AsTask();

    private static Task<SearchResult> Search(FilesRig rig, SearchRequest request) => rig.Service.SearchAsync(rig.Fp, request).AsTask();

    // ── suggest ──

    [Fact]
    public async Task Suggestions_are_ranked_project_relative_files_only()
    {
        using var rig = new FilesRig();
        rig.Write("src/views/chat/ChatView.tsx", "x");
        rig.Write("docs/chat-view-notes.md", "x");
        rig.Write("README.md", "x");
        Directory.CreateDirectory(rig.Abs("emptydir"));

        var result = await Suggest(rig, "chatv");

        Assert.Equal("src/views/chat/ChatView.tsx", result.Suggestions[0].Path);
        Assert.Equal("ChatView.tsx", result.Suggestions[0].Name);
        Assert.DoesNotContain(result.Suggestions, s => s.Path == "emptydir");
        Assert.False(result.Truncated);
        Assert.Equal("README.md", (await Suggest(rig, "")).Suggestions[0].Path); // 空查询:浅的在前
    }

    [Fact]
    public async Task Dependency_and_build_directories_are_not_suggested()
    {
        using var rig = new FilesRig();
        rig.Write("node_modules/pkg/index.js", "x");
        rig.Write(".git/config", "x");
        rig.Write("dist/bundle.js", "x");
        rig.Write("src/index.js", "x");

        var all = (await Suggest(rig, "index")).Suggestions.Select(s => s.Path);

        Assert.Equal(["src/index.js"], all);
    }

    [Fact]
    public async Task The_limit_defaults_and_is_clamped()
    {
        using var rig = new FilesRig();
        for (var i = 0; i < 60; i++)
        {
            rig.Write($"f{i:D2}.txt", "x");
        }

        Assert.Equal(FileLimits.SuggestDefault, (await Suggest(rig, "")).Suggestions.Count);
        Assert.Equal(FileLimits.SuggestMax, (await Suggest(rig, "", 1000)).Suggestions.Count);
        Assert.Equal(3, (await Suggest(rig, "", 3)).Suggestions.Count);
    }

    [Fact]
    public async Task The_index_lives_for_its_ttl_then_rebuilds_and_forget_drops_it()
    {
        using var rig = new FilesRig();
        rig.Write("one.txt", "x");
        Assert.Single((await Suggest(rig, "")).Suggestions);

        rig.Write("two.txt", "x");
        Assert.Single((await Suggest(rig, "")).Suggestions); // 还在新鲜期:连着敲键只走一趟盘

        rig.Time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(2, (await Suggest(rig, "")).Suggestions.Count);

        rig.Write("three.txt", "x");
        rig.Service.Forget("p1");
        Assert.Equal(3, (await Suggest(rig, "")).Suggestions.Count);
    }

    [Fact]
    public async Task A_deleted_project_root_gives_an_empty_list_not_an_error()
    {
        using var rig = new FilesRig();
        Directory.Delete(rig.Root, true);

        Assert.Empty((await Suggest(rig, "x")).Suggestions);
    }

    [Fact]
    public async Task The_walk_reports_truncation_instead_of_silently_dropping_files()
    {
        using var rig = new FilesRig(new FileServiceOptions { MaxWalkEntries = 3 });
        for (var i = 0; i < 6; i++)
        {
            rig.Write($"f{i}.txt", "x");
        }

        var result = await Suggest(rig, "");

        Assert.Equal(3, result.Suggestions.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Symbolic_links_are_not_followed_by_the_walk()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new FilesRig();
        File.WriteAllText(Path.Combine(rig.Outside, "secret.txt"), "needle");
        Directory.CreateSymbolicLink(rig.Abs("loop"), rig.Root);
        Directory.CreateSymbolicLink(rig.Abs("out"), rig.Outside);
        rig.Write("real.txt", "needle");

        Assert.Equal(["real.txt"], (await Suggest(rig, "")).Suggestions.Select(s => s.Path));
        Assert.Equal(["real.txt"], (await Search(rig, new SearchRequest("needle"))).Matches.Select(m => m.Path));
    }

    // ── search ──

    [Fact]
    public async Task Literal_search_is_case_insensitive_by_default_and_reports_line_and_column()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "first\nsecond Needle here\r\nthird");
        rig.Write("sub/b.txt", "no match");

        var result = await Search(rig, new SearchRequest("needle"));

        var match = Assert.Single(result.Matches);
        Assert.Equal("a.txt", match.Path);
        Assert.Equal(2, match.Line);
        Assert.Equal(8, match.Column);
        Assert.Equal("second Needle here", match.Text); // CR 不进结果
        Assert.Equal(2, result.FilesSearched);
        Assert.False(result.Truncated);

        Assert.Empty((await Search(rig, new SearchRequest("needle", CaseSensitive: true))).Matches);
        Assert.Single((await Search(rig, new SearchRequest("Needle", CaseSensitive: true))).Matches);
    }

    [Fact]
    public async Task Regex_search_uses_the_non_backtracking_engine()
    {
        using var rig = new FilesRig();
        rig.Write("a.cs", "var x = 12;\nvar y = abc;\nvar z = 345;");

        var result = await Search(rig, new SearchRequest(@"= \d+;", Regex: true));

        Assert.Equal([1, 3], result.Matches.Select(m => m.Line));
    }

    [Theory]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?<=a)b")]
    [InlineData("(unclosed")]
    [InlineData("")]
    public async Task Patterns_the_engine_cannot_run_are_refused_not_silently_changed(string pattern)
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "aab");

        await FilesRig.Fails(() => rig.Service.SearchAsync(rig.Fp, new SearchRequest(pattern, Regex: true)), FilesErrorCode.InvalidPattern);
    }

    [Fact]
    public async Task A_catastrophic_pattern_finishes_quickly()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", new string('a', 5000) + "!");

        var started = Environment.TickCount64;
        var result = await Search(rig, new SearchRequest("^(a+)+$", Regex: true));

        Assert.Empty(result.Matches);
        Assert.True(Environment.TickCount64 - started < 5000);
    }

    [Fact]
    public async Task Glob_filters_by_name_or_by_relative_path()
    {
        using var rig = new FilesRig();
        rig.Write("a.cs", "needle");
        rig.Write("a.md", "needle");
        rig.Write("src/deep/b.cs", "needle");
        rig.Write("docs/c.cs", "needle");

        Assert.Equal(["a.cs", "docs/c.cs", "src/deep/b.cs"], (await Search(rig, new SearchRequest("needle", Glob: "*.cs"))).Matches.Select(m => m.Path).Order());
        Assert.Equal(["src/deep/b.cs"], (await Search(rig, new SearchRequest("needle", Glob: "src/**/*.cs"))).Matches.Select(m => m.Path));
        Assert.Equal(["a.cs", "a.md"], (await Search(rig, new SearchRequest("needle", Glob: "a.{cs,md}"))).Matches.Select(m => m.Path).Order());
    }

    [Fact]
    public async Task Search_can_be_scoped_to_a_directory_or_one_file_and_results_stay_project_relative()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", "needle");
        rig.Write("src/b.txt", "needle");

        Assert.Equal(["src/b.txt"], (await Search(rig, new SearchRequest("needle", Path: "src"))).Matches.Select(m => m.Path));
        Assert.Equal(["a.txt"], (await Search(rig, new SearchRequest("needle", Path: "a.txt"))).Matches.Select(m => m.Path));
        await FilesRig.Fails(() => rig.Service.SearchAsync(rig.Fp, new SearchRequest("needle", Path: "../outside")), FilesErrorCode.InvalidPath);
        await FilesRig.Fails(() => rig.Service.SearchAsync(rig.Fp, new SearchRequest("needle", Path: "nope")), FilesErrorCode.NotFound);
    }

    [Fact]
    public async Task Binary_ignored_and_oversized_files_are_skipped()
    {
        using var rig = new FilesRig(new FileServiceOptions { SearchFileMax = 1024 });
        rig.WriteBytes("bin.dat", [.. "needle"u8.ToArray(), 0, 1, 2]);
        rig.Write("node_modules/p/x.js", "needle");
        rig.Write("big.txt", "needle" + new string('x', 4000));
        rig.Write("ok.txt", "needle");

        var result = await Search(rig, new SearchRequest("needle"));

        Assert.Equal(["ok.txt"], result.Matches.Select(m => m.Path));
    }

    [Fact]
    public async Task A_full_result_set_is_marked_truncated_and_long_lines_are_cut()
    {
        using var rig = new FilesRig();
        rig.Write("a.txt", string.Join('\n', Enumerable.Repeat("needle " + new string('y', 800), 10)));

        var result = await Search(rig, new SearchRequest("needle", Limit: 4));

        Assert.Equal(4, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.All(result.Matches, m => Assert.True(m.Text.Length <= 500));
    }
}
