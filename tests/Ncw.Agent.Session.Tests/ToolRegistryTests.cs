using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Session.Tests.Support;

namespace Ncw.Agent.Session.Tests;

/// <summary>
/// 统一工具注册表(03 §1):按内部 id 存、同来源同 id 替换(MCP 重连)、别的来源同 id 丢弃后来者(内置不可替换)、按来源批量注销、外部名白名单 + 超长 / 冲突用 fnv1a32 后缀且永不改变、
/// 禁用不进快照但仍列出、快照按 <c>IsEnabled</c> 过滤。
/// </summary>
public sealed class ToolRegistryTests
{
    private static ToolContext Context() =>
        new(new RunId("r1"), "", "s1", 0, null, NullProgress.Instance, new FileMutationQueue());

    private sealed class NullProgress : IProgress<ToolProgress>
    {
        public static NullProgress Instance { get; } = new();

        public void Report(ToolProgress value)
        {
        }
    }

    private sealed class Gated(string id, Func<bool> enabled) : IAgentTool
    {
        public ToolSpec Info { get; } = new(id, "gated", Jsn.Parse("""{"type":"object"}"""), new ToolTraits(true, false, false));

        public bool IsEnabled(ToolContext ctx) => enabled();

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct) => ValueTask.FromResult(ToolResult.Text("x"));
    }

    [Fact]
    public void Builtins_register_first_and_no_other_source_can_replace_them()
    {
        var bash = new FakeTool("Bash");
        var registry = new ToolRegistry([bash]);

        var impostor = new FakeTool("Bash");
        Assert.Equal(ToolRegistration.DroppedConflict, registry.Register(impostor, "mcp:evil"));

        Assert.Same(bash, registry.Find("Bash")!.Tool);
        Assert.Same(bash, Assert.Single(registry.Snapshot(Context())).Tool);
    }

    [Fact]
    public void The_same_id_from_the_same_source_replaces_in_place_and_keeps_its_position_and_name()
    {
        var registry = new ToolRegistry([new FakeTool("Read")]);
        var first = new FakeTool("mcp__srv__a");
        var second = new FakeTool("mcp__srv__a");
        registry.Register(first, "mcp:srv");
        registry.Register(new FakeTool("Write"), "builtin");
        var name = registry.Find("mcp__srv__a")!.ExternalName;

        Assert.Equal(ToolRegistration.Replaced, registry.Register(second, "mcp:srv"));

        Assert.Same(second, registry.Find("mcp__srv__a")!.Tool);
        Assert.Equal(name, registry.Find("mcp__srv__a")!.ExternalName);
        Assert.Equal(["Read", "mcp__srv__a", "Write"], registry.List().Select(e => e.InternalId));
    }

    [Fact]
    public void Unregistering_a_source_removes_all_of_its_tools_and_only_those()
    {
        var registry = new ToolRegistry([new FakeTool("Read")]);
        registry.Register(new FakeTool("mcp__a__x"), "mcp:a");
        registry.Register(new FakeTool("mcp__a__y"), "mcp:a");
        registry.Register(new FakeTool("mcp__b__x"), "mcp:b");

        Assert.Equal(2, registry.UnregisterSource("mcp:a"));

        Assert.Equal(["Read", "mcp__b__x"], registry.List().Select(e => e.InternalId));
        Assert.Equal(0, registry.UnregisterSource("mcp:a"));
    }

    [Fact]
    public void Replacing_a_source_drops_tools_that_are_gone_and_reports_conflicts_with_other_sources()
    {
        var registry = new ToolRegistry([new FakeTool("Read")]);
        registry.ReplaceSource("mcp:a", [new FakeTool("mcp__a__x"), new FakeTool("mcp__a__y")]);

        var dropped = registry.ReplaceSource("mcp:a", [new FakeTool("mcp__a__y"), new FakeTool("Read")]);

        Assert.Equal(["Read"], dropped);
        Assert.Equal(["Read", "mcp__a__y"], registry.List().Select(e => e.InternalId));
    }

    [Fact]
    public void External_names_use_the_whitelist_and_long_or_colliding_ids_get_a_stable_fnv1a_suffix()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("plugin.acme/do it"), "plugin");
        var longId = "mcp__" + new string('s', 40) + "__" + new string('t', 40);
        registry.Register(new FakeTool(longId), "mcp:long");
        // 消毒之后撞名:`a.b` 与 `a_b`
        registry.Register(new FakeTool("a_b"), "x");
        registry.Register(new FakeTool("a.b"), "x");

        var names = registry.List().Select(e => e.ExternalName).ToList();

        Assert.All(names, n => Assert.True(ToolRegistry.IsValidExternalName(n), n));
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal("plugin_acme_do_it", registry.Find("plugin.acme/do it")!.ExternalName);
        var suffix = "_" + ToolRegistry.Fnv1a32(longId).ToString("x8");
        Assert.EndsWith(suffix, registry.Find(longId)!.ExternalName);
        Assert.Equal(64, registry.Find(longId)!.ExternalName.Length);
        Assert.EndsWith("_" + ToolRegistry.Fnv1a32("a.b").ToString("x8"), registry.Find("a.b")!.ExternalName);
    }

    [Fact]
    public void An_external_name_never_changes_and_is_never_recycled_after_the_tool_is_gone()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("a.b"), "x");
        var first = registry.Find("a.b")!.ExternalName;
        registry.UnregisterSource("x");

        registry.Register(new FakeTool("a_b"), "y");   // `a_b` 与 `a.b` 消毒后同名:`a.b` 留下的名字(`a_b`)不回收给别人
        registry.Register(new FakeTool("a.b"), "x");

        Assert.Equal(first, registry.Find("a.b")!.ExternalName);
        Assert.NotEqual(first, registry.Find("a_b")!.ExternalName);
    }

    [Fact]
    public void A_disabled_tool_stays_listed_but_leaves_the_snapshot_and_can_be_disabled_before_it_exists()
    {
        var registry = new ToolRegistry([new FakeTool("Read"), new FakeTool("Write")]);
        registry.SetDisabled("Write", true);
        registry.SetDisabled("mcp__a__x", true);
        registry.Register(new FakeTool("mcp__a__x"), "mcp:a");

        Assert.Equal(["Read"], registry.Snapshot(Context()).Select(b => b.ExternalName));
        Assert.Equal(["Read", "Write", "mcp__a__x"], registry.List().Select(e => e.InternalId));
        Assert.True(registry.Find("Write")!.Disabled);

        registry.SetDisabled("Write", false);
        Assert.Equal(["Read", "Write"], registry.Snapshot(Context()).Select(b => b.ExternalName));
    }

    [Fact]
    public void The_snapshot_honours_is_enabled_and_changes_bump_the_version()
    {
        var on = true;
        var registry = new ToolRegistry([new Gated("Gated", () => on)]);
        var version = registry.Version;
        var notified = 0;
        registry.Changed += () => notified++;

        Assert.Single(registry.Snapshot(Context()));
        on = false;
        Assert.Empty(registry.Snapshot(Context()));
        registry.Register(new FakeTool("Extra"), "x");
        registry.SetDisabled("Extra", true);
        registry.Unregister("Extra");

        Assert.True(registry.Version > version);
        Assert.Equal(3, notified);
    }

    [Fact]
    public async Task A_call_in_flight_keeps_its_own_tool_after_the_source_is_unregistered()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("mcp__a__x"), "mcp:a");
        var binding = Assert.Single(registry.Snapshot(Context()));

        registry.UnregisterSource("mcp:a");

        var result = await binding.Tool.ExecuteAsync(new ToolInvocation("c1", binding.ExternalName, binding.Tool, default), Context(), CancellationToken.None);
        Assert.False(result.IsError);
    }
}
