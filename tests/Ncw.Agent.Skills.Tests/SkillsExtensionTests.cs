using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// Skills 扩展的会话集成(真 Storage + 假上游 + 真临时目录):系统提示词有目录但没有正文、模型调 <c>Skill</c> 工具拿到正文并带边界声明、压缩后附件带回调用过的正文、
/// 按工作区激活与全局开关过滤、每个 Run 重扫、扫描失败不拖垮 Run。
/// </summary>
public sealed class SkillsExtensionTests : IDisposable
{
    private readonly Fx _fx = new();
    private readonly MemorySkillStateStore _state = new();
    private Harness? _harness;

    public void Dispose()
    {
        _harness?.Dispose();
        _fx.Dispose();
    }

    private Harness Rig(IExecutionEnvironment? projectEnvironment = null)
    {
        var service = new SkillsService(new TestLocations(_fx, projectEnvironment), _state);
        return _harness = new Harness([service.CreateExtension("ws")]);
    }

    private void Global(string name, string description = "写提交信息", string body = "BODY-按 Conventional Commits 写。") =>
        Fx.Put(_fx.GlobalSkills, name, $"---\ndescription: {description}\n---\n{body}\n");

    private static string ToolResultText(Message m) => string.Concat(m.Parts.OfType<ToolResultPart>().Select(p => p.Output.Content));

    [Fact]
    public async Task Invariant_the_system_prompt_has_the_catalog_but_never_a_body()
    {
        Global("commit");
        var h = Rig();

        var ran = await h.RunAsync("hi");

        Assert.Equal(RunStatus.Done, ran.Status);
        var system = h.Llm.Requests[0].System;
        Assert.Contains("# Available Skills", system);
        Assert.Contains("- `commit` — 写提交信息", system);
        Assert.DoesNotContain("BODY-按 Conventional Commits", system);
        Assert.Contains("Skill", h.Llm.Requests[0].Tools.Select(t => t.ExternalName)); // 工具总是注册
    }

    [Fact]
    public async Task Without_any_skill_there_is_no_section_but_the_tool_stays()
    {
        var h = Rig();

        await h.RunAsync("hi");

        Assert.DoesNotContain("# Available Skills", h.Llm.Requests[0].System);
        Assert.Contains("Skill", h.Llm.Requests[0].Tools.Select(t => t.ExternalName));
    }

