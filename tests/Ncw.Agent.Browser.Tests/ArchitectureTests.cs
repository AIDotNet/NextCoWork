using System.Text.RegularExpressions;

namespace Ncw.Agent.Browser.Tests;

/// <summary>AGENTS.md 能机械检查的几条:依赖方向、浏览器进程 / 文件只经 IExecutionEnvironment、不用反射序列化、不下载浏览器。</summary>
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

    private static IEnumerable<(string File, int Line, string Text)> Sources()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Browser");
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

                yield return (Path.GetRelativePath(root, file), number, line);
            }
        }
    }

    [Fact]
    public void The_browser_assembly_references_only_kernel_tools_and_environments_of_the_agent_layers()
    {
#pragma warning disable IL2026 // 测试里读程序集引用
        var referenced = typeof(BrowserService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Storage" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" || n.StartsWith("Avalonia", StringComparison.Ordinal) || n.StartsWith("PuppeteerSharp", StringComparison.Ordinal) || n.Contains("Newtonsoft", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void Sources_never_touch_the_device_directly()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.[A-Z]\w*|new\s+(FileStream|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Environment\.(CurrentDirectory|GetEnvironmentVariable|GetFolderPath)|Path\.(GetTempPath|GetFullPath|Combine|GetTempFileName)", RegexOptions.CultureInvariant);
        var offenders = Sources().Where(s => forbidden.IsMatch(s.Text)).Select(s => $"{s.File}:{s.Line}: {s.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void Sources_never_use_reflection_serialization_or_download_anything()
    {
        var forbidden = new Regex(@"\bJsonSerializer\.|\bActivator\.|\.GetType\(\)\.GetProperties|\bHttpClient\b|\bWebClient\b|BrowserFetcher|DownloadFile", RegexOptions.CultureInvariant);
        var offenders = Sources().Where(s => forbidden.IsMatch(s.Text)).Select(s => $"{s.File}:{s.Line}: {s.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void The_project_is_aot_and_trim_clean_by_declaration()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Browser", "Ncw.Agent.Browser.csproj"));
        Assert.DoesNotContain("PackageReference", csproj); // 零第三方依赖
    }
}
