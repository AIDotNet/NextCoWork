using System.Diagnostics;

namespace Ncw.UI.Tests;

/// <summary>
/// 内联可视化的真实 WebView 冒烟(<b>默认跳过</b>):Headless 里没有原生 WebView,WebView 里的真实行为只能在有图形会话的机器上验证。
/// 设置环境变量 <c>NCW_WIDGET_SMOKE=1</c> 后,本测试起 <c>tools/WidgetSmoke</c>(真窗口 + 真实外壳 + 真实 NativeWebView + 真实宿主桥),
/// 页面里的 widget 代码跑一批检查和一轮恶意载荷(CSP 是否生效、导航 / 弹窗是否被拦、桥是否双向、主题 / 状态 / 数据是否推到、报高、伪造消息 / 洪水),逐项断言。
/// <para><b>只在 macOS(WKWebView)上验证过</b>:Windows(WebView2)、Linux(WebKitGTK / WPE)是同一份代码、同一个测试,但没有机器可跑;结论见 docs/design/03 §8。</para>
/// </summary>
public class NcwWidgetSmokeTests
{
    [Fact]
    public async Task The_real_web_view_enforces_the_csp_and_navigation_policy_and_the_bridge_survives_a_hostile_widget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NCW_WIDGET_SMOKE") == "1", "Set NCW_WIDGET_SMOKE=1 to run the real WebView smoke (needs a graphical session; verified on macOS only).");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NextCoWork.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var start = new ProcessStartInfo("dotnet", ["run", "--project", Path.Combine(root.FullName, "tools", "WidgetSmoke")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root.FullName,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The WebView smoke did not finish in 4 minutes.\n" + await stdout);
        }

        var output = await stdout;
        var lines = output.Split('\n').Where(l => l.StartsWith("SMOKE ", StringComparison.Ordinal)).ToList();
        var failed = lines.Where(l => l.Contains(" FAIL ", StringComparison.Ordinal)).ToList();
        Assert.True(lines.Count >= 20, $"The smoke produced too few results:\n{output}\n{await stderr}");
        Assert.True(failed.Count == 0, "WebView smoke failures:\n" + string.Join('\n', failed) + "\n\nFull output:\n" + output);
        Assert.Contains(lines, l => l.Contains("attack-bridge-locked PASS", StringComparison.Ordinal));
    }
}
