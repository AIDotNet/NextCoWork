using System.Diagnostics;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Tools.Tests;

/// <summary>Walker 的三道闸门(软链成环、软链出界、量与时间)+ 顺序(移植自 2.x <c>walk.test.ts</c>)。</summary>
public sealed class WalkerTests : IDisposable
{
    private readonly Fixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private Task<WalkResult> WalkAsync(Action<WalkOptionsBuilder>? tweak = null, CancellationToken ct = default)
    {
        var b = new WalkOptionsBuilder(_fx);
        tweak?.Invoke(b);
        return Walker.WalkAsync(b.Build(), TimeProvider.System, ct);
    }

    private sealed class WalkOptionsBuilder(Fixture fx)
    {
        public int MaxEntries { get; set; } = 20_000;

        public int MaxDepth { get; set; } = 32;

        public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(5);

        public IEnvironmentFs Fs { get; set; } = fx.Env.Fs;

        public WalkOptions Build() => new()
        {
            Fs = Fs,
            Root = EnvPath.Parse(fx.Root, Fixture.Style),
            MaxEntries = MaxEntries,
            MaxDepth = MaxDepth,
            Deadline = Deadline,
        };
    }

    [Fact]
    public async Task Lists_breadth_first_so_every_shallower_entry_comes_before_any_deeper_one()
    {
        for (var i = 0; i < 12; i++)
        {
            _fx.Put($"d{i}/sub/deep.txt", "x");
        }

        _fx.Put("top.txt", "x");
        var r = await WalkAsync();
        var depths = r.Entries.Select(e => e.Rel.Split('/').Length).ToList();
        Assert.Equal(depths.Order().ToList(), depths);
        Assert.Equal(13, r.Entries.Count(e => !e.IsDirectory));
    }

    [Fact]
    public async Task Terminates_on_a_symlink_loop_and_descends_into_a_linked_directory_once()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _fx.Put("a/file.txt", "x");
        Directory.CreateSymbolicLink(_fx.Abs("a/loop"), _fx.Abs("a"));
        var r = await WalkAsync();
        Assert.Contains(r.Entries, e => e.Rel == "a/loop");
        Assert.DoesNotContain(r.Entries, e => e.Rel.StartsWith("a/loop/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keeps_the_first_of_two_sibling_links_to_the_same_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _fx.Put("real/inner.txt", "x");
        Directory.CreateSymbolicLink(_fx.Abs("zz-alias"), _fx.Abs("real"));
        var rels = (await WalkAsync()).Entries.Select(e => e.Rel).ToList();
        Assert.Contains("real/inner.txt", rels);
        Assert.DoesNotContain("zz-alias/inner.txt", rels); // 排在前面的 real 先被展开
    }

    [Fact]
    public async Task Skips_a_symlink_that_escapes_the_root_without_failing_the_walk()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateSymbolicLink(_fx.Abs("escape"), _fx.Outside);
        _fx.Put("ok.txt", "x");
        var rels = (await WalkAsync()).Entries.Select(e => e.Rel).ToList();
        Assert.Contains("ok.txt", rels);
        Assert.DoesNotContain("escape/secret.txt", rels);
    }

