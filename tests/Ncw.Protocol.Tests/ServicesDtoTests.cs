using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 杂项 Agent 服务(设置 / 用量 / 定时任务 / 存储 / 搜索 / 附件 / 输入框 / 小组件 / 网关)的线上形状:每个代表性载荷往返、方法表声明齐全(种类、角色、能力、承载;command 带幂等键由路由检查)、
/// 危险方法的角色与承载限制写在表里(而不是散落在 handler 里)、冻结向量(<see cref="GoldenV1Services"/>)。
/// 冻结的旧向量不动;这一批只新增方法 / 主题 / 载荷类型,没改帧格式,所以仍是协议 v1。
/// </summary>
public class ServicesDtoTests
{
    private static ModelPickDto Pick(string m) => new(m, "p1");

    private static DeviceSettingsDto Settings() => new(
        Pick("sonnet"), Pick("haiku"), Pick("haiku"), Pick("haiku"), "low", Pick("haiku"), Pick("haiku"), Pick("img"),
        "ask", "off", true, 32_000, true, false, true, false, "/bin/zsh", 30, 600, true, false, "https://searx.example",
        new SubagentSettingsDto(Pick("sonnet"), "auto", 4, 8), new ProxySettingsDto(true, "manual", "http", "127.0.0.1", 7890, true, "u", "localhost", true),
        new GatewaySettingsDto(false, false, 19836), new DataSettingsDto("/backups", "daily"), new PersonalizationSettingsDto("Ada", "engineer", "be brief"));

    private static ScheduledTaskDto Task() => new(
        "t1", "morning", "summarize", "ws1", "sonnet", "p1", new ScheduleRuleDto("weekly", Time: "09:30", Weekdays: [1, 3, 5]), "Asia/Shanghai",
        new RepeatWindowDto(true, "11:00", 30), true, 1_700_000_000_000, 1_699_000_000_000, 1_699_500_000_000, "user", 0);

    private static UsageAttemptDto Attempt() => new(
        "u1", 1_700_000_000_000, "r1", "ws1", "s1", 1, "p1", "Provider", "anthropic", "https://api.example", "sonnet", "claude-x", "claude-x-1", 100, 50, 10, 5, 0, null, false, 800, 120, true, 200, null, null, "end_turn", 1234, "USD", null, null, 2, 0);

    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("settings.state", new DeviceSettingsState(7, Settings())),
        ("settings.update", new SettingsUpdateParams(new DeviceSettingsPatchDto(MaxOutputTokens: 9000, Gateway: new GatewaySettingsPatchDto(Enabled: true), Data: new DataSettingsPatchDto(BackupDirectory: "/b")))),
        ("usage.summary", new UsageSummaryDto(10, 9, 1, 1000, 500, 100, 50, 0, 20, 1, 1500, 0.1, 800.5, 120.5, 7, 1, [new UsageCostTotalDto("USD", 12345)])),
        ("usage.attempts", new UsageAttemptsResult([Attempt()])),
        ("schedules.task", Task()),
        ("schedules.topic", new SchedulesTopicState(3, [Task()], [new ScheduledRunDto("run1", "t1", "s9", "scheduled", "done", 1_700_000_000_000, 1_700_000_001_000, 1_700_000_060_000, "ok", null)])),
        ("schedules.create", new ScheduledTaskInputDto("n", "p", "ws1", "sonnet", new ScheduleRuleDto("once", At: "2026-12-01T09:00"), Timezone: "UTC")),
        ("storage.stats", new StorageStatsDto(1000, 200, 300, 400, 5, 60, 7, 80, 2, 1_700_000_000_000)),
        ("storage.restorePreview", new RestorePreviewDto("/b/x.zip", new BackupManifestDto("3.0.0", 1_700_000_000_000, 12, 5, 60, true), "tok")),
        ("storage.cleanupPreview", new CleanupPreviewDto("age", 3, 30, 4, 4096, ["s9"], "tok2")),
        ("storage.exportChunk", new ExportChunkDto([1, 2, 3, 255], true)),
        ("storage.importPreview", new ImportPreviewDto("up1", 1, 1, 2, 3, 1, 1, 0, "tok3")),
        ("search.result", new SearchConversationsResult([new SearchHitDto("s1", "ws1", "m1", "Title", "…snippet…", 1_700_000_000_000)])),
        ("attachments.item", new AttachmentDto("a1", "session", "s1", "截图.png", "image/png", 2048, "abc", 1_700_000_000_000, "ncw://attachments/sessions/s1/01ABC.png")),
        ("attachments.chunk", new AttachmentsUploadChunkParams("up1", 4096, [9, 8, 7])),
        ("composer.complete", new ComposerCompleteParams("req1", "ws1", null, "fix the", new ComposerContextDto("proj", "main", ["a.cs"], ["/test"]))),
        ("widgets.action", new WidgetActionParams("s1", "c1", "act", "{\"x\":1}", true)),
        ("gateway.status", new GatewayStatusDto(true, 19836, "127.0.0.1", false, true, [new GatewayHealthDto("p1", true, 0.9, 0, null, 120, null, 1_700_000_000_000)], null)),
        ("gateway.key", new GatewayKeyDto("viewer", "0123abcd")),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    public static string HexOf(object value) => Convert.ToHexStringLower(Encode(value));

