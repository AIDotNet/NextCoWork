using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Tools.Tests;

/// <summary>
/// 文件四件套的真 IO 测试(移植自 2.x <c>fs-tools.test.ts</c>)—— 真临时目录、真符号链接,不打桩文件系统。
/// 打桩会把这几个工具最容易出错的地方全部测没:realpath 之后的路径比较、断链软链、目录 vs 文件、二进制探测。
/// </summary>
public sealed class FileToolTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly ReadTool _read = new();
    private readonly WriteTool _write = new();
    private readonly EditTool _edit = new();
    private readonly LsTool _ls = new();

    public void Dispose() => _fx.Dispose();

    private async Task ReadFirstAsync(string rel, string run = "run-1")
    {
        var r = await _fx.RunAs(run, _read, ("file_path", _fx.Abs(rel)));
        Assert.False(r.IsError, $"preparatory Read failed: {r.Text()}");
    }

    // ────────────────────────────── Read ──────────────────────────────

    [Fact]
    public async Task Read_output_is_cat_n_format_with_a_right_aligned_six_wide_number_and_a_tab()
    {
        _fx.Put("a.txt", "one\ntwo");
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("a.txt")));
        Assert.False(r.IsError);
        Assert.Equal("     1\tone\n     2\ttwo", r.Text());
    }

    [Fact]
    public async Task Read_accepts_a_workspace_relative_path()
    {
        _fx.Put("a.txt", "hi");
        Assert.Equal("     1\thi", (await _fx.Run(_read, ("file_path", "a.txt"))).Text());
    }

    [Fact]
    public async Task Read_offset_and_limit_page_through_a_file_and_say_where_to_continue()
    {
        _fx.Put("big.txt", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"L{i}")));
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("big.txt")), ("offset", 3), ("limit", 2));
        var text = r.Text();
        Assert.Contains("     3\tL3", text);
        Assert.Contains("     4\tL4", text);
        Assert.DoesNotContain("L5", text);
        // 续读的指引必须给出下一个 offset,不然模型只能猜
        Assert.Contains("offset=5", text);
    }

    [Fact]
    public async Task Read_offset_past_the_end_is_an_error_that_says_how_many_lines_there_are()
    {
        _fx.Put("a.txt", "one\ntwo");
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("a.txt")), ("offset", 99));
        Assert.True(r.IsError);
        Assert.Contains("only 2 lines", r.Text());
    }

    [Fact]
    public async Task Read_truncates_overlong_lines_and_marks_them()
    {
        _fx.Put("min.js", new string('x', 5000));
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("min.js")));
        Assert.Contains("[line truncated]", r.Text());
        Assert.True(r.Text().Length < 3000);
    }

    [Fact]
    public async Task Read_of_a_missing_file_points_at_LS_and_Glob()
    {
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("nope.txt")));
        Assert.True(r.IsError);
        Assert.Contains("LS", r.Text());
        Assert.Contains("Glob", r.Text());
    }

    [Fact]
    public async Task Read_of_a_directory_is_refused_and_points_at_LS()
    {
        Directory.CreateDirectory(_fx.Abs("dir"));
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("dir")));
        Assert.True(r.IsError);
        Assert.Contains("LS", r.Text());
    }

    [Fact]
    public async Task Read_refuses_binary_files()
    {
        _fx.PutBytes("blob.bin", [0x50, 0x4b, 0x00, 0x01, 0x02]);
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("blob.bin")));
        Assert.True(r.IsError);
        Assert.Contains("binary file", r.Text());
    }

    [Fact]
    public async Task Read_of_an_empty_file_is_not_an_error_and_counts_as_read()
    {
        _fx.Put("empty.txt", "");
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("empty.txt")));
        Assert.False(r.IsError);
        Assert.Contains("empty", r.Text());
        // 空文件也算「读过」—— 否则模型永远没法用 Write 往一个占位空文件里填内容
        var w = await _fx.Run(_write, ("file_path", _fx.Abs("empty.txt")), ("content", "filled"));
        Assert.False(w.IsError, w.Text());
    }

    [Fact]
    public async Task Read_of_a_file_over_the_limit_is_refused_and_points_at_Grep_and_sed()
    {
        _fx.PutBytes("huge.txt", new byte[9 * 1024 * 1024].Select(_ => (byte)'a').ToArray());
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("huge.txt")));
        Assert.True(r.IsError);
        Assert.Contains("Grep", r.Text());
        Assert.Contains("sed", r.Text());
    }

    [Fact]
    public async Task Read_hides_CRLF_and_the_BOM_and_does_not_invent_a_trailing_empty_line()
    {
        _fx.PutBytes("win.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("win.txt")));
        Assert.Equal("     1\tone\n     2\ttwo", r.Text());
        var details = r.DetailsOrThrow();
        Assert.Equal(2, details.GetProperty("totalLines").GetInt32());
    }

    [Fact]
    public async Task Read_stops_at_the_output_budget_and_tells_the_model_where_to_continue()
    {
        _fx.Put("long.txt", string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"line {i} " + new string('y', 100))));
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("long.txt")), ("limit", 3000));
        var text = r.Text();
        Assert.True(text.Length <= 61_000, $"output was {text.Length} chars");
        Assert.True(r.Truncated);
        Assert.Contains("output size limit", text);
        var lastShown = text.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains('\t')).Last();
        var number = int.Parse(lastShown.Split('\t')[0].Trim());
        Assert.Contains($"offset={number + 1}", text);
    }

    [Fact]
    public async Task Read_details_carry_the_execution_environment_for_the_card()
    {
        _fx.Put("a.txt", "x");
        var details = (await _fx.Run(_read, ("file_path", _fx.Abs("a.txt")))).DetailsOrThrow();
        Assert.Equal("read", details.GetProperty("kind").GetString());
        var env = details.GetProperty("environment");
        Assert.Equal("dev-1", env.GetProperty("deviceId").GetString());
        Assert.Equal("local", env.GetProperty("environment").GetString());
        Assert.Equal("a.txt", details.GetProperty("displayPath").GetString());
    }

    [Fact]
    public async Task Read_honours_cancellation_by_throwing_not_by_returning_an_error()
    {
        _fx.Put("a.txt", "x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Run(_read, Fixture.Args(("file_path", _fx.Abs("a.txt"))), ct: cts.Token));
    }

    [Fact]
    public async Task Invalid_arguments_become_a_tool_error_that_names_the_property()
    {
        var r = await _fx.Run(_read, Fixture.Args(("offset", 0)));
        Assert.True(r.IsError);
        Assert.Contains("file_path", r.Text());
        var r2 = await _fx.Run(_read, Fixture.Args(("file_path", "a"), ("offset", "abc")));
        Assert.True(r2.IsError);
        Assert.Contains("offset", r2.Text());
    }

    [Fact]
    public async Task Loosely_typed_numbers_from_the_model_are_coerced()
    {
        _fx.Put("a.txt", "one\ntwo\nthree");
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("a.txt")), ("offset", "2"), ("limit", "1"));
        Assert.False(r.IsError, r.Text());
        Assert.StartsWith("     2\ttwo", r.Text());
    }

    // ────────────────────────────── Write ──────────────────────────────

    [Fact]
    public async Task Write_creates_a_new_file_and_its_parent_directories()
    {
        var r = await _fx.Run(_write, ("file_path", _fx.Abs("a/b/c.ts")), ("content", "export const x = 1\n"));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("Created", r.Text());
        Assert.Equal("export const x = 1\n", _fx.Text("a/b/c.ts"));
    }

    [Fact]
    public async Task Write_refuses_to_overwrite_an_existing_file_that_was_not_read_first()
    {
        _fx.Put("a.txt", "original");
        var r = await _fx.Run(_write, ("file_path", _fx.Abs("a.txt")), ("content", "new"));
        Assert.True(r.IsError);
        Assert.Contains("Read", r.Text());
        Assert.Equal("original", _fx.Text("a.txt"));
    }

    [Fact]
    public async Task Write_overwrites_after_a_Read()
    {
        _fx.Put("a.txt", "original");
        await ReadFirstAsync("a.txt");
        var r = await _fx.Run(_write, ("file_path", _fx.Abs("a.txt")), ("content", "new"));
        Assert.False(r.IsError);
        Assert.Contains("Overwrote", r.Text());
        Assert.Equal("new", _fx.Text("a.txt"));
    }

    [Fact]
    public async Task A_read_in_another_run_does_not_count()
    {
        _fx.Put("a.txt", "original");
        await ReadFirstAsync("a.txt", run: "run-A");
        var r = await _fx.RunAs("run-B", _write, ("file_path", _fx.Abs("a.txt")), ("content", "new"));
        Assert.True(r.IsError);
        Assert.Equal("original", _fx.Text("a.txt"));
    }

    [Fact]
    public async Task After_a_Write_the_same_run_can_Edit_without_reading()
    {
        await _fx.Run(_write, ("file_path", _fx.Abs("a.txt")), ("content", "hello world"));
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.txt")), ("old_string", "world"), ("new_string", "there"));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("hello there", _fx.Text("a.txt"));
    }

    [Fact]
    public async Task Write_rejects_a_directory()
    {
        Directory.CreateDirectory(_fx.Abs("dir"));
        var r = await _fx.Run(_write, ("file_path", _fx.Abs("dir")), ("content", "x"));
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Write_leaves_no_temporary_files_behind()
    {
        await _fx.Run(_write, ("file_path", _fx.Abs("a.txt")), ("content", "x"));
        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(_fx.Root).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public async Task Overwriting_keeps_CRLF_and_the_BOM_the_model_could_not_see()
    {
        _fx.PutBytes("win.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
        await ReadFirstAsync("win.txt");
        await _fx.Run(_write, ("file_path", _fx.Abs("win.txt")), ("content", "uno\ndos\n"));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("uno\r\ndos\r\n")], _fx.Bytes("win.txt"));
    }

    [Fact]
    public async Task Overwriting_an_LF_file_does_not_introduce_CRLF()
    {
        _fx.Put("unix.txt", "one\ntwo\n");
        await ReadFirstAsync("unix.txt");
        await _fx.Run(_write, ("file_path", _fx.Abs("unix.txt")), ("content", "a\nb\n"));
        Assert.Equal("a\nb\n", _fx.Text("unix.txt"));
    }

    [Fact]
    public async Task Overwriting_keeps_the_permission_bits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _fx.Put("run.sh", "#!/bin/sh\necho 1\n");
        File.SetUnixFileMode(_fx.Abs("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await ReadFirstAsync("run.sh");
        await _fx.Run(_write, ("file_path", _fx.Abs("run.sh")), ("content", "#!/bin/sh\necho 2\n"));
        Assert.True(File.GetUnixFileMode(_fx.Abs("run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task The_plan_mode_write_fence_only_allows_the_plan_file()
    {
        using var fx = new Fixture(writeFence: null);
        var plan = fx.P("plan.md");
        var fenced = new ToolRuntime { Environment = fx.Env, Root = fx.Runtime.Root, WriteFence = plan };
        var blocked = await fx.Run(_write, Fixture.Args(("file_path", fx.Abs("other.txt")), ("content", "x")), runtime: fenced);
        Assert.True(blocked.IsError);
        Assert.Contains("Plan mode", blocked.Text());
        Assert.False(File.Exists(fx.Abs("other.txt")));

        var allowed = await fx.Run(_write, Fixture.Args(("file_path", fx.Abs("plan.md")), ("content", "# plan")), runtime: fenced);
        Assert.False(allowed.IsError, allowed.Text());

        fx.Put("e.txt", "x");
        await fx.Run(_read, Fixture.Args(("file_path", fx.Abs("e.txt"))), runtime: fenced);
        var edit = await fx.Run(_edit, Fixture.Args(("file_path", fx.Abs("e.txt")), ("old_string", "x"), ("new_string", "y")), runtime: fenced);
        Assert.True(edit.IsError);
        Assert.Equal("x", fx.Text("e.txt"));
    }

    [Fact]
    public async Task Write_and_Edit_record_changes_with_the_first_before_and_the_last_after()
    {
        await _fx.Run(_write, ("file_path", _fx.Abs("n.txt")), ("content", "v1"));
        await _fx.Run(_edit, ("file_path", _fx.Abs("n.txt")), ("old_string", "v1"), ("new_string", "v2"));
        var changes = _fx.Runtime.Changes.Take(new Ncw.Agent.Kernel.RunId("run-1"));
        var change = Assert.Single(changes);
        Assert.Null(change.Before);
        Assert.Equal("v2", change.After);
        Assert.Equal(ChangeKind.Created, change.Kind);
        Assert.True(change.InWorkspace);
    }

    [Fact]
    public async Task Parallel_edits_to_the_same_file_are_serialized_by_the_mutation_queue()
    {
        _fx.Put("a.txt", string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line{i}")));
        await ReadFirstAsync("a.txt");
        var edits = Enumerable.Range(0, 20).Select(i =>
            _fx.Run(_edit, ("file_path", _fx.Abs("a.txt")), ("old_string", $"line{i}\n".Replace("line19\n", "line19")), ("new_string", $"LINE{i}\n".Replace("LINE19\n", "LINE19")))).ToArray();
        var results = await Task.WhenAll(edits);
        Assert.All(results, r => Assert.False(r.IsError, r.Text()));
        Assert.Equal(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"LINE{i}")), _fx.Text("a.txt"));
    }

    // ────────────────────────────── Edit ──────────────────────────────

    [Fact]
    public async Task Edit_replaces_a_unique_match()
    {
        _fx.Put("a.ts", "const a = 1\nconst b = 2\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "const b = 2"), ("new_string", "const b = 3"));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("const a = 1\nconst b = 3\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_requires_a_prior_Read()
    {
        _fx.Put("a.ts", "x");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "x"), ("new_string", "y"));
        Assert.True(r.IsError);
        Assert.Contains("Read", r.Text());
        Assert.Equal("x", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_with_a_non_unique_match_changes_nothing_and_reports_the_count()
    {
        _fx.Put("a.ts", "foo\nfoo\nfoo\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "foo"), ("new_string", "bar"));
        Assert.True(r.IsError);
        Assert.Contains("appears 3 times", r.Text());
        Assert.Equal("foo\nfoo\nfoo\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_replace_all_replaces_every_occurrence()
    {
        _fx.Put("a.ts", "foo\nfoo\nfoo\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "foo"), ("new_string", "bar"), ("replace_all", true));
        Assert.False(r.IsError);
        Assert.Contains("3 occurrence", r.Text());
        Assert.Equal("bar\nbar\nbar\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_not_found_gives_a_way_forward_not_the_file_contents()
    {
        _fx.Put("a.ts", $"const token = \"{Fixture.Secret}\"\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "nonexistent original"), ("new_string", "x"));
        Assert.True(r.IsError);
        Assert.DoesNotContain(Fixture.Secret, r.Text());
        Assert.Contains("line-number prefix", r.Text());
    }

    [Fact]
    public async Task Edit_rejects_identical_old_and_new_strings()
    {
        _fx.Put("a.ts", "x");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "x"), ("new_string", "x"));
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Edit_of_a_missing_file_points_at_Write()
    {
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("nope.ts")), ("old_string", "a"), ("new_string", "b"));
        Assert.True(r.IsError);
        Assert.Contains("Write", r.Text());
    }

    [Fact]
    public async Task Edit_treats_dollar_sequences_in_the_replacement_literally()
    {
        // 2.x 的 String.replace 会把 $& 当替换模式,这是个 bug;C# 的 Replace 没有这回事
        _fx.Put("a.ts", "price = X\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "X"), ("new_string", "$& $1 $$"));
        Assert.False(r.IsError);
        Assert.Equal("price = $& $1 $$\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_applies_several_replacements_in_one_call_each_matched_against_the_original()
    {
        _fx.Put("a.ts", "alpha\nbeta\ngamma\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("edits", Fixture.Edits(("gamma", "GAMMA"), ("alpha", "ALPHA")))));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("ALPHA\nbeta\nGAMMA\n", _fx.Text("a.ts"));
        Assert.Equal(2, r.DetailsOrThrow().GetProperty("replacements").GetInt32());
    }

    [Fact]
    public async Task Edit_matches_every_edit_against_the_original_so_text_created_by_an_earlier_edit_is_not_found()
    {
        _fx.Put("a.ts", "alpha\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("edits", Fixture.Edits(("alpha", "beta"), ("beta", "gamma")))));
        Assert.True(r.IsError);
        Assert.Contains("edits[1]", r.Text());
        Assert.Equal("alpha\n", _fx.Text("a.ts")); // 一处都没改
    }

    [Fact]
    public async Task Edit_rejects_overlapping_edits_and_changes_nothing()
    {
        _fx.Put("a.ts", "one two three\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("edits", Fixture.Edits(("one two", "A"), ("two three", "B")))));
        Assert.True(r.IsError);
        Assert.Contains("overlap", r.Text());
        Assert.Equal("one two three\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_with_one_failing_edit_applies_none_of_them()
    {
        _fx.Put("a.ts", "alpha\nbeta\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("edits", Fixture.Edits(("alpha", "A"), ("missing", "B")))));
        Assert.True(r.IsError);
        Assert.Equal("alpha\nbeta\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_rejects_mixing_the_single_form_with_edits()
    {
        _fx.Put("a.ts", "alpha\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("old_string", "alpha"), ("new_string", "A"), ("edits", Fixture.Edits(("alpha", "B")))));
        Assert.True(r.IsError);
        Assert.Contains("not both", r.Text());
    }

    [Fact]
    public async Task Edit_falls_back_to_a_looser_match_for_curly_quotes_and_dashes()
    {
        _fx.Put("a.md", "He said “hello” — and left.\n");
        await ReadFirstAsync("a.md");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.md")), ("old_string", "He said \"hello\" - and left."), ("new_string", "Bye."));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("Bye.\n", _fx.Text("a.md"));
        Assert.True(r.DetailsOrThrow().GetProperty("fuzzy").GetBoolean());
    }

    [Fact]
    public async Task Edit_loose_match_ignores_trailing_whitespace_and_touches_only_the_matched_region()
    {
        _fx.Put("a.ts", "keep   \nfoo()  \nbar()\nother   \n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "foo()\nbar()"), ("new_string", "baz()"));
        Assert.False(r.IsError, r.Text());
        // 只有匹配到的那一段被替换,别处的行尾空白原样保留
        Assert.Equal("keep   \nbaz()\nother   \n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_loose_match_applies_NFKC()
    {
        _fx.Put("a.ts", "let ｆｏｏ = 1\n"); // 全角 foo
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "let foo = 1"), ("new_string", "let bar = 2"));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("let bar = 2\n", _fx.Text("a.ts"));
    }

    [Fact]
    public async Task Edit_preserves_CRLF_and_the_BOM_and_matches_multi_line_text_written_with_LF()
    {
        _fx.PutBytes("win.cs", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class A\r\n{\r\n    int x;\r\n    int y;\r\n}\r\n")]);
        await ReadFirstAsync("win.cs");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("win.cs")), ("old_string", "    int x;\n    int y;"), ("new_string", "    int z;"));
        Assert.False(r.IsError, r.Text());
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class A\r\n{\r\n    int z;\r\n}\r\n")], _fx.Bytes("win.cs"));
    }

    [Fact]
    public async Task Edit_preserves_LF_files_as_LF()
    {
        _fx.Put("unix.txt", "a\nb\nc\n");
        await ReadFirstAsync("unix.txt");
        await _fx.Run(_edit, ("file_path", _fx.Abs("unix.txt")), ("old_string", "b"), ("new_string", "B\nB2"));
        Assert.Equal("a\nB\nB2\nc\n", _fx.Text("unix.txt"));
    }

    [Fact]
    public async Task Edit_keeps_the_permission_bits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _fx.Put("run.sh", "echo 1\n");
        File.SetUnixFileMode(_fx.Abs("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await ReadFirstAsync("run.sh");
        await _fx.Run(_edit, ("file_path", _fx.Abs("run.sh")), ("old_string", "1"), ("new_string", "2"));
        Assert.True(File.GetUnixFileMode(_fx.Abs("run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Edit_details_carry_a_diff_the_first_changed_line_and_the_environment()
    {
        _fx.Put("a.ts", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"l{i}")) + "\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, ("file_path", _fx.Abs("a.ts")), ("old_string", "l5"), ("new_string", "FIVE"));
        var d = r.DetailsOrThrow();
        Assert.Equal("edit", d.GetProperty("kind").GetString());
        Assert.Equal(5, d.GetProperty("firstChangedLine").GetInt32());
        var diff = d.GetProperty("diff").GetString()!;
        Assert.Contains("-l5", diff);
        Assert.Contains("+FIVE", diff);
        Assert.Contains(" l4", diff);
        Assert.Contains("@@ -2,7 +2,7 @@", diff);
        Assert.Equal("dev-1", d.GetProperty("environment").GetProperty("deviceId").GetString());
        Assert.DoesNotContain("diff", r.Text()); // Details 永远不发给模型
    }

    [Fact]
    public async Task Edit_diff_merges_nearby_edits_into_one_hunk()
    {
        _fx.Put("a.ts", string.Join('\n', Enumerable.Range(1, 12).Select(i => $"l{i}")) + "\n");
        await ReadFirstAsync("a.ts");
        var r = await _fx.Run(_edit, Fixture.Args(("file_path", _fx.Abs("a.ts")), ("edits", Fixture.Edits(("l3", "THREE"), ("l7", "SEVEN")))));
        var diff = r.DetailsOrThrow().GetProperty("diff").GetString()!;
        Assert.Equal(1, diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("+SEVEN", diff);
    }

    // ────────────────────────────── LS ──────────────────────────────

    [Fact]
    public async Task LS_lists_directories_first()
    {
        Directory.CreateDirectory(_fx.Abs("zdir"));
        _fx.Put("a.txt", "x");
        var r = await _fx.Run(_ls, ("path", _fx.Root));
        Assert.False(r.IsError);
        Assert.True(r.Text().IndexOf("zdir/", StringComparison.Ordinal) < r.Text().IndexOf("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LS_sorts_numbers_by_value_and_ignores_case()
    {
        foreach (var name in new[] { "v10.txt", "v2.txt", "B.txt", "a.txt" })
        {
            _fx.Put(name, "x");
        }

        var lines = (await _fx.Run(_ls, ("path", _fx.Root))).Text().Split('\n').Skip(1).Select(l => l.Split("  ")[0]).ToList();
        Assert.Equal(["a.txt", "B.txt", "v2.txt", "v10.txt"], lines);
    }

    [Fact]
    public async Task LS_ignore_filters_by_name_pattern()
    {
        _fx.Put("keep.ts", "x");
        _fx.Put("drop.log", "x");
        var r = await _fx.Run(_ls, Fixture.Args(("path", _fx.Root), ("ignore", new System.Text.Json.Nodes.JsonArray("*.log"))));
        Assert.Contains("keep.ts", r.Text());
        Assert.DoesNotContain("drop.log", r.Text());
    }

    [Fact]
    public async Task LS_says_when_a_directory_is_empty()
    {
        Directory.CreateDirectory(_fx.Abs("empty"));
        var r = await _fx.Run(_ls, ("path", _fx.Abs("empty")));
        Assert.False(r.IsError);
        Assert.Contains("empty directory", r.Text());
    }

    [Fact]
    public async Task LS_of_a_file_points_at_Read()
    {
        _fx.Put("a.txt", "x");
        var r = await _fx.Run(_ls, ("path", _fx.Abs("a.txt")));
        Assert.True(r.IsError);
        Assert.Contains("Read", r.Text());
    }

    [Fact]
    public async Task LS_of_a_missing_directory_is_an_error()
    {
        var r = await _fx.Run(_ls, ("path", _fx.Abs("nope")));
        Assert.True(r.IsError);
        Assert.Contains("does not exist", r.Text());
    }

    [Fact]
    public async Task LS_still_lists_a_dangling_symlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(_fx.Abs("dangling"), _fx.Abs("nothing-here"));
        var r = await _fx.Run(_ls, ("path", _fx.Root));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("dangling", r.Text());
    }

    [Fact]
    public async Task LS_shows_file_sizes_and_the_details_list_the_entries()
    {
        _fx.Put("a.txt", new string('x', 2048));
        var r = await _fx.Run(_ls, ("path", _fx.Root));
        Assert.Contains("a.txt  (2.0 KB)", r.Text());
        var entries = r.DetailsOrThrow().GetProperty("entries");
        Assert.Equal(2048, entries[0].GetProperty("size").GetInt64());
    }

    // ───────────────────── 项目根之外的路径(边界) ─────────────────────

    /// <summary>
    /// 工作区不是围栏 —— 「谁能碰哪个文件」由权限档位决定。这一组钉的是放开之后仍然必须成立的几件事:
    /// 真的能碰到、回执里的路径一律是绝对形式(相对形式对不上任何一个根)、没有工作区时绝对路径照样能用。
    /// </summary>
    [Fact]
    public async Task Outside_the_root_Read_works_by_relative_or_absolute_path_and_reports_absolute()
    {
        foreach (var path in new[] { "../outside/secret.txt", Path.Combine(_fx.Outside, "secret.txt") })
        {
            var r = await _fx.Run(_read, ("file_path", path));
            Assert.False(r.IsError, r.Text());
            Assert.Contains(Fixture.Secret, r.Text());
            var d = r.DetailsOrThrow();
            Assert.True(d.GetProperty("outsideWorkspace").GetBoolean());
            Assert.Equal(Path.Combine(_fx.Outside, "secret.txt"), d.GetProperty("displayPath").GetString());
        }
    }

    [Fact]
    public async Task Outside_the_root_Write_works_and_reports_the_absolute_path()
    {
        var target = Path.Combine(_fx.Outside, "planted.txt");
        var r = await _fx.Run(_write, ("file_path", target), ("content", "x"));
        Assert.False(r.IsError, r.Text());
        Assert.Equal("x", File.ReadAllText(target));
        Assert.Contains(target, r.Text()); // 不是 ../outside/planted.txt
        Assert.False(_fx.Runtime.Changes.Take(new Ncw.Agent.Kernel.RunId("run-1")).Single().InWorkspace);
    }

    [Fact]
    public async Task Outside_the_root_Edit_still_requires_a_Read_first()
    {
        var target = Path.Combine(_fx.Outside, "secret.txt");
        var denied = await _fx.Run(_edit, ("file_path", target), ("old_string", "AKIA"), ("new_string", "B"));
        Assert.True(denied.IsError, "an edit without a prior Read must be refused");
        Assert.True((await _fx.Run(_read, ("file_path", target))).IsError == false);
        var r = await _fx.Run(_edit, ("file_path", target), ("old_string", "AKIA"), ("new_string", "B"));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("B-TOTALLY-SECRET", File.ReadAllText(target));
    }

    [Fact]
    public async Task Outside_the_root_LS_works()
    {
        var r = await _fx.Run(_ls, ("path", _fx.Outside));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("secret.txt", r.Text());
    }

    [Fact]
    public async Task A_symlink_pointing_outside_is_followed_and_flagged_as_outside()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateSymbolicLink(_fx.Abs("link"), _fx.Outside);
        var r = await _fx.Run(_read, ("file_path", _fx.Abs("link/secret.txt")));
        Assert.False(r.IsError, r.Text());
        Assert.Contains(Fixture.Secret, r.Text());
        Assert.True(r.DetailsOrThrow().GetProperty("outsideWorkspace").GetBoolean());
    }

    [Fact]
    public async Task A_symlink_pointing_outside_cannot_be_used_to_write_a_new_file_unnoticed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateSymbolicLink(_fx.Abs("link"), _fx.Outside);
        var r = await _fx.Run(_write, ("file_path", _fx.Abs("link/new.txt")), ("content", "x"));
        Assert.False(r.IsError, r.Text());
        Assert.True(r.DetailsOrThrow().GetProperty("outsideWorkspace").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_fx.Outside, "new.txt")));
    }

    [Fact]
    public async Task Without_a_workspace_relative_paths_are_refused_and_absolute_paths_work()
    {
        using var fx = new Fixture(withRoot: false);
        var rejected = new (string Name, Task<ToolResult> Task)[]
        {
            ("Read", fx.Run(_read, ("file_path", "x.txt"))),
            ("Write", fx.Run(_write, ("file_path", "x.txt"), ("content", "y"))),
            ("Edit", fx.Run(_edit, ("file_path", "x.txt"), ("old_string", "a"), ("new_string", "b"))),
            ("LS", fx.Run(_ls, ("path", "x"))),
        };
        foreach (var (name, task) in rejected)
        {
            var r = await task;
            Assert.True(r.IsError, name);
            Assert.Contains("workspace", r.Text());
        }

        var ok = await fx.Run(_read, ("file_path", Path.Combine(fx.Outside, "secret.txt")));
        Assert.False(ok.IsError, ok.Text());
        Assert.Contains(Fixture.Secret, ok.Text());
    }

    /// <summary>用户登记的根常常不是 realpath 形式(macOS 的 /var、链到外置卷的 ~/code):根内的文件仍然是根内,回执仍然用相对路径。</summary>
    [Fact]
    public async Task A_root_registered_through_a_symlink_still_displays_in_workspace_files_relatively()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_fx.Base, "ws-link");
        Directory.CreateSymbolicLink(link, _fx.Root);
        _fx.Put("src/a.txt", "hi");
        var runtime = new ToolRuntime { Environment = _fx.Env, Root = EnvPath.Parse(link, Fixture.Style) };

        foreach (var input in new[] { "src/a.txt", Path.Combine(link, "src", "a.txt"), _fx.Abs("src/a.txt") })
        {
            var r = await _fx.Run(_read, Fixture.Args(("file_path", input)), runtime: runtime);
            Assert.False(r.IsError, r.Text());
            var d = r.DetailsOrThrow();
            Assert.False(d.GetProperty("outsideWorkspace").GetBoolean(), input);
            Assert.Equal("src/a.txt", d.GetProperty("displayPath").GetString());
        }

        var ls = await _fx.Run(_ls, Fixture.Args(("path", ".")), runtime: runtime);
        Assert.StartsWith(".:", ls.Text());
    }

    [Fact]
    public async Task A_NUL_in_a_path_is_rejected_with_a_message_not_an_exception()
    {
        var r = await _fx.Run(_read, ("file_path", "a\0b"));
        Assert.True(r.IsError);
        Assert.Contains("not valid", r.Text());
    }

    [Fact]
    public async Task A_context_without_an_execution_environment_fails_with_an_explanation()
    {
        var inner = new ToolContext(new Ncw.Agent.Kernel.RunId("r"), "c", "s", 0, null, new Progress<ToolProgress>(), new FileMutationQueue());
        var r = await _read.ExecuteAsync(new ToolInvocation("c", "Read", _read, Fixture.Args(("file_path", "a"))), inner, CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("execution environment", r.Text());
    }
}
