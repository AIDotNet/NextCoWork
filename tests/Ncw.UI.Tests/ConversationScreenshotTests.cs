using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using TKCode.Arc;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 对话区和模型设置页的 Headless 截图(docs/design/03 §2 的四个断点 × 深浅两种主题),并断言界面的结构:行的种类、卡片上的执行环境标识、输入框的状态。
/// 截图在 <c>bin/Debug/net10.0/screenshots/convo-*.png</c>。数据全部经 Store 进来(和设备推送的形状一致),不经过连接。
/// </summary>
public class ConversationScreenshotTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>四个断点:Wide 1500、Medium 1200、Narrow 900、Compact 600(手机宽度前的最窄桌面窗口);× 深色 / 浅色。</summary>
    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    public static IEnumerable<object[]> NarrowMatrix() =>
        from width in new[] { 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(ClientStores stores, double width, bool dark, double height = 860)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(stores, new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Dispatch();
        return (window, shell, vm);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>反复跑布局 / 渲染,直到条件成立(最多 <paramref name="rounds"/> 轮):虚拟化列表的滚动要几轮才收敛。</summary>
    private static void Settle(Func<bool> until, int rounds = 12)
    {
        for (var i = 0; i < rounds && !until(); i++)
        {
            Dispatch();
        }
    }

    private static void Snapshot(Window window, string scenario, double width, bool dark, bool last = true)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"convo-{scenario}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        if (last)
        {
            // 其他测试类默认深色,不能被这里的浅色主题带偏
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    // ───────────────────────── 场景数据 ─────────────────────────

    private static ClientStores EmptySession() => Base("s-ncw-3").Also(s => Session(s, "s-ncw-3", []));

    internal static ClientStores Streaming()
    {
        var stores = Base("s-ncw-1");
        Session(stores, "s-ncw-1",
        [
            User("u1", "帮我把会话列表接到真实的 sessions.list,并且运行中的状态点用 activeRuns。"),
            Assistant("a1", 9, Thinking("先看看现有的 Store 和 ProjectPanelViewModel 里会话列表是怎么派生的,再决定改哪里。", 6400), Text("好的,我先读一下现有实现。")),
        ], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v with
        {
            Streaming = v.Streaming.Add("a2", "会话列表的**真源**在设备上,控制端只保留一份镜像:\n\n1. 连接后对每个项目调用 `sessions.list`\n2. 运行中的状态点只看 `activeRuns`\n\n```csharp\nvar activity = SessionActivityView.Of(id, index, connection);\n```\n"),
            Thinking = v.Thinking.Add("a2", "需要保证设备断开时状态是未知。"),
            Tools = v.Tools.Add("c9", new ToolCallView("c9", "Bash", "{\"command\":\"dotnet test tests/Ncw.Client.Tests\"}", ToolCallStatus.Running, null, LocalEnv)),
        });
        return stores;
    }

    internal static ClientStores Tools()
    {
        var stores = Base("s-ncw-2");
        Session(stores, "s-ncw-2",
        [
            User("u1", "把 ProjectPanelViewModel 的会话过滤抽成纯函数,并跑一遍测试。"),
            Assistant("a1", 8,
                Text("我先读文件,然后改。"),
                Call("c1", "Read", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Client/ViewModels/ProjectPanelViewModel.cs\"}"),
                Call("c2", "Edit", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Client/ViewModels/SessionListQuery.cs\",\"old_string\":\"return sessions;\",\"new_string\":\"return sessions\\n    .Where(s => !s.Archived)\\n    .OrderByDescending(s => s.UpdatedAt);\"}"),
                Call("c3", "Bash", "{\"command\":\"dotnet test tests/Ncw.Client.Tests --no-build\"}"),
                Call("c4", "Bash", "{\"command\":\"dotnet build NextCoWork.slnx\"}"),
                Call("c5", "Write", "{\"file_path\":\"/srv/api/cmd/server/main.go\",\"content\":\"package main\\n\\nfunc main() {}\\n\"}")),
            ToolResults("r1", 7,
                Result("c1", "(file contents)"),
                Result("c2", "Edited 1 location"),
                Result("c3", "Passed!  - Failed: 0, Passed: 93, Skipped: 0, Total: 93\nDuration: 195 ms\nRun finished."),
                Result("c4", "error CS1061: 'DeviceLink' does not contain a definition for 'UpdateStores'\nBuild FAILED.\n    1 Error(s)", isError: true)),
        ], activeRunId: "run-2");
        Run(stores, "run-2", "s-ncw-2", v => v);
        Interactions(stores, Approval("i1", "run-2", "c5", "Write", "{\"file_path\":\"/srv/api/cmd/server/main.go\",\"content\":\"package main\\n\"}", SshEnv));
        return stores;
    }

    internal static ClientStores Approvals()
    {
        var stores = Base("s-ncw-1");
        Session(stores, "s-ncw-1",
        [
            User("u1", "清理构建产物并重新发布。"),
            Assistant("a1", 3, Text("需要运行几个命令,先确认一下。"), Call("c1", "Bash", "{\"command\":\"rm -rf out/ && dotnet publish -c Release\"}")),
        ], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v);
        Interactions(stores,
            Approval("i1", "run-1", "c1", "Bash", "{\"command\":\"rm -rf out/ && dotnet publish -c Release\"}", LocalEnv, dangerous: true),
            new AskUserInteractionDto
            {
                Id = "i2", RunId = "run-1", CreatedAt = Ms(Now.AddSeconds(-20)), Version = 13,
                Questions = [new AskUserQuestionDto("发布目标", "要发布到哪个平台?", [new AskUserOptionDto("osx-arm64", "Apple Silicon"), new AskUserOptionDto("win-x64", "Windows 64 位"), new AskUserOptionDto("linux-x64")], true, true)],
            },
            new PlanApprovalInteractionDto { Id = "i3", RunId = "run-1", CreatedAt = Ms(Now.AddSeconds(-10)), Version = 14, PlanId = "p1", Path = "plan.md", Plan = "## 计划\n\n1. 清理 `out/`\n2. 发布 **osx-arm64**\n3. 校验产物大小" },
            new GoalProposalInteractionDto { Id = "i4", RunId = "run-1", CreatedAt = Ms(Now.AddSeconds(-5)), Version = 15, SessionId = "s-ncw-1", Condition = "发布产物小于 60MB 且能启动" });
        return stores;
    }

    internal static ClientStores SubagentsAndTodo()
    {
        var stores = Base("s-ncw-1");
        Session(stores, "s-ncw-1",
        [
            User("u1", "并行调研三个模块,然后汇总。"),
            Assistant("a1", 6,
                Text("我派两个子代理去读代码,同时维护待办。"),
                Call("t1", "TodoWrite", "{\"todos\":[{\"content\":\"阅读 kernel\",\"status\":\"completed\",\"activeForm\":\"正在阅读 kernel\"},{\"content\":\"阅读 upstream\",\"status\":\"in_progress\",\"activeForm\":\"正在阅读 upstream\"},{\"content\":\"写汇总\",\"status\":\"pending\",\"activeForm\":\"正在写汇总\"}]}"),
                Call("k1", "Task", "{\"subagent_type\":\"code-analyst\",\"description\":\"阅读 kernel/upstream\"}"),
                Call("k2", "Task", "{\"subagent_type\":\"general-purpose\",\"description\":\"调研 pi 的设计\",\"run_in_background\":true}")),
            ToolResults("r1", 5, Result("t1", "ok"), Result("k2", "started")),
            new MessageDto("rep1", "user", Ms(Now.AddMinutes(-2)), true, "[{\"type\":\"subagent\",\"callId\":\"k2\",\"childRunId\":\"child-k2\",\"summary\":\"pi 把工具协议和事件日志拆成两层……\"}]"),
        ], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v);
        Subagent(stores, "s-ncw-1", "k1", "code-analyst", "阅读 kernel/upstream", SubagentStatus.Running, 14, "Grep");
        Subagent(stores, "s-ncw-1", "k2", "general-purpose", "调研 pi 的设计", SubagentStatus.Done, 45, null, "pi 把工具协议和事件日志拆成两层,事件带 seq……");
        return stores;
    }

    internal static ClientStores GoalBar()
    {
        var stores = Base("s-ncw-2");
        Session(stores, "s-ncw-2",
        [
            User("u1", "/goal 所有单元测试通过,并且 dotnet build 零警告"),
            Assistant("a1", 4, Text("已设定目标,开始修复失败的测试。"), "{\"type\":\"goal_status\",\"met\":false,\"condition\":\"所有单元测试通过,并且 dotnet build 零警告\",\"set\":true}"),
            Assistant("a2", 1, Text("还有 3 个测试失败,继续。"), "{\"type\":\"goal_status\",\"met\":false,\"condition\":\"所有单元测试通过,并且 dotnet build 零警告\",\"reason\":\"还有 3 个测试失败\"}"),
        ]);
        Goal(stores, "s-ncw-2");
        return stores;
    }

    internal static ClientStores Errors()
    {
        var stores = Base("s-ncw-2");
        Session(stores, "s-ncw-2",
        [
            User("u1", "继续上一轮的重构。", 30),
            new MessageDto("cb", "user", Ms(Now.AddMinutes(-20)), true, "[{\"type\":\"compact_boundary\",\"trigger\":\"auto\",\"preTokens\":182000,\"postTokens\":21000,\"summary\":\"用户在重构会话列表……\"}]"),
            User("u2", "把剩下的测试补完。", 5),
            Assistant("a1", 4, Text("好的,先看现有测试。"), "{\"type\":\"error\",\"error\":{\"code\":\"rate_limited\",\"message\":\"429 Too Many Requests\",\"retryable\":true}}"),
        ]);
        Run(stores, "run-9", "s-ncw-2", v => v with
        {
            Phase = RunPhase.Error,
            Settled = true,
            Error = new ErrorDto("upstream_unavailable", "The provider returned 529 overloaded", true, 529),
        });
        return stores;
    }

    private static ClientStores Retrying()
    {
        var stores = Base("s-ncw-1");
        Session(stores, "s-ncw-1", [User("u1", "总结一下今天的改动。")], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v with { Retry = new ProviderRetryEventDto(2, 4000, "overloaded") });
        return stores;
    }

    // ───────────────────────── 测试 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Compact_boundary_error_rows_and_a_failed_run_offer_retry(int width, bool dark)
    {
        var (window, _, vm) = Render(Errors(), width, dark);

        Assert.Contains(vm.Conversation.Rows, r => r is CompactRowViewModel { IsAuto: true, PreTokens: 182000 });
        var errors = vm.Conversation.Rows.OfType<ErrorRowViewModel>().ToList();
        Assert.Equal(2, errors.Count);
        Assert.False(errors[0].CanRetry); // 消息里的错误行不重试
        Assert.True(errors[1].CanRetry);  // 失败的 Run 的错误:最后一条可以重试
        Snapshot(window, "errors", width, dark);
    }

    /// <summary>英文界面:同一批场景换成 en-US 渲染一遍(缺翻译在构建时就会失败,这里看排版:英文更长,不能挤坏布局)。</summary>
    [AvaloniaTheory]
    [InlineData(1200)]
    [InlineData(600)]
    public void English_ui_renders_the_approval_and_provider_scenarios(int width)
    {
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.EnUS);
        try
        {
            var (window, _, vm) = Render(Approvals(), width, dark: true);
            Assert.Equal("Approval needed: Bash", Ncw.UI.Localization.NcwTexts.Format(Ncw.UI.Localization.NcwTextKind.InteractionToolTitle, "Bash"));
            Assert.Equal(4, vm.Conversation.Rows.OfType<InteractionRowViewModel>().Count());
            Snapshot(window, "approvals-en", width, dark: true);

            var stores = Base("s-ncw-1");
            Session(stores, "s-ncw-1", []);
            var (window2, _, vm2) = Render(stores, width, dark: true, height: 1100);
            vm2.Settings.OpenCommand.Execute(null);
            vm2.Settings.ShowCommand.Execute(SettingsSection.Models);
            vm2.Settings.Models.Providers[0].IsExpanded = true;
            Dispatch();
            Snapshot(window2, "providers-en", width, dark: true);
        }
        finally
        {
            TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
        }
    }

    [AvaloniaFact]
    public void Provider_retry_shows_a_status_row_while_the_run_is_still_running()
    {
        var (window, _, vm) = Render(Retrying(), 1200, true);

        var status = Assert.IsType<StatusRowViewModel>(vm.Conversation.Rows.Last());
        Assert.True(status.IsRetrying);
        Assert.Equal(2, status.Attempt);
        Snapshot(window, "retrying", 1200, true);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Empty_session_shows_the_empty_state_and_an_idle_composer(int width, bool dark)
    {
        var (window, shell, vm) = Render(EmptySession(), width, dark);

        Assert.True(vm.Conversation.HasSession);
        Assert.True(vm.Conversation.IsEmpty);
        Assert.Empty(vm.Conversation.Rows);
        Assert.Equal(ComposerPrimary.Disabled, vm.Conversation.Composer.State.Primary); // 空草稿:发送按钮灰着
        Assert.NotNull(vm.Conversation.Composer.SelectedModel);
        Assert.Equal("Claude Opus 5.5", vm.Conversation.Composer.SelectedModel!.DisplayName);
        Snapshot(window, "empty", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Streaming_reply_shows_thinking_markdown_and_a_running_tool_with_the_stop_button(int width, bool dark)
    {
        var (window, shell, vm) = Render(Streaming(), width, dark);

        var kinds = vm.Conversation.Rows.Select(r => r.GetType().Name).ToList();
        Assert.Contains(nameof(UserRowViewModel), kinds);
        Assert.Contains(nameof(ThinkingRowViewModel), kinds);
        var streaming = vm.Conversation.Rows.OfType<AssistantRowViewModel>().Single(r => r.IsStreaming);
        Assert.Contains("真源", streaming.Text);
        var tool = vm.Conversation.Rows.OfType<ToolRowViewModel>().Single();
        Assert.True(tool.IsRunning);
        Assert.Equal("本机 · local", tool.EnvLabel); // 执行环境标识:设备 + 环境
        // 运行中:没文字时主按钮是「停止」,有文字时可以排队 / 插话
        Assert.True(vm.Conversation.IsRunning);
        Assert.Equal(ComposerPrimary.Stop, vm.Conversation.Composer.State.Primary);
        vm.Conversation.Composer.Draft = "顺便把 README 也更新";
        Dispatch();
        Assert.Equal(ComposerPrimary.Queue, vm.Conversation.Composer.State.Primary);
        Assert.True(vm.Conversation.Composer.State.CanSteer);
        Snapshot(window, "streaming", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Tool_cards_show_done_failed_and_waiting_for_approval_with_environment_labels(int width, bool dark)
    {
        var (window, _, vm) = Render(Tools(), width, dark);

        var tools = vm.Conversation.Rows.OfType<ToolRowViewModel>().ToDictionary(t => t.Row.CallId);
        Assert.True(tools["c1"].IsDone);
        Assert.True(tools["c2"].IsDone && tools["c2"].HasStats);
        Assert.Equal("+3 −1".Replace(" ", ""), tools["c2"].AddedText + tools["c2"].RemovedText);
        Assert.True(tools["c3"].IsDone && tools["c3"].HasPreview);
        Assert.True(tools["c4"].IsFailed);
        Assert.True(tools["c5"].IsWaiting);
        // 等待审批的是 SSH 环境里的 Write:卡片上要说清是哪台设备的哪个环境
        Assert.Equal("本机 · local", tools["c1"].EnvLabel); // 落库的旧调用用项目环境兜底
        var approval = vm.Conversation.Rows.OfType<InteractionRowViewModel>().Single();
        Assert.Equal("本机 · dev-server", approval.EnvLabel);
        Assert.True(approval.IsSsh);

        tools["c3"].IsExpanded = true;
        tools["c2"].IsExpanded = true;
        Dispatch();
        Snapshot(window, "tools", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Approval_cards_cover_tool_ask_plan_and_goal_proposals(int width, bool dark)
    {
        var (window, _, vm) = Render(Approvals(), width, dark);

        var cards = vm.Conversation.Rows.OfType<InteractionRowViewModel>().ToList();
        Assert.Equal(4, cards.Count);
        var tool = cards.Single(c => c.IsToolPermission);
        Assert.True(tool.IsDangerous);
        Assert.Equal(["session", "project", "global"], tool.Scopes);
        Assert.Equal("session", tool.SelectedScope);
        Assert.Contains("rm -rf out/", tool.Target);
        Assert.Equal(3, cards.Single(c => c.IsAskUser).Questions[0].Options.Count);
        Assert.Contains("发布", cards.Single(c => c.IsPlan).Plan);
        Assert.Contains("60MB", cards.Single(c => c.IsGoalProposal).Condition);
        Snapshot(window, "approvals", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Subagent_cards_report_row_and_todo_panel(int width, bool dark)
    {
        var (window, _, vm) = Render(SubagentsAndTodo(), width, dark);

        var cards = vm.Conversation.Rows.OfType<SubagentRowViewModel>().ToList();
        Assert.Equal(2, cards.Count);
        Assert.True(cards.Single(c => c.Row.Ref.CallId == "k1").IsActive);
        Assert.Equal("Grep", cards.Single(c => c.Row.Ref.CallId == "k1").View.CurrentTool);
        Assert.True(cards.Single(c => c.Row.Ref.CallId == "k2").IsDone);
        Assert.Contains(vm.Conversation.Rows, r => r is ReportRowViewModel);
        Assert.NotNull(vm.Conversation.Todo);
        Assert.Equal((1, 3), (vm.Conversation.Todo!.Completed, vm.Conversation.Todo.Total));
        Assert.Equal("正在阅读 upstream", vm.Conversation.Todo.Current);
        Snapshot(window, "subagents", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Goal_bar_and_goal_status_rows(int width, bool dark)
    {
        var (window, _, vm) = Render(GoalBar(), width, dark);

        Assert.NotNull(vm.Conversation.Goal);
        Assert.Equal(2, vm.Conversation.Goal!.Iterations);
        Assert.Equal(2, vm.Conversation.Rows.OfType<GoalStatusRowViewModel>().Count());
        Snapshot(window, "goal", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(NarrowMatrix))]
    public void Narrow_windows_open_the_sidebar_as_a_drawer_over_the_conversation(int width, bool dark)
    {
        var (window, shell, vm) = Render(Streaming(), width, dark);
        Assert.False(shell.IsSidebarVisible);

        vm.ToggleSidebarCommand.Execute(null);
        Dispatch();

        Assert.True(shell.IsDrawerVisible);
        Assert.True(shell.IsSidebarVisible);
        Assert.Contains(vm.Panel.Sessions, s => s.IsRunning); // 抽屉里的会话列表:运行中的状态点来自 activeRuns
        Snapshot(window, "drawer", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Providers_settings_page_lists_providers_credentials_models_and_catalog_state(int width, bool dark)
    {
        var stores = Base("s-ncw-1");
        Session(stores, "s-ncw-1", []);
        stores.Catalog.Update(_ => new CatalogMirror(System.Collections.Immutable.ImmutableDictionary<string, DeviceCatalog>.Empty.Add("local",
            new DeviceCatalog(true, new CatalogStatusDto(3, "remote", Ms(Now.AddHours(-2)), Ms(Now.AddMinutes(-5)), null, 2314, 98, null), [], [], 0))));
        var (window, _, vm) = Render(stores, width, dark, height: 1100);

        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowCommand.Execute(SettingsSection.Models);
        Dispatch();
        var models = vm.Settings.Models;
        Assert.True(models.IsAvailable);
        Assert.True(models.CanEdit);
        Assert.Equal(["anthropic", "gateway", "codex"], models.Providers.Select(p => p.Id).ToList());
        Assert.Equal(2314, models.CatalogModels);
        models.Providers[0].Last4 = "3f2a";
        models.Providers[0].IsExpanded = true;
        Dispatch();
        Snapshot(window, "providers", width, dark, last: false);

        // OAuth 进度:授权 URL / 配对码由控制端展示
        stores.Providers.Update(m => ConfigReducer.ApplyOAuth(m, "local", new OAuthProgressDto("codex", OAuthPhases.Waiting, UserCode: "ABCD-1234", VerificationUrl: "https://auth.openai.com/device")));
        Dispatch();
        Assert.NotNull(models.OAuth);
        Assert.True(models.OAuth!.OpenCommand.CanExecute(null));
        Assert.Equal("ABCD-1234", models.OAuth.UserCode);
        var open = window.GetVisualDescendants().OfType<Ncw.UI.Controls.NcwButton>().First(b => ReferenceEquals(b.Command, models.OAuth.OpenCommand));
        Assert.True(open.IsEnabled, "open-browser button is enabled");
        Snapshot(window, "providers-oauth", width, dark);
    }

    [AvaloniaFact]
    public void Transcript_follows_the_latest_message_until_the_user_scrolls_up()
    {
        var stores = Base("s-ncw-1");
        var many = Enumerable.Range(0, 60).SelectMany(i => new[]
        {
            User($"u{i}", $"第 {i} 个问题:请解释一下这一段代码到底做了什么,为什么要这样设计?"),
            Assistant($"a{i}", 60 - i, Text($"第 {i} 个回答。" + string.Concat(Enumerable.Repeat("这是一段比较长的说明文字。", 8)))),
        }).ToList();
        Session(stores, "s-ncw-1", many);
        var (window, shell, vm) = Render(stores, 1200, true);

        var view = shell.GetVisualDescendants().OfType<ConversationView>().Single();
        var scroller = view.Scroller!;
        bool AtBottom() => scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 16;
        Settle(() => view.IsFollowing && AtBottom()); // 虚拟化列表按估算高度贴底,要几轮布局才收敛
        Assert.True(view.IsFollowing);
        Assert.True(AtBottom(), "starts at the bottom");

        // 往上滚:停止跟随;新消息到达不再把视图拽回底部
        scroller.Offset = new Avalonia.Vector(0, 200);
        Settle(() => !view.IsFollowing);
        Assert.False(view.IsFollowing);
        Session(stores, "s-ncw-1", many.Append(Assistant("a-new", 0, Text("新的回复"))));
        Settle(() => false, rounds: 6);
        Assert.False(view.IsFollowing);
        Assert.True(scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 100);

        // 回到最新:恢复跟随
        Settle(() => { scroller.ScrollToEnd(); return view.IsFollowing && AtBottom(); }, rounds: 20);
        Assert.True(view.IsFollowing);
        Snapshot(window, "follow", 1200, true);
    }
}

internal static class FixtureExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
