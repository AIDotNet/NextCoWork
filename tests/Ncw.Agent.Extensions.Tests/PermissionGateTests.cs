using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// 权限闸门的<b>穷举</b>表(移植自 2.x <c>permission-gate.test.ts</c>)。
/// ★ <c>Golden</c> 是<b>手写的期望值</b>,不是从 <c>Evaluate</c> 生成的:用「独立实现的期望函数」对拍没有意义(同一套 if 抄两遍,两边一起改错时测试照绿)。
/// 所以这里写死 48 行,改动策略必然改到这张表,改表的人会被迫逐行看一眼自己在放开什么。
/// 列:模式 / readOnly / destructive / needsNetwork / webSearch → 结果(1 = true)。
/// </summary>
public sealed class PermissionGateTests
{
    private const string Golden = """
        ask  1 1 1 1 allow
        ask  1 1 1 0 deny
        ask  1 1 0 1 allow
        ask  1 1 0 0 allow
        ask  1 0 1 1 allow
        ask  1 0 1 0 deny
        ask  1 0 0 1 allow
        ask  1 0 0 0 allow
        ask  0 1 1 1 ask
        ask  0 1 1 0 deny
        ask  0 1 0 1 ask
        ask  0 1 0 0 ask
        ask  0 0 1 1 ask
        ask  0 0 1 0 deny
        ask  0 0 0 1 ask
        ask  0 0 0 0 ask
        auto 1 1 1 1 allow
        auto 1 1 1 0 deny
        auto 1 1 0 1 allow
        auto 1 1 0 0 allow
        auto 1 0 1 1 allow
        auto 1 0 1 0 deny
        auto 1 0 0 1 allow
        auto 1 0 0 0 allow
        auto 0 1 1 1 ask
        auto 0 1 1 0 deny
        auto 0 1 0 1 ask
        auto 0 1 0 0 ask
        auto 0 0 1 1 allow
        auto 0 0 1 0 deny
        auto 0 0 0 1 allow
        auto 0 0 0 0 allow
        full 1 1 1 1 allow
        full 1 1 1 0 deny
        full 1 1 0 1 allow
        full 1 1 0 0 allow
        full 1 0 1 1 allow
        full 1 0 1 0 deny
        full 1 0 0 1 allow
        full 1 0 0 0 allow
        full 0 1 1 1 allow
        full 0 1 1 0 deny
        full 0 1 0 1 allow
        full 0 1 0 0 allow
        full 0 0 1 1 allow
        full 0 0 1 0 deny
        full 0 0 0 1 allow
        full 0 0 0 0 allow
        """;

    private sealed record Row(PermissionMode Mode, bool ReadOnly, bool Destructive, bool NeedsNetwork, bool WebSearch, string Want, string Label);