    [Fact]
    public async Task Ignores_dangling_links_and_the_fixed_skip_list()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(_fx.Abs("dangling"), _fx.Abs("nothing"));
        _fx.Put("node_modules/x.js", "x");
        _fx.Put(".git/config", "x");
        _fx.Put(".github/ci.yml", "x");
        _fx.Put(".DS_Store", "x");
        var rels = (await WalkAsync()).Entries.Select(e => e.Rel).ToList();
        Assert.DoesNotContain("dangling", rels);
        Assert.DoesNotContain(rels, r => r.StartsWith("node_modules", StringComparison.Ordinal) || r.StartsWith(".git/", StringComparison.Ordinal) || r == ".DS_Store");
        Assert.Contains(".github/ci.yml", rels); // 不跳过点开头的目录
    }

    [Fact]
    public async Task Truncates_at_MaxEntries_keeping_what_a_serial_BFS_would_keep()
    {
        for (var i = 0; i < 10; i++)
        {
            _fx.Put($"d{i}/f.txt", "x");
        }

        var full = await WalkAsync();
        var cut = await WalkAsync(b => b.MaxEntries = 13);
        Assert.True(cut.Truncated);
        Assert.Equal(full.Entries.Take(13).Select(e => e.Rel), cut.Entries.Select(e => e.Rel));
    }

    [Fact]
    public async Task Marks_the_result_truncated_when_the_depth_limit_is_hit()
    {
        _fx.Put("a/b/c/d/e.txt", "x");
        var r = await WalkAsync(b => b.MaxDepth = 3);
        Assert.True(r.Truncated);
        Assert.DoesNotContain(r.Entries, e => e.Rel.EndsWith("e.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_a_timeout_instead_of_a_silent_partial_result()
    {
        for (var i = 0; i < 50; i++)
        {
            _fx.Put($"d{i}/f.txt", "x");
        }

        var r = await WalkAsync(b => b.Deadline = TimeSpan.FromTicks(1));
        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        _fx.Put("a/f.txt", "x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WalkAsync(ct: cts.Token));
    }

    [Fact]
    public async Task A_failure_that_is_not_a_missing_directory_is_surfaced_not_swallowed()
    {
        for (var i = 0; i < 5; i++)
        {
            _fx.Put($"d{i}/f.txt", "x");
        }

        var broken = new ThrowingFs(_fx.Env.Fs, new EnvironmentException(EnvironmentErrorCode.Disconnected));
        await Assert.ThrowsAsync<EnvironmentException>(() => WalkAsync(b => b.Fs = broken));
    }
}

/// <summary>在第 N 次 List 之后开始抛指定异常的文件系统包装。</summary>
internal sealed class ThrowingFs(IEnvironmentFs inner, EnvironmentException error, int after = 1, bool onStat = false) : IEnvironmentFs
{
    private int _calls;

    private void Maybe()
    {
        if (Interlocked.Increment(ref _calls) > after)
        {
            throw error;
        }
    }

    public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default)
    {
        if (onStat)
        {
            Maybe();
        }

        return inner.StatAsync(path, ct);
    }

    public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) => inner.LStatAsync(path, ct);

    public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => inner.RealPathAsync(path, ct);

    public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default)
    {
        if (!onStat)
        {
            Maybe();
        }

        return inner.ListAsync(path, ct);
    }

    public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);

    public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => inner.OpenWriteAsync(path, options, ct);

    public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) => inner.MkdirAsync(path, recursive, ct);

    public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => inner.RenameAsync(source, destination, replace, ct);

    public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => inner.DeleteFileAsync(path, ct);

    public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => inner.DeleteDirectoryAsync(path, recursive, ct);

    public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => inner.CopyFileAsync(source, destination, maxBytes, ct);

    public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => inner.Watch(directory, options);
}

