namespace Ncw.Agent.Plugins.Tests;

/// <summary>清单解析与校验(2.x <c>shared/plugin/manifest.ts</c> 的单测逐条移植):装载期严格、读不懂整份拒绝、永不抛。</summary>
public sealed class ManifestTests
{
    private const string Valid = """
        {
          "name": "demo", "publisher": "acme", "version": "1.2.3", "engines": { "nextcowork": "^0.3.0" },
          "main": "./dist/extension.js",
          "permissions": ["storage", "workspace.read"], "optionalPermissions": ["net"],
          "hostPermissions": ["https://api.example.com/*"], "allowedCommands": ["git"],
          "activationEvents": ["onCommand:acme.demo.go", "onStartup"],
          "contributes": {
            "commands": [{ "command": "acme.demo.go", "title": "%cmd.go%" }],
            "tools": [{ "name": "lookup", "title": "%tool.lookup%", "shape": "search" }],
            "skills": [{ "path": "skills/demo-skill" }]
          }
        }
        """;

    private static ManifestParseResult Parse(string json) => PluginManifestParser.Parse(json);

    private static string With(string replace, string with) => Valid.Replace(replace, with, StringComparison.Ordinal);

    [Fact]
    public void A_valid_manifest_parses_with_id_derived_from_publisher_and_name()
    {
        var result = Parse(Valid);

        Assert.True(result.Ok);
        var m = result.Manifest!;
        Assert.Equal("acme.demo", m.Id);
        Assert.Equal(["storage", "workspace.read"], m.Permissions);
        Assert.Equal(["net"], m.OptionalPermissions);
        Assert.Equal("%tool.lookup%", m.Contributes.Tools.Single().Title);
        Assert.Equal(["skills/demo-skill"], m.Contributes.Skills);
        Assert.Equal(PluginKind.Extension, m.Kind);
        Assert.Equal("allowedCommands", Assert.Single(result.Warnings).Field); // 声明了白名单却没有 process 能力:警告,不是错误
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("-leading")]
    [InlineData("")]
    public void The_name_must_match_the_shared_pattern(string name)
    {
        var result = Parse(With("\"name\": \"demo\"", $"\"name\": \"{name}\""));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Field == "name");
    }

    [Fact]
    public void A_bad_version_or_engines_range_is_rejected()
    {
        Assert.Contains(Parse(With("\"1.2.3\"", "\"1.2\"")).Errors, e => e.Field == "version");
        Assert.Contains(Parse(With("^0.3.0", ">=1 <2")).Errors, e => e.Field == "engines.nextcowork");
        Assert.Contains(Parse(With("\"engines\": { \"nextcowork\": \"^0.3.0\" },", "")).Errors, e => e.Field == "engines.nextcowork");
    }

    [Theory]
    [InlineData("../evil.js")]
    [InlineData("/abs/evil.js")]
    [InlineData("C:/evil.js")]
    [InlineData("dist\\\\x.js")]
    [InlineData("dist/extension.ts")]
    public void Main_must_be_a_safe_relative_single_file_js_module(string main)
    {
        var result = Parse(With("./dist/extension.js", main));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Field == "main");
    }

    [Fact]
    public void Unknown_permissions_and_activation_events_are_errors_not_ignored()
    {
        Assert.Contains(Parse(With("\"storage\"", "\"root\"")).Errors, e => e.Field == "permissions" && e.Message.Contains("unknown permission"));
        Assert.Contains(Parse(With("onStartup", "onWhenever:x")).Errors, e => e.Field == "activationEvents");
    }

    [Fact]
    public void Host_permissions_are_https_patterns_without_wildcard_hosts()
    {
        foreach (var bad in new[] { "http://api.example.com/*", "https://*/x", "https://*.example.com/*", "ftp://x/y" })
        {
            Assert.Contains(Parse(With("https://api.example.com/*", bad)).Errors, e => e.Field == "hostPermissions");
        }

        Assert.True(PluginManifestParser.IsHostPattern("https://api.example.com:8443/v1/*"));
    }

    [Fact]
    public void Host_permissions_without_net_only_warn()
    {
        var json = With("\"optionalPermissions\": [\"net\"]", "\"optionalPermissions\": []");

        var result = Parse(json);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Field == "hostPermissions");
    }

    [Theory]
    [InlineData("/usr/bin/git")]
    [InlineData("git.exe")]
    [InlineData("rm -rf")]
    public void Allowed_commands_are_bare_executable_names(string command)
    {
        Assert.Contains(Parse(With("\"git\"", $"\"{command}\"")).Errors, e => e.Field == "allowedCommands");
    }

    [Fact]
    public void Contributed_titles_must_be_l10n_references_not_literal_copy()
    {
        Assert.Contains(Parse(With("%cmd.go%", "Go")).Errors, e => e.Field.EndsWith(".title", StringComparison.Ordinal));
        Assert.Contains(Parse(With("%tool.lookup%", "Lookup")).Errors, e => e.Field.Contains("tools.lookup", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("skills/")]
    [InlineData("other/demo-skill")]
    [InlineData("skills/Demo_Skill")]
    [InlineData("skills/a/b")]
    public void A_skill_must_live_directly_under_skills(string path)
    {
        Assert.Contains(Parse(With("skills/demo-skill", path)).Errors, e => e.Field == "contributes.skills");
    }

    [Fact]
    public void A_slash_command_must_point_at_a_declared_command()
    {
        var json = With("\"skills\": [{ \"path\": \"skills/demo-skill\" }]", "\"slashCommands\": [{ \"name\": \"go\", \"command\": \"acme.demo.missing\", \"title\": \"%cmd.go%\" }]");

        Assert.Contains(Parse(json).Errors, e => e.Field.StartsWith("contributes.slashCommands", StringComparison.Ordinal));
    }

    [Fact]
    public void Ui_contribution_points_are_recognised_but_listed_as_unsupported_not_rejected()
    {
        var json = With("\"skills\": [{ \"path\": \"skills/demo-skill\" }]", "\"views\": [{ \"id\": \"v\", \"title\": \"%t%\", \"path\": \"v.html\" }], \"themes\": [{ \"path\": \"t.json\" }], \"somethingNew\": []");

        var result = Parse(json);

        Assert.True(result.Ok);
        Assert.Equal(["views", "themes", "somethingNew"], result.Manifest!.Contributes.Unsupported);
    }

    [Fact]
    public void A_webapp_plugin_runs_no_code()
    {
        const string webapp = """
            { "name": "site", "publisher": "acme", "version": "1.0.0", "kind": "webapp", "engines": { "nextcowork": "^0.3.0" },
              "contributes": { "webApps": [{ "id": "site", "title": "%t%", "url": "https://example.com" }] } }
            """;
        Assert.True(Parse(webapp).Ok);
        Assert.Contains(Parse(webapp.Replace("\"kind\"", "\"main\": \"./x.js\", \"kind\"", StringComparison.Ordinal)).Errors, e => e.Field == "main");
        Assert.Contains(Parse(webapp.Replace("\"webApps\"", "\"commands\": [{\"command\":\"a\",\"title\":\"%t%\"}], \"webApps\"", StringComparison.Ordinal)).Errors, e => e.Field == "contributes.commands");
    }

    [Fact]
    public void Native_components_are_not_supported()
    {
        Assert.Contains(Parse(With("\"main\":", "\"nativeComponents\": [], \"main\":")).Errors, e => e.Field == "nativeComponents");
    }

    [Fact]
    public void Dependencies_validate_ids_and_forbid_self_dependency()
    {
        Assert.Contains(Parse(With("\"main\":", "\"dependencies\": { \"nonsense\": \"^1.0.0\" }, \"main\":")).Errors, e => e.Field == "dependencies.nonsense");
        Assert.Contains(Parse(With("\"main\":", "\"dependencies\": { \"acme.demo\": \"^1.0.0\" }, \"main\":")).Errors, e => e.Message.Contains("itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        foreach (var junk in new[] { "", "not json", "[]", "null", "{\"name\": 5}", "{\"contributes\": 3}" })
        {
            Assert.False(Parse(junk).Ok);
        }
    }

    [Theory]
    [InlineData("^0.3.0", "0.3.3", true)]
    [InlineData("^0.3.0", "0.4.0", false)]
    [InlineData("^0.2.0", "0.3.3", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=0.3.1", "0.3.3", true)]
    [InlineData(">=0.3.4", "0.3.3", false)]
    [InlineData("0.3.3", "0.3.3", true)]
    [InlineData("^1.0.0", "2.0.0", false)]
    [InlineData("nonsense", "0.3.3", false)]
    public void Engine_ranges_follow_the_four_supported_shapes(string range, string host, bool expected)
    {
        Assert.Equal(expected, PluginVersions.SatisfiesEngine(range, host));
    }

    [Fact]
    public void Engines_compare_against_the_plugin_api_version_and_old_ranges_are_deprecated_not_rejected()
    {
        Assert.Equal(EngineCompatibility.Ok, PluginApiVersion.Check("^0.3.0"));
        Assert.Equal(EngineCompatibility.Deprecated, PluginApiVersion.Check("^0.2.0"));
        Assert.Equal(EngineCompatibility.Incompatible, PluginApiVersion.Check("^0.9.0"));
        Assert.Equal(EngineCompatibility.Incompatible, PluginApiVersion.Check("garbage"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.1.0", "1.0.9", false)]
    [InlineData("1.0.0-beta", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-beta", false)]
    [InlineData("1.0.0", "bogus", false)]
    [InlineData("1.0.0", null, false)]
    public void Newer_version_rules(string current, string? latest, bool expected)
    {
        Assert.Equal(expected, PluginVersions.HasNewerVersion(current, latest));
    }
}
