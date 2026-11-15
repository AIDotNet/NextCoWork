using System.Text.RegularExpressions;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// AGENTS.md / 08 §00 依赖规则里能机械检查的几条:MCP 层不知道协议、不引用 Avalonia / 客户端 / Host,不用反射,不直接碰文件和进程(服务器进程一律经 IExecutionEnvironment),
/// 不产出界面文字,TLS 证书校验保持平台默认(不自定义回调)。移植自 2.x <c>tls-identity.test.ts</c> 的意图:远端 HTTPS 的证书必须按<b>配置里的主机名</b>校验——
/// 2.x 手写 <c>tls.connect</c> 要显式传 host / servername;.NET 的 <c>SocketsHttpHandler</c> 在环境 TCP 通道之上自己做 TLS,身份就是请求 URI 的主机,
/// 所以这里不需要(也不允许)自定义校验,测试只钉「没有人把校验关掉」。
/// </summary>
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

    private static IEnumerable<(string File, int Line, string Text)> ProductLines()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ncw.Agent.Mcp");
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
    public void The_assembly_does_not_reference_the_protocol_ui_client_or_host()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(McpService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Ncw.Agent.Session", referenced);
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_no_reflection_serialisation_and_no_direct_file_or_process_access()
    {
        // 服务器进程只经 IExecutionEnvironment 启动(AGENTS.md §5);配置 / 密钥只经注入的端口。网络类(HttpClient)允许。
        var forbidden = new Regex(
            @"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|ProcessStartInfo|\bFile\.\w+|\bDirectory\.\w+|\bFileStream\b|\bStreamReader\b",
            RegexOptions.CultureInvariant);
        var offenders = ProductLines().Where(l => forbidden.IsMatch(l.Text)).Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}").ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_messages_are_english_and_errors_carry_stable_keys()
    {
        Assert.Equal("mcp.connect.commandNotFound", McpErrorKeys.CommandNotFound);
        Assert.Equal("mcp.tool.schemaTooLarge", McpErrorKeys.ToolSchemaTooLarge);
        foreach (var key in typeof(McpErrorKeys).GetFields().Select(f => (string)f.GetRawConstantValue()!))
        {
            Assert.Matches("^mcp\\.[a-zA-Z.]+$", key);
        }

        var cjk = new Regex(@"[　-鿿＀-￯]");
        var offenders = ProductLines().Where(l => cjk.IsMatch(l.Text) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.Text.Contains("//")).Select(l => $"{l.File}:{l.Line}");
        Assert.Empty(offenders);
    }

    [Fact]
    public void Tls_certificate_validation_is_never_customised_or_disabled()
    {
        var forbidden = new Regex(@"ServerCertificateCustomValidationCallback|RemoteCertificateValidationCallback|DangerousAcceptAnyServerCertificateValidator|SslClientAuthenticationOptions|CheckCertificateRevocationList\s*=\s*false");
        Assert.Empty(ProductLines().Where(l => forbidden.IsMatch(l.Text)).Select(l => $"{l.File}:{l.Line}"));
    }

    [Fact]
    public void The_http_client_never_follows_redirects_and_never_uses_cookies()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Mcp", "Transport", "McpHttp.cs"));
        Assert.Contains("AllowAutoRedirect = false", text);
        Assert.Contains("UseCookies = false", text);
    }
}
