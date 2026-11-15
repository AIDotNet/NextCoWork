using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using Ncw.UI.Views;
using TKCode.Arc;
using TKCode.Arc.Motion;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 内联可视化卡片的 Headless 截图(docs/design/03 §2 的四个断点 × 深浅两种主题)。Headless 里没有原生 WebView,所以这里覆盖的是<b>外壳</b>:
/// 「WebView 不可用 → 静态快照」「加载中」「出错」「已过期」「设备断线 unknown」几种状态的布局与结构。WebView 里的真实渲染见 <see cref="NcwWidgetSmokeTests"/>(本机真窗口,环境变量开启)。
/// 截图在 <c>bin/Debug/net10.0/screenshots/widget-*.png</c>。
/// </summary>
public class WidgetScreenshotTests
{
    static WidgetScreenshotTests() => NcwWidgetSurface.DisableForTesting = true; // Headless 里没有原生 WebView:覆盖外壳的降级路径

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    private static string Q(string text) => "\"" + System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text) + "\"";

    private const string Snapshot = "<h3>Quarterly revenue</h3><p>North 42 · South 31 · East 27</p><svg viewBox=\"0 0 200 80\"><text x=\"4\" y=\"20\">Q1 growth +12%</text><text x=\"4\" y=\"40\">Q2 growth +8%</text></svg>";

    private static string CardJson(string title = "revenue_by_region", bool interactive = false, string? submitted = null) =>
        "{\"kind\":\"widget\",\"environment\":{\"deviceId\":\"local\",\"environment\":\"local\",\"name\":\"local\",\"type\":\"local\"}," +
        $"\"title\":{Q(title)},\"format\":\"html\",\"code\":{Q("<div id=\"o\">chart</div><script>1</script>")},\"snapshot\":{Q(Snapshot)}" +
        (interactive ? ",\"interactive\":true" : "") + (submitted is null ? "" : $",\"submitted\":{Q(submitted)}") +
        ",\"findings\":[{\"rule\":\"attr.event\",\"detail\":\"onclick\",\"count\":2}]}";

    private static string ResultWith(string callId, string content, string? details, bool isError = false) =>
        $"{{\"type\":\"tool_result\",\"callId\":\"{callId}\",\"output\":{{\"content\":{Q(content)}{(details is null ? "" : ",\"details\":" + details)}}},\"isError\":{(isError ? "true" : "false")}}}";

    private const string ShowInput = "{\"title\":\"revenue_by_region\",\"widget_code\":\"<div>x</div>\",\"loading_messages\":[\"Drawing the chart\",\"Adding labels\"]}";

    private static ClientStores Scenario(string kind)
    {
        var stores = Base("s-ncw-1");
        var user = User("u1", "Show revenue by region as a chart.");
        switch (kind)
        {
            case "ready":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 9, Text("Here it is."), Call("w1", "visualize_show_widget", ShowInput)), ToolResults("r1", 8, ResultWith("w1", "Rendered.", CardJson()))]);
                break;
            case "loading":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 1, Call("w1", "visualize_show_widget", ShowInput))], activeRunId: "run-1");
                Run(stores, "run-1", "s-ncw-1", v => v with { Tools = v.Tools.Add("w1", new ToolCallView("w1", "visualize_show_widget", ShowInput, ToolCallStatus.Running, null, LocalEnv)) });
                break;
            case "failed":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 9, Call("w1", "visualize_show_widget", ShowInput)), ToolResults("r1", 8, ResultWith("w1", "The widget was rejected.", null, isError: true))]);
                break;
            case "expired":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 9, Call("w1", "visualize_show_widget", "{\"title\":\"pick_db\",\"widget_code\":\"x\",\"interactive\":true}"))]);
                break;
            case "expired-card":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 9, Call("w1", "visualize_show_widget", "{\"title\":\"pick_db\",\"widget_code\":\"x\",\"interactive\":true}")), ToolResults("r1", 8, ResultWith("w1", "closed", CardJson("pick_db", interactive: true)))]);
                break;
            case "unknown":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 1, Call("w1", "visualize_show_widget", ShowInput))], activeRunId: "run-1");
                Run(stores, "run-1", "s-ncw-1", v => v with { Tools = v.Tools.Add("w1", new ToolCallView("w1", "visualize_show_widget", ShowInput, ToolCallStatus.Running, null, LocalEnv)) });
                stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connecting }) });
                break;
            case "offline-card":
                Session(stores, "s-ncw-1", [user, Assistant("a1", 9, Call("w1", "visualize_show_widget", ShowInput)), ToolResults("r1", 8, ResultWith("w1", "Rendered.", CardJson()))]);
                stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connecting }) });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return stores;
    }

    private static (Window Window, ShellViewModel Vm) Render(ClientStores stores, double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(stores, new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = 860, Content = shell };
        window.Show();
        Dispatch();
        return (window, vm);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Shot(Window window, string scenario, double width, bool dark)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"widget-{scenario}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
    }

    private static NcwWidgetHost Host(Window window) => window.GetVisualDescendants().OfType<NcwWidgetHost>().Single();

    private static WidgetRowViewModel Row(ShellViewModel vm) => Assert.IsType<WidgetRowViewModel>(Assert.Single(vm.Conversation.Rows, r => r is WidgetRowViewModel));

    private static IEnumerable<string?> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    private static IEnumerable<Avalonia.Controls.Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible);

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void A_ready_card_without_a_web_view_falls_back_to_the_static_snapshot_with_the_environment_label(int width, bool dark)
    {
        var (window, vm) = Render(Scenario("ready"), width, dark);
        var row = Row(vm);
        var host = Host(window);

        Assert.Equal(WidgetPhase.Ready, row.Phase);
        Assert.False(host.HasSurface);
        Assert.True(host.IsShowingSnapshot);
        var texts = Texts(window).ToList();
        Assert.Contains(texts, t => t is not null && t.Contains("Quarterly revenue", StringComparison.Ordinal));   // 快照里的文字
        Assert.Contains(texts, t => t is not null && t.Contains("Q2 growth +8%", StringComparison.Ordinal));
        Assert.Contains("本机 · local", texts);                                                                      // 执行环境标识
        Assert.Contains("revenue_by_region", texts);
        Assert.Contains(texts, t => t is not null && t.Contains("2 处不安全的内容已被移除", StringComparison.Ordinal));   // 消毒提示
        Assert.DoesNotContain(texts, t => t is not null && t.Contains("<script", StringComparison.Ordinal));          // 快照只当文字显示,不当标记
        Assert.Equal(2, Buttons(window).Count(b => b.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) is "重新加载" or "复制源码"));
        Shot(window, "ready-snapshot", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void A_card_still_being_drawn_shows_the_models_loading_message(int width, bool dark)
    {
        var (window, vm) = Render(Scenario("loading"), width, dark);
        var row = Row(vm);

        Assert.True(row.IsLoading);
        Assert.Null(row.Card);
        Assert.Contains("Drawing the chart", Texts(window));
        Assert.False(Host(window).IsShowingSnapshot);
        Shot(window, "loading", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void A_failed_call_says_so_and_offers_nothing_to_reload(int width, bool dark)
    {
        var (window, vm) = Render(Scenario("failed"), width, dark);

        Assert.True(Row(vm).IsFailed);
        Assert.Contains("无法显示这张可视化。", Texts(window));
        Assert.DoesNotContain(Buttons(window), b => b.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) is "重新加载"); // 没有卡片:没有重载 / 复制按钮
        Shot(window, "failed", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void An_orphaned_call_and_a_closed_interactive_card_are_expired(int width, bool dark)
    {
        var (window, vm) = Render(Scenario("expired"), width, dark);
        Assert.True(Row(vm).ShowExpiredPlaceholder);
        Assert.Contains("这张可视化已过期。", Texts(window));
        Shot(window, "expired", width, dark);

        var (window2, vm2) = Render(Scenario("expired-card"), width, dark);
        var row = Row(vm2);
        Assert.True(row.IsExpired);
        Assert.False(row.CanSubmit);
        Assert.True(row.ShowExpiredBadge);
        Assert.Contains("已过期", Texts(window2));             // 卡片头上的标记;内容(静态快照)照常显示
        Assert.True(Host(window2).IsShowingSnapshot);
        Shot(window2, "expired-card", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void A_disconnected_device_makes_the_card_unknown_not_failed_not_spinning(int width, bool dark)
    {
        var (window, vm) = Render(Scenario("unknown"), width, dark);
        var row = Row(vm);

        Assert.Equal(WidgetPhase.Unknown, row.Phase);
        Assert.True(row.ShowUnknownPlaceholder);
        Assert.Contains("设备已断开——状态未知。", Texts(window));
        Assert.DoesNotContain("正在绘制…", Texts(window));
        Assert.DoesNotContain("Drawing the chart", Texts(window));
        Assert.DoesNotContain("无法显示这张可视化。", Texts(window));
        Shot(window, "unknown", width, dark);

        // 内容已定稿的卡片:断线只是标「离线」(桥不能调设备),内容照常显示
        var (window2, vm2) = Render(Scenario("offline-card"), width, dark);
        var offline = Row(vm2);
        Assert.Equal(WidgetPhase.Ready, offline.Phase);
        Assert.True(offline.ShowOfflineBadge);
        Assert.Contains("离线", Texts(window2));
        Assert.True(Host(window2).IsShowingSnapshot);
        Shot(window2, "offline-card", width, dark);
    }

    [AvaloniaFact]
    public void English_ui_renders_the_card_states()
    {
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.EnUS);
        try
        {
            var (window, _) = Render(Scenario("ready"), 900, dark: true);
            var texts = Texts(window).ToList();
            Assert.Contains("Static preview (read-only)", texts);
            Assert.Contains(texts, t => t is not null && t.Contains("2 unsafe item(s) were removed", StringComparison.Ordinal));
            Shot(window, "ready-snapshot-en", 900, dark: true);

            var (window2, _) = Render(Scenario("unknown"), 900, dark: true);
            Assert.Contains("Device disconnected — status unknown.", Texts(window2));
            Shot(window2, "unknown-en", 900, dark: true);
        }
        finally
        {
            TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
        }
    }

    [AvaloniaFact]
    public void The_phone_shell_never_creates_a_web_view_and_shows_the_static_snapshot()
    {
        NcwWidgetSurface.DisableForTesting = false; // 证明降级来自触控密度,不是测试开关
        try
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(ArcMotionLevel.Off));
            var vm = new ShellViewModel(Scenario("ready"), new FixedClock(Now));
            var shell = new AdaptiveShell();
            var window = new Window { Width = 390, Height = 800, Content = shell };
            window.DataContext = vm;
            window.Show();
            Dispatch();
            vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
            for (var i = 0; i < 40; i++)
            {
                Dispatch();
                Thread.Sleep(20);
            }

            var host = Host(window);
            Assert.False(host.HasSurface);
            Assert.True(host.IsShowingSnapshot);
            Assert.Contains(Texts(window), t => t is not null && t.Contains("Quarterly revenue", StringComparison.Ordinal));
            Shot(window, "mobile-ready-snapshot", 390, dark: true);
        }
        finally
        {
            NcwWidgetSurface.DisableForTesting = true;
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(ArcMotionLevel.Off));
        }
    }
}
