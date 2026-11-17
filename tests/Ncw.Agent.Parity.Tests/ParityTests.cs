using Ncw.Agent.Parity.Tests.Compare;
using Ncw.Agent.Parity.Tests.Rig;

namespace Ncw.Agent.Parity.Tests;

/// <summary>
/// 回放对照(设计 01 §4、05 测试策略):同一份录制输入(上游 SSE 原文、工具结果、用户操作)分别在 2.x(录制时)和 C#(这里)上回放,
/// 比较事件序列、落库行和发给上游的请求。预期差异只能由 <see cref="Rules"/> 里带文档引用的规则放行;其余差异让测试失败,并给出可读的 diff。
/// </summary>
public sealed class ParityTests
{
    public static IEnumerable<object[]> Scenarios() => Recording.Ids();

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_csharp_agent_replays_the_recording_like_2x(string id)
    {
        var recording = Recording.ById(id);
        Assert.SkipWhen(OperatingSystem.IsWindows() && recording.NeedsPosixShell, "this recording drives hooks / MCP servers through a POSIX shell");
        var replayed = await Replay.RunAsync(recording);
        if (Environment.GetEnvironmentVariable("NCW_PARITY_DUMP") is { Length: > 0 } dump)
        {
            // 调试用:把 C# 回放的原始规范化结果写出来,和录制里的 expected 对着看。
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{id}.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["runs"] = replayed.Runs.DeepClone(),
                ["interactions"] = replayed.Interactions.DeepClone(),
                ["files"] = replayed.Files.DeepClone(),
                ["events"] = replayed.Events.DeepClone(),
                ["rows"] = replayed.Rows.DeepClone(),
                ["requests"] = replayed.Requests.DeepClone(),
                ["diagnostics"] = new System.Text.Json.Nodes.JsonArray([.. replayed.Diagnostics.Select(d => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(d)!)]),
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        var (mismatches, _) = Comparer.Compare(recording, replayed);
        if (mismatches.Count > 0)
        {
            Assert.Fail($"""
                Scenario '{id}' ({recording.Description})
                differs from the 2.x recording in ways that no registered rule explains:

                {string.Join("\n\n", mismatches.Select(m => $"── {m.Section} (- 2.x only, + C# only) ──\n{m.Report}"))}
                """);
        }
    }
}

/// <summary>按录制的种类选台架:单会话循环层回放(<see cref="ReplayRig"/>)或整机回放(<see cref="OrchestrationRig"/>)。</summary>
public static class Replay
{
    public static async Task<Replayed> RunAsync(Recording recording)
    {
        if (recording.Kind == "orchestration")
        {
            using var rig = new OrchestrationRig(recording);
            return await rig.RunAsync();
        }

        using var single = new ReplayRig(recording);
        return await single.RunAsync();
    }
}
