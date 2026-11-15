using System.Text.RegularExpressions;

namespace Ncw.Agent.Hooks.Tests;

/// <summary>AGENTS.md / 08 §00 依赖规则里能机械检查的几条:Hooks 层不知道协议、不引用 Avalonia / 客户端 / Host,不用反射,不直接碰文件和进程,不产出界面文字。</summary>
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

    private static IEnumerable<(string Relative, int Number, string Line)> ProductCode()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Hooks");
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

                yield return (Path.GetRelativePath(root, file), number, line);
            }
        }
    }

    private static string StripTrailingComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    [Fact]
    public void The_assembly_does_not_reference_the_protocol_ui_client_or_host()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(HooksService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Extensions", referenced);
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_no_reflection_serialisation_and_no_direct_file_or_process_access()
    {
        // 配置文件和钩子进程只经 IExecutionEnvironment(AGENTS.md §5):产品代码里没有 System.IO 的文件 API 和 Process。
        var forbidden = new Regex(
            @"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|\bFile\.\w+|\bDirectory\.\w+|\bFileStream\b|\bStreamReader\b|System\.Diagnostics\.Process",
            RegexOptions.CultureInvariant);
        var offenders = ProductCode().Where(l => forbidden.IsMatch(l.Line)).Select(l => $"{l.Relative}:{l.Number}: {l.Line.Trim()}").ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_only_english_model_text_and_stable_keys()
    {
        // 界面文字不在 Agent 层产生:源码(注释除外)里没有 CJK;错误 / 诊断只带稳定 key + 英文诊断。
        var cjk = new Regex(@"[㐀-鿿豈-﫿　-〿]");
        var offenders = ProductCode().Where(l => cjk.IsMatch(StripTrailingComment(l.Line))).Select(l => $"{l.Relative}:{l.Number}: {l.Line.Trim()}").ToList();
        Assert.Empty(offenders);

        Assert.Equal("hooks.diagnostic.timeout", HookDiagnosticKeys.Timeout);
        Assert.Equal("hooks.error.notFound", HookErrorKeys.NotFound);
        Assert.Equal("hooks.promptBlocked", HookErrorMessageKeys.PromptBlocked);
        foreach (var key in new[] { HookErrorKeys.Invalid, HookErrorKeys.FileUnreadable, HookDiagnosticKeys.Invalid, HookDiagnosticKeys.SpawnFailed })
        {
            Assert.Matches("^[a-zA-Z.]+$", key);
        }
    }

    [Fact]
    public void The_hooks_extension_sits_before_goals_and_todos_in_the_turn_end_order()
    {
        // 06 §5:权限 int.MinValue;Hooks 100;计划 400;目标 500;待办 600
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Hooks", "HooksExtension.cs"));

        Assert.Contains("public int Order => 100;", source);
    }
}
