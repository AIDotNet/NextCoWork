using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools.Tests;

/// <summary>纯函数测试(零 IO,所以可以把边界情况铺满)。glob / 预筛 / ReDoS 移植自 2.x 的 <c>glob-match / grep-prefilter / grep-*</c> 测试。</summary>
public sealed class GlobMatcherTests
{
    private static bool M(string pattern, string path) => GlobMatcher.Compile(pattern, caseSensitive: true).IsMatch(GlobMatcher.Normalize(path));

    [Fact]
    public void Star_matches_within_a_segment_but_never_across_a_slash()
    {
        Assert.True(M("*.ts", "a.ts"));
        Assert.True(M("*.ts", ".ts"));
        Assert.False(M("*.ts", "src/a.ts"));
        Assert.True(M("src/*.ts", "src/a.ts"));
        Assert.False(M("src/*.ts", "src/main/a.ts"));
        Assert.False(M("*.ts", "a.tsx"));
        Assert.False(M("*.ts", "a.ts.bak"));
    }

    /// <summary>核心断言:双星后面跟斜杠时必须允许零层,否则根目录下的文件全被静默漏掉。</summary>
    [Fact]
    public void Double_star_slash_matches_zero_levels_so_root_files_are_found()
    {
        Assert.True(M("**/*.ts", "a.ts"));
        Assert.True(M("**/*.ts", "src/a.ts"));
        Assert.True(M("**/*.ts", "src/main/kernel/a.ts"));
        Assert.True(M("src/**/*.ts", "src/a.ts"));
        Assert.True(M("src/**/*.ts", "src/main/a.ts"));
        Assert.False(M("src/**/*.ts", "lib/a.ts"));
        Assert.True(M("src/**", "src/main/deep/a.ts"));
        Assert.True(M("**", "anything/at/all.txt"));
        Assert.True(M("**/test/**", "src/test/a.ts"));
        Assert.False(M("**/test/**", "src/spec/a.ts"));
    }

    [Fact]
    public void Question_mark_and_braces()
    {
        Assert.True(M("a?.ts", "ab.ts"));
        Assert.False(M("a?.ts", "a.ts"));
        Assert.False(M("a?b", "a/b"));
        Assert.True(M("*.{ts,tsx}", "a.tsx"));
        Assert.False(M("*.{ts,tsx}", "a.js"));
        Assert.True(M("**/*.{js,jsx,mjs,cjs}", "a.cjs"));
    }

    [Fact]
    public void Malformed_patterns_from_the_model_never_throw()
    {
        _ = GlobMatcher.Compile("*.{ts,tsx", true);
        _ = GlobMatcher.Compile("}}}", true);
        _ = GlobMatcher.Compile("{", true);
    }

    [Fact]
    public void Regex_metacharacters_and_unsupported_syntax_are_literals()
    {
        Assert.True(M("a.ts", "a.ts"));
        Assert.False(M("a.ts", "axts"));
        Assert.True(M("a+b.txt", "a+b.txt"));
        Assert.True(M("(x).txt", "(x).txt"));
        Assert.True(M("[abc].ts", "[abc].ts"));
        Assert.False(M("[abc].ts", "a.ts"));
        Assert.False(M("*.ts", "a.ts.map"));
        Assert.False(M("src/a.ts", "x/src/a.ts"));
    }

    [Fact]
    public void Case_sensitivity_follows_the_flag()
    {
        Assert.False(GlobMatcher.IsMatch("*.TS", "a.ts", caseSensitive: true));
        Assert.True(GlobMatcher.IsMatch("*.TS", "a.ts", caseSensitive: false));
    }

    [Theory]
    [InlineData("**/*.test.ts", "src/main/kernel/__tests__/a.test.ts", true)]
    [InlineData("**/*.test.ts", "src/main/kernel/a.ts", false)]
    [InlineData("*.json", "src/package.json", false)]
    [InlineData("**/node_modules/**", "a/node_modules/b/c.js", true)]
    [InlineData(".nextcowork/skills/*/SKILL.md", "a/b/SKILL.md", false)]
    public void Real_world_patterns(string pattern, string path, bool expected) => Assert.Equal(expected, M(pattern, path));

