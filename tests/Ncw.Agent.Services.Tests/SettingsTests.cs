using System.Text.Json.Nodes;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Tests;

/// <summary>设备 / 项目设置的读写:出厂值与 2.x 一致、宽容读、深合并、成对写、整体校验、未知键保留、变更广播、每次现读。</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly SettingsRepository _repo;
    private readonly SettingsService _settings;

    public SettingsTests()
    {
        _repo = new SettingsRepository(_db.Db);
        _settings = new SettingsService(_repo);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_fresh_database_reads_the_2x_factory_values()
    {
        var s = _settings.Current();
        Assert.Equal(DeviceSettings.Default, s);
        Assert.Equal(("ask", "auto", true, 32_000, true, true, false, true), (s.DefaultPermissionMode, s.ModelProposedGoals, s.AutoCompact, s.MaxOutputTokens, s.PromptSuggestions, s.LocalCompletion, s.InlineCompletion, s.ImageGeneration));
        Assert.Equal((600, true, false, 30, "system"), (s.UpstreamIdleTimeoutSeconds, s.AccountRotation, s.Failover, s.GoalCheckinMinutes, s.Shell));
        Assert.Equal((4, 4, "inherit"), (s.Subagent.PerSessionLimit, s.Subagent.GlobalLimit, s.Subagent.Thinking));
        Assert.Equal((false, 19836), (s.Gateway.Enabled, s.Gateway.PreferredPort));
    }

    [Fact]
    public void A_2x_settings_row_is_read_with_its_own_key_names()
    {
        _repo.WriteJson("""
            {"defaultModel":"sonnet","defaultModelProviderId":"p1","contextManagement":{"autoCompact":false},"maxOutputTokens":16000,
             "gateway":{"enabled":true,"allowRemote":true,"preferredPort":20000,"failover":true},"providerAccountRotation":false,
             "upstreamIdleTimeoutSeconds":900,"subagent":{"model":"haiku","modelProviderId":"p2","thinking":"high","perSessionLimit":2,"globalLimit":0},
             "personalization":{"name":"Ada","background":"b","instructions":"be brief"},"compactModel":"c","compactThinking":"low",
             "goalEvaluatorModel":"judge","modelProposedGoals":"alwaysAsk","permissionReviewerModel":"rev","theme":"dark"}
            """);

        var s = _settings.Current();

        Assert.Equal(new ModelPick("sonnet", "p1"), s.DefaultModel);
        Assert.Equal((false, 16_000, true, true, 20_000), (s.AutoCompact, s.MaxOutputTokens, s.Failover, s.Gateway.AllowRemote, s.Gateway.PreferredPort));
        Assert.Equal((false, 900), (s.AccountRotation, s.UpstreamIdleTimeoutSeconds));
        Assert.Equal((new ModelPick("haiku", "p2"), "high", 2, 0), (s.Subagent.Model, s.Subagent.Thinking, s.Subagent.PerSessionLimit, s.Subagent.GlobalLimit));
        Assert.Equal(("Ada", "b", "be brief"), (s.Personalization.Name, s.Personalization.Background, s.Personalization.Instructions));
        Assert.Equal((new ModelPick("c"), "low", new ModelPick("judge"), "alwaysAsk", new ModelPick("rev")), (s.Compact, s.CompactThinking, s.GoalEvaluator, s.ModelProposedGoals, s.PermissionReviewer));
    }

    [Fact]
    public void Garbage_values_fall_back_to_the_factory_value_instead_of_throwing()
    {
        _repo.WriteJson("""{"maxOutputTokens":5,"upstreamIdleTimeoutSeconds":"fast","defaultPermissionMode":"yolo","subagent":{"perSessionLimit":99,"globalLimit":-1},"shell":7,"gateway":[1],"personalization":"x"}""");
        Assert.Equal(DeviceSettings.Default, _settings.Current());

        _repo.WriteJson("not json at all");
        Assert.Equal(DeviceSettings.Default, _settings.Current());
        _repo.WriteJson("[1,2,3]");
        Assert.Equal(DeviceSettings.Default, _settings.Current());
    }

    [Fact]
    public void An_update_is_a_deep_merge_two_switches_in_the_same_block_do_not_overwrite_each_other()
    {
        _settings.Update(new DeviceSettingsPatch { Gateway = new GatewaySettingsPatch(Enabled: true) });
        _settings.Update(new DeviceSettingsPatch { Gateway = new GatewaySettingsPatch(AllowRemote: true), Failover = true });

        var s = _settings.Current();
        Assert.Equal((true, true, 19836, true), (s.Gateway.Enabled, s.Gateway.AllowRemote, s.Gateway.PreferredPort, s.Failover));
    }

    [Fact]
    public void Unknown_keys_such_as_the_client_local_2x_settings_survive_every_write()
    {
        _repo.WriteJson("""{"theme":"dark","locale":"en-US","themeStudio":{"name":"mine"},"shortcuts":{"openSettings":"CmdOrCtrl+,"}}""");

        _settings.Update(new DeviceSettingsPatch { MaxOutputTokens = 8000, Subagent = new SubagentSettingsPatch(PerSessionLimit: 3) });

        var root = JsonNode.Parse(_repo.ReadJson()!)!.AsObject();
        Assert.Equal("dark", (string?)root["theme"]);
        Assert.Equal("mine", (string?)root["themeStudio"]!["name"]);
        Assert.Equal(8000, (int?)root["maxOutputTokens"]);
        Assert.Equal(3, (int?)root["subagent"]!["perSessionLimit"]);
    }

    [Fact]
    public void A_model_and_its_provider_are_written_as_a_pair_never_new_alias_with_the_old_provider()
    {
        _settings.Update(new DeviceSettingsPatch { DefaultModel = new ModelPick("a", "p1") });
        Assert.Equal(new ModelPick("a", "p1"), _settings.Current().DefaultModel);

        _settings.Update(new DeviceSettingsPatch { DefaultModel = new ModelPick("b") }); // 取消锁定供应商
        Assert.Equal(new ModelPick("b"), _settings.Current().DefaultModel);
        Assert.DoesNotContain("defaultModelProviderId", _repo.ReadJson()!);

        _settings.Update(new DeviceSettingsPatch { DefaultModel = ModelPick.None });
        Assert.False(_settings.Current().DefaultModel.IsSet);
    }

    [Fact]
    public void An_invalid_field_rejects_the_whole_patch_and_nothing_is_written()
    {
        _settings.Update(new DeviceSettingsPatch { MaxOutputTokens = 8000 });
        var before = _repo.ReadJson();

        var error = Assert.Throws<SettingsException>(() => _settings.Update(new DeviceSettingsPatch { PromptSuggestions = false, MaxOutputTokens = 100 }));

        Assert.Equal(("settings.invalid.range", "maxOutputTokens"), (error.Key, error.Field));
        Assert.Equal(before, _repo.ReadJson());
        Assert.True(_settings.Current().PromptSuggestions); // 同一补丁里合法的那一项也没生效
    }

    [Theory]
    [InlineData("defaultPermissionMode", "yolo", "settings.invalid.choice")]
    [InlineData("shell", "tcsh", "settings.invalid.choice")]
    [InlineData("compactThinking", "ludicrous", "settings.invalid.choice")]
    [InlineData("upstreamIdleTimeoutSeconds", "59", "settings.invalid.range")]
    [InlineData("upstreamIdleTimeoutSeconds", "3601", "settings.invalid.range")]
    [InlineData("goalCheckinMinutes", "0", "settings.invalid.range")]
    [InlineData("builtinSearxngUrl", "ftp://x", "settings.invalid.url")]
    public void Out_of_range_and_non_enum_values_are_rejected_with_a_stable_key(string field, string value, string key)
    {
        var patch = field switch
        {
            "defaultPermissionMode" => new DeviceSettingsPatch { DefaultPermissionMode = value },
            "shell" => new DeviceSettingsPatch { Shell = value },
            "compactThinking" => new DeviceSettingsPatch { CompactThinking = value },
            "upstreamIdleTimeoutSeconds" => new DeviceSettingsPatch { UpstreamIdleTimeoutSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "goalCheckinMinutes" => new DeviceSettingsPatch { GoalCheckinMinutes = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            _ => new DeviceSettingsPatch { BuiltinSearxngUrl = value },
        };
        Assert.Equal(key, Assert.Throws<SettingsException>(() => _settings.Update(patch)).Key);
    }

    [Fact]
    public void The_personalization_text_is_clipped_before_it_is_stored()
    {
        _settings.Update(new DeviceSettingsPatch { Personalization = new PersonalizationPatch(new string('n', 500), new string('b', 5000), new string('i', 20_000)) });

        var p = _settings.Current().Personalization;
        Assert.Equal((64, 2000, 8000), (p.Name.Length, p.Background.Length, p.Instructions.Length));
        Assert.DoesNotContain(new string('i', 8001), _repo.ReadJson()!); // 绕过界面的调用也撑不爆系统提示词的稳定前缀
    }

    [Fact]
    public void Changes_are_announced_with_a_monotonic_revision_and_a_no_op_does_not_bump_it()
    {
        var seen = new List<(long Rev, int Max)>();
        _settings.Changed += (rev, s) => seen.Add((rev, s.MaxOutputTokens));
        var start = _settings.Rev;

        _settings.Update(new DeviceSettingsPatch { MaxOutputTokens = 8000 });
        _settings.Update(new DeviceSettingsPatch { MaxOutputTokens = 8000 }); // 没有变化
        _settings.Update(new DeviceSettingsPatch { MaxOutputTokens = 9000 });

        Assert.Equal([(start + 1, 8000), (start + 2, 9000)], seen);
        Assert.Equal(start + 2, _settings.Rev);
    }

    [Fact]
    public void The_next_read_sees_the_write_there_is_no_cache()
    {
        var other = new SettingsService(new SettingsRepository(_db.Db)); // 另一个读者(Run / 路由 / 子代理读的就是这种)
        _settings.Update(new DeviceSettingsPatch { UpstreamIdleTimeoutSeconds = 1800, AccountRotation = false });

        Assert.Equal((1800, false), (other.Current().UpstreamIdleTimeoutSeconds, other.Current().AccountRotation));
    }

    // ───────────────────────── 项目设置 ─────────────────────────

    [Fact]
    public void Project_settings_default_to_the_2x_workspace_defaults_and_keep_other_keys()
    {
        _db.AddWorkspace("ws");
        var projects = new ProjectSettingsService(new WorkspaceRepository(_db.Db));

        Assert.Equal(("ask", "code", "auto", true, false, null, null), Read(projects.Get("ws")!));

        var updated = projects.Update("ws", new ProjectSettingsPatch { PermissionMode = "auto", DefaultModel = new ModelPick("m", "p"), MaxContext = true, CompactThinking = "high" })!;

        Assert.Equal(("auto", "code", "auto", true, true, null, "high"), Read(updated));
        Assert.Equal(new ModelPick("m", "p"), updated.DefaultModel);
        Assert.Null(projects.Get("missing"));
        Assert.Null(projects.Update("missing", new ProjectSettingsPatch { WebSearch = false }));

        static (string, string, string, bool, bool, ModelPick?, string?) Read(ProjectSettings p) => (p.PermissionMode, p.DefaultMode, p.DefaultThinking, p.WebSearch, p.MaxContext, p.CompactModel, p.CompactThinking);
    }

    [Fact]
    public void Clearing_the_project_compaction_override_goes_back_to_following_the_global_setting()
    {
        _db.AddWorkspace("ws");
        var projects = new ProjectSettingsService(new WorkspaceRepository(_db.Db));
        projects.Update("ws", new ProjectSettingsPatch { CompactModel = new ModelPick("cheap", "p"), CompactThinking = "low" });

        var cleared = projects.Update("ws", new ProjectSettingsPatch { ClearCompactModel = true, ClearCompactThinking = true })!;

        Assert.Null(cleared.CompactModel);
        Assert.Null(cleared.CompactThinking);
    }

    [Fact]
    public void An_invalid_project_value_is_rejected_and_other_settings_of_the_project_are_untouched()
    {
        _db.AddWorkspace("ws");
        var projects = new ProjectSettingsService(new WorkspaceRepository(_db.Db));

        Assert.Equal("settings.invalid.choice", Assert.Throws<SettingsException>(() => projects.Update("ws", new ProjectSettingsPatch { PermissionMode = "yolo", WebSearch = false })).Key);
        Assert.Equal("settings.invalid.choice", Assert.Throws<SettingsException>(() => projects.Update("ws", new ProjectSettingsPatch { DefaultMode = "Not A Mode" })).Key);
        Assert.True(projects.Get("ws")!.WebSearch);
    }
}
