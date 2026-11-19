using System.Text.Json.Nodes;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 「编辑模型」弹窗(docs/design/07「编辑模型」):预填有效值、只发改了的字段、先改名再更新、思考配置保留高级键、客户端先拦明显不对的输入。
/// </summary>
public class ModelEditorViewModelTests
{
    private sealed class FakeCommands : IModelEditorCommands
    {
        public List<string> Calls { get; } = [];

        public List<ModelUpdateParams> Updates { get; } = [];

        public ActionResult<ModelAliasDto> RenameResult { get; set; } = new(ActionOutcome.Ok);

        public Task<ActionResult<ModelAliasDto>> UpdateModelAsync(string deviceId, ModelUpdateParams update, CancellationToken ct = default)
        {
            Calls.Add($"update:{update.Alias}");
            Updates.Add(update);
            return Task.FromResult(new ActionResult<ModelAliasDto>(ActionOutcome.Ok));
        }

        public Task<ActionResult<ModelAliasDto>> RenameModelAsync(string deviceId, string providerId, string alias, string nextAlias, CancellationToken ct = default)
        {
            Calls.Add($"rename:{alias}->{nextAlias}");
            return Task.FromResult(RenameResult);
        }
    }

    /// <summary>导入时的占位缺省(200000 / 8192),思考配置没被接管——有效值来自目录。</summary>
    private static ModelAliasDto Alias(string? thinking = null, string? efforts = null, string[]? overrides = null, string? protocolOverride = null) =>
        new("deepseek-flash", "gw", "deepseek/deepseek-flash", 200_000, 8192, null, protocolOverride, null, null, true, true,
            overrides ?? [], thinking, efforts);

    private static ModelOptionDto Effective(string? mode = "effort", string[]? efforts = null) =>
        new("gw", "Gateway", "deepseek-flash", "deepseek-flash", "deepseek/deepseek-flash", 1_000_000, 65_536, true, true, false, true,
            mode, efforts ?? ["low", "medium", "high"], "anthropic");

    private static ModelEditorViewModel Editor(FakeCommands commands, ModelAliasDto? alias = null, ModelOptionDto? effective = null) =>
        new(commands, "dev", alias ?? Alias(), effective ?? Effective());

    [Fact]
    public void It_prefills_the_effective_values_and_offers_follow_the_catalog_for_thinking()
    {
        var vm = Editor(new FakeCommands());

        // 不是别名里存着的占位缺省,是设备算好的有效值
        Assert.Equal((1_000_000d, 65_536d, false), (vm.ContextWindow, vm.MaxOutputTokens, vm.SupportsImages));
        Assert.Equal(ModelEditorViewModel.ThinkingAuto, vm.ThinkingMode);
        Assert.Equal(ModelEditorViewModel.ThinkingAuto, vm.ThinkingModes[0]);
        Assert.Equal("effort", vm.EffectiveThinkingMode);
        Assert.Equal("anthropic", vm.ProviderProtocol);
        Assert.Equal(["low", "medium", "high"], vm.Efforts.Where(e => e.IsSelected).Select(e => e.Value));
        Assert.Equal("medium", vm.DefaultEffort);
        Assert.False(vm.IsDirty);
        Assert.False(vm.ShowsThinkingDetails);
    }

    [Fact]
    public async Task Saving_without_changes_sends_nothing_and_closes()
    {
        var commands = new FakeCommands();
        var vm = Editor(commands);
        var closed = false;
        vm.Completed += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(commands.Calls);
        Assert.True(closed);
    }

    [Fact]
    public async Task Only_the_changed_fields_are_sent()
    {
        var commands = new FakeCommands();
        var vm = Editor(commands);
        vm.ContextWindow = 128_000;
        vm.SupportsImages = true;
        Assert.True(vm.IsDirty);

        await vm.SaveCommand.ExecuteAsync(null);

        var update = Assert.Single(commands.Updates);
        Assert.Equal(new ModelUpdateParams("gw", "deepseek-flash", ContextWindow: 128_000, Vision: true), update);
    }