    [Fact]
    public async Task The_model_calls_the_skill_tool_and_gets_the_body_with_the_boundary_in_the_next_request()
    {
        Global("commit");
        var h = Rig();
        h.Llm.Turn(Up.Call("c1", "Skill", """{"name":"commit"}"""), Up.Text("done"));

        var ran = await h.RunAsync("commit this");

        Assert.Equal(RunStatus.Done, ran.Status);
        var second = h.Llm.Requests[1];
        var result = second.Messages.Select(ToolResultText).Single(t => t.Length > 0);
        Assert.Contains("# Skill: commit", result);
        Assert.Contains("BODY-按 Conventional Commits 写。", result);
        Assert.Contains("NOT A GRANT OF PERMISSION", result);
        // 正文只在 tool_result 里,系统提示词两个回合都没有
        Assert.DoesNotContain("BODY-按 Conventional Commits", second.System);
        // 落库的转录里也有(转录就是状态)
        Assert.Contains(h.Stored(), m => ToolResultText(m).Contains("BODY-按 Conventional Commits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invariant_the_catalog_is_fixed_for_the_whole_run_so_the_cache_prefix_holds_but_a_new_run_rescans()
    {
        Global("commit");
        var h = Rig();
        h.Llm.Turn(Up.Call("c1", "Skill", """{"name":"commit"}"""), Up.Text("done"));
        await h.RunAsync("first");
        Assert.Equal(h.Llm.Requests[0].System, h.Llm.Requests[1].System);

        Global("review", "审查代码");
        await h.RunAsync("second");

        Assert.DoesNotContain("`review`", h.Llm.Requests[1].System);
        Assert.Contains("`review` — 审查代码", h.Llm.Requests[2].System);
    }

    [Fact]
    public async Task A_skill_switched_off_globally_is_not_in_the_catalog_and_the_tool_does_not_find_it()
    {
        Global("commit");
        Global("review", "审查代码");
        _state.SetGlobalEnabled("commit", false);
        var h = Rig();
        h.Llm.Turn(Up.Call("c1", "Skill", """{"name":"commit"}"""), Up.Text("done"));

        await h.RunAsync("hi");

        Assert.DoesNotContain("`commit`", h.Llm.Requests[0].System);
        Assert.Contains("`review`", h.Llm.Requests[0].System);
        var result = h.Llm.Requests[1].Messages.Select(ToolResultText).Single(t => t.Length > 0);
        Assert.Contains("There is no Skill named \"commit\". Available: review", result);
    }

    [Fact]
    public async Task Workspace_selection_filters_the_catalog_empty_means_all_and_explicit_empty_means_none()
    {
        Global("commit");
        Global("review", "审查代码");
        var h = Rig();

        _state.SetSelection("ws", ["review"], explicitSelection: true);
        await h.RunAsync("one");
        Assert.DoesNotContain("`commit`", h.Llm.Requests[0].System);
        Assert.Contains("`review`", h.Llm.Requests[0].System);

        _state.SetSelection("ws", [], explicitSelection: false);
        await h.RunAsync("two");
        Assert.Contains("`commit`", h.Llm.Requests[1].System);
        Assert.Contains("`review`", h.Llm.Requests[1].System);

        _state.SetSelection("ws", [], explicitSelection: true);
        await h.RunAsync("three");
        Assert.DoesNotContain("# Available Skills", h.Llm.Requests[2].System);
    }

    [Fact]
    public async Task A_device_asset_skill_is_left_out_of_the_catalog_in_a_remote_project_and_the_tool_explains_why()
    {
        Global("needs-device");
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "needs-device", "scripts"));
        var h = Rig(new SshLikeEnvironment(_fx.Env));
        h.Llm.Turn(Up.Call("c1", "Skill", """{"name":"needs-device"}"""), Up.Text("done"));

        await h.RunAsync("hi");

        Assert.DoesNotContain("`needs-device`", h.Llm.Requests[0].System);
        Assert.Contains("remote host", h.Llm.Requests[1].Messages.Select(ToolResultText).Single(t => t.Length > 0));
    }

    [Fact]
    public async Task A_scan_that_cannot_reach_the_environment_does_not_stop_the_run_it_just_has_no_catalog()
    {
        Global("commit");
        var h = Rig(new DisconnectedEnvironment(_fx.Env));

        var ran = await h.RunAsync("hi");

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.DoesNotContain("# Available Skills", h.Llm.Requests[0].System);
        Assert.Contains(h.Log.Warnings, w => w.Contains("Skill catalog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invariant_after_compaction_the_skill_bodies_loaded_earlier_come_back_as_attachments()
    {
        Global("commit");
        var h = Rig();
        h.Llm.Turn(Up.Call("c1", "Skill", """{"name":"commit"}"""), Up.Text("done"));
        await h.RunAsync("commit this");

        var outcome = await h.Session.CompactNowAsync(h.Model, ThinkingLevel.Auto);

        Assert.Null(outcome.Error);
        var boundaryText = string.Concat(outcome.Boundary!.Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Contains("The Skill \"commit\" was loaded earlier in this conversation", boundaryText);
        Assert.Contains("BODY-按 Conventional Commits 写。", boundaryText);
    }

    // ───────────────────────── 压缩附件的纯函数 ─────────────────────────

    private static Message Call(string id, string call, string skill) =>
        new(new MessageId(id), Role.Assistant, [new ToolCallPart(call, "Skill", System.Text.Json.JsonDocument.Parse($$"""{"name":"{{skill}}"}""").RootElement.Clone())], DateTimeOffset.UnixEpoch);

    private static Message Result(string id, string call, string content, bool error = false) =>
        new(new MessageId(id), Role.User, [new ToolResultPart(call, new ToolOutput(content), error)], DateTimeOffset.UnixEpoch);

    [Fact]
    public void Attachments_take_the_latest_successful_body_per_skill_newest_first()
    {
        var history = new[]
        {
            Call("a1", "c1", "commit"), Result("r1", "c1", "old commit body"),
            Call("a2", "c2", "review"), Result("r2", "c2", "review body"),
            Call("a3", "c3", "commit"), Result("r3", "c3", "new commit body"),
            Call("a4", "c4", "broken"), Result("r4", "c4", "There is no Skill", error: true),
        };

        var texts = SkillsExtension.Attachments.AfterCompaction(history);

        Assert.Equal(2, texts.Count);
        Assert.Contains("\"commit\"", texts[0]);
        Assert.Contains("new commit body", texts[0]);
        Assert.DoesNotContain("old commit body", string.Concat(texts));
        Assert.Contains("\"review\"", texts[1]);
        Assert.All(texts, t => Assert.StartsWith("<system-reminder>", t));
    }

    [Fact]
    public void Attachments_are_clipped_per_skill_and_stop_at_the_total_budget()
    {
        var history = new List<Message>();
        for (var i = 0; i < 12; i++)
        {
            history.Add(Call($"a{i}", $"c{i}", $"skill-{i}"));
            history.Add(Result($"r{i}", $"c{i}", new string('x', 60_000)));
        }

        var texts = SkillsExtension.Attachments.AfterCompaction(history);

        Assert.True(texts.Count is > 1 and < 12);
        Assert.All(texts, t => Assert.True(t.Length < 30_000));
        Assert.Contains("truncated", texts[0]);
    }

    [Fact]
    public void No_skill_calls_means_no_attachments()
    {
        Assert.Empty(SkillsExtension.Attachments.AfterCompaction([new Message(new MessageId("u"), Role.User, [new TextPart("hi")], DateTimeOffset.UnixEpoch)]));
    }
}