    private static byte[] Encode(object value) => value switch
    {
        DeviceSettingsState v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DeviceSettingsState>()),
        SettingsUpdateParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<SettingsUpdateParams>()),
        UsageSummaryDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<UsageSummaryDto>()),
        UsageAttemptsResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<UsageAttemptsResult>()),
        ScheduledTaskDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ScheduledTaskDto>()),
        SchedulesTopicState v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<SchedulesTopicState>()),
        ScheduledTaskInputDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ScheduledTaskInputDto>()),
        StorageStatsDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<StorageStatsDto>()),
        RestorePreviewDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<RestorePreviewDto>()),
        CleanupPreviewDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<CleanupPreviewDto>()),
        ExportChunkDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ExportChunkDto>()),
        ImportPreviewDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ImportPreviewDto>()),
        SearchConversationsResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<SearchConversationsResult>()),
        AttachmentDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<AttachmentDto>()),
        AttachmentsUploadChunkParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<AttachmentsUploadChunkParams>()),
        ComposerCompleteParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ComposerCompleteParams>()),
        WidgetActionParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WidgetActionParams>()),
        GatewayStatusDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<GatewayStatusDto>()),
        GatewayKeyDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<GatewayKeyDto>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        DeviceSettingsState => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DeviceSettingsState>())),
        SettingsUpdateParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<SettingsUpdateParams>())),
        UsageSummaryDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<UsageSummaryDto>())),
        UsageAttemptsResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<UsageAttemptsResult>())),
        ScheduledTaskDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ScheduledTaskDto>())),
        SchedulesTopicState => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<SchedulesTopicState>())),
        ScheduledTaskInputDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ScheduledTaskInputDto>())),
        StorageStatsDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<StorageStatsDto>())),
        RestorePreviewDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<RestorePreviewDto>())),
        CleanupPreviewDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<CleanupPreviewDto>())),
        ExportChunkDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ExportChunkDto>())),
        ImportPreviewDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ImportPreviewDto>())),
        SearchConversationsResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<SearchConversationsResult>())),
        AttachmentDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<AttachmentDto>())),
        AttachmentsUploadChunkParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<AttachmentsUploadChunkParams>())),
        ComposerCompleteParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ComposerCompleteParams>())),
        WidgetActionParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WidgetActionParams>())),
        GatewayStatusDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<GatewayStatusDto>())),
        GatewayKeyDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<GatewayKeyDto>())),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_sample_round_trips_to_the_same_bytes(string name)
    {
        var value = Samples().Single(s => s.Name == name).Value;
        var bytes = Encode(value);

        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(ReEncode(value, bytes)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_vectors_v1_services(string name) =>
        Assert.Equal(GoldenV1Services.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void A_media_capable_client_decodes_legacy_device_settings_and_a_legacy_patch()
    {
        var state = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Services.SettingsState), PayloadShapes.GetShape<DeviceSettingsState>());
        var update = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Services.SettingsUpdate), PayloadShapes.GetShape<SettingsUpdateParams>());

        Assert.Equal(new DeviceSettingsState(7, Settings()), state);
        Assert.Null(update.Patch.Video);
        Assert.Null(update.Patch.Audio);
    }

    [Fact]
    public void A_media_settings_patch_distinguishes_a_selected_model_clear_and_no_change()
    {
        var shape = PayloadShapes.GetShape<SettingsUpdateParams>();
        var write = new SettingsUpdateParams(new DeviceSettingsPatchDto(Video: new ModelPickDto("model", "provider"), Audio: ModelPickDto.None));

        var read = FrameCodec.DecodePayload(FrameCodec.EncodePayload(write, shape), shape);

        Assert.Equal(write, read);
        Assert.Null(read.Patch.Image);
    }

    [Fact]
    public void A_settings_patch_keeps_absent_keys_distinct_from_null_values_on_the_wire()
    {
        var patch = FrameCodec.DecodePayload(
            FrameCodec.EncodePayload(new SettingsUpdateParams(new DeviceSettingsPatchDto(MaxOutputTokens: 9000)), PayloadShapes.GetShape<SettingsUpdateParams>()),
            PayloadShapes.GetShape<SettingsUpdateParams>()).Patch;

        Assert.Equal(9000, patch.MaxOutputTokens);
        Assert.Null(patch.Gateway);
        Assert.Null(patch.DefaultModel);
    }

    // ───────────────────────── 方法表 ─────────────────────────

    private static readonly MethodDef[] All =
    [
        .. SettingsMethods.All, .. UsageMethods.All, .. SchedulesMethods.All, .. StorageMethods.All, .. SearchMethods.All,
        .. AttachmentsMethods.All, .. ComposerMethods.All, .. WidgetsMethods.All, .. GatewayMethods.All,
    ];

    [Fact]
    public void Every_method_declares_a_capability_and_a_unique_name_in_its_domain()
    {
        Assert.Equal(All.Length, All.Select(static m => m.Name).Distinct().Count());
        Assert.All(All, m => Assert.False(string.IsNullOrEmpty(m.Capability), m.Name));
        Assert.All(All, m => Assert.StartsWith(m.Capability + ".", m.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Dangerous_methods_need_the_owner_and_the_local_channel_as_the_table_says()
    {
        string[] dangerous = ["storage.backup.restore", "storage.backup.restorePreview", "storage.cleanup.execute", "storage.import.write", "storage.import.preview", "storage.import.apply", "storage.import.discard", "settings.proxy.setPassword", "gateway.getKey", "gateway.rotateKey", "gateway.setEnabled"];
        foreach (var name in dangerous)
        {
            var method = All.Single(m => m.Name == name);
            Assert.Equal(Role.Owner, method.MinRole);
            Assert.Equal(Transports.Local, method.Allowed);
        }

        // 所有写定时任务的都是 owner(定时 Run 用 full 权限)
        Assert.All(SchedulesMethods.All.Where(static m => m.Kind == MethodKind.Command), m => Assert.Equal(Role.Owner, m.MinRole));
        // 所有 command 方法的种类声明了(路由据此要求幂等键)
        Assert.All(All.Where(static m => m.Name.EndsWith(".update", StringComparison.Ordinal) || m.Name.EndsWith(".create", StringComparison.Ordinal)), m => Assert.Equal(MethodKind.Command, m.Kind));
    }
}