    [Fact]
    public async Task Renaming_goes_first_and_the_update_targets_the_new_alias()
    {
        var commands = new FakeCommands();
        var vm = Editor(commands);
        vm.AliasName = "  flash  ";
        vm.DisplayName = "DeepSeek Flash";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["rename:deepseek-flash->flash", "update:flash"], commands.Calls);
        Assert.Equal("DeepSeek Flash", commands.Updates[0].DisplayName);
        Assert.Equal("flash", vm.Alias);
    }

    [Fact]
    public async Task A_failed_rename_keeps_the_dialog_open_and_touches_nothing_else()
    {
        var commands = new FakeCommands { RenameResult = new(ActionOutcome.Failed, ErrorKey: "providers.aliasExists") };
        var vm = Editor(commands);
        var closed = false;
        vm.Completed += () => closed = true;
        vm.AliasName = "taken";
        vm.ContextWindow = 512_000;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["rename:deepseek-flash->taken"], commands.Calls);
        Assert.Equal("providers.aliasExists", vm.ErrorKey);
        Assert.False(closed);
        vm.AliasName = "free"; // 改了就清掉错误
        Assert.Null(vm.ErrorKey);
    }

    [Fact]
    public async Task Allowing_off_writes_the_levels_and_keeps_the_advanced_thinking_keys()
    {
        var commands = new FakeCommands();
        var stored = "{\"mode\":\"effort\",\"defaultEnabled\":true,\"defaultEffort\":\"high\",\"parameterPath\":\"reasoning.effort\",\"effortMap\":{\"high\":\"hi\"}}";
        var vm = Editor(commands, Alias(stored, "[\"low\",\"high\"]", ["thinkingConfig", "reasoningEfforts"]));
        Assert.DoesNotContain(ModelEditorViewModel.ThinkingAuto, vm.ThinkingModes); // 已经接管了:没有「跟随模型目录」可回
        Assert.Equal(("effort", "high"), (vm.ThinkingMode, vm.DefaultEffort));
        Assert.True(vm.ShowsEfforts);

        vm.Efforts.Single(e => e.Value == "none").IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var update = Assert.Single(commands.Updates);
        Assert.Equal("[\"none\",\"low\",\"high\"]", update.ReasoningEffortsJson);
        var thinking = JsonNode.Parse(update.ThinkingConfigJson!)!.AsObject();
        Assert.Equal(("effort", "high", "reasoning.effort", "hi"),
            ((string?)thinking["mode"], (string?)thinking["defaultEffort"], (string?)thinking["parameterPath"], (string?)thinking["effortMap"]!["high"]));
        Assert.Null(update.ContextWindow); // 没动的不发
    }

    [Fact]
    public async Task Switching_to_a_budget_writes_a_budget_config_without_effort_fields()
    {
        var commands = new FakeCommands();
        var vm = Editor(commands);
        vm.ThinkingMode = "budget";
        Assert.True(vm.ShowsBudget && vm.ShowsDefaultEnabled && !vm.ShowsEfforts);
        vm.DefaultEnabled = false;
        vm.BudgetTokens = 16_000;

        await vm.SaveCommand.ExecuteAsync(null);

        var thinking = JsonNode.Parse(Assert.Single(commands.Updates).ThinkingConfigJson!)!.AsObject();
        Assert.Equal(("budget", false, 16_000), ((string?)thinking["mode"], (bool)thinking["defaultEnabled"]!, (int)thinking["defaultBudgetTokens"]!));
        Assert.False(thinking.ContainsKey("defaultEffort"));
        Assert.Null(commands.Updates[0].ReasoningEffortsJson);
    }

    [Fact]
    public void Unchecking_the_default_level_moves_the_default_to_a_remaining_one()
    {
        var vm = Editor(new FakeCommands());
        vm.ThinkingMode = "effort";

        vm.Efforts.Single(e => e.Value == "medium").IsSelected = false;

        Assert.Equal(["low", "high"], vm.DefaultEffortChoices);
        Assert.Equal("low", vm.DefaultEffort);
    }

    [Fact]
    public async Task Following_the_provider_protocol_clears_the_override()
    {
        var commands = new FakeCommands();
        var vm = Editor(commands, Alias(protocolOverride: "openai-chat"));
        Assert.Null(vm.ProviderProtocol); // 有覆盖时不知道供应商本来的协议,不瞎写

        vm.Protocol = "";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new ModelUpdateParams("gw", "deepseek-flash", ClearProtocolOverride: true), Assert.Single(commands.Updates));
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("output")]
    [InlineData("budget")]
    [InlineData("effort")]
    public async Task Obviously_wrong_input_is_stopped_before_reaching_the_device(string problem)
    {
        var commands = new FakeCommands();
        var vm = Editor(commands);
        string expected;
        switch (problem)
        {
            case "alias":
                vm.AliasName = "   ";
                expected = ModelEditorViewModel.Errors.AliasRequired;
                break;
            case "output":
                vm.MaxOutputTokens = 2_000_000;
                expected = ModelEditorViewModel.Errors.OutputTooLarge;
                break;
            case "budget":
                vm.ThinkingMode = "budget";
                vm.BudgetTokens = 100;
                expected = ModelEditorViewModel.Errors.BudgetTooSmall;
                break;
            default:
                vm.ThinkingMode = "effort";
                foreach (var effort in vm.Efforts)
                {
                    effort.IsSelected = effort.Value == "none";
                }

                expected = ModelEditorViewModel.Errors.NoEffort;
                break;
        }

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.ErrorKey);
        Assert.Empty(commands.Calls);
    }
}
