using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ncw.Agent.Widgets.Sanitizing;
using Ncw.Client.Widgets;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>widget 渲染外壳与服务端的一致性:CDN 白名单不比服务端宽、主题映射来自 Arc token(深浅不同)、WebView 只有一个文件碰。</summary>
public class WidgetHostTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Theory]
    [InlineData("src/Ncw.Desktop/Ncw.Desktop.csproj")]
    [InlineData("tools/WidgetSmoke/WidgetSmoke.csproj")]
    public void Windows_widget_hosts_use_the_shared_manifest_with_layered_child_window_support(string projectPath)
    {
        var root = RepoRoot();
        var project = Path.Combine(root, projectPath);
        var manifest = Assert.Single(XDocument.Load(project).Descendants("ApplicationManifest"));
        var manifestPath = Path.GetFullPath(manifest.Value, Path.GetDirectoryName(project)!);
        Assert.Equal(Path.Combine(root, "src", "Ncw.Desktop", "app.manifest"), manifestPath);

        var assembly = Assert.IsType<XElement>(XDocument.Load(manifestPath).Root);
        XNamespace asm = "urn:schemas-microsoft-com:asm.v1";
        XNamespace compat = "urn:schemas-microsoft-com:compatibility.v1";
        Assert.Equal(asm + "assembly", assembly.Name);
        var application = assembly.Element(compat + "compatibility")?.Element(compat + "application");
        Assert.NotNull(application);
        Assert.Contains(application.Elements(compat + "supportedOS"), e =>
            (string?)e.Attribute("Id") == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
        Assert.DoesNotContain(assembly.Descendants(), e => e.Name.LocalName is "dpiAware" or "dpiAwareness");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Web_view_attachment_failure_is_reported_after_the_attach_callback_returns(bool asynchronous)
    {
        using var surface = new NcwWidgetSurface();
        var failures = new List<(WidgetSurfaceFailure, string?)>();
        surface.Failed += (failure, detail) => failures.Add((failure, detail));
        var context = SynchronizationContext.Current;

        surface.AttachWebView(asynchronous ? FailLater : Fail);

        Assert.Same(context, SynchronizationContext.Current);
        Assert.Empty(failures);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((WidgetSurfaceFailure.Unavailable, "cannot create native child window"), Assert.Single(failures));

        static void Fail() => throw new InvalidOperationException("cannot create native child window");

        // 与 Avalonia 的附着入口相同:async void 的异常会回投到捕获的同步上下文。
        static async void FailLater()
        {
            await Task.Yield();
            Fail();
        }
    }

    [Fact]
    public void The_shells_cdn_whitelist_is_exactly_the_servers_and_every_server_host_is_usable()
    {
        // 客户端不能引用 Ncw.Agent.*,所以两边各写了一张表;这里(能同时引用两边的测试项目)核对它们逐项相同:不得比服务端宽,也不能窄到让规范里的例子静默失效
        Assert.Equal(WidgetUrls.CdnHosts.Order(StringComparer.Ordinal), WidgetShell.CdnHosts.Order(StringComparer.Ordinal));
        var csp = WidgetShell.Csp("n");
        foreach (var host in WidgetUrls.CdnHosts)
        {
            Assert.True(WidgetUrls.IsCdnUrl($"https://{host}/lib.js"));
            Assert.Contains($"https://{host}", csp);
        }
    }

    [Fact]
    public void Only_one_file_in_the_ui_project_touches_the_native_web_view()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Ncw.UI"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal)))
            .Where(f => File.ReadAllText(f).Contains("NativeWebView", StringComparison.Ordinal) || File.ReadAllText(f).Contains("Avalonia.Controls.WebView", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();
        Assert.Equal(["NcwWidgetSurface.cs"], offenders);

        // 客户端 / Agent 层不引用 WebView 包
        foreach (var project in new[] { "src/Ncw.Client/Ncw.Client.csproj", "src/Ncw.Core/Ncw.Core.csproj", "src/Ncw.Agent.Widgets/Ncw.Agent.Widgets.csproj", "src/Ncw.Agent/Ncw.Agent.csproj" })
        {
            Assert.DoesNotContain("WebView", File.ReadAllText(Path.Combine(RepoRoot(), project)), StringComparison.Ordinal);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_theme_pushed_to_widgets_is_read_from_the_current_arc_tokens(bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        try
        {
            var host = new NcwWidgetHost();
            var window = new Window { Content = host };
            window.Show();
            var theme = NcwWidgetTheme.Current(host);

            Assert.Equal(dark ? WidgetAppearance.Dark : WidgetAppearance.Light, theme.Appearance);
            foreach (var name in new[] { "--color-text-primary", "--color-text-secondary", "--color-text-tertiary", "--color-background-primary", "--color-background-secondary", "--color-background-tertiary",
                "--color-text-info", "--color-border-secondary", "--p", "--b", "--font-sans", "--font-mono", "--border-radius-md" })
            {
                Assert.True(theme.Tokens.ContainsKey(name), $"{name} is missing: every Arc token should resolve");
            }

            Assert.All(theme.Tokens.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            // 文字色就是 Arc 的 Foreground(换了主题就变),不是写死的
            var foreground = ArcTokens.GetColor(host, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Foreground);
            Assert.Equal(WidgetTokens.CssColor(foreground.A, foreground.R, foreground.G, foreground.B), theme.Tokens["--color-text-primary"]);
            Assert.Contains("system-ui", theme.Tokens["--font-sans"]);
            Assert.Contains("monospace", theme.Tokens["--font-mono"]);
            window.Close();
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    [AvaloniaFact]
    public void Dark_and_light_produce_different_text_and_surface_colors()
    {
        string Read(bool dark)
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var host = new NcwWidgetHost();
            var window = new Window { Content = host };
            window.Show();
            var t = NcwWidgetTheme.Current(host).Tokens;
            window.Close();
            return t["--color-text-primary"] + "|" + t["--color-background-primary"];
        }

        try
        {
            Assert.NotEqual(Read(true), Read(false));
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }
}
