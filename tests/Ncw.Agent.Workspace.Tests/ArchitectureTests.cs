using System.Text.RegularExpressions;

namespace Ncw.Agent.Workspace.Tests;

/// <summary>AGENTS.md 的硬性约束里,能机械检查的那几条:依赖方向、「只通过 IExecutionEnvironment 访问文件和进程」。</summary>
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
    public void The_workspace_assembly_does_not_reference_protocol_storage_host_or_ui()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(Ncw.Agent.Workspace.Files.FileService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Storage" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    /// <summary>文件和进程只能经项目的 IExecutionEnvironment:不允许直接用 System.IO 的文件 API 或 Process(执行环境实现自身除外)。</summary>
    [Fact]
    public void Workspace_sources_never_touch_the_device_directly()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.[A-Z]\w*|new\s+(FileStream|StreamReader|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Environment\.(CurrentDirectory|GetEnvironmentVariable|GetFolderPath)|Path\.(GetTempPath|GetFullPath|Combine|GetTempFileName)", RegexOptions.CultureInvariant);
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Workspace");
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
    public void The_project_file_references_only_core_and_environments()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Workspace", "Ncw.Agent.Workspace.csproj"));
        var references = Regex.Matches(csproj, "<ProjectReference Include=\"([^\"]+)\"").Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value)).Order().ToList();
        Assert.Equal(["Ncw.Agent.Environments", "Ncw.Core"], references);
        Assert.DoesNotContain("<PackageReference", csproj); // 版本只写在 Directory.Packages.props,而本项目不需要任何包
    }
}