    private static List<Row> Rows() => Golden.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PermissionModes.TryParse(p[0], out var mode);
        return new Row(mode, p[1] == "1", p[2] == "1", p[3] == "1", p[4] == "1", p[5], line);
    }).ToList();

    public static IEnumerable<object[]> GoldenRows() => Rows().Select(r => new object[] { r.Label });

    [Fact]
    public void The_table_is_complete_3_modes_x_4_booleans_with_no_duplicates()
    {
        var rows = Rows();
        Assert.Equal(48, rows.Count);
        Assert.Equal(48, rows.Select(r => $"{r.Mode}{r.ReadOnly}{r.Destructive}{r.NeedsNetwork}{r.WebSearch}").Distinct().Count());
        Assert.Equal(PermissionModes.All.OrderBy(m => m), rows.Select(r => r.Mode).Distinct().OrderBy(m => m));
        Assert.All(rows, r => Assert.Contains(r.Want, new[] { "allow", "deny", "ask" }));
    }

    [Theory]
    [MemberData(nameof(GoldenRows))]
    public void Golden_row(string label)
    {
        var r = Rows().Single(x => x.Label == label);
        var outcome = PermissionGate.Evaluate(new PermissionQuery(r.Mode, r.ReadOnly, r.Destructive, r.WebSearch, r.NeedsNetwork));
        Assert.Equal(r.Want, outcome.Kind.ToString().ToLowerInvariant());
    }

    // 上面 48 行钉死了结果,这一组钉的是「为什么是这个结果」——也就是那几行的顺序。

    [Fact]
    public void The_network_switch_comes_before_read_only_a_read_only_network_tool_is_still_denied_when_the_switch_is_off()
    {
        var o = PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Full, ReadOnly: true, Destructive: false, WebSearch: false, NeedsNetwork: true));
        Assert.Equal(GateKind.Deny, o.Kind);
    }

    [Fact]
    public void Full_cannot_loosen_the_network_switch_it_is_the_users_hard_switch_not_a_permission_mode()
    {
        foreach (var mode in PermissionModes.All)
        {
            Assert.Equal(GateKind.Deny, PermissionGate.Evaluate(new PermissionQuery(mode, false, false, WebSearch: false, NeedsNetwork: true)).Kind);
        }
    }

    [Fact]
    public void Read_only_tools_pass_in_ask_mode_asking_for_every_read_teaches_users_to_click_allow_blindly()
    {
        Assert.Equal(GateKind.Allow, PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Ask, true, true, WebSearch: false)).Kind);
    }

    [Fact]
    public void Auto_asks_only_for_destructive_operations()
    {
        Assert.Equal(GateKind.Ask, PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Auto, false, true, true)).Kind);
        Assert.Equal(GateKind.Allow, PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Auto, false, false, true)).Kind);
    }

    [Fact]
    public void NeedsNetwork_defaults_to_false()
    {
        Assert.Equal(GateKind.Allow, PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Full, false, true, WebSearch: false)).Kind);
    }

    [Fact]
    public void The_network_denial_blocks_the_curl_through_Bash_detour()
    {
        var o = PermissionGate.Evaluate(new PermissionQuery(PermissionMode.Full, true, false, WebSearch: false, NeedsNetwork: true));
        Assert.Contains("curl", o.Reason);
        Assert.Contains("user", o.Reason);
    }

    // ── NetworkSwitchTools ──

    [Fact]
    public void The_switch_list_is_exactly_WebFetch_and_web_search()
    {
        // 往这张表里加名字 = 让开关多管一个工具,是一次行为改变,必须改到这里、被人看一眼。
        Assert.Equal(["WebFetch", "web_search"], PermissionGate.NetworkSwitchTools.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void File_tools_are_not_in_the_switch_list_otherwise_the_switch_would_also_turn_off_reading_files()
    {
        foreach (var id in new[] { "Read", "Write", "Edit", "LS", "Glob", "Grep", "Bash", "TodoWrite" })
        {
            Assert.DoesNotContain(id, PermissionGate.NetworkSwitchTools);
        }
    }

    // ── Facts:系统提示词和闸门说同一件事 ──

    [Fact]
    public void Facts_say_a_different_thing_in_each_mode()
    {
        Assert.Contains("Permission mode: ask", PermissionGate.Facts(PermissionMode.Ask, true));
        Assert.Contains("wait for user approval", PermissionGate.Facts(PermissionMode.Ask, true));
        Assert.Contains("wait for user approval", PermissionGate.Facts(PermissionMode.Auto, true));
        Assert.DoesNotContain("wait for user approval", PermissionGate.Facts(PermissionMode.Full, true));
    }

    [Fact]
    public void Reading_is_always_run_without_asking_in_every_mode()
    {
        foreach (var mode in PermissionModes.All)
        {
            Assert.Contains("Reading and searching: run without asking", PermissionGate.Facts(mode, true));
        }
    }

    [Fact]
    public void Even_full_cannot_loosen_the_switch_and_the_text_closes_the_Bash_detour()
    {
        var s = PermissionGate.Facts(PermissionMode.Full, false);
        Assert.Contains("the network switch is off", s);
        Assert.Contains("Bash", s);
    }

    [Fact]
    public void What_the_prompt_says_is_what_the_table_decides()
    {
        foreach (var mode in PermissionModes.All)
        {
            foreach (var webSearch in new[] { true, false })
            {
                var write = PermissionGate.Evaluate(new PermissionQuery(mode, false, true, webSearch));
                var line = PermissionGate.Facts(mode, webSearch).Split('\n').First(l => l.StartsWith("- Writing files", StringComparison.Ordinal));
                Assert.Equal(write.Kind == GateKind.Ask, line.Contains("wait for user approval"));
                Assert.Equal(write.Kind == GateKind.Allow, line.Contains("run without asking"));
            }
        }
    }

    // ── 档位 ──

    [Fact]
    public void A_subagent_does_not_inherit_full_the_mode_is_the_min_of_parent_and_definition()
    {
        Assert.Equal(PermissionMode.Ask, PermissionModes.Min(PermissionMode.Ask, PermissionMode.Full));
        Assert.Equal(PermissionMode.Auto, PermissionModes.Min(PermissionMode.Full, PermissionMode.Auto));
        Assert.Equal(PermissionMode.Full, PermissionModes.Min(PermissionMode.Full, PermissionMode.Full));
    }

    [Fact]
    public void Modes_have_their_2x_wire_names()
    {
        foreach (var (wire, mode) in new[] { ("ask", PermissionMode.Ask), ("auto", PermissionMode.Auto), ("full", PermissionMode.Full) })
        {
            Assert.True(PermissionModes.TryParse(wire, out var parsed));
            Assert.Equal(mode, parsed);
            Assert.Equal(wire, mode.ToWire());
        }

        Assert.False(PermissionModes.TryParse("plan", out _));
        Assert.False(PermissionModes.TryParse(null, out _));
    }
}