public sealed class GlobToolTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly GlobTool _glob = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Matches_across_directories_and_root_files_count_too()
    {
        _fx.Put("a.ts", "");
        _fx.Put("src/b.ts", "");
        _fx.Put("src/deep/c.ts", "");
        _fx.Put("src/d.js", "");
        var lines = (await _fx.Run(_glob, ("pattern", "**/*.ts"))).Text().Split('\n');
        Assert.Contains("a.ts", lines);
        Assert.Contains("src/b.ts", lines);
        Assert.Contains("src/deep/c.ts", lines);
        Assert.DoesNotContain("src/d.js", lines);
    }

    [Fact]
    public async Task Single_star_does_not_cross_directories()
    {
        _fx.Put("a.ts", "");
        _fx.Put("src/b.ts", "");
        Assert.Equal(["a.ts"], (await _fx.Run(_glob, ("pattern", "*.ts"))).Text().Split('\n'));
    }

    [Fact]
    public async Task Sorts_by_modification_time_newest_first()
    {
        _fx.Put("old.ts", "");
        _fx.Put("new.ts", "");
        File.SetLastWriteTimeUtc(_fx.Abs("old.ts"), DateTime.UtcNow.AddHours(-3));
        Assert.Equal(["new.ts", "old.ts"], (await _fx.Run(_glob, ("pattern", "*.ts"))).Text().Split('\n'));
    }

    [Fact]
    public async Task Path_limits_the_search_to_a_subdirectory_and_keeps_workspace_relative_names()
    {
        _fx.Put("a.ts", "");
        _fx.Put("src/b.ts", "");
        Assert.Equal(["src/b.ts"], (await _fx.Run(_glob, ("pattern", "**/*.ts"), ("path", _fx.Abs("src")))).Text().Split('\n'));
    }

    [Fact]
    public async Task No_match_is_a_hint_not_an_error()
    {
        var r = await _fx.Run(_glob, ("pattern", "*.rs"));
        Assert.False(r.IsError);
        Assert.Contains("No files match", r.Text());
    }

    [Fact]
    public async Task Generated_directories_are_skipped()
    {
        _fx.Put("src/a.ts", "");
        _fx.Put("node_modules/pkg/index.ts", "");
        _fx.Put("dist/bundle.ts", "");
        _fx.Put(".git/hooks/x.ts", "");
        var text = (await _fx.Run(_glob, ("pattern", "**/*.ts"))).Text();
        Assert.Contains("src/a.ts", text);
        Assert.DoesNotContain("node_modules", text);
        Assert.DoesNotContain("dist/", text);
        Assert.DoesNotContain(".git/", text);
    }

    [Fact]
    public async Task A_directory_outside_the_root_can_be_listed_and_paths_are_absolute()
    {
        var r = await _fx.Run(_glob, ("pattern", "*"), ("path", _fx.Outside));
        Assert.False(r.IsError, r.Text());
        Assert.Contains(Path.Combine(_fx.Outside, "secret.txt"), r.Text());
    }

    [Fact]
    public async Task Without_a_workspace_a_relative_start_has_nothing_to_resolve_against_but_an_absolute_one_works()
    {
        using var fx = new Fixture(withRoot: false);
        var rel = await fx.Run(_glob, ("pattern", "*"));
        Assert.True(rel.IsError);
        Assert.Contains("workspace", rel.Text());
        var abs = await fx.Run(_glob, ("pattern", "*"), ("path", fx.Outside));
        Assert.False(abs.IsError, abs.Text());
        Assert.Contains(Path.Combine(fx.Outside, "secret.txt"), abs.Text());
    }

    [Fact]
    public async Task A_root_registered_through_a_symlink_still_gets_relative_results_from_Glob_and_Grep()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_fx.Base, "ws-link");
        Directory.CreateSymbolicLink(link, _fx.Root);
        _fx.Put("src/a.ts", "needle\n");
        var runtime = new ToolRuntime { Environment = _fx.Env, Root = EnvPath.Parse(link, Fixture.Style), GrepBackends = GrepBackends.None };
        Assert.Equal(["src/a.ts"], (await _fx.Run(_glob, Fixture.Args(("pattern", "**/*.ts")), runtime: runtime)).Text().Split('\n'));
        Assert.Equal(["src/a.ts"], (await _fx.Run(new GrepTool(), Fixture.Args(("pattern", "needle")), runtime: runtime)).Text().Split('\n'));
        Assert.Equal(["src/a.ts"], (await _fx.Run(_glob, Fixture.Args(("pattern", "**/*.ts"), ("path", "src")), runtime: runtime)).Text().Split('\n'));
    }

    [Fact]
    public async Task A_file_as_path_is_an_error_that_points_at_Read()
    {
        _fx.Put("a.ts", "");
        var r = await _fx.Run(_glob, ("pattern", "*"), ("path", _fx.Abs("a.ts")));
        Assert.True(r.IsError);
        Assert.Contains("Read", r.Text());
    }

    [Fact]
    public async Task Caps_the_listing_at_200_and_says_how_many_matched()
    {
        for (var i = 0; i < 230; i++)
        {
            _fx.Put($"f{i:D3}.txt", "");
        }

        var r = await _fx.Run(_glob, ("pattern", "*.txt"));
        Assert.Equal(200, r.Text().Split("\n\n")[0].Split('\n').Length);
        Assert.Contains("230 files matched", r.Text());
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_not_returned_as_an_empty_search()
    {
        _fx.Put("file.ts", "content");
        var broken = new ToolRuntime { Environment = new FsOverrideEnvironment(_fx.Env, new ThrowingFs(_fx.Env.Fs, new EnvironmentException(EnvironmentErrorCode.Disconnected), after: 0)), Root = _fx.Runtime.Root, GrepBackends = GrepBackends.None };
        var glob = await _fx.Run(_glob, Fixture.Args(("pattern", "**/*.ts")), runtime: broken);
        Assert.True(glob.IsError);
        Assert.Contains("not reachable", glob.Text());
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        _fx.Put("a.ts", "");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Run(_glob, Fixture.Args(("pattern", "*")), ct: cts.Token));
    }
}

/// <summary>替换 Fs 的环境包装(其余原样转给内层)。</summary>
internal sealed class FsOverrideEnvironment(IExecutionEnvironment inner, IEnvironmentFs fs) : IExecutionEnvironment
{
    public EnvironmentId Id => inner.Id;

    public EnvironmentKind Kind => inner.Kind;

    public string DisplayName => inner.DisplayName;

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => new(fs, inner.Facts.PathStyle);

    public IEnvironmentFs Fs => fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class GrepToolTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly GrepTool _grep = new();

    public void Dispose() => _fx.Dispose();

    // ── output_mode ──

    private void SeedModes()
    {
        _fx.Put("a.ts", "alpha\nbeta\nalpha again\n");
        _fx.Put("src/b.ts", "gamma\nalpha\n");
        _fx.Put("c.md", "alpha in markdown\n");
    }

    [Fact]
    public async Task Default_mode_is_files_with_matches_paths_only()
    {
        SeedModes();
        var r = await _fx.Run(_grep, ("pattern", "alpha"));
        var lines = r.Text().Split('\n');
        Assert.Contains("a.ts", lines);
        Assert.Contains("src/b.ts", lines);
        Assert.Contains("c.md", lines);
        Assert.DoesNotContain("beta", r.Text());
        Assert.DoesNotContain("alpha again", r.Text());
    }

