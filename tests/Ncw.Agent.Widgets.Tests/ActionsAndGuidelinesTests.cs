using System.Text.Json;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Widgets.Actions;
using Ncw.Agent.Widgets.Guidelines;

namespace Ncw.Agent.Widgets.Tests;

/// <summary>2.x <c>shared/__tests__/widget.test.ts</c>(<c>bindWidgetAction</c> / <c>inferWidgetFormat</c>)与 <c>visualize-guidelines/__tests__/guidelines.test.ts</c> 的逐条移植。</summary>
public sealed class ActionsAndGuidelinesTests
{
    private static WidgetActionDef Action(string json) => Assert.Single(WidgetActions.Parse(Jsn.Parse($"[{json}]")));

    private const string RunTests = """
        {"id":"run_tests","label":"Run tests","tool":"Bash",
         "input":{"command":"npm test -- {{suite}} --shard={{shard}}","nested":["{{suite}}"]},
         "params":{"suite":{"type":"enum","values":["api","ui"]},"shard":{"type":"number","min":1,"max":4,"integer":true}}}
        """;

    private static string Bind(string actionJson, string paramsJson) => WidgetActions.Bind(Action(actionJson), Jsn.Parse(paramsJson)).GetRawText();

    // ───────────────────────── widget.test.ts · bindWidgetAction ─────────────────────────

    [Fact]
    public void Valid_params_are_bound_into_every_string_leaf()
    {
        Assert.Equal("""{"command":"npm test -- api --shard=2","nested":["api"]}""", Bind(RunTests, """{"suite":"api","shard":2}"""));
    }

    // ★ 参数会进命令行——枚举之外的值、越界数字、多余参数一律拒绝,不做「尽量代」
    [Fact]
    public void A_value_outside_the_enum_is_refused_including_injection()
    {
        var e = Assert.Throws<WidgetActionException>(() => Bind(RunTests, """{"suite":"api; rm -rf ~","shard":1}"""));
        Assert.Contains("one of", e.Message);
    }

    [Theory]
    [InlineData("""{"suite":"ui","shard":5}""")]
    [InlineData("""{"suite":"ui","shard":0}""")]
    [InlineData("""{"suite":"ui","shard":1.5}""")]
    [InlineData("""{"suite":"ui","shard":"1"}""")]
    [InlineData("""{"suite":"ui"}""")]
    [InlineData("""{"shard":1}""")]
    [InlineData("""{"suite":["api"],"shard":1}""")]
    public void Out_of_range_non_integer_wrongly_typed_and_missing_params_are_refused(string parameters)
    {
        Assert.Throws<WidgetActionException>(() => Bind(RunTests, parameters));
    }

    [Fact]
    public void Undeclared_params_are_refused()
    {
        var e = Assert.Throws<WidgetActionException>(() => Bind(RunTests, """{"suite":"ui","shard":1,"extra":"x"}"""));
        Assert.Contains("Unknown parameter", e.Message);
    }

    [Fact]
    public void An_action_without_params_returns_its_input_unchanged()
    {
        const string read = """{"id":"r","label":"R","tool":"Read","input":{"file_path":"/a/b"}}""";
        Assert.Equal("""{"file_path":"/a/b"}""", Bind(read, "{}"));
        Assert.Equal("""{"file_path":"/a/b"}""", Bind(read, "null"));
    }

    [Fact]
    public void Binding_only_touches_string_leaves_and_never_changes_types()
    {
        const string json = """{"id":"r","label":"R","tool":"Bash","input":{"n":5,"ok":true,"none":null,"s":"{{ suite }}x","arr":[1,{"k":"{{suite}}"}]},"params":{"suite":{"type":"enum","values":["a"]}}}""";
        Assert.Equal("""{"n":5,"ok":true,"none":null,"s":"ax","arr":[1,{"k":"a"}]}""", Bind(json, """{"suite":"a"}"""));
    }

    [Fact]
    public void A_number_param_substitutes_as_text_inside_the_string()
    {
        const string json = """{"id":"r","label":"R","tool":"Bash","input":{"command":"x {{n}}"},"params":{"n":{"type":"number","min":0,"max":10}}}""";
        Assert.Equal("""{"command":"x 2.5"}""", Bind(json, """{"n":2.5}"""));
    }

    // ───────────────────────── 声明的校验 ─────────────────────────

