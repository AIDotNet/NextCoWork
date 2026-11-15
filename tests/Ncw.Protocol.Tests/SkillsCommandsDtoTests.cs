using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// Skills / 命令的线上形状:每个类型往返、方法表声明齐全(种类 / 角色 / 能力 / 承载)。冻结的向量在 <see cref="GoldenV1Skills"/>(只增向量,不改已冻结的旧向量);
/// 这批只新增方法和载荷类型,没改帧格式也没改任何已冻结类型的键,所以仍是协议 v1。这些测试失败说明某个 DTO 的键名 / 形状变了,必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
public class SkillsCommandsDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static SkillInfoDto Skill(string name = "commit") => new(
        name, name, "写提交信息", "开发工具", SkillSourceKinds.Zip, SkillScopes.Global, null, true, true, false, "1.0.0", new string('a', 64), "/data/skills/" + name, 3, 1_700_000_000_000);

    [Fact]
    public void Every_skills_payload_round_trips()
    {
        var info = RoundTrip(new SkillsListResult([Skill(), Skill("review") with { SourceKind = SkillSourceKinds.Plugin, Scope = SkillScopes.Plugin, PluginId = "acme.pdf", ClientAssetsOnly = true }]));
        Assert.Equal(["commit", "review"], info.Skills.Select(s => s.Name));
        Assert.Equal(("acme.pdf", true), (info.Skills[1].PluginId, info.Skills[1].ClientAssetsOnly));

        var diagnostics = RoundTrip(new SkillsDiagnosticsResult([new SkillDiagnosticDto("/p/x", "skills.outsideRoot", "outside", new Dictionary<string, string> { ["name"] = "x" })]));
        Assert.Equal(("skills.outsideRoot", "x"), (diagnostics.Diagnostics[0].Key, diagnostics.Diagnostics[0].Params!["name"]));

        var global = RoundTrip(new SkillsSetGlobalEnabledParams("commit", false, "ws"));
        Assert.Equal(("commit", false, "ws"), (global.SkillId, global.Enabled, global.WorkspaceId));
        var active = RoundTrip(new SkillsSetWorkspaceActiveParams("commit", "ws", true));
        Assert.Equal(("commit", "ws", true), (active.SkillId, active.WorkspaceId, active.Active));

        var zip = RoundTrip(new SkillsInstallZipParams([1, 2, 3, 0, 255], "demo-v1.zip", "project", "ws"));
        Assert.Equal([1, 2, 3, 0, 255], zip.Zip);
        Assert.Equal(("demo-v1.zip", "project", "ws"), (zip.FileName, zip.Scope, zip.WorkspaceId));
        var market = RoundTrip(new SkillsInstallMarketParams("pdf", null, "global"));
        Assert.Equal(("pdf", null, "global", null), (market.Slug, market.Version, market.Scope, market.WorkspaceId));
        var installed = RoundTrip(new SkillsInstallResult(Skill(), [Skill()]));
        Assert.Equal("commit", installed.Installed.Name);
        Assert.Single(installed.Skills);
        var uninstall = RoundTrip(new SkillsUninstallParams("commit"));
        Assert.Equal(("commit", null, null), (uninstall.SkillId, uninstall.Scope, uninstall.WorkspaceId));
    }

    [Fact]
    public void Market_payloads_round_trip_and_optional_fields_stay_null()
    {
        var item = new SkillMarketItemDto("pdf", "pdf", "PDF", "d", "docs", "https://nextco.work/i.png", "Acme", "1.0.0", null, 123, 7, null);
        var list = RoundTrip(new SkillsMarketListResult([item]));
        Assert.Equal(("PDF", "https://nextco.work/i.png", 123L, 7, null), (list.Items[0].DisplayName, list.Items[0].IconUrl, list.Items[0].FileSize, list.Items[0].DownloadCount, list.Items[0].TriggerCount));
        Assert.Equal(["a", "b"], RoundTrip(new SkillsMarketCategoriesResult(["a", "b"])).Categories);
        var detail = RoundTrip(new SkillMarketDetailDto(item, [new SkillMarketVersionDto("1.0.0", "first", "abc", 10)]));
        Assert.Equal(("1.0.0", "first"), (detail.Versions[0].Version, detail.Versions[0].Changelog));
        var query = RoundTrip(new SkillsMarketListParams("pd"));
        Assert.Equal(("pd", null), (query.Query, query.Category));
        Assert.Equal("pdf", RoundTrip(new SkillsMarketDetailParams("pdf")).Slug);
        Assert.Null(RoundTrip(new SkillsScopeParams()).WorkspaceId);
    }

    [Fact]
    public void Every_commands_payload_round_trips()
    {
        var list = RoundTrip(new CommandsListResult([new CommandDto("init", "Analyze", "Run $ARGUMENTS", CommandScopes.Builtin, "", "[update]")]));
        Assert.Equal(("init", "Run $ARGUMENTS", "[update]"), (list.Commands[0].Name, list.Commands[0].Prompt, list.Commands[0].ArgumentHint));
        var all = RoundTrip(new CommandsListAllResult([new CommandItemDto("review", "d", CommandScopes.Project, "/p/review.md", null, false)]));
        Assert.Equal(("project", false, null), (all.Commands[0].Scope, all.Commands[0].Enabled, all.Commands[0].ArgumentHint));
        Assert.Equal("commands.promptEmpty", RoundTrip(new CommandsDiagnosticsResult([new CommandDiagnosticDto("/p", "commands.promptEmpty", "empty")])).Diagnostics[0].Key);
        var set = RoundTrip(new CommandsSetEnabledParams("init", false, "ws"));
        Assert.Equal(("init", false, "ws"), (set.Name, set.Enabled, set.WorkspaceId));
        Assert.Null(RoundTrip(new CommandsScopeParams()).WorkspaceId);
    }

    [Fact]
    public void Every_skills_and_commands_method_declares_kind_role_capability_and_transport()
    {
        Assert.Equal(10, SkillsMethods.All.Count);
        Assert.Equal(4, CommandsMethods.All.Count);
        var names = SkillsMethods.All.Concat(CommandsMethods.All).Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(
            ["skills.list", "skills.diagnostics", "skills.setGlobalEnabled", "skills.setWorkspaceActive", "skills.installZip", "skills.installMarket", "skills.uninstall", "skills.marketList", "skills.marketCategories", "skills.marketDetail"],
            SkillsMethods.All.Select(m => m.Name));
        Assert.Equal(["commands.list", "commands.diagnostics", "commands.listAll", "commands.setEnabled"], CommandsMethods.All.Select(m => m.Name));

        foreach (var method in SkillsMethods.All)
        {
            Assert.Equal(Capabilities.Skills, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
        }

        foreach (var method in CommandsMethods.All)
        {
            Assert.Equal(Capabilities.Commands, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
        }

        (MethodDef Method, MethodKind Kind, Role Role)[] expected =
        [
            (SkillsMethods.List, MethodKind.Query, Role.Viewer),
            (SkillsMethods.Diagnostics, MethodKind.Query, Role.Viewer),
            (SkillsMethods.SetGlobalEnabled, MethodKind.Command, Role.Owner),
            (SkillsMethods.SetWorkspaceActive, MethodKind.Command, Role.Member),
            (SkillsMethods.InstallZip, MethodKind.Command, Role.Owner),
            (SkillsMethods.InstallMarket, MethodKind.Command, Role.Owner),
            (SkillsMethods.Uninstall, MethodKind.Command, Role.Owner),
            (SkillsMethods.MarketList, MethodKind.Query, Role.Member),
            (SkillsMethods.MarketCategories, MethodKind.Query, Role.Member),
            (SkillsMethods.MarketDetail, MethodKind.Query, Role.Member),
            (CommandsMethods.List, MethodKind.Query, Role.Viewer),
            (CommandsMethods.Diagnostics, MethodKind.Query, Role.Viewer),
            (CommandsMethods.ListAll, MethodKind.Query, Role.Viewer),
            (CommandsMethods.SetEnabled, MethodKind.Command, Role.Owner),
        ];
        foreach (var (method, kind, role) in expected)
        {
            Assert.Equal((kind, role), (method.Kind, method.MinRole));
        }

        Assert.Equal("skills", Capabilities.Skills);
        Assert.Equal("commands", Capabilities.Commands);
    }

    [Fact]
    public void An_install_zip_request_with_the_maximum_payload_fits_in_one_frame()
    {
        var bytes = FrameCodec.EncodePayload(new SkillsInstallZipParams(new byte[SkillsLimits.MaxZipBytes], "x.zip", "global"), PayloadShapes.GetShape<SkillsInstallZipParams>());

        Assert.True(bytes.Length < ProtocolInfo.MaxFrameBytes - 1024);
    }

    private static Dictionary<string, string> Vectors() => new()
    {
        ["SkillsList"] = Hex(new SkillsListResult([Skill()])),
        ["SkillsListPlugin"] = Hex(new SkillsListResult([Skill("pdf") with { SourceKind = SkillSourceKinds.Plugin, Scope = SkillScopes.Plugin, PluginId = "acme.pdf", ClientAssetsOnly = true, Version = null, Sha256 = null }])),
        ["SkillsDiagnostics"] = Hex(new SkillsDiagnosticsResult([new SkillDiagnosticDto("/p/x", "skills.outsideRoot", "outside", new Dictionary<string, string> { ["name"] = "x" })])),
        ["SetGlobalEnabledParams"] = Hex(new SkillsSetGlobalEnabledParams("commit", false, "ws")),
        ["SetWorkspaceActiveParams"] = Hex(new SkillsSetWorkspaceActiveParams("commit", "ws", true)),
        ["InstallZipParams"] = Hex(new SkillsInstallZipParams([1, 2, 3, 0, 255], "demo-v1.zip", "project", "ws")),
        ["InstallMarketParams"] = Hex(new SkillsInstallMarketParams("pdf", "1.0.0", "global")),
        ["InstallResult"] = Hex(new SkillsInstallResult(Skill(), [Skill()])),
        ["UninstallParams"] = Hex(new SkillsUninstallParams("commit", "global", "ws")),
        ["MarketList"] = Hex(new SkillsMarketListResult([new SkillMarketItemDto("pdf", "pdf", "PDF", "d", "docs", "https://nextco.work/i.png", "Acme", "1.0.0", null, 123, 7, null)])),
        ["MarketDetail"] = Hex(new SkillMarketDetailDto(
            new SkillMarketItemDto("pdf", "pdf", "PDF", "d", "docs", null, null, "1.0.0", null, null, null, null), [new SkillMarketVersionDto("1.0.0", "first", "abc", 10)])),
        ["CommandsList"] = Hex(new CommandsListResult([new CommandDto("init", "Analyze", "Run $ARGUMENTS", CommandScopes.Builtin, "", "[update]")])),
        ["CommandsListAll"] = Hex(new CommandsListAllResult([new CommandItemDto("review", "d", CommandScopes.Project, "/p/review.md", null, false)])),
        ["CommandsSetEnabledParams"] = Hex(new CommandsSetEnabledParams("init", false, "ws")),
    };

    [Fact]
    public void Golden_vectors_v1_skills_and_commands()
    {
        var v = Vectors();
        Assert.Equal(GoldenV1Skills.SkillsList, v["SkillsList"]);
        Assert.Equal(GoldenV1Skills.SkillsListPlugin, v["SkillsListPlugin"]);
        Assert.Equal(GoldenV1Skills.SkillsDiagnostics, v["SkillsDiagnostics"]);
        Assert.Equal(GoldenV1Skills.SetGlobalEnabledParams, v["SetGlobalEnabledParams"]);
        Assert.Equal(GoldenV1Skills.SetWorkspaceActiveParams, v["SetWorkspaceActiveParams"]);
        Assert.Equal(GoldenV1Skills.InstallZipParams, v["InstallZipParams"]);
        Assert.Equal(GoldenV1Skills.InstallMarketParams, v["InstallMarketParams"]);
        Assert.Equal(GoldenV1Skills.InstallResult, v["InstallResult"]);
        Assert.Equal(GoldenV1Skills.UninstallParams, v["UninstallParams"]);
        Assert.Equal(GoldenV1Skills.MarketList, v["MarketList"]);
        Assert.Equal(GoldenV1Skills.MarketDetail, v["MarketDetail"]);
        Assert.Equal(GoldenV1Skills.CommandsList, v["CommandsList"]);
        Assert.Equal(GoldenV1Skills.CommandsListAll, v["CommandsListAll"]);
        Assert.Equal(GoldenV1Skills.CommandsSetEnabledParams, v["CommandsSetEnabledParams"]);
    }
}