    [Fact]
    public async Task Content_mode_gives_the_matching_lines()
    {
        SeedModes();
        var r = await _fx.Run(_grep, ("pattern", "alpha"), ("glob", "*.ts"), ("output_mode", "content"));
        Assert.Contains("a.ts:alpha", r.Text());
        Assert.Contains("a.ts:alpha again", r.Text());
        Assert.DoesNotContain("beta", r.Text());
    }

    [Fact]
    public async Task Dash_n_adds_line_numbers()
    {
        SeedModes();
        var r = await _fx.Run(_grep, ("pattern", "alpha again"), ("output_mode", "content"), ("-n", true));
        Assert.Contains("a.ts:3:alpha again", r.Text());
    }

    [Fact]
    public async Task Count_mode_gives_a_count_per_file()
    {
        SeedModes();
        var r = await _fx.Run(_grep, ("pattern", "alpha"), ("glob", "**/*.ts"), ("output_mode", "count"));
        Assert.Contains("a.ts:2", r.Text());
        Assert.Contains("src/b.ts:1", r.Text());
    }

    [Fact]
    public async Task Head_limit_truncates_the_result_and_says_so()
    {
        SeedModes();
        var r = await _fx.Run(_grep, ("pattern", "alpha"), ("head_limit", 1));
        Assert.Contains("showing the first 1", r.Text());
    }

    [Fact]
    public async Task Context_lines_use_a_dash_and_do_not_include_lines_outside_the_window()
    {
        _fx.Put("a.txt", "L1\nL2\nHIT\nL4\nL5\n");
        var lines = (await _fx.Run(_grep, ("pattern", "HIT"), ("output_mode", "content"), ("-C", 1), ("-n", true))).Text().Split('\n');
        Assert.Contains("a.txt-2-L2", lines);
        Assert.Contains("a.txt:3:HIT", lines);
        Assert.Contains("a.txt-4-L4", lines);
        Assert.DoesNotContain("a.txt-1-L1", lines);
    }

    [Fact]
    public async Task B_only_looks_back_and_A_only_looks_forward()
    {
        _fx.Put("a.txt", "L1\nL2\nHIT\nL4\nL5\n");
        var b = (await _fx.Run(_grep, ("pattern", "HIT"), ("output_mode", "content"), ("-B", 1), ("-n", true))).Text();
        Assert.Contains("a.txt-2-L2", b);
        Assert.DoesNotContain("L4", b);
        var a = (await _fx.Run(_grep, ("pattern", "HIT"), ("output_mode", "content"), ("-A", 1), ("-n", true))).Text();
        Assert.Contains("a.txt-4-L4", a);
        Assert.DoesNotContain("L2", a);
    }

    [Fact]
    public async Task Non_adjacent_context_groups_are_separated_by_a_double_dash()
    {
        _fx.Put("b.txt", "HIT\nx\nx\nx\nx\nx\nHIT\n");
        var r = await _fx.Run(_grep, ("pattern", "HIT"), ("glob", "b.txt"), ("output_mode", "content"), ("-C", 1));
        Assert.Contains("--", r.Text());
    }

    // ── 过滤与选项 ──

    [Fact]
    public async Task Ignore_case()
    {
        _fx.Put("a.ts", "HelloWorld\n");
        Assert.Contains("No content matching", (await _fx.Run(_grep, ("pattern", "helloworld"))).Text());
        Assert.Contains("a.ts", (await _fx.Run(_grep, ("pattern", "helloworld"), ("-i", true))).Text());
    }

    [Fact]
    public async Task Glob_filters_by_file_name_and_type_by_language()
    {
        _fx.Put("a.ts", "needle\n");
        _fx.Put("a.md", "needle\n");
        _fx.Put("a.py", "needle\n");
        var glob = (await _fx.Run(_grep, ("pattern", "needle"), ("glob", "*.ts"))).Text();
        Assert.Contains("a.ts", glob);
        Assert.DoesNotContain("a.md", glob);
        var type = (await _fx.Run(_grep, ("pattern", "needle"), ("type", "py"))).Text();
        Assert.Contains("a.py", type);
        Assert.DoesNotContain("a.ts", type);
    }

    /// <summary>认不出的 type 必须报错。当成「不过滤」的话,模型拿到的是范围完全不对的结果。</summary>
    [Fact]
    public async Task An_unknown_type_is_an_error_listing_the_known_ones()
    {
        _fx.Put("a.ts", "needle\n");
        var r = await _fx.Run(_grep, ("pattern", "needle"), ("type", "typescript"));
        Assert.True(r.IsError);
        Assert.Contains("ts", r.Text());
    }

    [Fact]
    public async Task Multiline_lets_the_pattern_cross_lines()
    {
        _fx.Put("a.ts", "interface X {\n  field: string\n}\n");
        Assert.Contains("No content matching", (await _fx.Run(_grep, ("pattern", "interface X.*field"))).Text());
        Assert.Contains("a.ts", (await _fx.Run(_grep, ("pattern", "interface X.*field"), ("multiline", true))).Text());
    }

