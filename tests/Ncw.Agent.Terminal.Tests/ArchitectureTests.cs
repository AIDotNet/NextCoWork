using System.Text.RegularExpressions;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>AGENTS.md 的硬性约束里能机械检查的几条:依赖方向、管理器不直接碰设备、P/Invoke 的写法、托管代码里不 fork。</summary>
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

    private static IEnumerable<string> Sources(params string[] subdirectories)
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Terminal", Path.Combine(subdirectories));
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void The_terminal_assembly_depends_only_on_environments()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(TerminalManager).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Agent.Storage" or "Ncw.Agent.Host" or "Ncw.Agent.Extensions" or "Ncw.Client" or "Ncw.UI" or "Ncw.Core" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void The_project_file_references_only_environments_and_no_packages()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Terminal", "Ncw.Agent.Terminal.csproj"));
        var references = Regex.Matches(csproj, "<ProjectReference Include=\"([^\"]+)\"").Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value)).ToList();
        Assert.Equal(["Ncw.Agent.Environments"], references);
        Assert.DoesNotContain("<PackageReference", csproj); // 版本只写在 Directory.Packages.props,而本项目不需要任何包(没有外部 PTY 库)
    }

    /// <summary>管理器是逻辑层:文件和进程只能经 <c>IExecutionEnvironment</c>。直接碰设备的只有 <c>Pty/</c>(本机 PTY 本来就是 LocalEnvironment 的一部分)。</summary>
    [Fact]
    public void The_manager_never_touches_the_device_directly()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.[A-Z]\w*|new\s+(FileStream|StreamReader|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Environment\.(CurrentDirectory|GetEnvironmentVariable|GetFolderPath)|Path\.(GetTempPath|GetFullPath|GetTempFileName)", RegexOptions.CultureInvariant);
        var offenders = new List<string>();
        foreach (var file in Sources("Manager"))
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*') || code.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (forbidden.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 托管代码里不 fork(06 §9.1):fork 之后子进程里只剩调用线程,运行时的状态是按多线程设计的,会偶发死锁。
    /// POSIX 一律 <c>posix_spawn</c>;P/Invoke 一律 <c>LibraryImport</c> 源生成(不用反射式封送,可裁剪、可 NativeAOT)。
    /// </summary>
    [Fact]
    public void Native_calls_are_source_generated_and_never_fork()
    {
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"\[DllImport\b"))
            {
                offenders.Add($"{Path.GetFileName(file)}: DllImport");
            }

            foreach (Match match in Regex.Matches(text, @"EntryPoint\s*=\s*""(fork|vfork|forkpty|openpty|login_tty|execv\w*)"""))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>本层不产出界面文字:字符串字面量里不出现中文(注释除外);错误只有错误码和英文诊断,审批意图只带结构化数据。</summary>
    [Fact]
    public void The_layer_has_no_user_interface_text_in_string_literals()
    {
        var literalWithCjk = new Regex("\"[^\"\\n]*\\p{IsCJKUnifiedIdeographs}[^\"\\n]*\"");
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = Regex.Replace(line, @"\s//.*$", "");
                if (!code.TrimStart().StartsWith("//", StringComparison.Ordinal) && !code.TrimStart().StartsWith('*') && !code.TrimStart().StartsWith("<!--", StringComparison.Ordinal) && literalWithCjk.IsMatch(code))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
