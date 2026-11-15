using System.Text.RegularExpressions;
using Ncw.Agent.Kernel.Subagents;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>AGENTS.md / 08 §00 依赖规则里能机械检查的几条:子代理层不知道协议、不引用 Avalonia / 客户端 / Host,不用反射,不直接碰文件和进程,不产出界面文字。</summary>
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

    [Fact]
    public void The_assembly_does_not_reference_the_protocol_ui_client_or_host()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(SubagentService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Runs", referenced);
        Assert.Contains("Ncw.Agent.Extensions", referenced);
        Assert.Contains("Ncw.Core", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_no_reflection_serialisation_and_no_direct_file_or_process_access()
    {
        // 定义文件只经 IExecutionEnvironment 读(AGENTS.md §5):本层的产品代码里没有 System.IO 的文件 API 和 Process。
        var forbidden = new Regex(
            @"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|\bFile\.\w+|\bDirectory\.\w+|\bFileStream\b|\bStreamReader\b",
            RegexOptions.CultureInvariant);
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Subagents");
        var offenders = new List<string>();
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
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal) || code.StartsWith('*'))
                {
                    continue;
                }

                if (forbidden.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_diagnostics_and_errors_carry_stable_keys()
    {
        Assert.Equal("agents.toolsNone", AgentDiagnosticKeys.ToolsNone);
        Assert.Equal("agents.permissionModeInvalid", AgentDiagnosticKeys.PermissionModeInvalid);
        Assert.Equal("subagent.error.noOutput", SubagentErrorKeys.NoOutput);
        // 诊断文字是英文(给日志和排错),没有 CJK
        foreach (var field in new[] { AgentDiagnosticKeys.DirectoryUnreadable, AgentDiagnosticKeys.FileNameInvalid, AgentDiagnosticKeys.TooMany })
        {
            Assert.Matches("^[a-zA-Z.]+$", field);
        }
    }

    [Fact]
    public void Source_files_carry_no_hard_coded_cjk_in_string_literals()
    {
        // 界面文字不在这一层产生:字符串字面量(发给模型的文字、诊断)全是英文。注释里的中文不算。
        var literal = new Regex("\"[^\"\\n]*[\\u4e00-\\u9fff][^\"\\n]*\"", RegexOptions.CultureInvariant);
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Subagents");
        var offenders = new List<string>();
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
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*'))
                {
                    continue;
                }

                var commentStart = line.IndexOf("//", StringComparison.Ordinal);
                var scan = commentStart >= 0 && !line[..commentStart].Contains('"') ? line[..commentStart] : line;
                if (literal.IsMatch(scan))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{number}");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