    [Fact]
    public void Normalize_strips_leading_dot_slash_and_converts_backslashes()
    {
        Assert.Equal("a.ts", GlobMatcher.Normalize("././a.ts"));
        Assert.Equal("a/./b", GlobMatcher.Normalize("a/./b"));
        Assert.Equal("src/a.ts", GlobMatcher.Normalize("src\\a.ts"));
    }
}

public sealed class GrepPrefilterTests
{
    [Fact]
    public void Extracts_the_required_literal_conservatively()
    {
        Assert.Equal("GiftBalance", GrepPrefilter.RequiredLiteral("GiftBalance"));
        Assert.Equal("foo.bar(", GrepPrefilter.RequiredLiteral("foo\\.bar\\("));
        Assert.Equal("ErrorHandler", GrepPrefilter.RequiredLiteral("log.*ErrorHandler"));
        Assert.Equal("function", GrepPrefilter.RequiredLiteral("function\\s+\\w+"));
        Assert.Equal("colo", GrepPrefilter.RequiredLiteral("colou?r"));
        Assert.Equal("abc", GrepPrefilter.RequiredLiteral("abcx*yz"));
        Assert.Equal("abc", GrepPrefilter.RequiredLiteral("abcd{0,2}"));
        Assert.Equal("abcd", GrepPrefilter.RequiredLiteral("abcd+efg"));
        Assert.Null(GrepPrefilter.RequiredLiteral("fooBar|bazQux"));
        Assert.Equal("rest", GrepPrefilter.RequiredLiteral("(?:optionalThing)?rest"));
        Assert.Null(GrepPrefilter.RequiredLiteral("[abc]+xy"));
        Assert.Equal("{foo}", GrepPrefilter.RequiredLiteral("{foo}"));
        Assert.Null(GrepPrefilter.RequiredLiteral("\\w+\\s*="));
        Assert.Null(GrepPrefilter.RequiredLiteral("a.b"));
    }

    [Fact]
    public void Case_handling_follows_the_ignore_case_flag()
    {
        Assert.False(GrepPrefilter.Create("GiftBalance", false)!("giftbalance"));
        Assert.True(GrepPrefilter.Create("GiftBalance", true)!("x giftbalance y"));
        Assert.True(GrepPrefilter.Create("a\\.b\\(c", true)!("A.B(C"));
        Assert.False(GrepPrefilter.Create("a\\.b\\(c", true)!("AxB(C"));
    }

    /// <summary>守的是「只许误报,不许漏报」:预筛说「不可能命中」的文件会被整个跳过,漏报一次就是一句看起来很可信的「仓库里没有」。</summary>
    [Fact]
    public void Never_rejects_a_text_the_original_regex_matches()
    {
        string[] patterns =
        [
            "GiftBalance", "colou?r", "log.*Error", "function\\s+\\w+", "abcd+efg", "a\\.b\\(c",
            "(?:pre)?fix_value", "x{2}yz", "\\bimport\\b", "end$", "^start", "lazy+?tail", "{foo}", "k{0}abc",
        ];
        string[] texts =
        [
            "const GiftBalance = 1", "color colour", "log: Error here", "function  foo()", "abcddddefg",
            "a.b(c", "fix_value prefix_value", "xxyz", "import x", "the end", "start here", "lazyyytail",
            "{foo}", "abc", "KABC", "COLOR", "",
        ];
        foreach (var p in patterns)
        {
            foreach (var ignoreCase in new[] { false, true })
            {
                var pre = GrepPrefilter.Create(p, ignoreCase);
                if (pre is null)
                {
                    continue;
                }

                var re = new Regex(p, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                foreach (var t in texts.Where(t => re.IsMatch(t)))
                {
                    Assert.True(pre(t), $"{p} (ignoreCase={ignoreCase}) on \"{t}\"");
                }
            }
        }
    }
}

public sealed class RedosTests
{
    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("^(a+)+$")]
    [InlineData("(\\s*\\w+)*$")]
    [InlineData("(a{2,})+$")]
    [InlineData("((a+)b)+$")]
    public void Nested_unbounded_quantifiers_are_flagged_with_a_fix(string pattern)
    {
        var risk = Redos.Risk(pattern);
        Assert.NotNull(risk);
        Assert.Contains("Fix it by", risk);
        Assert.Contains("backtracking", risk);
    }

