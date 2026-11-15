using System.Diagnostics;
using Ncw.Core.Text;

namespace Ncw.Core.Tests;

/// <summary>
/// 前置块解析器的测试,移植自 2.x <c>src/main/kernel/__tests__/frontmatter.test.ts</c>(一条 <c>it</c> 对一条 <c>[Fact]</c>,顺序不变)。
/// 两类用例,分开看:<b>能力</b>(支持的子集确实能解析出来)与<b>防线</b>(不可信输入 —— SKILL.md 来自 zip / git —— 的那几条:
/// 尺寸闸门、控制字符、危险键。它们不是「边界情况」,而是这个模块存在的理由)。名字里带 <c>Invariant</c> 的是 2.x 标星(★)的不变量。
/// 原型污染那一组在 C# 里没有污染可言,但「这三个键被拒绝并记入 skipped」是对等规则,断言原样保留。
/// </summary>
public sealed class FrontmatterTests
{
    private static Frontmatter Parse(string src) => FrontmatterParser.Parse(src);

    /// <summary>取字符串值;键不存在或是列表都会让测试失败。</summary>
    private static string Text(Frontmatter fm, string key)
    {
        Assert.True(fm.Data.TryGetValue(key, out var v), $"key '{key}' missing");
        Assert.False(v.IsList, $"key '{key}' is a list");
        return v.Text;
    }

    /// <summary>取列表值;键不存在或是字符串都会让测试失败。</summary>
    private static string[] Items(Frontmatter fm, string key)
    {
        Assert.True(fm.Data.TryGetValue(key, out var v), $"key '{key}' missing");
        Assert.True(v.IsList, $"key '{key}' is not a list");
        return [.. v.Items];
    }

    private static string Skipped(Frontmatter fm) => string.Join(",", fm.Skipped);

