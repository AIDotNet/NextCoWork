using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 「添加 / 编辑供应商」弹窗(docs/design/07 §3「添加供应商」):模版分组与搜索、选模版填表、id 不撞、要 key 的模版先拦、
/// 保存 = 写供应商 → 存 key → 测连接、测试没过时弹窗不关、编辑直接进表单、老设备只有自定义。
/// </summary>
public class ProviderEditorViewModelTests
{
    internal sealed class FakeCommands : IProviderEditorCommands
    {
        public List<ProvidersUpsertParams> Upserts { get; } = [];

        public List<(string Id, string Key)> Keys { get; } = [];

        public List<string> Tests { get; } = [];

        public ActionResult<ProviderDto> UpsertResult { get; set; } = new(ActionOutcome.Ok);

        public Func<string, ProviderTestResultDto> OnTest { get; set; } = _ => new ProviderTestResultDto(true, 120, 200, null, null);

        public Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default)
        {
            Upserts.Add(provider);
            return Task.FromResult(UpsertResult);
        }

        public Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default)
        {
            Keys.Add((providerId, apiKey));
            return Task.FromResult(new ActionResult<CredentialInfoDto>(ActionOutcome.Ok, new CredentialInfoDto(true, apiKey[^4..], null, null, null)));
        }

        public Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default)
        {
            Tests.Add(providerId);
            return Task.FromResult(new ActionResult<ProviderTestResultDto>(ActionOutcome.Ok, OnTest(providerId)));
        }
    }

    internal static readonly ProviderTemplateDto[] Templates =
    [
        new("anthropic", "anthropic", "Anthropic", ProviderTemplateGroups.International, null, "anthropic", "https://api.anthropic.com", "https://console.anthropic.com/settings/keys", true, true),
        new("openai", "openai", "OpenAI", ProviderTemplateGroups.International, null, "openai-responses", "https://api.openai.com/v1", "https://platform.openai.com/api-keys", true, true),
        new("xai", "xai", "xAI", ProviderTemplateGroups.International, null, "openai-chat", "https://api.x.ai/v1", "https://console.x.ai", true, false),
        new("deepseek", "deepseek", "DeepSeek", ProviderTemplateGroups.China, null, "openai-chat", "https://api.deepseek.com", "https://platform.deepseek.com/api_keys", true, true),
        new("deepseek-anthropic", "deepseek", "DeepSeek", ProviderTemplateGroups.China, ProviderTemplateVariants.Anthropic, "anthropic", "https://api.deepseek.com/anthropic",
            "https://platform.deepseek.com/api_keys", true, false),
        new("openrouter", "openrouter", "OpenRouter", ProviderTemplateGroups.Aggregator, null, "openai-chat", "https://openrouter.ai/api/v1", "https://openrouter.ai/keys", true, false),
        new("ollama", "ollama", "Ollama", ProviderTemplateGroups.Local, null, "openai-chat", "http://localhost:11434/v1", null, false, true),
        new("future", "future", "Future", "space", null, "openai-chat", "https://api.future.example", "https://future.example/keys", true, false),
    ];

    private static ProviderEditorViewModel New(FakeCommands commands, IReadOnlyCollection<string>? existing = null, int discoverable = 0) =>
        new(commands, "dev", Templates, existing ?? [], discoverable, v => v == ProviderTemplateVariants.Anthropic ? "Anthropic 兼容" : v);

    private static ProviderTemplateItem Item(ProviderEditorViewModel vm, string id) => vm.Groups.SelectMany(g => g.Items).First(i => i.Id == id);

    [Fact]
    public void Groups_start_with_popular_then_follow_the_fixed_order_and_end_with_custom()
    {
        var vm = New(new FakeCommands());

        Assert.Equal(ProviderEditorStep.Pick, vm.Step);
        Assert.Equal(
            [ProviderTemplateItems.FeaturedGroup, ProviderTemplateGroups.International, ProviderTemplateGroups.China, ProviderTemplateGroups.Aggregator, ProviderTemplateItems.OtherGroup],
            vm.Groups.Select(g => g.Key));
        Assert.Equal(["anthropic", "openai", "deepseek", "ollama"], vm.Groups[0].Items.Select(i => i.Id));
        // 常用里的不重复出现;认不出的分组进「其他」,「自定义」在最后
        Assert.Equal(["xai"], vm.Groups[1].Items.Select(i => i.Id));
        Assert.Equal(["future", ProviderTemplateItems.CustomId], vm.Groups[^1].Items.Select(i => i.Id));
        Assert.True(vm.Groups[^1].Items[^1].IsCustom);
    }

    [Fact]
    public void Searching_matches_names_addresses_and_endpoint_labels()
    {
        var vm = New(new FakeCommands());

        vm.Query = "deep";
        Assert.Equal(["deepseek", "deepseek-anthropic"], Assert.Single(vm.Groups).Items.Select(i => i.Id));

        vm.Query = "openrouter.ai";
        Assert.Equal(["openrouter"], Assert.Single(vm.Groups).Items.Select(i => i.Id));

        vm.Query = "兼容"; // 端点标签的本地化文字
        Assert.Equal(["deepseek-anthropic"], Assert.Single(vm.Groups).Items.Select(i => i.Id));

        vm.Query = "nothing-like-this";
        Assert.Empty(vm.Groups);
        Assert.True(vm.HasNoResults);
    }

    [Fact]
    public void Picking_a_template_fills_the_form_and_folds_the_advanced_fields()
    {
        var vm = New(new FakeCommands());

        vm.PickCommand.Execute(Item(vm, "deepseek-anthropic"));

        Assert.Equal(ProviderEditorStep.Form, vm.Step);
        Assert.Equal(("deepseek-anthropic", "DeepSeek", "anthropic", "https://api.deepseek.com/anthropic"), (vm.Id, vm.Name, vm.Protocol, vm.BaseUrl));
        Assert.False(vm.ShowAdvanced);
        Assert.True(vm.CanCollapseAdvanced);
        Assert.True(vm.NeedsKey);
        Assert.Equal("https://platform.deepseek.com/api_keys", vm.KeyUrl);
        Assert.Equal("api.deepseek.com", vm.AdvancedSummary);
    }

    [Fact]
    public void The_suggested_id_never_collides_with_an_existing_provider()
    {
        var vm = New(new FakeCommands(), existing: ["deepseek"]);

        vm.PickCommand.Execute(Item(vm, "deepseek"));

        Assert.Equal("deepseek-2", vm.Id);
    }

    [Fact]
    public void Custom_opens_every_field_empty()
    {
        var vm = New(new FakeCommands());

        vm.PickCustomCommand.Execute(null);

        Assert.True(vm.IsCustom);
        Assert.True(vm.ShowAdvanced);
        Assert.False(vm.CanCollapseAdvanced);
        Assert.Equal(("", "", ""), (vm.Id, vm.Name, vm.BaseUrl));
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Back_returns_to_the_grid_and_drops_the_typed_key()
    {
        var vm = New(new FakeCommands());
        vm.PickCommand.Execute(Item(vm, "openai"));
        vm.ApiKey = "sk-typed";

        vm.BackCommand.Execute(null);

        Assert.Equal(ProviderEditorStep.Pick, vm.Step);
        Assert.Equal("", vm.ApiKey);
    }

    [Fact]
    public async Task A_template_that_needs_a_key_is_not_saved_without_one()
    {
        var commands = new FakeCommands();
        var vm = New(commands);
        vm.PickCommand.Execute(Item(vm, "anthropic"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ProviderEditorViewModel.Errors.KeyRequired, vm.ErrorKey);
        Assert.Empty(commands.Upserts);
    }

    [Fact]
    public async Task Saving_writes_the_provider_stores_the_key_tests_it_and_closes()
    {
        var commands = new FakeCommands();
        var vm = New(commands);
        var closed = 0;
        vm.Completed += () => closed++;
        vm.PickCommand.Execute(Item(vm, "deepseek"));
        vm.ApiKey = "  sk-deepseek-1234  ";

        await vm.SaveCommand.ExecuteAsync(null);

        var upsert = Assert.Single(commands.Upserts);
        Assert.Equal(("deepseek", "DeepSeek", "openai-chat", "https://api.deepseek.com"), (upsert.Id, upsert.Name, upsert.Protocol, upsert.BaseUrl));
        Assert.Equal([("deepseek", "sk-deepseek-1234")], commands.Keys);
        Assert.Equal(["deepseek"], commands.Tests);
        Assert.Equal("", vm.ApiKey); // 只写不读:送出就清空
        Assert.Equal("deepseek", vm.SavedProviderId);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task A_local_service_saves_without_a_key()
    {
        var commands = new FakeCommands();
        var vm = New(commands);
        vm.PickCommand.Execute(Item(vm, "ollama"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsLocalService);
        Assert.Single(commands.Upserts);
        Assert.Empty(commands.Keys);
        Assert.Equal("ollama", vm.SavedProviderId);
    }

    [Fact]
    public async Task A_failed_connection_test_keeps_the_dialog_open_and_retrying_updates_the_same_provider()
    {
        var commands = new FakeCommands { OnTest = _ => new ProviderTestResultDto(false, null, 401, "providers.fetchRejected", null) };
        var vm = New(commands);
        var closed = 0;
        vm.Completed += () => closed++;
        vm.PickCommand.Execute(Item(vm, "openai"));
        vm.ApiKey = "sk-wrong-0000";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.TestFailed);
        Assert.Equal("providers.fetchRejected", vm.TestErrorKey);
        Assert.Equal(0, closed);
        Assert.Equal("openai", vm.SavedProviderId);
        Assert.False(vm.CanEditId); // 已经建出来了:id 锁住、不能再换模版
        vm.BackCommand.Execute(null);
        Assert.Equal(ProviderEditorStep.Form, vm.Step);

        // 改了 key 再试:还是同一个 id(更新,不是再建一个),这次不要求必须填 key
        commands.OnTest = _ => new ProviderTestResultDto(true, 80, 200, null, null);
        vm.ApiKey = "sk-right-1111";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["openai", "openai"], commands.Upserts.Select(u => u.Id));
        Assert.False(vm.TestFailed);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Keep_closes_after_a_failed_test()
    {
        var commands = new FakeCommands { OnTest = _ => new ProviderTestResultDto(false, null, null, "providers.fetchFailed", null) };
        var vm = New(commands);
        var closed = 0;
        vm.Completed += () => closed++;
        vm.PickCommand.Execute(Item(vm, "ollama"));
        await vm.SaveCommand.ExecuteAsync(null);

        vm.KeepCommand.Execute(null);

        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task A_rejected_write_shows_the_device_reason_and_does_not_test()
    {
        var commands = new FakeCommands { UpsertResult = new(ActionOutcome.Invalid, ErrorKey: "providers.invalidBaseUrl") };
        var vm = New(commands);
        vm.PickCustomCommand.Execute(null);
        vm.Id = "gw";
        vm.Name = "Gateway";
        vm.BaseUrl = "not a url";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("providers.invalidBaseUrl", vm.ErrorKey);
        Assert.Empty(commands.Tests);
        Assert.Null(vm.SavedProviderId);
    }

    [Fact]
    public async Task A_taken_id_is_caught_before_writing()
    {
        var commands = new FakeCommands();
        var vm = New(commands, existing: ["gw"]);
        vm.PickCustomCommand.Execute(null);
        vm.Id = "gw";
        vm.Name = "Gateway";
        vm.BaseUrl = "https://gw.example.com/v1";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ProviderEditorViewModel.Errors.IdTaken, vm.ErrorKey);
        Assert.Empty(commands.Upserts);
    }

    [Fact]
    public void The_discovery_banner_shows_only_on_the_grid_when_something_can_be_imported()
    {
        var none = New(new FakeCommands(), discoverable: 0);
        var some = New(new FakeCommands(), discoverable: 3);
        var asked = 0;
        some.DiscoveryRequested += () => asked++;

        Assert.False(none.ShowsDiscoveryBanner);
        Assert.True(some.ShowsDiscoveryBanner);
        some.GoToDiscoveryCommand.Execute(null);
        Assert.Equal(1, asked);
        some.PickCommand.Execute(Item(some, "openai"));
        Assert.False(some.ShowsDiscoveryBanner);
    }

    [Fact]
    public void Only_what_the_user_types_marks_the_form_as_edited()
    {
        var vm = New(new FakeCommands());

        vm.PickCommand.Execute(Item(vm, "deepseek")); // 模版填进去的不算
        Assert.False(vm.IsDirty);

        vm.ApiKey = "sk-1";
        Assert.True(vm.IsDirty);

        vm.BackCommand.Execute(null); // 回到网格就清掉
        Assert.False(vm.IsDirty);

        var edit = new ProviderEditorViewModel(new FakeCommands(), "dev", new ProviderDto("x", "X", "openai-chat", "https://x.example", 50, true, "5m", null, false, null), null);
        Assert.False(edit.IsDirty);
        edit.BaseUrl = "https://y.example";
        Assert.True(edit.IsDirty);
    }

    [Fact]
    public void An_old_device_without_templates_goes_straight_to_the_custom_form()
    {
        var vm = new ProviderEditorViewModel(new FakeCommands(), "dev", [], []);

        Assert.Equal(ProviderEditorStep.Form, vm.Step);
        Assert.True(vm.IsCustom);
        Assert.False(vm.ShowsBack);
    }

    [Fact]
    public async Task Editing_starts_on_the_form_keeps_the_id_and_does_not_require_a_new_key()
    {
        var commands = new FakeCommands();
        var provider = new ProviderDto("deepseek", "DeepSeek", "openai-chat", "https://api.deepseek.com", 20, true, "5m", """{"x":1}""", true, null);
        var vm = new ProviderEditorViewModel(commands, "dev", provider, Templates[3]);

        Assert.Equal(ProviderEditorStep.Form, vm.Step);
        Assert.False(vm.IsNew);
        Assert.False(vm.ShowsBack);
        Assert.False(vm.CanEditId);
        Assert.True(vm.ShowAdvanced);
        Assert.Equal("deepseek", vm.Template?.Service); // logo、「获取 key」跟着认出来的模版

        vm.Name = "DeepSeek 主力";
        await vm.SaveCommand.ExecuteAsync(null);

        var upsert = Assert.Single(commands.Upserts);
        Assert.Equal(("deepseek", "DeepSeek 主力", 20, """{"x":1}"""), (upsert.Id, upsert.Name, upsert.Priority, upsert.ProtocolOptionsJson));
        Assert.Empty(commands.Keys); // 留空 = 不改 key
    }

    [Fact]
    public async Task Switching_provider_kind_filters_templates_switches_available_protocols_and_saves_kind()
    {
        ProviderTemplateDto[] mixedTemplates =
        [
            .. Templates,
            new("volcengine-image", "volcengine", "火山方舟", ProviderTemplateGroups.China, null, "volcengine-images", "https://ark.cn-beijing.volces.com/api/v3",
                "https://console.volcengine.com/ark", true, true, Kind: "image"),
            new("volcengine-video", "volcengine", "火山方舟", ProviderTemplateGroups.China, null, "volcengine-videos", "https://ark.cn-beijing.volces.com/api/v3",
                "https://console.volcengine.com/ark", true, true, Kind: "video"),
            new("openai-audio", "openai", "OpenAI", ProviderTemplateGroups.International, null, "openai-audio", "https://api.openai.com/v1",
                "https://platform.openai.com/api-keys", true, true, Kind: "audio"),
        ];
        var commands = new FakeCommands();
        var vm = new ProviderEditorViewModel(commands, "dev", mixedTemplates, []);

        vm.Kind = "image";
        Assert.Equal(["volcengine-image", ProviderTemplateItems.CustomId], vm.Groups.SelectMany(g => g.Items).Select(i => i.Id).ToArray());
        Assert.Equal(["openai-images", "volcengine-images"], vm.AvailableProtocols);
        Assert.Equal("openai-images", vm.Protocol);

        vm.PickCommand.Execute(Item(vm, "volcengine-image"));
        Assert.Equal(("volcengine-image", "image", "volcengine-images"), (vm.Id, vm.Kind, vm.Protocol));
        vm.ApiKey = "ark-key-8888";
        await vm.SaveCommand.ExecuteAsync(null);

        var upsert = Assert.Single(commands.Upserts);
        Assert.Equal(("volcengine-image", "volcengine-images", "image"), (upsert.Id, upsert.Protocol, upsert.Kind));

        var videoVm = new ProviderEditorViewModel(new FakeCommands(), "dev", mixedTemplates, [], initialKind: "video");
        Assert.Equal("video", videoVm.Kind);
        Assert.Equal(["openai-videos", "volcengine-videos"], videoVm.AvailableProtocols);
        Assert.Equal("openai-videos", videoVm.Protocol);
    }
}
