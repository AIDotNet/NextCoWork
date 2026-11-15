using System.Text.RegularExpressions;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>
/// AGENTS.md 里能机械检查的几条:Jaunt 只在这个项目里(§6),<c>JsEngine</c> 只在插件运行时里创建、必须设 <c>JsLimits</c> 和 <c>JsProfile.Hardened</c>;
/// 本层不知道协议 / Avalonia / 客户端 / Host(§5);不用反射 / 反射序列化(§2);文件和进程只经执行环境(§5);不产出界面文字(§2);宿主 API 只能显式绑定、每个绑定先做权限检查(§6)。
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

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>去掉行尾注释(<c>https://</c> 这类不算)。</summary>
    private static string CodeOnly(string line) => Regex.Replace(line, @"(?<!:)//.*$", "");

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f));

    private static IEnumerable<(string File, int Line, string Text)> Code(IEnumerable<string> files) =>
        files.SelectMany(f => File.ReadLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !x.Text.TrimStart().StartsWith('*') && !x.Text.TrimStart().StartsWith("///", StringComparison.Ordinal));

    [Fact]
    public void Only_the_plugin_host_references_jaunt_and_only_it_creates_engines()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tools"), "*.csproj", SearchOption.AllDirectories)))
        {
            if (File.ReadAllText(project).Contains("\"Jaunt\"", StringComparison.Ordinal) && !project.EndsWith("Ncw.Agent.Plugins.csproj", StringComparison.Ordinal))
            {
                offenders.Add(project);
            }
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var text = File.ReadAllText(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}Ncw.Agent.Plugins{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(text, @"using Jaunt;|\bnew\s+JsEngine\b|\bJsPlugin\b"))
            {
                offenders.Add(file);
            }
        }

        Assert.Empty(offenders);
        // 版本只写在 Directory.Packages.props 里
        Assert.Contains("<PackageVersion Include=\"Jaunt\"", File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Packages.props")), StringComparison.Ordinal);
        Assert.DoesNotContain("Version=", Regex.Match(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Plugins", "Ncw.Agent.Plugins.csproj")), "<PackageReference Include=\"Jaunt\"[^>]*>").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_engine_is_hardened_and_budgeted_and_every_binding_comes_from_the_explicit_method_table()
    {
        var runtime = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Plugins", "Runtime", "PluginRuntime.cs"));
        Assert.Single(Regex.Matches(runtime, @"new JsEngine\(")); // 唯一的创建点
        Assert.Contains("Profile = JsProfile.Hardened", runtime, StringComparison.Ordinal);
        Assert.Contains("Limits = new JsLimits", runtime, StringComparison.Ordinal);
        Assert.Contains("MaxInstructions", runtime, StringComparison.Ordinal);
        Assert.Contains("MaxTrackedBytes", runtime, StringComparison.Ordinal);

        // 绑定只在 PluginBinder 里,宿主方法全部来自方法表;每个绑定先经 CallAsync(白名单 → 授权 → 参数 → 审批)
        var binder = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ncw.Agent.Plugins", "Runtime", "PluginBinder.cs"));
        Assert.Contains("foreach (var method in PluginHostMethods.All)", binder, StringComparison.Ordinal);
        Assert.Contains("host.Permissions.Has(permission)", binder, StringComparison.Ordinal);
        var bindingSites = Code(Sources("Ncw.Agent.Plugins")).Where(x => Regex.IsMatch(x.Text, @"\.SetAsyncFunction<|\.SetFunction\(|\.SetFunction<")).Select(x => Path.GetFileName(x.File)).Distinct();
        Assert.Equal(["PluginBinder.cs"], bindingSites);
    }

    [Fact]
    public void Every_host_method_that_reaches_the_device_declares_its_permission_in_the_table()
    {
        var methods = Code(Sources("Ncw.Agent.Plugins")).Where(x => x.File.EndsWith("HostMethods.cs", StringComparison.Ordinal) && x.Text.TrimStart().StartsWith("new(\"", StringComparison.Ordinal))
            .Select(x => Regex.Match(x.Text, @"new\(""([^""]+)"",\s*([^,]+),")).Where(m => m.Success).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

        Assert.Equal("PluginPermissions.WorkspaceRead", methods["workspace.readFile"]);
        Assert.Equal("PluginPermissions.WorkspaceWrite", methods["workspace.writeFile"]);
        Assert.Equal("PluginPermissions.WorkspaceWrite", methods["workspace.deleteFile"]);
        Assert.Equal("PluginPermissions.Process", methods["process.exec"]);
        Assert.Equal("PluginPermissions.Net", methods["net.fetch"]);
        Assert.Equal("PluginPermissions.Storage", methods["storage.set"]);
        Assert.Equal("PluginPermissions.Secrets", methods["secrets.get"]);
        // 不要任何能力的只有「读自己的身份 / 自查 / 提问 / 内部」这几类,没有一个碰文件、进程、网络
        var open = methods.Where(kv => kv.Value == "null").Select(kv => kv.Key).Order().ToList();
        Assert.Equal(
            ["appearance.get", "configuration.get", "diagnostics.log", "env.appInfo", "permissions.contains", "permissions.remove", "permissions.request", "window.setStatusBarItem", "window.showConfirm", "window.showInputBox", "window.showQuickPick"],
            open);
    }

    [Fact]
    public void The_assembly_does_not_reference_the_protocol_ui_client_or_host()
    {
#pragma warning disable IL2026 // 测试里读程序集引用,不涉及裁剪
        var referenced = typeof(PluginManager).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
#pragma warning restore IL2026
        Assert.DoesNotContain(referenced, n => n is "Ncw.Protocol" or "Ncw.Client" or "Ncw.UI" or "Ncw.Agent.Host" or "Ncw.Desktop" || n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains("Jaunt", referenced);
        Assert.Contains("Ncw.Agent.Environments", referenced);
    }

    [Fact]
    public void The_sources_use_no_reflection_no_reflection_serialisation_and_no_direct_file_or_process_access()
    {
        // 文件只经 IExecutionEnvironment(AGENTS.md §5):本层产品代码里没有 System.IO 的文件 API 和 Process。zip 读内存 Stream(System.IO.Compression)是允许的。
        var forbidden = new Regex(
            @"System\.Reflection|\bActivator\.|\.GetType\(\)\.Get\w+\(|MakeGenericType|JsonSerializer\.(Serialize|Deserialize)\s*<|new\s+Process\b|Process\.Start|(?<![\w.])File\.\w+|(?<![\w.])Directory\.\w+|\bFileStream\b|\bStreamReader\b|\bDirectoryInfo\b|\bFileInfo\b|\bEnvironment\.GetEnvironmentVariable",
            RegexOptions.CultureInvariant);
        var offenders = Code(Sources("Ncw.Agent.Plugins")).Where(x => forbidden.IsMatch(CodeOnly(x.Text))).Select(x => $"{Path.GetFileName(x.File)}:{x.Line}: {x.Text.Trim()}").ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_user_visible_text_is_produced_here_keys_are_stable_and_messages_are_english()
    {
        Assert.Equal("plugins.notFound", PluginErrorKeys.NotFound);
        Assert.Equal("plugins.authRequired", PluginErrorKeys.AuthRequired);
        Assert.Equal("plugins.diag.permissionDenied", PluginDiagnosticKeys.PermissionDenied);

        // 本层源码里没有 CJK 字面量(诊断与错误消息是英文;界面文字是控制端按 key 翻译的)
        var cjk = new Regex(@"[一-鿿]");
        var offenders = Code(Sources("Ncw.Agent.Plugins")).Where(x => cjk.IsMatch(CodeOnly(x.Text))).Select(x => $"{Path.GetFileName(x.File)}:{x.Line}").ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_prelude_is_javascript_only_and_the_repository_has_no_node_or_typescript_build_step()
    {
        // AGENTS.md §6:整个仓库只用 C#,不引入 Node / npm / TypeScript 构建步骤。插件 JS 是数据(夹具、测试里的字符串),不是仓库的构建输入。
        var root = RepoRoot();
        foreach (var banned in new[] { "package.json", "package-lock.json", "tsconfig.json", "pnpm-lock.yaml", "yarn.lock" })
        {
            var found = Directory.EnumerateFiles(root, banned, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Fixtures{Path.DirectorySeparatorChar}") && !IsBuildOutput(f) && !f.Contains($"{Path.DirectorySeparatorChar}out{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.Empty(found);
        }
    }
}