    /// <summary>把 map 拍平成按键排序的「键=值」行,用于整体比较并且失败时能看出差在哪。</summary>
    private static string[] Dump(IReadOnlyDictionary<string, FrontmatterValue> data) =>
        [.. data.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            p.Value.IsList
                ? $"{p.Key}=L[{string.Join(" ; ", p.Value.Items.Select(i => "<" + i + ">"))}]"
                : $"{p.Key}=T<{p.Value.Text}>")];

    private static string[] Dump(Dictionary<string, FrontmatterValue?> data) =>
        Dump(data.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.Value, StringComparer.Ordinal));

    /// <summary>往返一次,回到 data。</summary>
    private static Dictionary<string, FrontmatterValue> Round(Dictionary<string, FrontmatterValue?> data, string body = "body") =>
        Parse(FrontmatterParser.Serialize(data, body)).Data.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static void AssertRoundTrips(Dictionary<string, FrontmatterValue?> data) =>
        Assert.Equal(Dump(data), Dump(Round(data)));

    // ────────────────────────── 基本形状 ──────────────────────────

    [Fact]
    public void Parses_keys_and_body()
    {
        var fm = Parse("---\nname: commit\ndescription: 写提交信息\n---\n正文第一行\n正文第二行");
        Assert.Equal("commit", Text(fm, "name"));
        Assert.Equal("写提交信息", Text(fm, "description"));
        Assert.Equal("正文第一行\n正文第二行", fm.Body);
    }

    [Fact]
    public void Three_dots_also_close_the_block()
    {
        var fm = Parse("---\nname: x\n...\nbody");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Equal("body", fm.Body);
    }

    [Fact]
    public void Colon_in_value_splits_on_the_first_one_only()
    {
        var fm = Parse("---\ndescription: 用法:先读再写\n---\n");
        Assert.Equal("用法:先读再写", Text(fm, "description"));
    }

    [Fact]
    public void Invariant_inline_hash_is_not_stripped_as_a_comment()
    {
        // ★ Claude Code 自己的 description 里就带 #
        var fm = Parse("---\ndescription: 以 # 开头的行是注释\n---\n");
        Assert.Equal("以 # 开头的行是注释", Text(fm, "description"));
    }

    [Fact]
    public void Whole_line_hash_is_a_comment_and_silently_skipped()
    {
        var fm = Parse("---\n# 这是注释\nname: x\n---\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Empty(fm.Skipped);
    }

    [Fact]
    public void Blank_lines_do_not_affect_parsing()
    {
        var fm = Parse("---\nname: x\n\ndescription: y\n---\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Equal("y", Text(fm, "description"));
    }

    // ────────────────────────── 块标量 ──────────────────────────

    [Fact]
    public void Invariant_literal_block_keeps_newlines_and_keys_after_the_block_still_parse()
    {
        // ★ installSkillZip 那个「装不上」的回归保护:官方 Skill 的 description 就是这么写的,而安装器把 skipped 非空当硬失败。
        var fm = Parse("---\ndescription: |\n  第一行\n  第二行\nname: x\n---\n正文");
        Assert.Equal("第一行\n第二行\n", Text(fm, "description"));
        Assert.Equal("x", Text(fm, "name"));
        Assert.Empty(fm.Skipped);
        Assert.Equal("正文", fm.Body);
        // ★ 缩进的续行不能被当成「嵌套 map」再报一次,更不能被当成键值解析出来
        Assert.False(fm.Data.ContainsKey("第一行"));
    }

    [Fact]
    public void Folded_block_folds_newlines_to_spaces_and_blank_lines_are_newlines()
    {
        var fm = Parse("---\nd: >\n  一\n  二\n\n  三\n---\n");
        Assert.Equal("一 二\n三\n", Text(fm, "d"));
    }

    [Fact]
    public void Invariant_folded_block_keeps_literal_newlines_around_more_indented_lines()
    {
        var fm = Parse("---\nd: >\n  开头\n    缩进行\n  结尾\n---\n");
        Assert.Equal("开头\n  缩进行\n结尾\n", Text(fm, "d"));
    }

    [Fact]
    public void Chomping_indicator_clip_keeps_one_newline_minus_strips_all_plus_keeps_all()
    {
        Assert.Equal("x\n", Text(Parse("---\nd: |\n  x\n\n\n---\n"), "d"));
        Assert.Equal("x", Text(Parse("---\nd: |-\n  x\n\n---\n"), "d"));
        Assert.Equal("x\n\n", Text(Parse("---\nd: |+\n  x\n\n---\n"), "d"));
    }

    [Fact]
    public void Indentation_indicator_is_accepted_in_both_orders_and_preserves_leading_spaces()
    {
        // `|2` 把块的缩进钉死在 2 列,于是第一行多出来的两个空格是内容的一部分
        Assert.Equal("  缩进\n顶格\n", Text(Parse("---\nd: |2\n    缩进\n  顶格\n---\n"), "d"));
        Assert.Equal("x", Text(Parse("---\nd: |-2\n  x\n---\n"), "d"));
        Assert.Equal("x", Text(Parse("---\nd: |2-\n  x\n---\n"), "d"));
    }

    [Fact]
    public void Blank_lines_and_hash_inside_a_block_are_neither_comments_nor_separators()
    {
        var fm = Parse("---\nd: |\n  一\n\n  # 井号\nname: x\n---\n");
        Assert.Equal("一\n\n# 井号\n", Text(fm, "d"));
        Assert.Equal("x", Text(fm, "name"));
    }

    [Fact]
    public void Empty_block_reads_as_empty_value_and_the_next_key_is_unaffected()
    {
        var fm = Parse("---\nd: |\nname: x\n---\n");
        Assert.Null(FrontmatterParser.String(fm, "d"));
        Assert.Equal("x", Text(fm, "name"));
        Assert.Empty(fm.Skipped);
    }

    [Fact]
    public void Invariant_block_content_is_also_length_clamped_because_it_enters_the_system_prompt()
    {
        var fm = Parse("---\nd: |\n  " + new string('x', FrontmatterLimits.ValueMax + 100) + "\n---\n");
        Assert.Equal(FrontmatterLimits.ValueMax, Text(fm, "d").Length);
        Assert.Contains("truncated", Skipped(fm));
    }

    // ────────────────────────── 一层嵌套 ──────────────────────────

    [Fact]
    public void Invariant_real_marketplace_skill_with_metadata_block_no_longer_fails_the_whole_install()
    {
        var fm = Parse(string.Join(
            "\n",
            "---",
            "name: programming-tutor",
            "description: \"全能 AI 编程导师\"",
            "license: MIT",
            "metadata:",
            "  author: \"Samuel Kahessay\"",
            "  version: \"1.0.1\"",
            "  category: \"education\"",
            "---",
            "正文"));
        Assert.Empty(fm.Skipped);
        Assert.Equal("programming-tutor", Text(fm, "name"));
        Assert.Equal("MIT", Text(fm, "license"));
        Assert.Equal("Samuel Kahessay", Text(fm, "metadata.author"));
        Assert.Equal("1.0.1", Text(fm, "metadata.version"));
        Assert.Equal("正文", fm.Body);
        // 父键本身是一张 map,不该再留着那个空串占位
        Assert.False(fm.Data.ContainsKey("metadata"));
    }

    [Fact]
    public void Top_level_keys_still_parse_after_a_nested_block_ends()
    {
        var fm = Parse("---\nm:\n  a: 1\nname: x\n---\n");
        Assert.Equal("1", Text(fm, "m.a"));
        Assert.Equal("x", Text(fm, "name"));
        Assert.Empty(fm.Skipped);
    }

    [Fact]
    public void Lists_under_a_nested_key_are_accepted_in_block_and_flow_style()
    {
        var block = Parse("---\nm:\n  tags:\n    - a\n    - b\n---\n");
        Assert.Equal(new[] { "a", "b" }, Items(block, "m.tags"));
        var flow = Parse("---\nm:\n  tags: [a, b]\n---\n");
        Assert.Equal(new[] { "a", "b" }, Items(flow, "m.tags"));
    }

    [Fact]
    public void A_nested_value_can_also_be_a_block_scalar()
    {
        var fm = Parse("---\nm:\n  note: |\n    第一行\n    第二行\n  after: x\n---\n");
        Assert.Equal("第一行\n第二行\n", Text(fm, "m.note"));
        Assert.Equal("x", Text(fm, "m.after"));
    }

    [Fact]
    public void Invariant_proto_key_inside_a_nested_map_is_still_rejected()
    {
        var fm = Parse("---\nm:\n  __proto__: polluted\n---\n");
        Assert.False(fm.Data.ContainsKey("m.__proto__"));
        Assert.Contains("security", Skipped(fm));
    }

    [Fact]
    public void An_empty_parent_key_without_children_is_still_an_empty_value()
    {
        var fm = Parse("---\nm:\nname: x\n---\n");
        Assert.Equal("", Text(fm, "m"));
        Assert.Equal("x", Text(fm, "name"));
    }

    // ────────────────────────── 引号 ──────────────────────────

    [Fact]
    public void Single_and_double_quotes_are_both_stripped()
    {
        var fm = Parse("---\na: 'hello'\nb: \"world\"\n---\n");
        Assert.Equal("hello", Text(fm, "a"));
        Assert.Equal("world", Text(fm, "b"));
    }

    [Fact]
    public void Doubled_single_quote_inside_single_quotes_is_a_literal_single_quote()
    {
        var fm = Parse("---\na: 'it''s'\n---\n");
        Assert.Equal("it's", Text(fm, "a"));
    }

    [Fact]
    public void Backslash_n_inside_double_quotes_becomes_a_real_newline()
    {
        var fm = Parse("---\na: \"x\\ny\"\n---\n");
        Assert.Equal("x\ny", Text(fm, "a"));
    }

    [Fact]
    public void Unbalanced_quotes_are_kept_as_is_without_guessing()
    {
        var fm = Parse("---\na: 'unterminated\n---\n");
        Assert.Equal("'unterminated", Text(fm, "a"));
    }

    // ────────────────────────── 列表 ──────────────────────────

    [Fact]
    public void Flow_sequence()
    {
        var fm = Parse("---\ntools: [Read, Grep, Glob]\n---\n");
        Assert.Equal(new[] { "Read", "Grep", "Glob" }, Items(fm, "tools"));
    }

    [Fact]
    public void Quotes_inside_a_flow_sequence_are_stripped_too()
    {
        var fm = Parse("---\ntools: ['Read', \"Grep\"]\n---\n");
        Assert.Equal(new[] { "Read", "Grep" }, Items(fm, "tools"));
    }

    [Fact]
    public void Block_sequence()
    {
        var fm = Parse("---\ntools:\n  - Read\n  - Grep\n---\n");
        Assert.Equal(new[] { "Read", "Grep" }, Items(fm, "tools"));
    }

    [Fact]
    public void Block_sequence_without_indentation_is_accepted()
    {
        var fm = Parse("---\ntools:\n- Read\n- Grep\n---\n");
        Assert.Equal(new[] { "Read", "Grep" }, Items(fm, "tools"));
    }

    [Fact]
    public void Another_key_can_follow_a_block_sequence()
    {
        var fm = Parse("---\ntools:\n  - Read\nname: x\n---\n");
        Assert.Equal(new[] { "Read" }, Items(fm, "tools"));
        Assert.Equal("x", Text(fm, "name"));
    }

    [Fact]
    public void Top_level_list_item_without_a_key_is_recorded_in_skipped()
    {
        var fm = Parse("---\n- 孤儿\n---\n");
        Assert.Contains("list item", Skipped(fm));
    }

    [Fact]
    public void Empty_list_value_stays_an_empty_string_not_missing()
    {
        var fm = Parse("---\ntools:\n---\n");
        Assert.Equal("", Text(fm, "tools"));
    }

    // ────────────────────────── 不支持的语法照实记下 ──────────────────────────

    [Fact]
    public void Nesting_deeper_than_one_level_is_skipped()
    {
        var fm = Parse("---\nname: x\na:\n  b:\n    c: 1\n---\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.False(fm.Data.ContainsKey("a.b.c"));
        Assert.Contains("nesting", Skipped(fm));
    }

    [Fact]
    public void Indented_line_without_a_parent_key_is_skipped()
    {
        var fm = Parse("---\nname: x\n  stray: 1\n---\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.False(fm.Data.ContainsKey("name.stray"));
        Assert.Contains("parent key", Skipped(fm));
    }

    [Fact]
    public void Anchors_and_aliases_are_skipped()
    {
        var fm = Parse("---\na: &anchor v\nb: *anchor\n---\n");
        Assert.Equal(2, fm.Skipped.Count(s => s.Contains("anchor", StringComparison.Ordinal)));
        Assert.False(fm.Data.ContainsKey("a"));
    }

    [Fact]
    public void Invalid_key_names_are_skipped()
    {
        var fm = Parse("---\n9bad: v\nname: x\n---\n");
        Assert.False(fm.Data.ContainsKey("9bad"));
        Assert.Equal("x", Text(fm, "name"));
        Assert.Contains("key name", Skipped(fm));
    }

    [Fact]
    public void Lines_that_are_not_key_value_shaped_are_skipped()
    {
        var fm = Parse("---\n随便一行字\nname: x\n---\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Contains("key: value", Skipped(fm));
    }

    // ────────────────────────── 没有前置块 ──────────────────────────

    [Fact]
    public void When_the_first_line_is_not_dashes_the_whole_source_is_the_body()
    {
        const string src = "# 标题\n内容";
        var fm = Parse(src);
        Assert.Equal(src, fm.Body);
        Assert.Empty(fm.Data);
    }

    [Fact]
    public void Empty_string_does_not_blow_up()
    {
        Assert.Equal("", Parse("").Body);
    }

    [Fact]
    public void Invariant_missing_closing_marker_treats_the_whole_thing_as_body_instead_of_swallowing_the_file()
    {
        // ★ 吞掉正文的话,Skill 会变成「有 frontmatter 但没内容」—— 加载器判定它有效,模型收到一份空指令
        const string src = "---\nname: x\n还有很多正文";
        var fm = Parse(src);
        Assert.Equal(src, fm.Body);
        Assert.False(fm.Data.ContainsKey("name"));
        Assert.Contains("closing marker", Skipped(fm));
    }

    [Fact]
    public void Invariant_no_input_ever_throws()
    {
        string[] nasty =
        [
            "---",
            "---\n",
            "---\n---",
            "---\n:\n---\n",
            "---\n: v\n---\n",
            "---\na:\n- \n---\n",
            "---\n[\n---\n",
            new string('-', 10_000),
            "---\n" + string.Concat(Enumerable.Repeat("a: b\n", 5000)) + "---\n",
        ];
        foreach (var s in nasty)
        {
            var ex = Record.Exception(() => Parse(s));
            Assert.True(ex is null, $"threw on {s[..Math.Min(20, s.Length)]}: {ex}");
        }
    }

    // ────────────────────────── BOM 与 CRLF ──────────────────────────

    [Fact]
    public void Bom_does_not_affect_first_line_detection()
    {
        var fm = Parse("\uFEFF---\nname: x\n---\nbody");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Equal("body", fm.Body);
    }

    [Fact]
    public void Crlf_files_parse_normally_and_no_carriage_return_is_left_in_values()
    {
        var fm = Parse("---\r\nname: x\r\ndescription: y\r\n---\r\nbody\r\n");
        Assert.Equal("x", Text(fm, "name"));
        Assert.Equal("y", Text(fm, "description"));
        Assert.DoesNotContain('\r', fm.Body);
    }

    // ────────────────────────── 防线 ──────────────────────────

    // ★ 原型污染:一份从 git 装来的 SKILL.md 里写一行 `__proto__: polluted` 是零成本的。
    // C# 没有原型,谈不上污染;但这三个键「被拒绝并记入 skipped」是 2.x 的对等规则,所以断言保留。

    [Fact]
    public void Invariant_proto_key_does_not_enter_data_and_is_recorded_in_skipped()
    {
        var fm = Parse("---\n__proto__: polluted\nname: x\n---\n");
        Assert.False(fm.Data.ContainsKey("__proto__"));
        Assert.DoesNotContain(fm.Data.Values, v => v.Text == "polluted");
        // 正常的键不受影响
        Assert.Equal("x", Text(fm, "name"));
        Assert.Contains("security", Skipped(fm));
    }

    [Fact]
    public void Invariant_constructor_and_prototype_keys_are_rejected_the_same_way()
    {
        var fm = Parse("---\nconstructor: x\nprototype: y\n---\n");
        Assert.False(fm.Data.ContainsKey("constructor"));
        Assert.False(fm.Data.ContainsKey("prototype"));
        Assert.Equal(2, fm.Skipped.Count);
    }

    [Fact]
    public void Invariant_copying_data_into_an_ordinary_dictionary_carries_no_forbidden_key()
    {
        var fm = Parse("---\n__proto__: polluted\n---\n");
        var copy = new Dictionary<string, FrontmatterValue>(fm.Data, StringComparer.Ordinal);
        foreach (var (k, v) in fm.Data)
        {
            copy[k] = v;
        }

        Assert.Empty(copy);
    }

    [Fact]
    public void Data_is_read_only_and_ordinal_keyed_as_a_second_safety_net()
    {
        // 2.x 这条是「data 本身没有原型」;C# 里对应的双保险是:产出的 map 不可写、键按序数比较。
        var fm = Parse("---\nname: x\n---\n");
        Assert.True(fm.Data is IDictionary<string, FrontmatterValue> { IsReadOnly: true });
        Assert.True(fm.Data.ContainsKey("name"));
        Assert.False(fm.Data.ContainsKey("NAME"));
    }

    [Fact]
    public void Invariant_front_matter_over_the_block_limit_parses_only_a_prefix_and_the_body_position_is_still_right()
    {
        var filler = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"k{i}: {new string('v', 40)}"));
        var fm = Parse($"---\nname: first\n{filler}\nlast: 尾巴\n---\n真正的正文");
        Assert.Equal("first", Text(fm, "name"));
        Assert.False(fm.Data.ContainsKey("last"));
        Assert.Contains("KB", Skipped(fm));
        // ★ 正文没有被前置块吃掉
        Assert.Equal("真正的正文", fm.Body);
    }

    [Fact]
    public void Invariant_key_count_is_capped()
    {
        var many = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"k{i}: v"));
        var fm = Parse($"---\n{many}\n---\n");
        Assert.True(fm.Data.Count <= FrontmatterLimits.KeysMax);
        Assert.Contains("key count", Skipped(fm));
    }

    [Fact]
    public void Invariant_single_value_is_capped()
    {
        // 5000 字符:超过值上限(4KB),但整块仍在块上限(8KB)以内 —— 这样触发的是值那道闸
        var fm = Parse("---\ndescription: " + new string('x', 5000) + "\n---\n");
        Assert.Equal(FrontmatterLimits.ValueMax, Text(fm, "description").Length);
        Assert.Contains("truncated", Skipped(fm));
    }

    [Fact]
    public void Invariant_a_single_line_that_blows_the_block_budget_is_not_parsed_at_all_block_gate_comes_before_value_gate()
    {
        // ★ 两道闸是有先后的,这一条把先后钉住。一个 20KB 的单行值先撞上块上限(8KB),于是那一行整个不解析 —— 而不是被截成 4KB 存进去。
        var fm = Parse("---\ndescription: " + new string('x', 20_000) + "\nname: x\n---\n正文");
        Assert.False(fm.Data.ContainsKey("description"));
        Assert.Contains("KB", Skipped(fm));
        Assert.Equal("正文", fm.Body);
    }

    [Fact]
    public void Invariant_list_is_capped()
    {
        var items = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"- t{i}"));
        var fm = Parse($"---\ntools:\n{items}\n---\n");
        Assert.Equal(FrontmatterLimits.ListMax, Items(fm, "tools").Length);
    }

    [Fact]
    public void A_huge_file_without_a_closing_marker_also_returns_quickly()
    {
        var sw = Stopwatch.StartNew();
        Parse("---\n" + string.Concat(Enumerable.Repeat("a: b\n", 200_000)));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds}ms");
    }

    // ★ 控制字符:description 直接进系统提示词。一个 ANSI 转义或 NUL 落在里面之后,谁也说不清下游(日志、终端、转录渲染)会拿它做什么。
    // 用 (char)n 造字符,不在源码里写字面控制字符 —— 后者在编辑器和 diff 里都是隐形的。

    [Fact]
    public void Invariant_control_characters_in_values_are_stripped()
    {
        var esc = ((char)27).ToString();
        var nul = ((char)0).ToString();
        var fm = Parse("---\ndescription: 前" + esc + "[31m红" + nul + "后\n---\n");
        var d = Text(fm, "description");
        Assert.DoesNotContain((char)27, d);
        Assert.DoesNotContain((char)0, d);
        Assert.Contains("红", d);
    }

    [Fact]
    public void Control_characters_in_list_items_are_stripped_too()
    {
        var nul = ((char)0).ToString();
        var fm = Parse("---\ntools: [Re" + nul + "ad]\n---\n");
        Assert.Equal("Read", Items(fm, "tools")[0]);
    }

    // ────────────────────────── 取值助手 ──────────────────────────

    private static readonly Frontmatter HelperFm = Parse("---\na: hello\nb: [x]\nc: \"   \"\n---\n");

    [Fact]
    public void String_gets_a_string_value()
    {
        Assert.Equal("hello", FrontmatterParser.String(HelperFm, "a"));
    }

    [Fact]
    public void Invariant_String_returns_null_for_a_list_because_joining_is_guessing()
    {
        // ★ 拼起来是猜,猜错比缺失更难查
        Assert.Null(FrontmatterParser.String(HelperFm, "b"));
    }

    [Fact]
    public void String_of_only_whitespace_is_the_same_as_missing()
    {
        Assert.Null(FrontmatterParser.String(HelperFm, "c"));
    }

    [Fact]
    public void String_of_a_missing_key_is_null()
    {
        Assert.Null(FrontmatterParser.String(HelperFm, "nope"));
    }

    // ★ 这一组钉的是「两种写法必须合流」。用户会把 Claude Code 的 agent 文件原样粘过来,里面写的是 `tools: Read, Grep, Glob`(一个裸标量);
    // 而 `allowed-tools` 更常写成 `[Read, Grep]`。两种都得认成同一个东西 —— 不然子代理会带着一个叫 "Read, Grep, Glob" 的工具名去查表。

    [Fact]
    public void Invariant_List_gives_the_same_answer_for_yaml_array_and_comma_separated_string()
    {
        var a = Parse("---\ntools: [Read, Grep, Glob]\n---\n");
        var b = Parse("---\ntools: Read, Grep, Glob\n---\n");
        var c = Parse("---\ntools:\n  - Read\n  - Grep\n  - Glob\n---\n");
        Assert.Equal(new[] { "Read", "Grep", "Glob" }, FrontmatterParser.List(a, "tools"));
        Assert.Equal(FrontmatterParser.List(a, "tools"), FrontmatterParser.List(b, "tools"));
        Assert.Equal(FrontmatterParser.List(a, "tools"), FrontmatterParser.List(c, "tools"));
    }

    [Fact]
    public void A_single_item_is_also_a_list()
    {
        Assert.Equal(new[] { "Read" }, FrontmatterParser.List(Parse("---\ntools: Read\n---\n"), "tools"));
    }

    [Fact]
    public void List_of_empty_value_and_of_missing_key_are_both_null_because_telling_them_apart_is_not_worth_it()
    {
        Assert.Null(FrontmatterParser.List(Parse("---\ntools:\n---\n"), "tools"));
        Assert.Null(FrontmatterParser.List(Parse("---\na: b\n---\n"), "tools"));
    }

    [Fact]
    public void List_drops_extra_commas_and_whitespace()
    {
        Assert.Equal(
            new[] { "Read", "Grep" },
            FrontmatterParser.List(Parse("---\ntools: Read, , Grep,\n---\n"), "tools"));
    }

    [Fact]
    public void Bool_recognizes_the_common_spellings()
    {
        var fm = Parse("---\na: true\nb: false\nc: yes\nd: no\ne: TRUE\nf: on\ng: off\nh: 1\ni: 0\n---\n");
        Assert.True(FrontmatterParser.Bool(fm, "a"));
        Assert.False(FrontmatterParser.Bool(fm, "b"));
        Assert.True(FrontmatterParser.Bool(fm, "c"));
        Assert.False(FrontmatterParser.Bool(fm, "d"));
        Assert.True(FrontmatterParser.Bool(fm, "e"));
        Assert.True(FrontmatterParser.Bool(fm, "f"));
        Assert.False(FrontmatterParser.Bool(fm, "g"));
        Assert.True(FrontmatterParser.Bool(fm, "h"));
        Assert.False(FrontmatterParser.Bool(fm, "i"));
    }

    [Fact]
    public void Invariant_Bool_returns_null_for_unrecognized_values_instead_of_false()
    {
        // 当成 false 的话,`globalEnabled: maybe` 会静默变成「关闭」,而用户看到的是「我明明写了」。null 让调用方能选自己的默认值。
        Assert.Null(FrontmatterParser.Bool(Parse("---\na: 随便\n---\n"), "a"));
        Assert.Null(FrontmatterParser.Bool(Parse("---\na: b\n---\n"), "nope"));
    }

    [Fact]
    public void True_stays_a_string_when_parsed_and_coercion_only_happens_in_Bool()
    {
        Assert.Equal("true", Text(Parse("---\na: true\n---\n"), "a"));
    }

    // ────────────────────────── 序列化 ──────────────────────────
    // 序列化存在的全部意义是「写回去还能原样读出来」,所以这一节几乎每条都是往返断言,而不是在比对生成的文本长什么样。
    // 断言生成文本的写法是脆的:它把缩进、引号策略、键序这些实现细节全锁死了,而真正要保的东西 —— 值不丢、不变形 —— 反而没被直接断言。

    [Fact]
    public void Invariant_serialize_one_level_nesting_round_trips_so_saving_in_the_editor_does_not_eat_the_metadata_block()
    {
        var data = new Dictionary<string, FrontmatterValue?>
        {
            ["name"] = "programming-tutor",
            ["metadata.author"] = "Samuel Kahessay",
            ["metadata.version"] = "1.0.1",
            ["metadata.tags"] = new[] { "python", "javascript" },
        };
        AssertRoundTrips(data);
        // 子键必须连成一片:写成两个 `metadata:` 块的话,读回来只剩后一个
        var text = FrontmatterParser.Serialize(data, "");
        Assert.Single(text.Split('\n'), l => l == "metadata:");
    }

    [Fact]
    public void When_a_scalar_and_a_map_share_a_name_only_the_map_half_is_written()
    {
        // 两个都写出去的话读回来只会剩一个,而标量那半本来就是解析时的占位
        var round = Round(new Dictionary<string, FrontmatterValue?> { ["m"] = "", ["m.a"] = "x" });
        Assert.Equal(new[] { "m.a=T<x>" }, Dump(round));
    }

    [Fact]
    public void Three_level_keys_are_not_written_because_they_could_not_be_read_back()
    {
        var round = Round(new Dictionary<string, FrontmatterValue?> { ["name"] = "x", ["a.b.c"] = "v" });
        Assert.Equal(new[] { "name=T<x>" }, Dump(round));
    }

    [Fact]
    public void Plain_scalars_and_lists_come_back_unchanged()
    {
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?>
        {
            ["name"] = "commit",
            ["description"] = "写提交信息",
            ["tools"] = new[] { "Read", "Grep" },
        });
    }

    [Fact]
    public void Invariant_values_with_literal_backslashes_round_trip()
    {
        // 这条是 `Unquote` 那个链式 replace 顺序 bug 的回归保护:曾经 `a\nb`(反斜杠 + 字母 n)写出去再读回来会变成 `a` + 反斜杠 + 真换行。
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?>
        {
            ["re"] = "a\\nb",
            ["win"] = "C:\\Users\\me",
            ["tail"] = "结尾一个反斜杠\\",
        });
    }

    [Fact]
    public void Newlines_tabs_and_quotes_round_trip()
    {
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?>
        {
            ["multi"] = "第一行\n第二行",
            ["tabbed"] = "a\tb",
            ["quoted"] = "他说\"好\"",
            ["both"] = "x\n\"y\"\\z",
        });
    }

    [Fact]
    public void Bare_values_the_parser_would_read_as_something_else_all_round_trip()
    {
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?>
        {
            ["empty"] = "",
            ["spaced"] = "  前后有空格  ",
            ["looksList"] = "[a, b]",
            ["anchor"] = "&ref",
            ["alias"] = "*ref",
            ["block"] = "|",
            ["block2"] = ">-2",
            // ★ 缩进指示在前的写法也是块标量头,NeedsQuote 漏了它就会在读回来时吃掉后面几行
            ["block3"] = "|2-",
            ["block4"] = ">2",
            ["dash"] = "- 看着像列表项",
            ["hash"] = "#不是注释",
            ["colon"] = "foo: bar",
            ["truthy"] = "true",
            ["numeric"] = "123",
        });
    }

    [Fact]
    public void Invariant_list_items_containing_commas_fall_back_to_block_style_because_flow_style_splits_on_commas()
    {
        var data = new Dictionary<string, FrontmatterValue?> { ["tools"] = new[] { "a, b", "c" } };
        AssertRoundTrips(data);
        Assert.Contains("  - ", FrontmatterParser.Serialize(data, ""));
    }

    [Fact]
    public void List_items_with_brackets_or_surrounding_whitespace_also_fall_back_to_block_style()
    {
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?> { ["k"] = new[] { "[x]", "y" } });
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?> { ["k"] = new[] { " 前后空格 ", "y" } });
    }

    [Fact]
    public void An_empty_list_round_trips_to_an_empty_list()
    {
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?> { ["k"] = Array.Empty<string>() });
    }

    [Fact]
    public void Invariant_body_does_not_accumulate_blank_lines_so_repeated_round_trips_converge()
    {
        // 序列化时若在 `---` 后补一个空行,而 body 又来自上一次 parse,每存一次就会多一个前导换行,一直涨。
        var data = new Dictionary<string, FrontmatterValue?> { ["a"] = "x" };
        var body = "\n正文\n";
        for (var i = 0; i < 5; i++)
        {
            body = Parse(FrontmatterParser.Serialize(data, body)).Body;
        }

        Assert.Equal("\n正文\n", body);
    }

    [Fact]
    public void Crlf_body_is_normalized_to_lf()
    {
        var data = new Dictionary<string, FrontmatterValue?> { ["a"] = "x" };
        Assert.Equal("一\n二", Parse(FrontmatterParser.Serialize(data, "一\r\n二")).Body);
    }

    [Fact]
    public void Unrecognized_keys_come_back_unchanged_because_the_form_only_covers_the_ones_it_knows()
    {
        // 从 Claude Code 粘过来的 agent 文件里常有 `color: blue` 这类本应用不认识的键。
        AssertRoundTrips(new Dictionary<string, FrontmatterValue?>
        {
            ["description"] = "必填",
            ["color"] = "blue",
            ["argument-hint"] = "<path>",
        });
    }

    [Fact]
    public void Order_decides_key_order_and_the_rest_are_alphabetical_because_reshuffling_on_every_save_turns_diffs_into_noise()
    {
        var out_ = FrontmatterParser.Serialize(
            new Dictionary<string, FrontmatterValue?> { ["zeta"] = "1", ["description"] = "2", ["alpha"] = "3", ["name"] = "4" },
            "",
            new FrontmatterSerializeOptions { Order = ["name", "description"] });
        var keys = out_.Split('\n')
            .Where(l => l.Length > 0 && l[0] is >= 'a' and <= 'z')
            .Select(l => l[..l.IndexOf(':')])
            .ToArray();
        Assert.Equal(new[] { "name", "description", "alpha", "zeta" }, keys);
    }

    [Fact]
    public void Forbidden_keys_like_proto_are_not_written()
    {
        var text = FrontmatterParser.Serialize(
            new Dictionary<string, FrontmatterValue?> { ["__proto__"] = "polluted", ["ok"] = "x" },
            "");
        Assert.DoesNotContain("polluted", text);
        Assert.Contains("ok: x", text);
    }

    [Fact]
    public void Null_values_are_skipped_not_written_as_empty()
    {
        var text = FrontmatterParser.Serialize(
            new Dictionary<string, FrontmatterValue?> { ["a"] = "x", ["b"] = null },
            "");
        Assert.Contains("a: x", text);
        Assert.DoesNotContain("b:", text);
    }

    [Fact]
    public void No_keys_at_all_means_no_front_matter_block_because_command_files_may_have_none()
    {
        Assert.Equal("只有正文", FrontmatterParser.Serialize(new Dictionary<string, FrontmatterValue?>(), "只有正文"));
    }

    [Fact]
    public void Invalid_key_names_are_skipped_outright()
    {
        var text = FrontmatterParser.Serialize(
            new Dictionary<string, FrontmatterValue?> { ["bad key"] = "x", ["good"] = "y" },
            "");
        Assert.DoesNotContain("bad key", text);
        Assert.Contains("good: y", text);
    }
}