    [Theory]
    [InlineData("log.*Error")]
    [InlineData("function\\s+\\w+")]
    [InlineData("interface\\s+X[\\s\\S]*?field")]
    [InlineData("(?:foo|bar)+")]
    [InlineData("(TODO)+")]
    [InlineData("a{1,20}b")]
    [InlineData("\\[[^\\]]*\\]")]
    [InlineData("const \\w+ = ")]
    public void Ordinary_patterns_are_not_flagged(string pattern) => Assert.Null(Redos.Risk(pattern));

    [Fact]
    public void Linear_time_engine_makes_the_classic_bomb_harmless()
    {
        var re = Redos.Compile("(a+)+$", RegexOptions.None, TimeSpan.FromSeconds(5));
        var bomb = new string('a', 50_000) + "b";
        var started = DateTime.UtcNow;
        Assert.DoesNotMatch(re, bomb);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_backtracking_only_pattern_with_a_dangerous_shape_is_refused()
    {
        // 前瞻让它只能用回溯引擎;嵌套无界量词再被静态筛查拦下
        var e = Assert.Throws<ToolFailureException>(() => Redos.Compile("(?=x)(a+)+$", RegexOptions.None, TimeSpan.FromSeconds(1)));
        Assert.Contains("backtracking", e.Message);
    }

    [Fact]
    public void A_backtracking_only_pattern_gets_a_match_timeout()
    {
        var re = Redos.Compile("(?=a)(a|aa)+$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        Assert.Throws<RegexMatchTimeoutException>(() => re.IsMatch(new string('a', 5000) + "b"));
    }

    [Fact]
    public void A_syntax_error_surfaces_as_an_ArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => Redos.Compile("([unclosed", RegexOptions.None, TimeSpan.FromSeconds(1)));
    }
}

public sealed class SchemaValidatorTests
{
    private static readonly JsonElement Schema = S.Build(S.Object(
        ["name"],
        ("name", S.String("n", minLength: 1)),
        ("count", S.Integer("c", minimum: 1, maximum: 10)),
        ("ratio", new System.Text.Json.Nodes.JsonObject { ["type"] = "number" }),
        ("flag", S.Boolean("f")),
        ("mode", S.Enum("m", "a", "b")),
        ("note", S.String("d", @default: "")),
        ("items", S.Array("i", S.Object(["x"], ("x", S.Integer("x"))), maxItems: 2))));

    private static SchemaResult V(string json) => JsonSchemaValidator.Validate(Schema, Jsn.Parse(json));

    [Fact]
    public void Accepts_valid_input_and_fills_defaults_and_keeps_unknown_properties()
    {
        var r = V("""{"name":"x","extra":{"k":1}}""");
        Assert.True(r.IsValid, r.Error);
        Assert.Equal("", r.Value.GetProperty("note").GetString());
        Assert.Equal(1, r.Value.GetProperty("extra").GetProperty("k").GetInt32());
    }

    [Fact]
    public void Coerces_loosely_typed_values()
    {
        var r = V("""{"name":5,"count":"7","ratio":"0.5","flag":"true","items":[{"x":"3"}]}""");
        Assert.True(r.IsValid, r.Error);
        Assert.Equal("5", r.Value.GetProperty("name").GetString());
        Assert.Equal(7, r.Value.GetProperty("count").GetInt32());
        Assert.Equal(0.5, r.Value.GetProperty("ratio").GetDouble());
        Assert.True(r.Value.GetProperty("flag").GetBoolean());
        Assert.Equal(3, r.Value.GetProperty("items")[0].GetProperty("x").GetInt32());
    }

    [Fact]
    public void Integral_floats_are_integers_but_fractions_are_not()
    {
        Assert.True(V("""{"name":"x","count":3.0}""").IsValid);
        var bad = V("""{"name":"x","count":3.5}""");
        Assert.False(bad.IsValid);
        Assert.Contains("count: expected an integer", bad.Error);
    }

