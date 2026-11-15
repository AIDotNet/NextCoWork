using System.Text.RegularExpressions;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Extensions.Planning.TodoLists;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Planning.Tests;

/// <summary>AGENTS.md 的硬性约束里能机械检查的几条(依赖方向、只经 IExecutionEnvironment 碰设备、不用反射序列化、不产出界面文字),以及三个扩展的装配约定。</summary>
public sealed class ArchitectureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<(string File, int Line, string Code)> SourceLines()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Planning");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*') || code.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (Path.GetFileName(file), number, code);
            }
        }
    }

    [Fact]
    public void The_assembly_does_not_reference_protocol_host_clients_or_avalonia()
    {
#pragma warning disable IL2026
        var referenced = typeof(PlanningKit).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Host" or "Ncw.Client" or "Ncw.UI" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Kernel", referenced);
        Assert.Contains("Ncw.Agent.Extensions", referenced);
        Assert.Contains("Ncw.Core", referenced);
    }

    [Fact]
    public void Core_and_the_lower_agent_layers_do_not_reference_the_planning_project()
    {
        foreach (var assembly in new[] { typeof(Ncw.Core.Domain.Todos), typeof(Ncw.Agent.Kernel.Runs.RunRegistry), typeof(Ncw.Agent.Kernel.Session.AgentSession), typeof(Ncw.Agent.Extensions.Permissions.PermissionExtension) })
        {
#pragma warning disable IL2026
            var referenced = assembly.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
#pragma warning restore IL2026
            Assert.DoesNotContain("Ncw.Agent.Planning", referenced);
        }
    }

    [Fact]
    public void Sources_never_touch_files_or_processes_directly_plan_files_go_through_the_execution_environment()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.[A-Z]\w*|new\s+(FileStream|StreamReader|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Path\.(GetTempPath|GetFullPath|Combine|GetTempFileName)", RegexOptions.CultureInvariant);
        var offenders = SourceLines().Where(l => forbidden.IsMatch(l.Code)).Select(l => $"{l.File}:{l.Line}: {l.Code}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void No_reflection_based_serialization_no_ui_text_and_no_js_engine()
    {
        var forbidden = new Regex(@"JsonSerializer\.|Activator\.CreateInstance|\.GetType\(\)\.GetProperties|NcwTexts|System\.Reflection|Newtonsoft|new\s+JsEngine", RegexOptions.CultureInvariant);
        var offenders = SourceLines().Where(l => forbidden.IsMatch(l.Code)).Select(l => $"{l.File}:{l.Line}: {l.Code}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void The_extensions_run_after_hooks_and_the_turn_end_order_is_goal_then_checklist()
    {
        var plan = new PlanModeExtension(new PlanService());
        var goals = new GoalExtension(new GoalService(new GoalOptions { Llm = new FakeLlm(), Transcript = new MemoryGoalTranscript() }));
        var todos = new TodoExtension();

        // 收尾裁决按 Order 取第一个不是 Stop 的:Hooks(100)< 目标 < 待办——目标的续跑 / 强停优先于清单补报
        Assert.True(goals.Order < todos.Order);
        Assert.True(plan.Order > 100 && goals.Order > 100 && todos.Order > 100);
        Assert.Equal(["plan-mode", "goals", "todos"], new IAgentExtension[] { plan, goals, todos }.OrderBy(e => e.Order).Select(e => e.Id));
    }

    [Fact]
    public void The_kit_registers_exactly_the_enabled_features_and_their_run_extensions()
    {
        using var rig = new E2eRig();
        Assert.Equal(["plan-mode", "goals", "todos"], rig.Kit.Extensions.Select(e => e.Id));
        Assert.Equal(2, rig.Kit.RunExtensions.Count);

        var onlyTodos = PlanningKit.Create(new PlanningKitOptions { Llm = new FakeLlm(), Sessions = rig.Repo, EnablePlanMode = false, EnableGoals = false });
        Assert.Equal(["todos"], onlyTodos.Extensions.Select(e => e.Id));
        Assert.Empty(onlyTodos.RunExtensions);
        onlyTodos.Goals.Dispose();

        var none = PlanningKit.Create(new PlanningKitOptions { Llm = new FakeLlm(), Sessions = rig.Repo, EnablePlanMode = false, EnableGoals = false, EnableTodos = false });
        Assert.Empty(none.Extensions);
        none.Goals.Dispose();
    }

    [Fact]
    public void The_kit_extensions_register_exactly_the_four_tools()
    {
        using var rig = new E2eRig();
        var tools = rig.Kit.Extensions.SelectMany(e => new ExtensionHost([e]).RegisteredTools.Select(t => t.Info.InternalId)).Order().ToList();

        Assert.Equal(["EnterPlanMode", "ExitPlanMode", "ProposeGoal", "TodoWrite"], tools);
    }

    [Fact]
    public void Every_tool_declares_its_traits_explicitly_and_the_shared_state_ones_are_not_concurrency_safe()
    {
        using var rig = new E2eRig();
        var tools = rig.Kit.Extensions.SelectMany(e => new ExtensionHost([e]).RegisteredTools).ToDictionary(t => t.Info.InternalId);

        Assert.True(tools["TodoWrite"].Info.Traits.ReadOnly);
        Assert.True(tools["ExitPlanMode"].Info.Traits.ReadOnly);
        Assert.False(tools["EnterPlanMode"].Info.Traits.ReadOnly);
        Assert.False(tools["ProposeGoal"].Info.Traits.ConcurrencySafe);
        Assert.All(tools.Values, t => Assert.False(t.Info.Traits.NeedsNetwork));
        Assert.All(tools.Values, t => Assert.False(t.Info.Traits.Destructive));
    }
}