    [Fact]
    public async Task Multiline_reports_the_line_of_the_match_start()
    {
        _fx.Put("a.ts", "x\ninterface X {\n  field: string\n}\n");
        var r = await _fx.Run(_grep, ("pattern", "interface X[\\s\\S]*?field"), ("multiline", true), ("output_mode", "content"), ("-n", true));
        Assert.Contains("a.ts:2:interface X {", r.Text());
    }

    [Fact]
    public async Task A_path_pointing_at_one_file_searches_only_that_file()
    {
        _fx.Put("a.ts", "needle\n");
        _fx.Put("b.ts", "needle\n");
        Assert.Equal(["a.ts"], (await _fx.Run(_grep, ("pattern", "needle"), ("path", _fx.Abs("a.ts")))).Text().Split('\n'));
    }

    [Fact]
    public async Task Binary_files_are_skipped()
    {
        _fx.PutBytes("blob.bin", [0x6e, 0x65, 0x00, 0x65, 0x64]);
        _fx.Put("a.ts", "ne\n");
        var r = await _fx.Run(_grep, ("pattern", "ne"));
        Assert.Contains("a.ts", r.Text());
        Assert.DoesNotContain("blob.bin", r.Text());
    }

    [Fact]
    public async Task Dependency_directories_cannot_be_searched()
    {
        _fx.Put("src/a.ts", "needle\n");
        _fx.Put("node_modules/pkg/i.ts", "needle\n");
        var r = await _fx.Run(_grep, ("pattern", "needle"));
        Assert.Contains("src/a.ts", r.Text());
        Assert.DoesNotContain("node_modules", r.Text());
    }

    [Fact]
    public async Task An_invalid_regex_is_an_error_not_an_empty_result()
    {
        var r = await _fx.Run(_grep, ("pattern", "([unclosed"));
        Assert.True(r.IsError);
        Assert.Contains("Invalid regular expression", r.Text());
        Assert.Contains(".NET", r.Text());
    }

    [Fact]
    public async Task A_missing_path_is_an_error()
    {
        var r = await _fx.Run(_grep, ("pattern", "x"), ("path", _fx.Abs("nope")));
        Assert.True(r.IsError);
        Assert.Contains("does not exist", r.Text());
    }

    [Fact]
    public async Task A_directory_outside_the_root_can_be_searched_and_hits_are_absolute()
    {
        var r = await _fx.Run(_grep, ("pattern", "AKIA"), ("path", _fx.Outside));
        Assert.False(r.IsError, r.Text());
        Assert.Contains(Path.Combine(_fx.Outside, "secret.txt"), r.Text());
    }

    [Fact]
    public async Task Without_a_workspace_a_relative_start_has_nothing_to_resolve_against()
    {
        using var fx = new Fixture(withRoot: false);
        var r = await fx.Run(_grep, ("pattern", "x"));
        Assert.True(r.IsError);
        Assert.Contains("workspace", r.Text());
    }

    [Fact]
    public async Task CRLF_files_match_end_of_line_anchors_and_lines_have_no_carriage_return()
    {
        _fx.PutBytes("win.txt", System.Text.Encoding.UTF8.GetBytes("first\r\nneedle\r\nlast\r\n"));
        var r = await _fx.Run(_grep, ("pattern", "needle$"), ("output_mode", "content"), ("-n", true));
        Assert.Equal("win.txt:2:needle", r.Text());
    }

    [Fact]
    public async Task A_BOM_does_not_hide_a_match_at_the_start_of_the_file()
    {
        _fx.PutBytes("bom.txt", [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("needle here\n")]);
        Assert.Contains("bom.txt", (await _fx.Run(_grep, ("pattern", "^needle"))).Text());
    }

    // ── 并发扫描:输出必须和串行时逐字节一致 ──

    [Fact]
    public async Task Keeps_traversal_order_and_cuts_at_the_match_cap_exactly_where_a_serial_scan_would()
    {
        // 每个文件 10 条命中,40 个文件 → 上限 200 恰好落在第 20 个文件
        for (var i = 0; i < 40; i++)
        {
            _fx.Put($"f{i:D2}.txt", string.Concat(Enumerable.Repeat("hit\n", 10)));
        }

        var expected = Enumerable.Range(0, 20).Select(i => $"f{i:D2}.txt:10").ToList();
        for (var run = 0; run < 3; run++)
        {
            var r = await _fx.Run(_grep, ("pattern", "hit"), ("output_mode", "count"));
            Assert.Equal(expected, r.Text().Split("\n\n")[0].Split('\n'));
            Assert.Contains("match cap", r.Text());
        }
    }

