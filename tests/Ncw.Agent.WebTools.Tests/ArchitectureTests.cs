using System.Text.RegularExpressions;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>AGENTS.md 的硬性约束里能机械检查的几条(联网工具这一层):依赖方向、不碰设备文件与进程、不用反射序列化、不产出界面文字。</summary>
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

    private static IEnumerable<string> Sources()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.WebTools");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static IEnumerable<(string File, int Line, string Code)> Lines()
    {
        foreach (var file in Sources())
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

                yield return (file, number, code);
            }
        }
    }

    [Fact]
    public void The_assembly_does_not_reference_protocol_storage_host_or_ui()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(Net.IWebHttp).Assembly.GetReferencedAssemblies().Select(static a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, static n => n is "Ncw.Protocol" or "Ncw.Agent.Storage" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Kernel", referenced);
        Assert.Contains("Ncw.Agent.Tools", referenced);
    }

    [Fact]
    public void Sources_never_touch_the_device_file_system_or_start_processes()
    {
        var forbidden = new Regex(@"\b(File|Directory|FileInfo|DirectoryInfo)\.[A-Z]\w*|new\s+(FileStream|StreamReader|StreamWriter|Process)\b|System\.Diagnostics\.Process|\bProcess\.(Start|GetProcess)|Environment\.(GetEnvironmentVariable|GetFolderPath)|Path\.(GetTempPath|GetFullPath|Combine)", RegexOptions.CultureInvariant);
        var offenders = Lines().Where(l => forbidden.IsMatch(l.Code)).Select(static l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Code}").ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Sources_do_not_use_reflection_based_serialization_or_regex_construction_at_runtime()
    {
        var forbidden = new Regex(@"JsonSerializer\.(Serialize|Deserialize)|new\s+Regex\(|Regex\.(IsMatch|Match|Replace|Split)\(|Activator\.CreateInstance|Type\.GetType|\.GetMethod\(|\.GetProperty\(|Assembly\.Load|Newtonsoft", RegexOptions.CultureInvariant);
        var offenders = Lines().Where(l => forbidden.IsMatch(l.Code)).Select(static l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Code}").ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Network_traffic_goes_only_through_the_injected_http_seam()
    {
        // HttpClient / SocketsHttpHandler / Socket 只允许出现在 Net/WebHttp.cs(GuardedHttp 与 PinnedConnector);其余文件(工具、搜索适配器)只能用 IWebHttp。
        var offenders = Lines()
            .Where(l => Regex.IsMatch(l.Code, @"\bnew\s+(HttpClient|SocketsHttpHandler|Socket|TcpClient)\b|HttpClient\.|WebRequest\b|Dns\.") && Path.GetFileName(l.File) != "WebHttp.cs")
            .Select(static l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Code}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Sources_produce_no_ui_text_in_chinese_except_the_catalogs_brand_names()
    {
        var cjk = new Regex("\"[^\"]*[\\u4e00-\\u9fff][^\"]*\""); // 只看字符串字面量(注释可以是中文)
        var offenders = Lines().Where(l => cjk.IsMatch(l.Code) && Path.GetFileName(l.File) != "SearchModel.cs").Select(static l => $"{Path.GetFileName(l.File)}:{l.Line}").ToList();
        Assert.Empty(offenders);
    }
}