    [Fact]
    public void Null_for_an_optional_property_means_absent_but_null_for_a_required_one_is_an_error()
    {
        Assert.True(V("""{"name":"x","count":null}""").IsValid);
        Assert.False(V("""{"name":null}""").IsValid);
    }

    [Fact]
    public void Reports_every_problem_with_its_path()
    {
        var r = V("""{"count":11,"mode":"z","items":[{"y":1},{"x":1},{"x":2}]}""");
        Assert.False(r.IsValid);
        Assert.Contains("name: required property is missing", r.Error);
        Assert.Contains("count: must be <= 10", r.Error);
        Assert.Contains("mode: must be one of a, b", r.Error);
        Assert.Contains("items: expected at most 2 item(s), got 3", r.Error);
        Assert.Contains("items.0.x: required property is missing", r.Error);
    }

    [Fact]
    public void Rejects_wrong_container_types_and_empty_strings()
    {
        Assert.Contains("expected an object", JsonSchemaValidator.Validate(Schema, Jsn.Parse("[1]")).Error);
        Assert.Contains("name: must not be empty", V("""{"name":""}""").Error);
        Assert.Contains("items: expected an array", V("""{"name":"x","items":"no"}""").Error);
        Assert.Contains("flag: expected a boolean", V("""{"name":"x","flag":"maybe"}""").Error);
    }

    [Fact]
    public void Every_builtin_tool_has_a_valid_object_schema_and_description()
    {
        foreach (var tool in BuiltinTools.Create())
        {
            Assert.Equal("object", tool.Info.InputSchema.GetProperty("type").GetString());
            Assert.True(tool.Info.Description.Length > 20, tool.Info.InternalId);
            Assert.False(string.IsNullOrWhiteSpace(tool.Info.PromptSnippet), tool.Info.InternalId);
            Assert.True(Ncw.Agent.Llm.ToolInfo.IsValidName(tool.Info.InternalId), tool.Info.InternalId);
        }
    }
}

