using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Tests.Management;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Management;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 设置页「插件」标签的 Headless 截图:已安装列表 × 四个断点(1500 / 1200 / 900 / 600)× 深浅主题;其余场景(权限含敏感高亮与待批准、配置表单、诊断与 faulted、市场含详情与 authRequired 提示、
/// 待处理提示含执行审批的设备 + 环境、错误、viewer 读不了、member 只读、二次确认)各拍宽屏深色和窄屏浅色。数据经脚本化后端和 Store 进来。截图在 <c>bin/Debug/net10.0/screenshots/plugins-*.png</c>。
/// </summary>
public class PluginsScreenshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed record Host(Window Window, ShellViewModel Shell, DesktopShell View, ManagementKit Kit);

    private sealed record Scenario(string Name, string Role, Action<ManagementKit> Seed, Func<Host, Task>? Act = null, Action<Host>? Check = null, double Height = 1800);

    // ───────────────────────── 数据 ─────────────────────────

    private static readonly PluginConfigurationDto Schema = new("%config.title%",
    [
        new PluginConfigPropertyDto("greeting", "string", "%config.greeting%", "%config.greeting.hint%", [], "Hello", null, null),
        new PluginConfigPropertyDto("loud", "boolean", "%config.loud%", null, [], null, null, false),
        new PluginConfigPropertyDto("retries", "number", "%config.retries%", null, [], null, 3, null),
        new PluginConfigPropertyDto("mode", "enum", "%config.mode%", null, ["fast", "safe"], "safe", null, null),
    ]);

    private static readonly PluginMessagesDto[] Messages =
    [
        new("en-US", new Dictionary<string, string>
        {
            ["plugin.acme.hello.config.title"] = "Hello settings", ["plugin.acme.hello.config.greeting"] = "Greeting", ["plugin.acme.hello.config.greeting.hint"] = "Said when the plugin starts.",
            ["plugin.acme.hello.config.loud"] = "Say it loudly", ["plugin.acme.hello.config.retries"] = "Retries", ["plugin.acme.hello.config.mode"] = "Mode",
            ["plugin.acme.hello.cmd.hello"] = "Say hello", ["plugin.acme.hello.cmd.report"] = "Write a report",
            ["plugin.acme.hello.confirm.title"] = "Delete the cache folder?", ["plugin.acme.hello.confirm.detail"] = "The folder is rebuilt on the next run.",
            ["plugin.acme.hello.ask.name"] = "Name the new branch", ["plugin.acme.hello.pick.which"] = "Which environment should it deploy to?",
            ["plugin.acme.hello.pick.staging"] = "Staging", ["plugin.acme.hello.pick.prod"] = "Production",
        }),
        new("zh-CN", new Dictionary<string, string>
        {
            ["plugin.acme.hello.config.title"] = "Hello 设置", ["plugin.acme.hello.config.greeting"] = "问候语", ["plugin.acme.hello.config.greeting.hint"] = "插件启动时说的话。",
            ["plugin.acme.hello.config.loud"] = "大声说", ["plugin.acme.hello.config.retries"] = "重试次数", ["plugin.acme.hello.config.mode"] = "模式",
            ["plugin.acme.hello.cmd.hello"] = "打个招呼", ["plugin.acme.hello.cmd.report"] = "生成报告",
            ["plugin.acme.hello.confirm.title"] = "删除缓存文件夹?", ["plugin.acme.hello.confirm.detail"] = "下次运行时会重新生成这个文件夹。",
            ["plugin.acme.hello.ask.name"] = "给新分支起个名字", ["plugin.acme.hello.pick.which"] = "部署到哪个环境?",
            ["plugin.acme.hello.pick.staging"] = "预发", ["plugin.acme.hello.pick.prod"] = "生产",
        }),
    ];

    private static void SeedPlugins(ManagementKit kit)
    {
        kit.Stores.Selection.Update(_ => new Ncw.Client.Stores.SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), null));
        var plugins = kit.Plugins;
        plugins.Plugins.AddRange([
            FakePlugins.Info("acme.hello", "Hello", required: ["storage", "workspace.read"], optional: ["process", "net"], granted: ["storage", "workspace.read"],
                allowedCommands: ["echo", "git status"], hostPermissions: ["https://api.example.com/*"], config: Schema, messages: Messages, registeredTools: ["think", "summarize"],
                commands: [new PluginCommandDto("acme.hello.hello", "%cmd.hello%", "sparkles"), new PluginCommandDto("acme.hello.report", "%cmd.report%", null)],
                description: "Greets you and writes small reports about your project.", slug: "hello"),
            FakePlugins.Info("acme.fmt", "Formatter", PluginStatuses.Active, required: ["workspace.write"], granted: ["workspace.write"], version: "2.4.1", description: "Formats files with your team's rules.", slug: "fmt"),
            FakePlugins.Info("acme.wait", "Release notes", PluginStatuses.PendingApproval, required: ["storage", "scm.read", "net"], granted: ["storage"], hostPermissions: ["https://github.com/*"], description: "Drafts release notes from merged pull requests."),
            FakePlugins.Info("acme.crash", "Linter", PluginStatuses.Faulted, required: ["workspace.read"], version: "0.9.2", description: "Runs lint rules on save.",
                diagnostics: [FakePlugins.Diag("plugins.diag.faulted", "The plugin ran past its 5 s call budget and was terminated.", "error", "lint.js"), FakePlugins.Diag("plugins.diag.permissionDenied", "agent.intercept is not granted", "warn", "agent.registerInterceptor")]),
            FakePlugins.Info("acme.off", "Notes sync", PluginStatuses.Disabled, enabled: false, required: ["storage"], granted: ["storage"], description: "Keeps notes in sync across devices.",
                diagnostics: [FakePlugins.Diag("plugins.diag.unsupported", "contributes.views is not supported in 3.0", "info", "package.json")]),
            FakePlugins.Info("acme.bad", "Broken sample", PluginStatuses.Error, enabled: false, description: "Could not be loaded.",
                diagnostics: [FakePlugins.Diag("plugins.diag.mainMissing", "main.js is missing from the package", "error", "package.json")]),
        ]);
        plugins.ActivityEntries.AddRange([
            new PluginActivityDto(Now.AddMinutes(-2).ToUnixTimeMilliseconds(), "acme.hello", "workspace.readFile", "read src/App.cs", "ok", 4),
            new PluginActivityDto(Now.AddMinutes(-3).ToUnixTimeMilliseconds(), "acme.fmt", "workspace.writeFile", "wrote src/App.cs", "ok", 11),
            new PluginActivityDto(Now.AddMinutes(-7).ToUnixTimeMilliseconds(), "acme.hello", "process.exec", "echo hello", "denied", 1),
            new PluginActivityDto(Now.AddMinutes(-9).ToUnixTimeMilliseconds(), "acme.crash", "workspace.findFiles", "find **/*.cs", "timeout", 5000),
            new PluginActivityDto(Now.AddMinutes(-12).ToUnixTimeMilliseconds(), "acme.hello", "net.fetch", "GET https://api.example.com/v1/greeting", "error", 230),
        ]);
        plugins.MarketItems.AddRange([
            FakePlugins.MarketItem("hello", "Hello", ["storage", "workspace.read"], "1.2.0", pluginId: "acme.hello"),
            FakePlugins.MarketItem("pdf-tools", "PDF tools", ["net", "workspace.read"], "0.8.0", ["pdf-tools", "pdf-forms"]),
            FakePlugins.MarketItem("deploy", "One-click deploy", ["process", "net"], "3.0.1"),
        ]);
        plugins.MarketHasNext = true;
        plugins.UpdateList = [new PluginUpdateDto("acme.hello", "hello", "Hello", "1.0.0", "1.2.0", ["net"], true), new PluginUpdateDto("acme.off", "off", "Notes sync", "1.0.0", "1.1.0", [], false)];
    }

    private static void SeedPrompts(ManagementKit kit)
    {
        var p = kit.Plugins;
        p.Interactions.AddRange([
            FakePlugins.Exec("r1", "acme.hello", "git push origin feature/login --force-with-lease", "ssh-build", "build-box (SSH)", "ssh", "dev_8f3a1c"),
            FakePlugins.Confirm("r2", "acme.hello", "confirm.title", "confirm.detail", danger: true),
            FakePlugins.Input("r3", "acme.hello", "ask.name", "ask.name", "feature/"),
            FakePlugins.Pick("r4", "acme.hello", "pick.which", ("staging", "pick.staging"), ("prod", "pick.prod")),
            FakePlugins.PermissionRequest("r5", "acme.wait", "confirm.title", "process", "net"),
        ]);
    }

    // ───────────────────────── 渲染 ─────────────────────────

    private static Host Render(Scenario scenario, double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var kit = new ManagementKit(scenario.Role, clockNow: Now);
        scenario.Seed(kit);
        var shell = new ShellViewModel(kit.Stores, kit.Clock, management: kit.Pages, scheduledTasks: kit.Scheduled);
        var view = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = scenario.Height, Content = view };
        window.Show();
        Dispatch();
        shell.Settings.OpenCommand.Execute(null);
        shell.Settings.ShowCommand.Execute(SettingsSection.Plugins);
        Dispatch(10);
        var host = new Host(window, shell, view, kit);
        if (scenario.Act is { } act)
        {
            act(host).GetAwaiter().GetResult();
            Dispatch(10);
        }

        return host;
    }

    private static void Dispatch(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Host host, string name, double width, bool dark)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        host.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"plugins-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Finish() => ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));

    private static SettingsOverlay Overlay(Host host) => host.Window.GetVisualDescendants().OfType<SettingsOverlay>().Single();

    private static PluginRowViewModel Row(Host host, string id) => host.Kit.Pages.Plugins.Plugins.Single(p => p.Id == id);

    private static readonly Scenario Installed = new("installed", "owner", SeedPlugins, Height: 1500, Check: h =>
    {
        Assert.Equal(PageState.Ready, h.Kit.Pages.Plugins.State);
        Assert.Equal(6, h.Kit.Pages.Plugins.Plugins.Count);
        Assert.NotEmpty(h.Window.GetVisualDescendants().OfType<PluginsPage>());
    });

    public static IEnumerable<object[]> InstalledMatrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    [AvaloniaTheory]
    [MemberData(nameof(InstalledMatrix))]
    public void Installed_list(int width, bool dark)
    {
        var host = Render(Installed, width, dark);
        Installed.Check?.Invoke(host);
        Assert.True(Overlay(host).IsCompact == width < 760, "窄屏时导航收成图标栏");
        Snapshot(host, "installed", width, dark);
        Finish();
    }

    // ───────────────────────── 其余场景 ─────────────────────────

    private static Action<ManagementKit> With(Action<ManagementKit> tweak) => kit =>
    {
        SeedPlugins(kit);
        tweak(kit);
    };

    private static readonly Scenario[] Extra =
    [
        new("permissions", "owner", SeedPlugins, Height: 2300, Act: h =>
        {
            Row(h, "acme.hello").IsExpanded = true;
            Row(h, "acme.wait").IsExpanded = true;
            return Task.CompletedTask;
        }, Check: h =>
        {
            Assert.Contains(Row(h, "acme.hello").Permissions, p => p.IsDangerous);
            Assert.True(Row(h, "acme.wait").IsPendingApproval);
        }),
        new("config", "owner", SeedPlugins, Height: 2300, Act: async h =>
        {
            var hello = Row(h, "acme.hello");
            hello.IsExpanded = true; // 假设备同步完成:配置值立即到位
            hello.ConfigFields.Single(f => f.Key == "greeting").Text = "Howdy, partner"; // 未应用的草稿
            h.Kit.Plugins.Config["acme.hello"] = new() { ["mode"] = new("fast", null, null) };
            await h.Kit.Pages.Plugins.LoadConfigAsync(hello);
            hello.ConfigFields.Single(f => f.Key == "greeting").Error = "plugins.configurationInvalid";
        }, Check: h =>
        {
            Assert.Equal(4, Row(h, "acme.hello").ConfigFields.Count);
            Assert.True(Row(h, "acme.hello").ConfigFields[0].IsDirty);
            Assert.Equal("Howdy, partner", Row(h, "acme.hello").ConfigFields.Single(f => f.Key == "greeting").Text);
        }),
        new("diagnostics", "owner", SeedPlugins, Height: 2200, Act: async h =>
        {
            h.Kit.Pages.Plugins.ShowActivityCommand.Execute(null);
            await h.Kit.Pages.Plugins.LoadActivityCommand.ExecuteAsync(null);
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Plugins.HasFaulted);
            Assert.Equal(5, h.Kit.Pages.Plugins.Activity.Count);
            Assert.Equal(4, h.Kit.Pages.Plugins.Diagnostics.Count);
        }),
        new("market", "owner", With(k => k.Plugins.AuthRequired = true), Height: 2400, Act: async h =>
        {
            var page = h.Kit.Pages.Plugins;
            page.ShowMarketCommand.Execute(null);
            await page.SearchMarketCommand.ExecuteAsync(null);
            await page.CheckUpdatesCommand.ExecuteAsync(null);
            await page.ShowDetailCommand.ExecuteAsync(page.Market[1]);
            await page.InstallMarketCommand.ExecuteAsync("deploy");
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Plugins.AuthRequired);
            Assert.Equal(3, h.Kit.Pages.Plugins.Market.Count);
            Assert.True(h.Kit.Pages.Plugins.Market.Single(m => m.Slug == "hello").HasUpdate);
        }),
        new("prompts", "owner", With(SeedPrompts), Height: 2400, Check: h =>
        {
            var exec = h.Kit.Pages.Plugins.Prompts.Single(p => p.IsExec);
            Assert.True(exec.HasEnvironment && exec.Danger);
            Assert.Equal(5, h.Kit.Pages.Plugins.Prompts.Count);
        }),
        new("error", "owner", With(k => k.Plugins.NextListFailure = R.Fail<PluginsTopicState>(Ncw.Client.Actions.ActionOutcome.Failed, "plugins.operationFailed")), Height: 700,
            Check: h => Assert.Equal(PageState.Error, h.Kit.Pages.Plugins.State)),
        new("market-error", "owner", With(k => k.Plugins.NextMarketFailure = R.Fail<PluginsMarketListResult>(Ncw.Client.Actions.ActionOutcome.Failed, "plugins.marketFailed")), Height: 900, Act: async h =>
        {
            h.Kit.Pages.Plugins.ShowMarketCommand.Execute(null); // 切到市场标签自己发起第一次搜索
            await Task.CompletedTask;
        }, Check: h => Assert.True(h.Kit.Pages.Plugins.MarketFailed)),
        new("viewer", "viewer", SeedPlugins, Height: 700, Check: h => Assert.Equal(PageState.Forbidden, h.Kit.Pages.Plugins.State)),
        new("member", "member", With(SeedPrompts), Height: 2400, Act: h =>
        {
            Row(h, "acme.hello").IsExpanded = true;
            return Task.CompletedTask;
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Plugins.IsReadOnly);
            Assert.True(h.Kit.Pages.Plugins.CanAnswer);
        }),
        new("offline", "owner", SeedPlugins, Height: 700, Act: h =>
        {
            h.Kit.Rig.SetConnection(DeviceConnection.Offline);
            return Task.CompletedTask;
        }, Check: h => Assert.Equal(PageState.Unknown, h.Kit.Pages.Plugins.State)),
        new("empty", "owner", With(k => k.Plugins.Plugins.Clear()), Height: 800, Check: h => Assert.True(h.Kit.Pages.Plugins.ShowsEmpty)),
        new("confirm", "owner", SeedPlugins, Height: 1700, Act: async h =>
        {
            Row(h, "acme.fmt").AskUninstallCommand.Execute(null);
            Row(h, "acme.hello").IsExpanded = true;
            await Row(h, "acme.hello").Permissions.First(p => p.Name == "storage").ToggleCommand.ExecuteAsync(null);
        }, Check: h => Assert.True(h.Kit.Pages.Plugins.HasPendingRevoke)),
    ];

    public static IEnumerable<object[]> ExtraMatrix() =>
        from scenario in Extra.Select(s => s.Name)
        from combo in new[] { (1500, true), (600, false) }
        select new object[] { scenario, combo.Item1, combo.Item2 };

    [AvaloniaTheory]
    [MemberData(nameof(ExtraMatrix))]
    public void Extra_state(string name, int width, bool dark)
    {
        var scenario = Extra.Single(s => s.Name == name);
        var host = Render(scenario, width, dark);
        scenario.Check?.Invoke(host);
        Snapshot(host, name, width, dark);
        Finish();
    }
}
