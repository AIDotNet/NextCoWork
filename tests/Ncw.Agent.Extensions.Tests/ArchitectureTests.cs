using System.Text.RegularExpressions;
using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>AGENTS.md 的硬性约束里能机械检查的几条:依赖方向、只经 IExecutionEnvironment 碰设备、不用反射序列化、不产出界面文字。</summary>
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
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Extensions");
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

                yield return (Path.GetFileName(file), number, code);
            }
        }
    }

    [Fact]
    public void The_extensions_assembly_does_not_reference_protocol_storage_session_or_ui()
    {
#pragma warning disable IL2026
        var referenced = typeof(PermissionExtension).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Storage" or "Ncw.Agent.Session" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Kernel", referenced);
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void Sources_never_touch_files_or_processes_directly()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.[A-Z]\w*|new\s+(FileStream|StreamReader|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Path\.(GetTempPath|GetFullPath|Combine|GetTempFileName)", RegexOptions.CultureInvariant);
        var offenders = SourceLines().Where(l => forbidden.IsMatch(l.Code)).Select(l => $"{l.File}:{l.Line}: {l.Code}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void No_reflection_based_serialization_and_no_ui_text()
    {
        var forbidden = new Regex(@"JsonSerializer\.|Activator\.CreateInstance|\.GetType\(\)\.GetProperties|NcwTexts|System\.Reflection|Newtonsoft", RegexOptions.CultureInvariant);
        var offenders = SourceLines().Where(l => forbidden.IsMatch(l.Code)).Select(l => $"{l.File}:{l.Line}: {l.Code}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void Permission_rule_stores_are_injected_interfaces_not_storage_types()
    {
        Assert.True(typeof(IPermissionRuleStore).IsInterface);
        Assert.True(typeof(Interactions.IInteractionJournal).IsInterface);
    }
}
