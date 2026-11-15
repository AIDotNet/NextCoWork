using System.Text.RegularExpressions;

namespace Ncw.Agent.Widgets.Tests;

/// <summary>AGENTS.md 里能机械检查的几条:依赖方向、不碰设备(文件 / 进程)、不用反射序列化、不产出界面文字。</summary>
public sealed partial class ArchitectureTests
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
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Widgets");
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
    public void The_assembly_does_not_reference_protocol_host_clients_storage_or_avalonia()
    {
#pragma warning disable IL2026
        var referenced = typeof(VisualizeTools).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Host" or "Ncw.Agent.Storage" or "Ncw.Client" or "Ncw.UI" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Kernel", referenced);
        Assert.Contains("Ncw.Agent.Extensions", referenced);
    }

    [Fact]
    public void The_code_has_no_file_api_no_process_and_no_reflection_serialization()
    {
        var banned = new[] { "System.IO.File", "File.Read", "File.Write", "Directory.", "Process.Start", "new Process", "JsonSerializer.", "Activator.", "GetManifestResource", "typeof(" };
        // 设计规范文件是常量字符串(正文里出现的 "File." 之类只是文字):按文件名排除,并由下一条测试断言它们没有任何代码。
        var offenders = SourceLines().Where(l => !IsGuidelineFile(l.File) && banned.Any(b => l.Code.Contains(b, StringComparison.Ordinal))).ToList();
        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Select(o => $"{o.File}:{o.Line} {o.Code}")));
    }

    private static bool IsGuidelineFile(string file) =>
        file is "Core.cs" or "SvgSetup.cs" or "ArtAndIllustration.cs" or "UiComponents.cs" or "ColorPalette.cs" or "ChartsChartJs.cs" or "DiagramTypes.cs";

    [Fact]
    public void Guideline_files_contain_nothing_but_one_constant_string()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Widgets", "Guidelines");
        foreach (var file in new[] { "Core", "SvgSetup", "ArtAndIllustration", "UiComponents", "ColorPalette", "ChartsChartJs", "DiagramTypes" })
        {
            var text = File.ReadAllText(Path.Combine(root, file + ".cs"));
            Assert.Single(Regex.Matches(text, @"public const string "));
            Assert.DoesNotContain("static string", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_assembly_produces_no_ui_text_every_message_is_an_english_constant()
    {
        // 本层不引用本地化(NcwTexts 在 Ncw.UI);发给模型的文字是英文常量,界面文字由控制端按规则 id 翻译。
        Assert.DoesNotContain(SourceLines(), l => l.Code.Contains("NcwTexts", StringComparison.Ordinal) || l.Code.Contains("NcwText", StringComparison.Ordinal));
    }
}