    [Fact]
    public async Task Line_numbers_agree_with_split_by_newline_including_a_last_line_without_a_newline()
    {
        _fx.Put("a.txt", "x\nneedle one\n\nneedle two");
        var r = await _fx.Run(_grep, ("pattern", "needle"), ("output_mode", "content"), ("-n", true));
        Assert.Equal(["a.txt:2:needle one", "--", "a.txt:4:needle two"], r.Text().Split('\n'));
    }

    [Fact]
    public async Task Finds_a_hit_at_the_end_of_a_file_larger_than_the_single_read_threshold()
    {
        _fx.Put("big.txt", string.Concat(Enumerable.Repeat("filler line\n", 40_000)) + "needleAtTheEnd\n");
        var r = await _fx.Run(_grep, ("pattern", "needleAtTheEnd"), ("output_mode", "content"), ("-n", true));
        Assert.Equal("big.txt:40001:needleAtTheEnd", r.Text());
    }

    [Fact]
    public async Task Skips_a_large_file_whose_first_bytes_contain_NUL_and_a_file_over_the_size_cap()
    {
        _fx.PutBytes("big.bin", [0, .. System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("needle\n", 60_000)))]);
        _fx.PutBytes("giant.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("needle\n", 800_000))));
        _fx.Put("a.ts", "needle\n");
        Assert.Equal(["a.ts"], (await _fx.Run(_grep, ("pattern", "needle"))).Text().Split('\n'));
    }

    [Fact]
    public async Task A_remote_failure_mid_scan_stops_the_workers_and_surfaces_the_error()
    {
        for (var i = 0; i < 60; i++)
        {
            _fx.Put($"f{i}.txt", "needle\n");
        }

        var fs = new ThrowingFs(_fx.Env.Fs, new EnvironmentException(EnvironmentErrorCode.Disconnected), after: 6, onStat: true);
        var runtime = new ToolRuntime { Environment = new FsOverrideEnvironment(_fx.Env, fs), Root = _fx.Runtime.Root, GrepBackends = GrepBackends.None };
        var r = await _fx.Run(_grep, Fixture.Args(("pattern", "needle")), runtime: runtime);
        Assert.True(r.IsError);
        Assert.Contains("not reachable", r.Text());
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        _fx.Put("a.txt", "needle\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Run(_grep, Fixture.Args(("pattern", "needle")), ct: cts.Token));
    }

    // ── ReDoS:断言的是时间,不是结果 ──

    /// <summary>
    /// 这一组断言的是<b>时间</b>。线性时间引擎让经典炸弹根本没有回溯;只能回溯的写法,要么被静态筛查拒绝,要么在匹配超时后返回「部分结果 + 说明」。
    /// 谁把这几层去掉,这里会直接超时,而不是给出一个错误答案。
    /// </summary>
    [Fact]
    public async Task The_classic_bomb_returns_immediately_with_the_correct_answer()
    {
        // 行会先截到 2000 字符再匹配,所以炸弹要放在截断线以内
        _fx.Put("bomb.txt", new string('a', 1500) + "b\n");
        var started = Stopwatch.StartNew();
        var r = await _fx.Run(_grep, ("pattern", "(a+)+$"), ("output_mode", "content"));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("No content matching", r.Text()); // 行尾是 b,确实没有
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task A_backtracking_only_dangerous_pattern_is_refused_at_once_with_a_way_to_fix_it()
    {
        _fx.Put("bomb.txt", new string('a', 5000) + "b\n");
        var started = Stopwatch.StartNew();
        var r = await _fx.Run(_grep, ("pattern", "(?=a)(a+)+$"), ("output_mode", "content"));
        Assert.True(r.IsError);
        Assert.Contains("backtracking", r.Text());
        Assert.Contains("Fix it by", r.Text());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task A_dangerous_alternation_hits_the_match_timeout_and_the_result_says_it_is_incomplete()
    {
        _fx.Put("bomb.txt", new string('a', 1500) + "b\n");
        var started = Stopwatch.StartNew();
        var r = await _fx.Run(_grep, ("pattern", "(?=a)(a|aa)+$"));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("incomplete", r.Text());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task A_benign_pattern_on_a_very_long_line_returns_quickly()
    {
        _fx.Put("long.txt", new string('x', 200_000) + "\nneedle\n");
        var started = Stopwatch.StartNew();
        var r = await _fx.Run(_grep, ("pattern", "needle"));
        Assert.Contains("long.txt", r.Text());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Content_mode_gets_line_text_only_when_asked_for()
    {
        _fx.Put("a.txt", "L1\nalpha\nL3\n");
        Assert.Equal(["a.txt-1-L1", "a.txt:2:alpha", "a.txt-3-L3"], (await _fx.Run(_grep, ("pattern", "alpha"), ("output_mode", "content"), ("-n", true), ("-C", 1))).Text().Split('\n'));
        Assert.Equal(["a.txt:1"], (await _fx.Run(_grep, ("pattern", "alpha"), ("output_mode", "count"))).Text().Split('\n'));
        Assert.Equal(["a.txt"], (await _fx.Run(_grep, ("pattern", "alpha"))).Text().Split('\n'));
    }

    [Fact]
    public async Task Details_say_which_backend_ran_and_carry_the_environment()
    {
        _fx.Put("a.txt", "alpha\n");
        var d = (await _fx.Run(_grep, ("pattern", "alpha"))).DetailsOrThrow();
        Assert.Equal("managed", d.GetProperty("backend").GetString());
        Assert.Equal("dev-1", d.GetProperty("environment").GetProperty("deviceId").GetString());
    }
}

/// <summary>
/// Grep 的原生后端(rg / git grep / grep)。核心断言是「<b>换后端不换输出</b>」:同一份夹具、同一组参数,原生后端与托管引擎的输出逐字相同。
/// 机器上没装的后端整组跳过 —— 它们是可选的加速,不是必需品。
/// </summary>
public sealed class GrepNativeBackendTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly GrepTool _grep = new();

    public GrepNativeBackendTests()
    {
        _fx.Put("a.ts", "import x\nconst needle = 1\nexport {}\n");
        _fx.Put("src/b.ts", "one\ntwo needle\nthree\nfour\nNEEDLE five\n");
        _fx.Put("src/deep/c.js", "needle()\n");
        _fx.Put("src/d.md", "needle in docs\n");
        _fx.Put(".hidden/e.ts", "needle hidden\n");
        _fx.Put("node_modules/pkg/index.ts", "needle in deps\n");
        _fx.PutBytes("bin.dat", System.Text.Encoding.UTF8.GetBytes("needle\0binary"));
    }

    public void Dispose() => _fx.Dispose();

    private static bool Has(string bin)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(bin, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void GitInit(string dir)
    {
        using var p = Process.Start(new ProcessStartInfo("git", "init -q") { WorkingDirectory = dir, UseShellExecute = false });
        p!.WaitForExit();
    }

    private ToolRuntime With(GrepBackends backends) => new() { Environment = _fx.Env, Root = _fx.Runtime.Root, GrepBackends = backends };

    private async Task<string> GrepAsync(GrepBackends backends, params (string, object?)[] args)
    {
        var r = await _fx.Run(_grep, Fixture.Args(args), runtime: With(backends));
        Assert.False(r.IsError, r.Text());
        return r.Text();
    }

    /// <summary>结果按文件分组、组间排序后再比:比较的是「每个文件给出的内容」,不是文件的先后。</summary>
    private static string[] ByFile(string output)
    {
        var blocks = new Dictionary<string, List<string>>();
        var current = "";
        foreach (var line in output.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "^([^:-]+)[:-]");
            if (m.Success)
            {
                current = m.Groups[1].Value;
            }

            if (line == "--")
            {
                continue;
            }

            if (!blocks.TryGetValue(current, out var list))
            {
                blocks[current] = list = [];
            }

            list.Add(line);
        }

        return blocks.Values.Select(l => string.Join('\n', l)).Order().ToArray();
    }

    public static IEnumerable<object[]> Cases() =>
    [
        [new[] { "pattern=needle" }],
        [new[] { "pattern=needle", "-i=true", "output_mode=count" }],
        [new[] { "pattern=needle", "output_mode=content", "-n=true", "-C=1" }],
        [new[] { "pattern=needle", "glob=**/*.ts" }],
        [new[] { "pattern=needle", "glob=*.ts" }],
        [new[] { "pattern=needle", "type=js" }],
        [new[] { "pattern=needle", "path=src" }],
        [new[] { "pattern=needle", "path=src/b.ts", "output_mode=content", "-n=true" }],
        [new[] { "pattern=ne+dle\\s\\w+", "output_mode=content" }],
    ];

    private (string, object?)[] Parse(string[] pairs) => pairs.Select<string, (string, object?)>(p =>
    {
        var i = p.IndexOf('=');
        var (k, v) = (p[..i], p[(i + 1)..]);
        object? value = v switch
        {
            "true" => true,
            "src" => _fx.Abs("src"),
            "src/b.ts" => _fx.Abs("src/b.ts"),
            _ when k is "path" => _fx.Abs(v),
            _ => v,
        };
        return (k, value);
    }).ToArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Git_grep_gives_the_same_output_as_the_managed_engine(string[] pairs)
    {
        if (!Has("git"))
        {
            return;
        }

        GitInit(_fx.Root);
        Assert.Equal(ByFile(await GrepAsync(GrepBackends.None, Parse(pairs))), ByFile(await GrepAsync(GrepBackends.Git, Parse(pairs))));
        Assert.Equal("git", (await _fx.Run(_grep, Fixture.Args(Parse(pairs)), runtime: With(GrepBackends.Git))).DetailsOrThrow().GetProperty("backend").GetString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Plain_grep_gives_the_same_output_as_the_managed_engine(string[] pairs)
    {
        if (!Has("grep"))
        {
            return;
        }

        Assert.Equal(ByFile(await GrepAsync(GrepBackends.None, Parse(pairs))), ByFile(await GrepAsync(GrepBackends.Grep, Parse(pairs))));
        Assert.Equal("grep", (await _fx.Run(_grep, Fixture.Args(Parse(pairs)), runtime: With(GrepBackends.Grep))).DetailsOrThrow().GetProperty("backend").GetString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Ripgrep_gives_the_same_output_as_the_managed_engine(string[] pairs)
    {
        if (!Has("rg"))
        {
            return;
        }

        Assert.Equal(ByFile(await GrepAsync(GrepBackends.None, Parse(pairs))), ByFile(await GrepAsync(GrepBackends.Rg, Parse(pairs))));
    }

    [Fact]
    public async Task Native_backends_skip_dependency_directories_and_binary_files_but_search_hidden_ones()
    {
        if (!Has("grep"))
        {
            return;
        }

        var text = await GrepAsync(GrepBackends.Grep, ("pattern", "needle"));
        Assert.DoesNotContain("node_modules", text);
        Assert.DoesNotContain("bin.dat", text);
        Assert.Contains(".hidden/e.ts", text);
    }

    [Fact]
    public async Task Git_grep_respects_gitignore_but_still_searches_untracked_files()
    {
        if (!Has("git"))
        {
            return;
        }

        GitInit(_fx.Root);
        _fx.Put(".gitignore", "ignored/\n");
        _fx.Put("ignored/x.ts", "needle ignored\n");
        _fx.Put("fresh.ts", "needle fresh\n");
        var text = await GrepAsync(GrepBackends.Git, ("pattern", "needle"));
        Assert.Contains("fresh.ts", text);
        Assert.DoesNotContain("ignored/x.ts", text);
    }

    [Fact]
    public async Task When_the_start_point_itself_is_ignored_git_grep_is_not_used_so_nothing_is_silently_missed()
    {
        if (!Has("git"))
        {
            return;
        }

        GitInit(_fx.Root);
        _fx.Put(".gitignore", "ignored/\n");
        _fx.Put("ignored/x.ts", "needle ignored\n");
        var text = await GrepAsync(GrepBackends.Git, ("pattern", "needle"), ("path", _fx.Abs("ignored")));
        Assert.Contains("ignored/x.ts", text);
    }

    [Fact]
    public async Task Multiline_falls_through_to_the_managed_engine_when_only_git_and_grep_are_allowed()
    {
        _fx.Put("m.ts", "interface Foo {\n  bar: string\n}\n");
        var args = new (string, object?)[] { ("pattern", "interface Foo[\\s\\S]*?bar"), ("multiline", true), ("output_mode", "content"), ("-n", true) };
        var viaNative = await GrepAsync(GrepBackends.Git | GrepBackends.Grep, args);
        Assert.Equal(await GrepAsync(GrepBackends.None, args), viaNative);
        Assert.Contains("m.ts", viaNative);
    }

    [Fact]
    public async Task An_invalid_regex_is_finally_reported_by_the_managed_engine_not_turned_into_an_empty_result()
    {
        var r = await _fx.Run(_grep, Fixture.Args(("pattern", "([unclosed")), runtime: With(GrepBackends.Git | GrepBackends.Grep));
        Assert.True(r.IsError);
        Assert.Contains("Invalid regular expression", r.Text());
    }

    [Fact]
    public void Name_hints_only_push_down_root_independent_basename_patterns()
    {
        Assert.Equal(["*.ts"], NativeGrep.NameHints("*.ts"));
        Assert.Equal(["*.ts", "*.tsx"], NativeGrep.NameHints("**/*.{ts,tsx}"));
        Assert.Null(NativeGrep.NameHints("src/**/*.ts"));
        Assert.Null(NativeGrep.NameHints("**/a/*.ts"));
        Assert.Null(NativeGrep.NameHints("*.{a,{b,c}}"));
    }

    [Fact]
    public void Native_output_parsing_accepts_both_separators_and_dedupes_lines()
    {
        var cwd = EnvPath.Posix("/w");
        var parsed = NativeGrep.Parse(cwd, "./a.ts\0 12:text\n./a.ts\012:more\nsrc/b.ts\03\0z\nnonsense\n");
        // rg / grep: "path\0行号:正文"(行号前不带空格才算);git grep -z: "path\0行号\0正文"
        Assert.Equal([12], parsed[EnvPath.Posix("/w/a.ts")]);
        Assert.Equal([3], parsed[EnvPath.Posix("/w/src/b.ts")]);
    }
}
