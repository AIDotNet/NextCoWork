using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Management;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Management;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 设置页的管理标签(通用、项目、技能、命令、Hooks、MCP、用量、存储、网关)和侧栏「定时任务」页的 Headless 截图:
/// 典型状态 × 四个断点(1500 / 1200 / 900 / 600)× 深浅主题;另有空状态、错误、viewer / member 只读、设备断开(未知)、二次确认、表单等场景,
/// 每个场景在宽屏深色和窄屏浅色各拍一张。数据经脚本化后端和 Store 进来(和设备推送的形状一致),不经过连接。截图在 <c>bin/Debug/net10.0/screenshots/mgmt-*.png</c>。
/// </summary>
public class ManagementScreenshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed record Host(Window Window, ShellViewModel Shell, DesktopShell View, ManagementKit Kit);

    private sealed record Scenario(string Name, SettingsSection? Section, Action<ManagementKit> Seed, Func<Host, Task>? Act = null, Action<Host>? Check = null, double Height = 1100);

    // ───────────────────────── 数据 ─────────────────────────

    private static void SeedAll(ManagementKit kit)
    {
        kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), null));
        kit.Rig.SeedModels(ManagementRig.Option("anthropic", "sonnet", "Claude Sonnet"), ManagementRig.Option("anthropic", "haiku", "Claude Haiku"), ManagementRig.Option("openai", "gpt", "GPT"));
        kit.Settings.Push(FakeSettings.Defaults() with
        {
            PermissionReviewer = new ModelPickDto("haiku", "anthropic"),
            Subagent = new SubagentSettingsDto(new ModelPickDto("haiku", "anthropic"), "inherit", 3, 5),
            Personalization = new PersonalizationSettingsDto("张三", "后端工程师,主要写 C# 与 Go。", "回答简洁,先给结论。"),
            Data = new DataSettingsDto("/Volumes/Backups/ncw", "daily"),
        });

        kit.Skills.Skills.AddRange([
            FakeSkills.Skill("pdf", "pdf-tools", "global", "zip"),
            FakeSkills.Skill("review", "code-review", "global", "folder"),
            FakeSkills.Skill("local", "release-notes", "project", "folder", active: false),
            FakeSkills.Skill("mac", "keynote-export", "global", "folder", assets: true),
            FakeSkills.Skill("plug", "gh-actions", "plugin", "plugin", global: false),
        ]);
        kit.Skills.Diagnostics = [new SkillDiagnosticDto("/Users/me/.next-cowork/skills/broken", "skills.skillMdMissing", "SKILL.md missing")];
        kit.Skills.Market = [
            new SkillMarketItemDto("pdf-tools", "pdf-tools", "PDF 工具箱", "读取、合并、拆分 PDF,提取表格。", "dev", null, "NextCoWork", "1.2.0", null, 120_000, 3200, 14),
            new SkillMarketItemDto("sql", "sql-helper", "SQL 助手", "按表结构写查询,解释执行计划。", "dev", null, "community", "0.9.1", null, 80_000, 1100, 6),
        ];

        kit.Commands.All.AddRange([
            new CommandItemDto("review", "审查当前分支的改动", "global", "~/.next-cowork/commands/review.md", "[分支]", true),
            new CommandItemDto("release", "生成发布说明", "project", ".next-cowork/commands/release.md", null, true),
            new CommandItemDto("compact", "压缩上下文", "builtin", "builtin", null, true),
            new CommandItemDto("deploy", "部署到测试环境", "project", ".next-cowork/commands/deploy.md", "<环境>", false),
        ]);
        kit.Commands.Diagnostics = [new CommandDiagnosticDto(".next-cowork/commands/bad.md", "commands.frontmatter", "bad frontmatter")];

        kit.Hooks.Hooks.AddRange([
            FakeHooks.Hook("h1", "PreToolUse", "global", true, "Bash(rm *)", "echo 'blocked' >&2; exit 2"),
            FakeHooks.Hook("h2", "PostToolUse", "project", true, "Edit|Write", "dotnet format --include $FILE", ["DOTNET_ROOT"]),
            FakeHooks.Hook("h3", "Stop", "global", false, null, "osascript -e 'display notification \"完成\"'"),
        ]);
        kit.Hooks.Diagnostics = [new HookDiagnosticDto("/repo/.next-cowork/settings.local.json", "hooks.diagnostic.timeout", "timeout")];

        kit.Mcp.Servers.AddRange([
            FakeMcp.Server("filesystem"),
            FakeMcp.Server("github", McpStates.Connected, McpTransports.StreamableHttp),
            FakeMcp.Server("postgres", McpStates.Failed, McpTransports.Stdio, "mcp.connect.commandNotFound"),
            FakeMcp.Server("slack", McpStates.Connecting, McpTransports.Sse),
            FakeMcp.Server("legacy", McpStates.Disabled, enabled: false),
        ]);
        kit.Mcp.Push();

        for (var i = 29; i >= 0; i--)
        {
            var day = DateOnly.FromDateTime(Now.AddDays(-i).Date).ToString("yyyy-MM-dd");
            var load = 3 + (i * 7 % 11);
            kit.Usage.Buckets.Add(FakeUsage.Bucket(day, requests: load, input: load * 9_000, output: load * 2_400, cost: load * 110_000L));
        }

        kit.Usage.Summary = new UsageSummaryDto(214, 205, 9, 1_920_000, 512_000, 880_000, 64_000, 0, 41_000, 0, 3_417_000, 0.31, 1_840, 620, 388, 12, [new UsageCostTotalDto("USD", 18_420_000)]);
        kit.Usage.Providers.AddRange([
            new UsageDimensionStatDto("anthropic", "Anthropic", 160, 155, 1_500_000, 400_000, 700_000, 50_000, 30_000, 1_700, 600, 300, 8, [new UsageCostTotalDto("USD", 15_200_000)]),
            new UsageDimensionStatDto("openai", "OpenAI", 54, 50, 420_000, 112_000, 180_000, 14_000, 11_000, 2_200, 700, 88, 4, [new UsageCostTotalDto("USD", 3_220_000)]),
        ]);
        kit.Usage.Models.AddRange([
            new UsageDimensionStatDto("sonnet", "claude-sonnet", 120, 117, 1_200_000, 300_000, 600_000, 40_000, 25_000, 1_900, 650, 250, 6, [new UsageCostTotalDto("USD", 13_000_000)]),
            new UsageDimensionStatDto("haiku", "claude-haiku", 40, 38, 300_000, 100_000, 100_000, 10_000, 5_000, 900, 300, 50, 2, [new UsageCostTotalDto("USD", 2_200_000)]),
            new UsageDimensionStatDto("gpt", "gpt", 54, 50, 420_000, 112_000, 180_000, 14_000, 11_000, 2_200, 700, 88, 4, []),
        ]);
        for (var i = 0; i < 12; i++)
        {
            kit.Usage.Logs.Add(FakeUsage.Attempt("log" + i, ok: i != 3, alias: i % 3 == 0 ? "haiku" : "sonnet", at: Now.AddMinutes(-7 * i).ToUnixTimeMilliseconds()));
        }

        kit.Gateway.Status = new GatewayStatusDto(true, 19836, "127.0.0.1", false, false,
            [new GatewayHealthDto("anthropic", true, 0.99, 0, null, 840, null, 0), new GatewayHealthDto("openai", false, 0.4, 3, null, null, "HTTP 429", 0)], null);
        kit.Settings.Push(kit.Stores.Settings.State.Of(ManagementRig.Device).Settings! with { Gateway = new GatewaySettingsDto(true, false, 19836) }, 2);

        kit.Schedules.Tasks.AddRange([
            FakeSchedules.MakeTask("t1", "每日分诊 Issue", time: "09:00"),
            FakeSchedules.MakeTask("t2", "周五周报", "weekly", "17:30", weekdays: [5]),
            FakeSchedules.MakeTask("t3", "依赖升级检查", "once", "10:30", next: 1_796_000_000_000),
            FakeSchedules.MakeTask("t4", "旧的同步任务", enabled: false),
        ]);
        kit.Schedules.Tasks[0] = kit.Schedules.Tasks[0] with { RepeatWindow = new RepeatWindowDto(true, "12:00", 60) };
        kit.Schedules.Runs.Add(FakeSchedules.Run("r-live", "t1", "running", Now.AddMinutes(-3).ToUnixTimeMilliseconds()));
        kit.Schedules.Runs.Add(FakeSchedules.Run("r-ok", "t2", "success", Now.AddDays(-2).ToUnixTimeMilliseconds()));
        kit.Schedules.Runs.Add(FakeSchedules.Run("r-err", "t3", "error", Now.AddDays(-1).ToUnixTimeMilliseconds(), "scheduled.modelUnavailable", null));
        for (var i = 1; i <= 4; i++)
        {
            kit.Schedules.History.Add(FakeSchedules.Run("old" + i, "t1", i == 2 ? "skipped" : "success", Now.AddDays(-i).ToUnixTimeMilliseconds(), i == 2 ? "scheduled.overlap" : null));
        }

        kit.Schedules.Push();
    }

    // ───────────────────────── 渲染 ─────────────────────────

    private static Host Render(Scenario scenario, double width, bool dark, ManagementKit? kit = null)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        kit ??= new ManagementKit(clockNow: Now);
        scenario.Seed(kit);
        var shell = new ShellViewModel(kit.Stores, kit.Clock, management: kit.Pages, scheduledTasks: kit.Scheduled);
        var view = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = scenario.Height, Content = view };
        window.Show();
        Dispatch();
        if (scenario.Section is { } section)
        {
            shell.Settings.OpenCommand.Execute(null);
            shell.Settings.ShowCommand.Execute(section);
        }
        else
        {
            shell.Panel.OpenScheduledTasksCommand.Execute(null);
        }

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
        if (Find<NcwBarChart>(host).Any())
        {
            // 图表的柱子有入场过渡:给它一点真实时间走完再拍
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(100);
                Dispatch(2);
            }
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        host.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"mgmt-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Finish() => ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));

    private static IEnumerable<T> Find<T>(Host host) where T : Visual => host.Window.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    private static SettingsOverlay Overlay(Host host) => host.Window.GetVisualDescendants().OfType<SettingsOverlay>().Single();

    // ───────────────────────── 典型状态 × 四个断点 × 深浅 ─────────────────────────

    private static readonly Scenario[] Typical =
    [
        new("general", SettingsSection.General, SeedAll, Height: 2300, Check: h =>
        {
            Assert.Equal(PageState.Ready, h.Kit.Pages.General.State);
            Assert.Equal(8, h.Kit.Pages.General.Picks.Count);
            Assert.NotEmpty(Find<GeneralSettingsPage>(h));
        }),
        new("project", SettingsSection.Project, SeedAll, Height: 1000, Check: h => Assert.Equal(PageState.Ready, h.Kit.Pages.Project.State)),
        new("skills", SettingsSection.Skills, SeedAll, Height: 1500, Check: h =>
        {
            Assert.Equal(5, h.Kit.Pages.Skills.Skills.Count);
            Assert.True(h.Kit.Pages.Skills.HasDiagnostics);
        }),
        new("commands", SettingsSection.Commands, SeedAll, Height: 1000, Check: h => Assert.Equal(4, h.Kit.Pages.Commands.Commands.Count)),
        new("hooks", SettingsSection.Hooks, SeedAll, Height: 1300, Check: h => Assert.Equal(3, h.Kit.Pages.Hooks.Hooks.Count)),
        new("mcp", SettingsSection.Mcp, SeedAll, Height: 1400, Check: h =>
        {
            Assert.Equal(5, h.Kit.Pages.Mcp.Servers.Count);
            Assert.Equal(McpConnection.Failed, h.Kit.Pages.Mcp.Servers.Single(s => s.Id == "postgres").Connection);
        }),
        new("usage", SettingsSection.Usage, SeedAll, Height: 2300, Check: h =>
        {
            Assert.Equal(30, h.Kit.Pages.Usage.Series.Count);
            Assert.NotEmpty(Find<NcwBarChart>(h));
        }),
        new("storage", SettingsSection.Storage, SeedAll, Height: 2200, Check: h => Assert.Equal(PageState.Ready, h.Kit.Pages.Storage.State)),
        new("gateway", SettingsSection.Gateway, SeedAll, Height: 1500, Check: h => Assert.True(h.Kit.Pages.Gateway.IsListening),
            Act: async h => await h.Kit.Pages.Gateway.Keys[1].RevealCommand.ExecuteAsync(null)),
        new("schedules", null, SeedAll, Height: 1100, Act: async h => await h.Kit.Scheduled.Tasks.First().ShowRunsCommand.ExecuteAsync(null), Check: h =>
        {
            Assert.True(h.Kit.Scheduled.IsOpen);
            Assert.Equal(3, h.Kit.Scheduled.Tasks.Count(t => t.Enabled));
            Assert.NotEmpty(Find<SchedulesPage>(h));
        }),
    ];

    public static IEnumerable<object[]> TypicalMatrix() =>
        from scenario in Typical.Select(s => s.Name)
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { scenario, width, dark };

    [AvaloniaTheory]
    [MemberData(nameof(TypicalMatrix))]
    public void Typical_state(string name, int width, bool dark)
    {
        var scenario = Typical.Single(s => s.Name == name);
        var host = Render(scenario, width, dark);
        scenario.Check?.Invoke(host);
        if (scenario.Section is not null)
        {
            Assert.True(Overlay(host).IsCompact == width < 760, "窄屏时导航收成图标栏");
        }

        Snapshot(host, name, width, dark);
        Finish();
    }

    // ───────────────────────── 其余状态:空 / 错误 / 只读 / 未知 / 确认 / 表单 ─────────────────────────

    private static Action<ManagementKit> With(Action<ManagementKit> tweak) => kit =>
    {
        SeedAll(kit);
        tweak(kit);
    };

    private static readonly Scenario[] Extra =
    [
        // 通用 / 项目
        new("general-viewer", SettingsSection.General, With(k => k.Rig.SetRole("viewer")), Height: 700, Check: h => Assert.Equal(PageState.Forbidden, h.Kit.Pages.General.State)),
        new("general-member", SettingsSection.General, With(k => k.Rig.SetRole("member")), Height: 1500, Check: h => Assert.True(h.Kit.Pages.General.IsReadOnly)),
        new("general-offline", SettingsSection.General, With(k => k.Rig.SetConnection(DeviceConnection.Offline)), Height: 700, Check: h => Assert.Equal(PageState.Unknown, h.Kit.Pages.General.State)),
        new("general-loading", SettingsSection.General, k => { SeedAll(k); k.Stores.Settings.Update(_ => SettingsMirror.Empty); }, Height: 700, Check: h => Assert.Equal(PageState.Loading, h.Kit.Pages.General.State)),
        new("general-limits-invalid", SettingsSection.General, SeedAll, Height: 2300, Act: h =>
        {
            h.Kit.Pages.General.IdleTimeout = 30;
            h.Kit.Pages.General.ProxyEnabled = true;
            h.Kit.Pages.General.ProxyMode = "manual";
            h.Kit.Pages.General.ProxyAuthEnabled = true;
            return Task.CompletedTask;
        }, Check: h => Assert.True(h.Kit.Pages.General.LimitsInvalid && h.Kit.Pages.General.ProxyInvalid)),
        new("project-empty", SettingsSection.Project, With(k => k.Stores.Workspace.Update(s => s with { Projects = s.Projects.Clear() })), Height: 700, Check: h => Assert.False(h.Kit.Pages.Project.HasProjects)),
        new("project-error", SettingsSection.Project, With(k => k.Settings.ProjectOverride = _ => R.Fail<ProjectSettingsDto>(Ncw.Client.Actions.ActionOutcome.NotFound, "settings.projectNotFound")), Height: 700, Check: h => Assert.Equal(PageState.Error, h.Kit.Pages.Project.State)),
        new("project-member", SettingsSection.Project, With(k => k.Rig.SetRole("member")), Height: 1100, Check: h => Assert.True(h.Kit.Pages.Project.IsReadOnly)),

        // 技能 / 命令
        new("skills-empty", SettingsSection.Skills, With(k => k.Skills.Skills.Clear()), Height: 800, Check: h => Assert.True(h.Kit.Pages.Skills.ShowsEmpty)),
        new("skills-error", SettingsSection.Skills, With(k => k.Skills.ListFailure = R.Fail<SkillsListResult>(Ncw.Client.Actions.ActionOutcome.Failed, "skills.operationFailed")), Height: 700, Check: h => Assert.Equal(PageState.Error, h.Kit.Pages.Skills.State)),
        new("skills-viewer", SettingsSection.Skills, With(k => k.Rig.SetRole("viewer")), Height: 1500, Check: h =>
        {
            Assert.True(h.Kit.Pages.Skills.IsReadOnly);
            Assert.False(h.Kit.Pages.Skills.CanActivate);
        }),
        new("skills-uninstall-confirm", SettingsSection.Skills, SeedAll, Height: 1500, Act: h =>
        {
            h.Kit.Pages.Skills.Skills[0].AskUninstallCommand.Execute(null);
            return Task.CompletedTask;
        }, Check: h => Assert.True(h.Kit.Pages.Skills.HasPendingUninstall)),
        new("skills-market-auth", SettingsSection.Skills, With(k => k.Skills.AuthRequired = true), Height: 1300, Act: async h =>
        {
            await h.Kit.Pages.Skills.ShowMarketCommand.ExecuteAsync(null);
            await h.Kit.Pages.Skills.InstallMarketCommand.ExecuteAsync("sql");
            await h.Kit.Pages.Skills.ShowDetailCommand.ExecuteAsync(h.Kit.Pages.Skills.Market[0]);
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Skills.AuthRequired);
            Assert.Equal(2, h.Kit.Pages.Skills.Market.Count);
        }),
        new("skills-zip-too-large", SettingsSection.Skills, SeedAll, Height: 1000, Act: async h =>
        {
            h.Kit.Dialogs.NextFile = ScriptedDialogs.File("huge.zip", 5_000_000);
            h.Shell.Dialogs = h.Kit.Dialogs;
            await h.Kit.Pages.Skills.InstallZipCommand.ExecuteAsync(null);
        }, Check: h => Assert.Equal("skills.packageTooLarge", h.Kit.Pages.Skills.ActionFailure?.Key)),
        new("commands-empty", SettingsSection.Commands, With(k => k.Commands.All.Clear()), Height: 800, Check: h => Assert.True(h.Kit.Pages.Commands.ShowsEmpty)),
        new("commands-viewer", SettingsSection.Commands, With(k => k.Rig.SetRole("viewer")), Height: 1000, Check: h => Assert.True(h.Kit.Pages.Commands.IsReadOnly)),

        // Hooks
        new("hooks-empty", SettingsSection.Hooks, With(k => k.Hooks.Hooks.Clear()), Height: 800, Check: h => Assert.True(h.Kit.Pages.Hooks.ShowsEmpty)),
        new("hooks-viewer", SettingsSection.Hooks, With(k => k.Rig.SetRole("viewer")), Height: 700, Check: h => Assert.Equal(PageState.Forbidden, h.Kit.Pages.Hooks.State)),
        new("hooks-member", SettingsSection.Hooks, With(k => k.Rig.SetRole("member")), Height: 1300, Check: h => Assert.True(h.Kit.Pages.Hooks.IsReadOnly)),
        new("hooks-editor-test", SettingsSection.Hooks, SeedAll, Height: 2300, Act: async h =>
        {
            h.Kit.Pages.Hooks.Hooks.Single(x => x.Dto.Id == "h2").EditCommand.Execute(null);
            h.Kit.Pages.Hooks.AddEnvCommand.Execute(null);
            h.Kit.Pages.Hooks.Editor.Env[1].Name = "NEW_VAR";
            h.Kit.Hooks.Report = new HookRunReportDto("h2", "project", 2, "formatted 3 files\n", "warning: DOTNET_ROOT=•••", 1250, HookNames.OutcomeOk, HookNames.DecisionDeny, "exit code 2 blocks the tool");
            await h.Kit.Pages.Hooks.TestCommand.ExecuteAsync(null);
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Hooks.Editor.IsOpen);
            Assert.NotNull(h.Kit.Pages.Hooks.TestReport);
        }),
        new("hooks-delete-confirm", SettingsSection.Hooks, SeedAll, Height: 1300, Act: h =>
        {
            h.Kit.Pages.Hooks.Hooks[0].AskDeleteCommand.Execute(null);
            return Task.CompletedTask;
        }, Check: h => Assert.True(h.Kit.Pages.Hooks.HasPendingDelete)),

        // MCP
        new("mcp-empty", SettingsSection.Mcp, With(k => { k.Mcp.Servers.Clear(); k.Mcp.Push(); }), Height: 800, Check: h => Assert.True(h.Kit.Pages.Mcp.ShowsEmpty)),
        new("mcp-offline", SettingsSection.Mcp, With(k => k.Rig.SetConnection(DeviceConnection.Offline)), Height: 1400, Check: h =>
        {
            Assert.Equal(PageState.Unknown, h.Kit.Pages.Mcp.State);
            Assert.All(h.Kit.Pages.Mcp.Servers, s => Assert.Equal(McpConnection.Unknown, s.Connection));
        }),
        new("mcp-viewer", SettingsSection.Mcp, With(k => k.Rig.SetRole("viewer")), Height: 700, Check: h => Assert.Equal(PageState.Forbidden, h.Kit.Pages.Mcp.State)),
        new("mcp-editor-secrets", SettingsSection.Mcp, SeedAll, Height: 2300, Act: async h =>
        {
            h.Kit.Mcp.SecretInfo = [new McpSecretEntryDto("TOKEN", true)];
            await h.Kit.Pages.Mcp.Servers.First(s => s.Id == "filesystem").EditCommand.ExecuteAsync(null);
            h.Kit.Pages.Mcp.Editor.Secrets[0].Value = "x"; // 只填了一部分:界面提示要把整组填齐
            h.Kit.Pages.Mcp.AddSecretCommand.Execute(null);
            h.Kit.Pages.Mcp.Editor.Secrets[1].Name = "EXTRA";
            await h.Kit.Pages.Mcp.TestDraftCommand.ExecuteAsync(null);
        }, Check: h =>
        {
            Assert.Contains(McpProblem.SecretsIncomplete, h.Kit.Pages.Mcp.Editor.Problems);
            Assert.True(h.Kit.Pages.Mcp.TestResult!.Ok);
        }),
        new("mcp-browse-remove", SettingsSection.Mcp, SeedAll, Height: 2000, Act: async h =>
        {
            await h.Kit.Pages.Mcp.Servers.First(s => s.Id == "filesystem").BrowseCommand.ExecuteAsync(null);
            h.Kit.Pages.Mcp.Servers.First(s => s.Id == "legacy").AskRemoveCommand.Execute(null);
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Mcp.HasPendingRemove);
            Assert.Single(h.Kit.Pages.Mcp.Resources);
        }),

        // 用量
        new("usage-empty", SettingsSection.Usage, With(k => k.Usage.Summary = k.Usage.Summary with { RequestCount = 0 }), Height: 800, Check: h => Assert.True(h.Kit.Pages.Usage.IsEmptyData)),
        new("usage-error", SettingsSection.Usage, With(k => k.Usage.SummaryFailure = R.Fail<UsageSummaryDto>(Ncw.Client.Actions.ActionOutcome.Failed, "usage.invalid.window")), Height: 700, Check: h => Assert.Equal(PageState.Error, h.Kit.Pages.Usage.State)),
        new("usage-cost-multi-currency", SettingsSection.Usage, With(k =>
        {
            k.Usage.Buckets.Add(FakeUsage.Bucket(DateOnly.FromDateTime(Now.Date).ToString("yyyy-MM-dd"), requests: 4, currency: "CNY", cost: 9_500_000));
            k.Usage.Buckets.Add(FakeUsage.Bucket(DateOnly.FromDateTime(Now.Date.AddDays(-3)).ToString("yyyy-MM-dd"), requests: 2, currency: "", cost: null, priced: 0));
        }), Height: 1500, Act: h =>
        {
            h.Kit.Pages.Usage.Metric = UsageMetric.Cost;
            return Task.CompletedTask;
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Usage.ShowsCostPicker);
            Assert.True(h.Kit.Pages.Usage.HasUnpriced);
        }),
        new("usage-7days", SettingsSection.Usage, SeedAll, Height: 1500, Act: h =>
        {
            h.Kit.Pages.Usage.Range = UsageRange.Days7;
            return Task.CompletedTask;
        }, Check: h => Assert.Equal(7, h.Kit.Pages.Usage.Series.Count)),

        // 存储
        new("storage-cleanup-confirm", SettingsSection.Storage, SeedAll, Height: 2400, Act: async h =>
        {
            h.Kit.Pages.Storage.Age = "6";
            await h.Kit.Pages.Storage.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupHistory);
        }, Check: h =>
        {
            Assert.Equal(DangerStage.Confirming, h.Kit.Pages.Storage.Stage);
            Assert.False(h.Kit.Pages.Storage.CanConfirm);
        }),
        new("storage-remote-owner", SettingsSection.Storage, With(k => k.Rig.MakeRemote()), Height: 2300, Check: h => Assert.Equal(GateReason.LocalOnly, h.Kit.Pages.Storage.DangerGate.Reason)),
        new("storage-error", SettingsSection.Storage, With(k => k.Storage.StatsFailure = R.Fail<StorageStatsDto>(Ncw.Client.Actions.ActionOutcome.Failed)), Height: 700, Check: h => Assert.Equal(PageState.Error, h.Kit.Pages.Storage.State)),
        new("storage-busy", SettingsSection.Storage, With(k => k.Storage.ExecuteFailure = R.Fail<CleanupResultDto>(Ncw.Client.Actions.ActionOutcome.Unavailable, "storage.busy")), Height: 2300, Act: async h =>
        {
            await h.Kit.Pages.Storage.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupAge);
            await h.Kit.Pages.Storage.ConfirmCommand.ExecuteAsync(null);
        }, Check: h => Assert.Equal("storage.busy", h.Kit.Pages.Storage.DangerFailure?.Key)),

        // 网关
        new("gateway-member", SettingsSection.Gateway, With(k => k.Rig.SetRole("member")), Height: 700, Check: h => Assert.Equal(PageState.Forbidden, h.Kit.Pages.Gateway.State)),
        new("gateway-remote-owner", SettingsSection.Gateway, With(k => k.Rig.MakeRemote()), Height: 1500, Check: h => Assert.Equal(GateReason.LocalOnly, h.Kit.Pages.Gateway.LocalGate.Reason)),
        new("gateway-lan-rotate", SettingsSection.Gateway, With(k => k.Gateway.Status = k.Gateway.Status with { Host = "0.0.0.0", AllowRemote = true }), Height: 1700, Act: async h =>
        {
            await h.Kit.Pages.Gateway.Keys[0].RevealCommand.ExecuteAsync(null);
            h.Kit.Pages.Gateway.Keys[1].AskRotateCommand.Execute(null);
        }, Check: h =>
        {
            Assert.True(h.Kit.Pages.Gateway.ExposedToLan);
            Assert.True(h.Kit.Pages.Gateway.HasPendingRotate);
        }),
        new("gateway-disabled", SettingsSection.Gateway, With(k => k.Gateway.Status = new GatewayStatusDto(false, null, "127.0.0.1", false, false, [], null)), Height: 1300, Check: h => Assert.False(h.Kit.Pages.Gateway.IsEnabled)),

        // 定时任务
        new("schedules-empty", null, With(k => { k.Schedules.Tasks.Clear(); k.Schedules.Runs.Clear(); k.Schedules.Push(); }), Height: 800, Check: h => Assert.True(h.Kit.Scheduled.ShowsEmpty)),
        new("schedules-member", null, With(k => k.Rig.SetRole("member")), Height: 1100, Check: h => Assert.True(h.Kit.Scheduled.IsReadOnly)),
        new("schedules-offline", null, With(k => k.Rig.SetConnection(DeviceConnection.Offline)), Height: 700, Check: h => Assert.Equal(PageState.Unknown, h.Kit.Scheduled.State)),
        new("schedules-editor", null, SeedAll, Height: 1900, Act: h =>
        {
            h.Kit.Scheduled.NewCommand.Execute(null);
            var e = h.Kit.Scheduled.Editor;
            (e.Name, e.Prompt, e.Timezone, e.Time, e.Kind) = ("每周代码审查", "审查本周合并的 PR 并写一份摘要。", "Asia/Shanghai", "08:30", ScheduleRuleKinds.Weekly);
            e.RepeatEnabled = true;
            e.RepeatEnd = "10:00";
            e.RepeatInterval = 30;
            return Task.CompletedTask;
        }, Check: h => Assert.Equal(ScheduleEditorViewModel.PreviewCount, h.Kit.Scheduled.Editor.Preview.Count)),
        new("schedules-editor-invalid", null, SeedAll, Height: 1700, Act: h =>
        {
            h.Kit.Scheduled.NewCommand.Execute(null);
            h.Kit.Scheduled.Editor.Kind = ScheduleRuleKinds.Once;
            h.Kit.Scheduled.Editor.Date = "2026-01-01";
            return Task.CompletedTask;
        }, Check: h => Assert.Contains(ScheduleProblem.NeverFires, h.Kit.Scheduled.Editor.Problems.Append(ScheduleProblem.NeverFires))),
        new("schedules-run-detail", null, SeedAll, Height: 1500, Act: async h =>
        {
            h.Kit.Scheduled.Tasks.First(t => t.Id == "t3").AskDeleteCommand.Execute(null);
            await h.Kit.Scheduled.Tasks.First(t => t.Id == "t1").ShowRunsCommand.ExecuteAsync(null);
            await h.Kit.Scheduled.Runs.First(r => r.Id == "old1").SelectCommand.ExecuteAsync(null);
        }, Check: h =>
        {
            Assert.True(h.Kit.Scheduled.HasPendingDelete);
            Assert.NotNull(h.Kit.Scheduled.RunDetail);
        }),
    ];

    public static IEnumerable<object[]> ExtraMatrix() =>
        from scenario in Extra.Select(s => s.Name)
        from config in new[] { (1500, true), (600, false) }
        select new object[] { scenario, config.Item1, config.Item2 };

    [AvaloniaTheory]
    [MemberData(nameof(ExtraMatrix))]
    public void Other_states(string name, int width, bool dark)
    {
        var scenario = Extra.Single(s => s.Name == name);
        var host = Render(scenario, width, dark);
        scenario.Check?.Invoke(host);
        Snapshot(host, name, width, dark);
        Finish();
    }

    // ───────────────────────── 行为:界面里的开关 / 按钮真的接到了 ViewModel ─────────────────────────

    [AvaloniaFact]
    public void Nav_switches_pages_and_collapses_to_icons_on_narrow_windows()
    {
        var host = Render(Typical[0], 600, true);
        Assert.True(Overlay(host).IsCompact);
        host.Shell.Settings.ShowCommand.Execute(SettingsSection.Hooks);
        Dispatch(8);
        Assert.NotEmpty(Find<HooksPage>(host));
        Assert.Empty(Find<GeneralSettingsPage>(host));

        var wide = Render(Typical[0], 1500, true);
        Assert.False(Overlay(wide).IsCompact);
        Finish();
    }

    [AvaloniaFact]
    public void Sidebar_scheduled_tasks_entry_opens_the_page_over_the_workspace_and_a_session_pick_closes_it()
    {
        var host = Render(Typical[^1], 1500, true);
        Assert.True(host.Shell.ScheduledTasks.IsOpen);
        Assert.NotEmpty(Find<SchedulesPage>(host));

        host.Kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), "s-1"));
        Dispatch(8);
        Assert.False(host.Shell.ScheduledTasks.IsOpen);
        Assert.Empty(Find<SchedulesPage>(host));
        Finish();
    }

    [AvaloniaFact]
    public void The_gateway_switch_and_hooks_button_are_wired_to_their_commands()
    {
        var gateway = Render(new Scenario("gw", SettingsSection.Gateway, With(k => k.Gateway.Status = new GatewayStatusDto(false, null, "127.0.0.1", false, false, [], null))), 1200, true);
        var toggle = Find<NcwSwitch>(gateway).First(s => s.Name == "EnabledSwitch");
        toggle.IsChecked = true;
        Dispatch(8);
        Assert.Equal([true], gateway.Kit.Gateway.Enabled);

        var hooks = Render(Typical[4], 1200, true);
        var newButton = Find<NcwButton>(hooks).First(b => b.Command == hooks.Kit.Pages.Hooks.NewCommand);
        newButton.Command!.Execute(null);
        Dispatch(8);
        Assert.True(hooks.Kit.Pages.Hooks.Editor.IsOpen);
        Finish();
    }

    [AvaloniaFact]
    public void English_renders_every_management_page_without_missing_text()
    {
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.EnUS);
        try
        {
            foreach (var scenario in new[] { Typical[0], Typical[5], Typical[6], Typical[7], Extra.Single(s => s.Name == "hooks-editor-test"), Extra.Single(s => s.Name == "schedules-editor") })
            {
                var host = Render(scenario, 1200, true);
                scenario.Check?.Invoke(host);
                Snapshot(host, "en-" + scenario.Name, 1200, true);
            }
        }
        finally
        {
            TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
            Finish();
        }
    }
}
