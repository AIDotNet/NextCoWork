using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// Hooks 的线上形状:每个类型往返、方法表声明齐全(种类 / 角色 / 能力 / 承载)、环境变量的值不进任何「读」方向的 DTO。
/// 冻结的向量在 <see cref="GoldenV1Hooks"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class HooksDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static HookDto Hook() => new(
        "h1", HookNames.ScopeProject, "PreToolUse", HookNames.TypeCommand, "Bash", true, 10_000, "guard", "echo guard", null, null, null, ["TOKEN"], "/p/.next-cowork/settings.local.json");

    private static HookUpsertDto Upsert() => new(
        null, "Stop", HookNames.TypeCommand, null, true, 60_000, "notify", "echo done", null, null, null, [new HookEnvVarDto("TOKEN", "s3cret"), new HookEnvVarDto("KEPT", null)]);

    [Fact]
    public void Every_hooks_dto_round_trips()
    {
        Assert.Equal(Hex(Hook()), Hex(RoundTrip(Hook())));
        Assert.Equal(["TOKEN"], RoundTrip(new HooksListResult([Hook()])).Hooks[0].EnvNames);
        var upsert = RoundTrip(new HooksSaveParams(HookNames.ScopeGlobal, null, Upsert()));
        Assert.Equal("s3cret", upsert.Hook.Env![0].Value);
        Assert.Null(upsert.Hook.Env[1].Value); // null = 沿用已存的值
        Assert.Equal("hooks.warn.weakMatcher", RoundTrip(new HooksSaveResult(Hook(), "hooks.warn.weakMatcher")).WarningKey);
        Assert.Equal("h1", RoundTrip(new HooksDeleteParams(HookNames.ScopeProject, "ws", "h1")).Id);
        Assert.False(RoundTrip(new HooksSetEnabledParams(HookNames.ScopeProject, "ws", "h1", false)).Enabled);
        Assert.Equal("ws", RoundTrip(new HooksListParams("ws")).WorkspaceId);
        Assert.Null(RoundTrip(new HooksListParams()).WorkspaceId);
        var diagnostics = RoundTrip(new HooksDiagnosticsResult([new HookDiagnosticDto("/p/s.json", "hooks.diagnostic.invalid", "2 entries ignored", new Dictionary<string, string> { ["count"] = "2" })]));
        Assert.Equal("2", diagnostics.Diagnostics[0].Params!["count"]);
        var report = RoundTrip(new HookRunReportDto("test", HookNames.ScopeGlobal, 2, "out", "err", 12, HookNames.OutcomeBlocked, HookNames.DecisionDeny, "no", null, null, null));
        Assert.Equal((2, "blocked", "deny"), (report.ExitCode, report.Outcome, report.Decision));
        Assert.Equal("met", RoundTrip(new HookRunReportDto("t", "global", 0, "", "", 1, "ok", PromptVerdict: HookNames.VerdictMet)).PromptVerdict);
    }

    [Fact]
    public void The_read_side_carries_only_variable_names_never_values()
    {
        // 读方向(列表 / 保存结果 / 诊断)的 DTO 里没有任何能装环境变量值的字段:值只在 hooks.save / hooks.test 的请求里单向写入
        var hook = Hook();
        var listJson = Convert.ToHexStringLower(FrameCodec.EncodePayload(new HooksListResult([hook]), PayloadShapes.GetShape<HooksListResult>()));

        Assert.DoesNotContain(Convert.ToHexStringLower("s3cret"u8), listJson);
        Assert.Equal(typeof(string[]), typeof(HookDto).GetProperty(nameof(HookDto.EnvNames))!.PropertyType);
        Assert.DoesNotContain(typeof(HookDto).GetProperties(), p => p.PropertyType == typeof(HookEnvVarDto) || p.PropertyType == typeof(HookEnvVarDto[]));
        Assert.DoesNotContain(typeof(HooksSaveResult).GetProperties(), p => p.PropertyType == typeof(HookEnvVarDto[]));
    }

    [Fact]
    public void Every_hooks_method_declares_kind_role_capability_and_transport_and_writes_are_owner_only()
    {
        Assert.Equal(6, HooksMethods.All.Count);
        Assert.Equal(HooksMethods.All.Count, HooksMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in HooksMethods.All)
        {
            Assert.Equal(Capabilities.Hooks, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            // 读 = member 起(命令文本可见,viewer 不给);写(含试运行:真的会执行命令)= owner,且是 command(带幂等键)
            Assert.Equal(method.Kind == MethodKind.Query, method.MinRole == Role.Member);
            Assert.Equal(method.Kind == MethodKind.Command, method.MinRole == Role.Owner);
        }

        Assert.Equal("hooks", Capabilities.Hooks);
        Assert.Equal(["hooks.list", "hooks.diagnostics", "hooks.save", "hooks.delete", "hooks.setEnabled", "hooks.test"], HooksMethods.All.Select(m => m.Name));
        Assert.Equal(MethodKind.Command, HooksMethods.Test.Kind); // 试运行执行命令
    }

    [Fact]
    public void Golden_vectors_v1_hooks()
    {
        Assert.Equal(GoldenV1Hooks.HookDto, Hex(Hook()));
        Assert.Equal(GoldenV1Hooks.ListResult, Hex(new HooksListResult([Hook()])));
        Assert.Equal(GoldenV1Hooks.SaveParams, Hex(new HooksSaveParams(HookNames.ScopeGlobal, null, Upsert())));
        Assert.Equal(GoldenV1Hooks.SaveResult, Hex(new HooksSaveResult(Hook(), "hooks.warn.weakMatcher")));
        Assert.Equal(GoldenV1Hooks.SetEnabledParams, Hex(new HooksSetEnabledParams(HookNames.ScopeProject, "ws", "h1", false)));
        Assert.Equal(GoldenV1Hooks.DiagnosticsResult, Hex(new HooksDiagnosticsResult(
            [new HookDiagnosticDto("/p/s.json", "hooks.diagnostic.invalid", "2 entries ignored", new Dictionary<string, string> { ["count"] = "2" })])));
        Assert.Equal(GoldenV1Hooks.RunReport, Hex(new HookRunReportDto("test", HookNames.ScopeGlobal, 2, "out", "err", 12, HookNames.OutcomeBlocked, HookNames.DecisionDeny, "no", null, null, null)));
    }
}
