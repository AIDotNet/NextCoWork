using System.Text.RegularExpressions;
using Ncw.Agent.Kernel.Runs;

namespace Ncw.Agent.Runs.Tests;

/// <summary>AGENTS.md / 08 §00 依赖规则里能机械检查的几条:编排层不知道协议、不引用 Avalonia 和客户端、不引用一方扩展,不用反射。</summary>
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
    public void The_runs_assembly_does_not_reference_the_protocol_ui_client_host_or_the_first_party_extensions()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(RunRegistry).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Agent.Extensions" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Kernel", referenced);
        Assert.Contains("Ncw.Agent.Session", referenced);
        Assert.Contains("Ncw.Core", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_and_no_reflection_based_serialisation_and_no_direct_process_access()
    {
        var forbidden = new Regex(@"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|\bFile\.(Read|Write|Open|Create|Delete)", RegexOptions.CultureInvariant);
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Runs");
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

                if (forbidden.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_errors_only_carry_keys()
    {
        // 本层的错误一律是英文诊断 + MessageKey;界面文字属于控制端。
        Assert.Equal("run.abandoned", RunErrorKeys.Abandoned);
        Assert.Equal("run.internal", RunErrorKeys.Internal);
        Assert.Equal("run.notStarted", RunErrorKeys.NotStarted);
    }
}