public sealed class BuiltinToolsTests
{
    [Fact]
    public void The_Claude_Code_names_are_all_present_unique_and_in_a_fixed_order()
    {
        var ids = BuiltinTools.Create().Select(t => t.Info.InternalId).ToList();
        Assert.Equal(["Read", "Write", "Edit", "LS", "Glob", "Grep", "Bash", "BashOutput", "KillShell"], ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>计划模式靠 ReadOnly 摘掉写工具。标错的代价是计划模式下真的写了盘。</summary>
    [Fact]
    public void Traits_match_what_the_tools_actually_do()
    {
        var byId = BuiltinTools.Create().ToDictionary(t => t.Info.InternalId, t => t.Info.Traits);
        foreach (var id in new[] { "Read", "LS", "Glob", "Grep", "BashOutput" })
        {
            Assert.True(byId[id].ReadOnly, id);
            Assert.False(byId[id].Destructive, id);
        }

        foreach (var id in new[] { "Write", "Edit", "Bash" })
        {
            Assert.False(byId[id].ReadOnly, id);
            Assert.True(byId[id].Destructive, id);
        }

        // KillShell:不是 destructive(每次收工都弹审批的话,模型会学会不收工),也不是只读
        Assert.False(byId["KillShell"].ReadOnly);
        Assert.False(byId["KillShell"].Destructive);
        Assert.All(byId.Values, t => Assert.False(t.NeedsNetwork));
    }

    [Fact]
    public void Each_call_returns_fresh_instances()
    {
        Assert.NotSame(BuiltinTools.Create()[0], BuiltinTools.Create()[0]);
    }
}

public sealed class TrackerTests
{
    [Fact]
    public void ReadTracker_buckets_by_run_and_caps_the_number_of_runs()
    {
        var tracker = new ReadTracker();
        var path = Ncw.Agent.Environments.EnvPath.Posix("/w/a.ts");
        tracker.MarkRead(new Ncw.Agent.Kernel.RunId("r1"), path);
        Assert.True(tracker.WasRead(new Ncw.Agent.Kernel.RunId("r1"), path));
        Assert.False(tracker.WasRead(new Ncw.Agent.Kernel.RunId("r2"), path));
        for (var i = 0; i < ReadTracker.MaxRuns; i++)
        {
            tracker.MarkRead(new Ncw.Agent.Kernel.RunId($"x{i}"), path);
        }

        Assert.False(tracker.WasRead(new Ncw.Agent.Kernel.RunId("r1"), path)); // 最旧的被挤掉
    }

    [Fact]
    public void ReadTracker_folds_case_for_windows_paths_only()
    {
        var tracker = new ReadTracker();
        var run = new Ncw.Agent.Kernel.RunId("r");
        tracker.MarkRead(run, Ncw.Agent.Environments.EnvPath.Windows(@"C:\Work\A.ts"));
        Assert.True(tracker.WasRead(run, Ncw.Agent.Environments.EnvPath.Windows(@"c:\work\a.ts")));
        tracker.MarkRead(run, Ncw.Agent.Environments.EnvPath.Posix("/w/A.ts"));
        Assert.False(tracker.WasRead(run, Ncw.Agent.Environments.EnvPath.Posix("/w/a.ts")));
    }

    private static Ncw.Agent.Environments.EnvPath P(string v) => Ncw.Agent.Environments.EnvPath.Posix(v);

    [Fact]
    public void ChangeRecorder_keeps_the_first_before_and_advances_to_the_last_after()
    {
        var rec = new ChangeRecorder();
        var run = new Ncw.Agent.Kernel.RunId("run");
        rec.Record(run, P("/w/a.ts"), "a.ts", "v0", "v1", true);
        rec.Record(run, P("/w/a.ts"), "a.ts", "v1", "v2", true);
        var change = Assert.Single(rec.Take(run));
        Assert.Equal("v0", change.Before);
        Assert.Equal("v2", change.After);
        Assert.Equal(ChangeKind.Modified, change.Kind);
    }

    [Fact]
    public void ChangeRecorder_created_is_not_flipped_by_later_modifications()
    {
        var rec = new ChangeRecorder();
        var run = new Ncw.Agent.Kernel.RunId("run");
        rec.Record(run, P("/w/n.ts"), "n.ts", null, "a", true);
        rec.Record(run, P("/w/n.ts"), "n.ts", "a", "b", true);
        Assert.Equal((null, "b", ChangeKind.Created), rec.Take(run).Select(c => (c.Before, c.After, c.Kind)).Single());
    }

    [Fact]
    public void ChangeRecorder_take_clears_and_buckets_by_run()
    {
        var rec = new ChangeRecorder();
        rec.Record(new Ncw.Agent.Kernel.RunId("r1"), P("/w/a.ts"), "a.ts", "1", "2", true);
        rec.Record(new Ncw.Agent.Kernel.RunId("r2"), P("/w/b.ts"), "b.ts", "3", "4", false);
        Assert.Equal(["a.ts"], rec.Take(new Ncw.Agent.Kernel.RunId("r1")).Select(c => c.DisplayPath).ToArray());
        Assert.Empty(rec.Take(new Ncw.Agent.Kernel.RunId("r1")));
        Assert.False(rec.Take(new Ncw.Agent.Kernel.RunId("r2")).Single().InWorkspace);
    }

    [Fact]
    public void ChangeRecorder_notifies_on_every_write_and_a_throwing_listener_does_not_break_recording()
    {
        var rec = new ChangeRecorder();
        var seen = new List<ChangeKind>();
        rec.Changed += e => seen.Add(e.Kind);
        rec.Changed += _ => throw new InvalidOperationException("listener bug");
        var run = new Ncw.Agent.Kernel.RunId("run");
        rec.Record(run, P("/w/a.ts"), "a.ts", null, "1", true);
        rec.Record(run, P("/w/a.ts"), "a.ts", "1", "2", true);
        Assert.Equal([ChangeKind.Created, ChangeKind.Modified], seen);
        Assert.Equal("2", Assert.Single(rec.Take(run)).After);
    }
}
