using System.Text.RegularExpressions;

namespace Ncw.Agent.Skills.Tests;

/// <summary>AGENTS.md / 08 §00 依赖规则里能机械检查的几条:Skills 层不知道协议、不引用 Avalonia / 客户端 / Host,不用反射,文件和进程只经执行环境,不产出界面文字。</summary>
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

    /// <summary>去掉行尾注释(<c>https://</c> 这类不算)。</summary>
    private static string CodeOnly(string line) => Regex.Replace(line, @"(?<!:)//.*$", "");

    [Fact]
    public void The_assembly_does_not_reference_the_protocol_ui_client_or_host()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(SkillsService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Core", referenced);
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_no_reflection_serialisation_and_no_direct_file_or_process_access()
    {
        // 文件只经 IExecutionEnvironment(AGENTS.md §5):本层产品代码里没有 System.IO 的文件 API 和 Process。zip 读内存 Stream(System.IO.Compression)是允许的。
        var forbidden = new Regex(
            @"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|(?<![\w.])File\.\w+|(?<![\w.])Directory\.\w+|\bFileStream\b|\bStreamReader\b|\bDirectoryInfo\b|\bFileInfo\b",
            RegexOptions.CultureInvariant);
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Skills");
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

                if (forbidden.IsMatch(CodeOnly(line)))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_keys_are_stable_and_messages_are_english()
    {
        Assert.Equal("skills.packageTooLarge", SkillErrorKeys.PackageTooLarge);
        Assert.Equal("skills.clientAssetsUnavailable", SkillErrorKeys.ClientAssetsUnavailable);
        Assert.Equal("skills.descriptionMissing", SkillDiagnosticKeys.DescriptionMissing);
        Assert.Equal("commands.promptEmpty", Commands.CommandDiagnosticKeys.PromptEmpty);

        // 本层源码里没有 CJK 字面量(诊断与错误消息是英文;「未分类」是领域数据,发给模型 / 界面按值翻译)
        var cjk = new Regex(@"[\u4e00-\u9fff]");
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Skills");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !x.Text.TrimStart().StartsWith('*') && cjk.IsMatch(CodeOnly(x.Text).Replace("未分类", "", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.File)}:{x.Line}")
            .ToList();
        Assert.Empty(offenders);
    }
}