    [Theory]
    [InlineData("""[{"id":"A","label":"x","tool":"Bash","input":{}}]""", "snake_case")]
    [InlineData("""[{"id":"a","label":"","tool":"Bash","input":{}}]""", "label")]
    [InlineData("""[{"id":"a","label":"x","tool":"","input":{}}]""", "tool")]
    [InlineData("""[{"id":"a","label":"x","tool":"Bash"}]""", "input")]
    [InlineData("""[{"id":"a","label":"x","tool":"Bash","input":{"c":"{{p}}"},"params":{"p":{"type":"string"}}}]""", "enum")]
    [InlineData("""[{"id":"a","label":"x","tool":"Bash","input":{"c":"{{p}}"},"params":{"p":{"type":"number","min":5,"max":1}}}]""", "min > max")]
    [InlineData("""[{"id":"a","label":"x","tool":"Bash","input":{"c":"{{p}}"},"params":{"p":{"type":"enum","values":[]}}}]""", "1-32")]
    [InlineData("""[{"id":"a","label":"x","tool":"Bash","input":{"c":"{{p}}"},"params":{"1p":{"type":"enum","values":["a"]}}}]""", "not valid")]
    public void Malformed_declarations_are_refused_with_a_reason(string array, string expected)
    {
        var e = Assert.Throws<WidgetActionException>(() => WidgetActions.Parse(Jsn.Parse(array)));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void At_most_sixteen_actions()
    {
        var many = "[" + string.Join(',', Enumerable.Range(0, 17).Select(i => "{\"id\":\"a" + i + "\",\"label\":\"x\",\"tool\":\"Read\",\"input\":{}}")) + "]";
        Assert.Throws<WidgetActionException>(() => WidgetActions.Parse(Jsn.Parse(many)));
    }

    [Fact]
    public void Actions_round_trip_through_the_card_json()
    {
        var def = Action(RunTests);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            WidgetActions.Write(w, [def]);
        }

        var back = Assert.Single(WidgetActions.Parse(Jsn.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()))));
        Assert.Equal(def.Id, back.Id);
        Assert.Equal(def.Tool, back.Tool);
        Assert.Equal(def.Input.GetRawText(), back.Input.GetRawText());
        Assert.Equal(2, back.Params.Count);
    }

    // ───────────────────────── widget.test.ts · inferWidgetFormat(3.0:只有 html / svg) ─────────────────────────

    [Fact]
    public void The_format_is_recognised_from_the_start_and_tsx_is_refused()
    {
        Assert.Equal("svg", WidgetPipeline.ResolveFormat(null, "<svg viewBox=\"0 0 1 1\"></svg>"));
        Assert.Equal("html", WidgetPipeline.ResolveFormat(null, "<div>hi</div>"));
        Assert.Throws<Ncw.Agent.Tools.ToolFailureException>(() => WidgetPipeline.ResolveFormat(null, "import { Stack } from 'nextcowork/widget'\nexport default function W() {}"));
        Assert.Throws<Ncw.Agent.Tools.ToolFailureException>(() => WidgetPipeline.ResolveFormat(null, "export default function W() { return null }"));
        Assert.Throws<Ncw.Agent.Tools.ToolFailureException>(() => WidgetPipeline.ResolveFormat("tsx", "<div/>"));
    }

    // ───────────────────────── guidelines.test.ts ─────────────────────────

    [Fact]
    public void Every_module_assembles_into_something_longer_than_core()
    {
        foreach (var module in WidgetGuidelines.AvailableModules)
        {
            Assert.True(WidgetGuidelines.Get([module]).Length > WidgetGuidelines.Core.Length, module);
        }
    }

    // 顺序是模型看到的枚举顺序,改动要当成产品改动看。3.0 去掉了 `components`(TSX 的规范)。
    [Fact]
    public void The_module_order_is_the_enum_order_the_model_sees()
    {
        Assert.Equal(["diagram", "mockup", "interactive", "chart", "art"], WidgetGuidelines.AvailableModules);
    }

    [Fact]
    public void The_text_starts_with_core_and_ends_with_a_newline()
    {
        var text = WidgetGuidelines.Get(["diagram"]);
        Assert.StartsWith(WidgetGuidelines.Core, text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    // ★ 去重是必须的:一次请求两个模块时,共享的段落只能出现一次(正文里满是「见上 / 见下」)
    [Fact]
    public void Sections_shared_by_two_modules_appear_once()
    {
        var text = WidgetGuidelines.Get(["chart", "mockup"]);
        Assert.Equal(1, Count(text, WidgetGuidelines.Section("ui-components")));
        Assert.Equal(1, Count(text, WidgetGuidelines.Section("color-palette")));
        Assert.Contains(WidgetGuidelines.Section("charts"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(WidgetGuidelines.Section("diagram-types"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_the_same_module_repeatedly_does_not_lengthen_the_text()
    {
        Assert.Equal(WidgetGuidelines.Get(["art"]), WidgetGuidelines.Get(["art", "art", "art"]));
    }

    [Fact]
    public void Sections_are_separated_by_two_blank_lines()
    {
        Assert.Contains($"\n\n\n{WidgetGuidelines.Section("ui-components")}", WidgetGuidelines.Get(["mockup"]), StringComparison.Ordinal);
    }

    // ★ 钉的是 read_me 里那道长度检查的存在理由:单模块都安全,但 diagram + chart 会越过截断线
    [Fact]
    public void Every_single_module_fits_under_the_output_cap_but_diagram_plus_chart_does_not()
    {
        foreach (var module in WidgetGuidelines.AvailableModules)
        {
            Assert.True(WidgetGuidelines.Get([module]).Length <= VisualizeReadMeTool.OutputLimit, module);
        }

        Assert.True(WidgetGuidelines.Get(["diagram", "chart"]).Length > VisualizeReadMeTool.OutputLimit);
    }

    [Fact]
    public void Each_module_brings_the_sections_it_should()
    {
        Assert.Contains(WidgetGuidelines.Section("svg-setup"), WidgetGuidelines.Get(["art"]), StringComparison.Ordinal);
        Assert.Contains(WidgetGuidelines.Section("art"), WidgetGuidelines.Get(["art"]), StringComparison.Ordinal);
        Assert.Contains(WidgetGuidelines.Section("diagram-types"), WidgetGuidelines.Get(["diagram"]), StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle) => haystack.Split(needle).Length - 1;
}
