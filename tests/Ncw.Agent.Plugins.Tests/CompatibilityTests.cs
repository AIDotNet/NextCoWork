namespace Ncw.Agent.Plugins.Tests;

/// <summary>
/// 2.x 兼容性验证(05 风险表「Jaunt 成熟度」的 spike):2.x 自带插件(<c>plugins/ncw.*</c>)和示例插件(<c>examples/*</c>)的<b>真实清单</b>(<c>Fixtures2x/*.json</c>,从 main 分支原样拷来)都要能被 3.0 的清单解析读懂。
/// 结论(也写在设计 02 A7.1):非 UI 的插件(think / wechat-exports / claude-code / codex 的命令、工具、配置)原样可用;
/// 依赖 React 视图 / 自定义编辑器的(办公三件套、image-studio、markdown-studio、note-editor、excalidraw)<b>清单读得懂、能装能启用,但视图部分不会生效</b>——解析给出逐条「不支持」诊断(Q2);原生组件(<c>nativeComponents</c>)是硬拒绝。
/// 2.x 插件的 <c>main</c> 本来就是 esbuild 打出来的单文件 ESM(清单校验要求 <c>.js</c>),所以 3.0 不需要在加载时擦除 TypeScript 类型(sucrase 在 2.x 只用于 widget 的 TSX,不用于插件)。
/// </summary>
public sealed class CompatibilityTests
{
    private static ManifestParseResult Parse(string name) =>
        PluginManifestParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures2x", $"{name}.json")));

    [Theory]
    [InlineData("acme.think")]
    [InlineData("acme.wechat-exports")]
    [InlineData("acme.claude-code")]
    [InlineData("acme.codex")]
    [InlineData("ncw.bilibili")]
    public void The_non_ui_plugins_of_2x_parse_and_load_as_is(string name)
    {
        var result = Parse(name);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        Assert.NotEqual(EngineCompatibility.Incompatible, PluginApiVersion.Check(result.Manifest!.Engines)); // engines 比的是插件 API 版本,不是应用版本
    }

    [Fact]
    public void The_thinking_and_wechat_plugins_keep_their_tools_permissions_and_declared_shapes()
    {
        var think = Parse("acme.think").Manifest!;
        Assert.Equal("acme.think", think.Id);
        Assert.Equal(["think"], think.Contributes.Tools.Select(t => t.Name));
        Assert.Equal(("reasoning", "%tool.think.summary%"), (think.Contributes.Tools[0].Shape, think.Contributes.Tools[0].CardSummary));
        Assert.Empty(think.Permissions);

        var wechat = Parse("acme.wechat-exports").Manifest!;
        Assert.Equal(["list_exports", "read_export", "search_exports"], wechat.Contributes.Tools.Select(t => t.Name));
        Assert.Equal(["workspace.read"], wechat.Permissions);
        Assert.Equal(["onTool:list_exports", "onTool:read_export", "onTool:search_exports"], wechat.ActivationEvents);
    }

    [Fact]
    public void A_zero_code_web_app_plugin_parses_but_there_is_nothing_to_run()
    {
        var bilibili = Parse("ncw.bilibili").Manifest!;

        Assert.Equal(PluginKind.WebApp, bilibili.Kind);
        Assert.Contains("webApps", bilibili.Contributes.Unsupported); // 3.0 没有网页应用的承载界面:诊断里逐条说明
    }

    [Theory]
    [InlineData("acme.image-studio")]
    [InlineData("acme.markdown-studio")]
    [InlineData("acme.note-editor")]
    [InlineData("acme.excalidraw")]
    public void Editor_plugins_parse_but_their_view_contributions_are_reported_unsupported_per_contribution_point(string name)
    {
        var result = Parse(name);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        var unsupported = result.Manifest!.Contributes.Unsupported;
        Assert.Contains(unsupported, u => u is "views" or "customEditors");
    }

    [Theory]
    [InlineData("ncw.writer")]
    [InlineData("ncw.sheets")]
    [InlineData("ncw.slides")]
    public void The_office_suite_parses_it_is_pure_editor_ui_so_after_install_it_has_nothing_to_run_and_says_so(string name)
    {
        var result = Parse(name);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        Assert.Equal(["customEditors", "views"], result.Manifest!.Contributes.Unsupported.Order());
        Assert.Empty(result.Manifest.Contributes.Commands);
        Assert.Empty(result.Manifest.Contributes.Tools);
    }

    [Fact]
    public void A_manifest_with_native_components_is_refused_whole()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures2x", "acme.think.json")).Replace("\"permissions\"", "\"nativeComponents\": [], \"permissions\"", StringComparison.Ordinal);

        Assert.Contains(PluginManifestParser.Parse(json).Errors, e => e.Field == "nativeComponents");
    }

    [Fact]
    public void Manifests_that_only_contribute_ui_become_active_plugins_without_contributions_and_the_diagnostics_list_what_was_ignored()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures2x", "ncw.writer.json"));
        var manifest = PluginManifestParser.Parse(json).Manifest!;

        Assert.Equal(["customEditors", "views"], manifest.Contributes.Unsupported.Order());
        Assert.Equal(["ncw.office-runtime"], manifest.Dependencies.Keys);
    }
}
