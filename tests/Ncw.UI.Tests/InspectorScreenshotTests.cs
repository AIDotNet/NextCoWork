using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Inspector;
using Ncw.UI.Views.Desktop.Conversation;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 右侧检查器各页签的 Headless 截图(四个断点 × 深浅两种主题)和界面结构断言。
/// ≥ 1440(Wide)渲染完整的桌面外壳,检查器停靠在右侧;更窄的断点按设计 03 §2 检查器以抽屉形式覆盖,抽屉本身还没有实现(外壳在 &lt; 1440 时不显示检查器),
/// 所以这几档把检查器面板单独放在对应宽度的窗口右侧(宽度取 400 / 480 / 520,即窗口变窄时抽屉的可能宽度),验证面板在各种宽度下的版式。
/// 截图在 <c>bin/Debug/net10.0/screenshots/inspector-*.png</c>。数据全部经 Store 和脚本化后端进来(和设备推送的形状一致),不经过连接。
/// </summary>
public class InspectorScreenshotTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    /// <summary>窗口宽度 → 检查器宽度:Wide 是停靠的默认宽度 320(完整外壳里),其余是抽屉的可能宽度。</summary>
    private static double PanelWidth(double window) => window switch { >= 1440 => 320, >= 1024 => 400, >= 720 => 480, _ => 520 };

    private sealed record View(Window Window, InspectorViewModel Inspector, InspectorPanel Panel, DesktopShell? Shell, ShellViewModel? ShellVm);

    private static View Render(InspectorRig rig, double width, bool dark, double height = 860, Action<InspectorViewModel>? setup = null)
    {
        // 动效关掉:截图是静态的,弹簧动画在 headless 里不会随时间推进,会拍到半截
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var clock = new FixedClock(Now);
        Window window;
        InspectorViewModel inspector;
        InspectorPanel panel;
        DesktopShell? shell = null;
        ShellViewModel? shellVm = null;
        if (width >= 1440)
        {
            shellVm = new ShellViewModel(rig.Stores, clock, inspector: rig.Services);
            inspector = shellVm.Inspector;
            setup?.Invoke(inspector);
            shell = new DesktopShell { DataContext = shellVm, Chrome = new WindowChrome(78, false) };
            window = new Window { Width = width, Height = height, Content = shell };
            window.Show();
            Dispatch();
            panel = shell.GetVisualDescendants().OfType<InspectorPanel>().Single();
        }
        else
        {
            inspector = new InspectorViewModel(rig.Stores, clock, rig.Services);
            setup?.Invoke(inspector);
            panel = new InspectorPanel { DataContext = inspector, Width = PanelWidth(width), HorizontalAlignment = HorizontalAlignment.Right };
            var host = new Border { Child = panel };
            window = new Window { Width = width, Height = height, Content = host };
            window.Show();
            host.Background = new SolidColorBrush(ArcTokens.GetColor(window, ArcDesignTokenKind.Background));
        }

        Dispatch();
        return new View(window, inspector, panel, shell, shellVm);
    }

    private static void Dispatch(int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(View view, string scenario, double width, bool dark, bool last = true)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        view.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"inspector-{scenario}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        if (last)
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Full));
        }
    }

    private static IEnumerable<T> Find<T>(View view) where T : Visual => view.Window.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    // ───────────────────────── 文件 ─────────────────────────

    private const string AppSource = """
        using System;
        using System.Collections.Generic;

        namespace Ncw.UI.Views.Desktop;

        /// <summary>右侧检查器:默认是文件管理器。</summary>
        public sealed partial class InspectorPanel : UserControl
        {
            private InspectorViewModel? _vm;

            public InspectorPanel()
            {
                InitializeComponent();
                _tick.Tick += (_, _) => _vm?.Tick();
            }

            protected override void OnDataContextChanged(EventArgs e)
            {
                base.OnDataContextChanged(e);
                _vm = DataContext as InspectorViewModel;
            }
        }
        """;

    private static InspectorRig FilesRig()
    {
        var rig = InspectorRig.Create();
        rig.Files.SetContent(InspectorRig.Project, "src/Ncw.UI/Views/Desktop/InspectorPanel.axaml.cs", AppSource);
        rig.Files.SetContent(InspectorRig.Project, "src/Ncw.Client/ViewModels/InspectorViewModel.cs", "namespace Ncw.Client.ViewModels;\n\n// TODO: 接入 Store\npublic sealed class InspectorViewModel {}\n");
        rig.Files.SetContent(InspectorRig.Project, "docs/design/03-ui-layout.md", "# 布局\n\nTODO: 补充检查器页签\n");
        return rig;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Files_tree_expanded_selected_with_git_markers(double width, bool dark)
    {
        var rig = FilesRig();
        var view = Render(rig, width, dark);
        var tree = view.Inspector.Files.Tree;
        await tree.ExpandAsync(tree.Find("src/Ncw.UI")!);
        await tree.ExpandAsync(tree.Find("src/Ncw.UI/Views")!);
        await tree.ExpandAsync(tree.Find("src/Ncw.UI/Views/Desktop")!);
        await tree.ExpandAsync(tree.Find("docs")!);
        view.Inspector.Files.Selected = tree.Find("src/Ncw.UI/Views/Desktop/InspectorPanel.axaml");
        Dispatch(8);

        // 真实的树:src 默认展开,目录在前,Git 状态角标和「含变更的目录后一个点」
        Assert.Equal("src", tree.Roots.First(n => n.IsDirectory && n.Name == "src").Name);
        Assert.Equal("M", tree.Find("src/Ncw.UI/Views/Desktop/InspectorPanel.axaml")!.ChangeLetter);
        Assert.True(tree.Find("src/Ncw.Core")!.HasChangedDescendant);
        Assert.Contains(Find<FileRow>(view), r => (r.DataContext as FileNodeViewModel)?.Name == "InspectorPanel.axaml");
        Snapshot(view, "files-tree", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Files_search_by_name_and_by_content(double width, bool dark)
    {
        var rig = FilesRig();
        var view = Render(rig, width, dark);
        var search = view.Inspector.Files.Search;

        search.Query = "Inspector";
        await search.Settled;
        Dispatch(8);

        Assert.True(view.Inspector.Files.ShowsSearch);
        Assert.Contains(search.Results, r => r.Name == "InspectorViewModel.cs");
        Snapshot(view, "files-search-names", width, dark, last: false);

        search.Mode = FileSearchMode.Contents;
        search.Query = "TODO";
        await search.Settled;
        Dispatch(8);

        Assert.All(search.Results, r => Assert.True(r.HasLine));
        Assert.Equal(2, search.Results.Count);
        Snapshot(view, "files-search-contents", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task File_preview_shows_highlighted_code_and_edit_mode_a_text_box(double width, bool dark)
    {
        var rig = FilesRig();
        var view = Render(rig, width, dark);
        var files = view.Inspector.Files;

        await files.OpenPathAsync("src/Ncw.UI/Views/Desktop/InspectorPanel.axaml.cs", null);
        Dispatch(10);

        Assert.True(files.HasPreview);
        Assert.False(files.ShowsTree);
        Assert.Contains("class InspectorPanel", files.Preview!.DisplayText);
        Assert.Equal("csharp", files.Preview.Language);
        Assert.Contains(Find<NcwCodeBlock>(view), _ => true);
        Snapshot(view, "file-preview", width, dark, last: false);

        files.Preview.EditCommand.Execute(null);
        files.Preview.EditText = files.Preview.EditText + "\n// edited\n";
        Dispatch(8);

        Assert.True(files.Preview.CanSave);
        Assert.Contains(Find<NcwTextArea>(view), t => t.Name == "EditBox");
        Snapshot(view, "file-edit", width, dark);
    }

    // ───────────────────────── Git ─────────────────────────

    private static async Task<InspectorRig> GitRig(View? _ = null)
    {
        var rig = InspectorRig.Create();
        await Task.CompletedTask;
        return rig;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Git_changes_grouped_into_staged_unstaged_untracked(double width, bool dark)
    {
        var rig = await GitRig();
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Git);
        Dispatch(8);
        var git = view.Inspector.Git;

        // 分组完全来自 Core 的 GitStatus 派生:新增(已暂存)/ 修改(未暂存)/ 未跟踪
        Assert.Equal(["src/Ncw.Client/ViewModels/InspectorViewModel.cs"], git.Staged.Select(i => i.Path));
        Assert.Equal(["README.md", "docs/design/03-ui-layout.md", "src/Ncw.UI/Views/Desktop/InspectorPanel.axaml"], git.Unstaged.Select(i => i.Path).Order(StringComparer.Ordinal).Select(p => p));
        Assert.Equal(["src/Ncw.Core/Domain/Subagents.cs"], git.Untracked.Select(i => i.Path));
        Assert.Equal("v3/avalonia", git.Branch);
        Assert.Contains(Find<GitPane>(view), _ => true);
        Snapshot(view, "git-changes", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Git_diff_unified_and_side_by_side(double width, bool dark)
    {
        var rig = await GitRig();
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Git);
        var git = view.Inspector.Git;

        await git.OpenDiffCommand.ExecuteAsync(git.Unstaged.First(i => i.Name == "03-ui-layout.md"));
        Dispatch(8);

        Assert.True(git.HasDiff);
        Assert.Equal((2, 1), (git.Diff!.Added, git.Diff.Removed));
        Assert.Contains(Find<DiffView>(view), _ => true);
        Snapshot(view, "git-diff-unified", width, dark, last: false);

        git.Diff.SetModeCommand.Execute(DiffMode.Split);
        Dispatch(8);

        Assert.True(git.Diff.IsSplit);
        Snapshot(view, "git-diff-split", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Git_commit_box_generates_a_message_then_history(double width, bool dark)
    {
        var rig = await GitRig();
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Git);
        var git = view.Inspector.Git;
        await git.StageAllCommand.ExecuteAsync(null);

        await git.GenerateMessageCommand.ExecuteAsync(null);
        Dispatch(8);

        Assert.Equal("feat(ui): 接入检查器的文件、Git 与终端页签", git.CommitMessage);
        Assert.True(git.CanCommit);
        Assert.Equal(5, git.Staged.Count); // 全部暂存后(未暂存 + 未跟踪),暂存区有 5 个文件
        Assert.Empty(git.Unstaged);
        Snapshot(view, "git-commit", width, dark, last: false);

        await git.ToggleHistoryCommand.ExecuteAsync(null);
        Dispatch(8);

        Assert.Equal(2, git.Commits.Count);
        Snapshot(view, "git-history", width, dark);
    }

    // ───────────────────────── 审查 ─────────────────────────

    private static ReviewChangeSetDto ReviewSet(string runId, string state, long updated, params (string Path, string Kind, int Add, int Del)[] files) =>
        new(runId, "s-ncw-1", "nextcowork", state, files.Length, files.Sum(f => f.Add), files.Sum(f => f.Del),
            [.. files.Select(f => new ReviewFileEntryDto(f.Path, f.Kind, f.Add, f.Del, false, true))], updated);

    private static InspectorRig ReviewRig(string state = ReviewStates.Applied)
    {
        var rig = InspectorRig.Create();
        rig.Review.Sets =
        [
            ReviewSet("run-2", state, Ms(Now.AddMinutes(-4)),
                ("src/Ncw.Client/ViewModels/InspectorViewModel.cs", ReviewChangeKinds.Modified, 42, 11),
                ("src/Ncw.Client/ViewModels/Inspector/GitPanelViewModel.cs", ReviewChangeKinds.Created, 310, 0),
                ("docs/design/03-ui-layout.md", ReviewChangeKinds.Modified, 6, 2),
                ("src/Ncw.Core/Domain/OldThing.cs", ReviewChangeKinds.Deleted, 0, 25)),
            ReviewSet("run-1", ReviewStates.Applied, Ms(Now.AddHours(-2)), ("README.md", ReviewChangeKinds.Modified, 3, 1)),
        ];
        rig.Review.Diffs["docs/design/03-ui-layout.md"] = new ReviewFileDiffDto("docs/design/03-ui-layout.md", ReviewChangeKinds.Modified,
            "# 布局\n\n## 右侧面板\n\n| 页签 | 内容 |\n|---|---|\n| 文件 | 文件管理器 |\n| Git | 变更列表 |\n\n页签组成规则是纯函数。\n",
            "# 布局\n\n## 右侧面板\n\n| 页签 | 内容 |\n|---|---|\n| 文件 | 文件管理器 |\n| Git | 变更列表与提交 |\n| 终端 | 多标签终端 |\n\n页签组成规则是纯函数。\n", false);
        rig.Stores.Review.Update(m => ReviewReducer.ApplyMany(m, "local", rig.Review.Sets));
        return rig;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Review_rounds_files_and_diff(double width, bool dark)
    {
        var rig = ReviewRig();
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Review);
        Dispatch(8);
        var review = view.Inspector.Review;

        Assert.Equal(["run-2", "run-1"], review.Runs.Select(r => r.RunId));
        Assert.Equal(4, review.Files.Count);
        Assert.True(review.CanUndo);
        Snapshot(view, "review-list", width, dark, last: false);

        await review.OpenFileCommand.ExecuteAsync(review.Files.Single(f => f.Name == "03-ui-layout.md"));
        Dispatch(8);

        Assert.True(review.HasDiff);
        Assert.Equal((2, 1), (review.Diff!.Added, review.Diff.Removed));
        Snapshot(view, "review-diff", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Review_undo_precheck_conflicts_and_the_undone_state(double width, bool dark)
    {
        var rig = ReviewRig();
        rig.Review.Conflicts = ["README.md", "docs/design/03-ui-layout.md"];
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Review);
        var review = view.Inspector.Review;

        await review.UndoCommand.ExecuteAsync(null);
        Dispatch(8);

        Assert.True(review.HasPendingConflicts); // 预检发现磁盘上又被改过:列出来,等用户确认
        Snapshot(view, "review-conflicts", width, dark, last: false);

        // 带预检结果提示的撤销之后,变更集是「已撤销」,按钮变成「重做」
        rig.Review.Conflicts = [];
        rig.Stores.Review.Update(m => ReviewReducer.Apply(m, "local", "run-2", rig.Review.Sets[0] with { State = ReviewStates.Reverted, UpdatedAt = rig.Review.Sets[0].UpdatedAt + 1 }));
        review.CancelUndoCommand.Execute(null);
        Dispatch(8);

        Assert.True(review.CanRedo);
        Assert.False(review.CanUndo);
        Snapshot(view, "review-reverted", width, dark);
    }

    // ───────────────────────── 终端 ─────────────────────────

    private static async Task<View> OpenTerminal(InspectorRig rig, double width, bool dark, Action<InspectorRig>? before = null)
    {
        rig.AddTerminal(InspectorRig.Terminal("t1", "NextCoWork"));
        rig.AddTerminal(InspectorRig.Terminal("t2", "dotnet test", created: 2_000));
        before?.Invoke(rig);
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Terminal);
        await view.Inspector.Terminals.LoadAsync();
        Dispatch(8);
        return view;
    }

    private static void Push(InspectorRig rig, string id, string text)
    {
        rig.Terminals.Sinks[id].OnSnapshot(InspectorRig.Bytes(text), false);
        Dispatch(10);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Terminal_with_colored_output_titled_with_the_execution_environment(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        var view = await OpenTerminal(rig, width, dark);
        var panel = view.Inspector.Terminals;

        Assert.Equal(["t1", "t2"], panel.Tabs.Select(t => t.Id));
        Assert.Equal("t2", panel.Active!.Id);
        Assert.True(rig.Terminals.Sinks.ContainsKey("t2")); // 选中的标签已订阅
        Push(rig, "t2", InspectorRig.SampleOutput);

        Assert.Equal("本机 · local", panel.Active.EnvLabel); // 环境标识(设备 + 环境)显示在标题上
        var terminal = Find<NcwTerminal>(view).Single();
        Assert.False(terminal.IsUnavailable);
        Assert.True(terminal.Grid.Columns >= 20 && terminal.Grid.Rows >= 5);
        Snapshot(view, "terminal-live", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Terminal_exited_keeps_the_output_and_shows_the_exit_code(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        var view = await OpenTerminal(rig, width, dark);
        Push(rig, "t2", InspectorRig.SampleOutput);

        rig.Stores.Terminals.Update(m => TerminalReducer.ApplyExit(m, "local", "t2", new TerminalExitDto(1, null, "Exited"), 9));
        Dispatch(8);

        var tab = view.Inspector.Terminals.Active!;
        Assert.True(tab.IsExited);
        Assert.Equal(new TerminalExit(TerminalExitKind.Code, 1), tab.Exit);
        Assert.False(tab.CanType);
        Snapshot(view, "terminal-exited", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Terminal_unknown_when_the_device_disconnects_without_ending_or_counting(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        var view = await OpenTerminal(rig, width, dark);
        Push(rig, "t2", InspectorRig.SampleOutput);

        rig.Stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Offline }) });
        rig.Stores.Terminals.Update(m => TerminalReducer.MarkDeviceUnknown(m, "local"));
        Dispatch(8);

        var tab = view.Inspector.Terminals.Active!;
        Assert.True(tab.IsUnknown); // 不判成结束
        Assert.False(tab.IsExited);
        Assert.False(tab.CanType);
        Assert.False(view.Inspector.Terminals.CanCreate);
        Snapshot(view, "terminal-unknown", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Remote_terminal_waits_for_approval_showing_device_and_environment(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        rig.Terminals.NextIntent = new TerminalIntentDto("intent-1", "t-ssh", "nextcowork", new TerminalEnvDto("local", "dev-server", "dev-server", "ssh"), "/srv/api", "/bin/bash", 0);
        var view = Render(rig, width, dark);
        view.Inspector.AddCommand.Execute(InspectorTabKind.Terminal);
        await view.Inspector.Terminals.NewTerminalCommand.ExecuteAsync(null);
        Dispatch(8);

        var tab = view.Inspector.Terminals.Active!;
        Assert.True(tab.IsAwaitingApproval);
        Assert.Equal("本机 · dev-server", tab.EnvLabel);
        Assert.True(tab.IsRemote);
        Snapshot(view, "terminal-approval", width, dark);
    }

    // ───────────────────────── 子代理 ─────────────────────────

    private static InspectorRig SubagentRig()
    {
        var rig = InspectorRig.Create();
        // 真实数据:对话镜像里的卡片(模拟数据里 Workspace 的那两张去掉,免得重复)
        rig.Stores.Workspace.Update(w => w with { Subagents = [] });
        Subagent(rig.Stores, "s-ncw-1", "k1", "code-analyst", "阅读 kernel/upstream", SubagentStatus.Running, 14, "Grep");
        Subagent(rig.Stores, "s-ncw-1", "k2", "general-purpose", "调研 pi 的设计", SubagentStatus.Done, 45, null, "pi 把工具协议和事件日志拆成两层……");
        // 重启之后只靠回执重建的旧卡片:没有子会话 id、没有子 Run id
        var legacy = new SubagentRecord("s-ncw-1", "k3", "explorer", "整理旧的设计文档", SubagentStatus.Done, Now.AddHours(-3), Now.AddHours(-2), 8, null);
        var entry = new SubagentEntry(new SubagentRef("local", "s-ncw-1", "k3"), legacy, SubagentTelemetry.Empty, 1);
        rig.Stores.Conversations.Update(c => c with { Subagents = c.Subagents.SetItem(entry.Ref, entry) });
        Session(rig.Stores, "child-s-k1",
        [
            User("cu1", "阅读 kernel 与 upstream 的实现,总结事件日志的 seq 规则。", 3),
            Assistant("ca1", 3,
                Thinking("先看 Kernel 的事件定义,再看 upstream 的归约。", 3200),
                Text("好的,我先搜索 `seq` 的使用位置。"),
                Call("cc1", "Grep", "{\"pattern\":\"seq\",\"path\":\"src/Ncw.Agent.Kernel\"}"),
                Call("cc2", "Read", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Agent.Runs/EventLog.cs\"}")),
            ToolResults("cr1", 2, Result("cc1", "src/Ncw.Agent.Kernel/Events.cs:42: long Seq"), Result("cc2", "(file contents)")),
            Assistant("ca2", 1, Text("事件的 `seq` **严格连续**,有缺口就是丢了:\n\n1. 每个事件占一个序号\n2. 退出事件也占一个\n")),
        ]);
        return rig;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Subagents_cards_with_stop_and_open_and_the_old_card_notice(double width, bool dark)
    {
        var rig = SubagentRig();
        var view = Render(rig, width, dark);
        view.Inspector.ActivateCommand.Execute(InspectorTabKind.Subagents);
        Dispatch(8);

        Assert.True(view.Inspector.IsSubagents);
        Assert.Contains(view.Inspector.Subagents, s => s.CallId == "k1" && s.CanStop && s.CanOpen);
        // 重启之后只靠回执重建的旧卡片(k3 没有子会话 id):点不开,给出明确的提示
        var old = view.Inspector.Subagents.Single(s => !s.CanOpen);
        Assert.Equal("k3", old.CallId);
        await view.Inspector.OpenSubagentCommand.ExecuteAsync(old);
        Dispatch(8);

        Assert.Equal(SubagentNotice.NoChildSession, view.Inspector.Notice);
        Snapshot(view, "subagents-list", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Subagent_transcript_shows_the_child_sessions_rows(double width, bool dark)
    {
        var rig = SubagentRig();
        var view = Render(rig, width, dark);
        view.Inspector.ActivateCommand.Execute(InspectorTabKind.Subagents);

        view.Inspector.ShowTranscript(new SessionRef("local", "child-s-k1"), "code-analyst · 阅读 kernel/upstream");
        Dispatch(8);

        var transcript = view.Inspector.Transcript!;
        Assert.False(transcript.IsLoading);
        Assert.IsType<UserRowViewModel>(transcript.Rows[0]);
        Assert.Contains(transcript.Rows, r => r is ToolRowViewModel { Name: "Grep" });
        Assert.Contains(transcript.Rows, r => r is AssistantRowViewModel);
        Snapshot(view, "subagents-transcript", width, dark);
    }

    // ───────────────────────── 输入区 @ / 命令 ─────────────────────────

    private sealed class TestLookups(MockFiles files) : Ncw.Client.ViewModels.IComposerLookups
    {
        public async Task<IReadOnlyList<FileSuggestionItem>?> SuggestFilesAsync(SessionRef session, string workspaceId, string query, CancellationToken ct)
        {
            var result = await files.SuggestAsync(session.DeviceId, workspaceId, query, 12, ct);
            return result.Value is { } v ? [.. v.Suggestions.Select(s => new FileSuggestionItem(s.Path, s.Name, s.Path.Contains('/') ? s.Path[..s.Path.LastIndexOf('/')] : ""))] : null;
        }

        public Task<IReadOnlyList<CommandDto>?> ListCommandsAsync(SessionRef session, string workspaceId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommandDto>?>(
            [
                new CommandDto("review", "审查当前改动", "审查:$ARGUMENTS", "project", "file", "[路径]"),
                new CommandDto("release-notes", "根据提交历史起草发布说明", "起草发布说明", "global", "file", null),
            ]);

        public Task<string?> ExpandAsync(SessionRef session, string workspaceId, string draft, CancellationToken ct) => Task.FromResult<string?>(draft);
    }

    private static (Window Window, ShellViewModel Vm) RenderShellWithLookups(InspectorRig rig, double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(rig.Stores, new FixedClock(Now), inspector: rig.Services, lookups: new TestLookups(rig.Files));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = 860, Content = shell };
        window.Show();
        Dispatch();
        return (window, vm);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Composer_at_popup_suggests_files_and_picking_adds_a_reference_chip(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        var (window, vm) = RenderShellWithLookups(rig, width, dark);
        var composer = vm.Conversation.Composer;

        composer.Draft = "看一下 @Insp";
        await composer.WhenSuggested;
        Dispatch(8);

        Assert.True(composer.ShowsSuggestions);
        Assert.Contains(composer.FileSuggestions, f => f.Name == "InspectorViewModel.cs");
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(), b => b.Name == "FilePopup" && b.IsEffectivelyVisible);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"inspector-composer-at-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        composer.MoveSuggestionCommand.Execute(1);
        composer.AcceptSuggestion();
        Dispatch(8);

        Assert.Single(composer.FileRefs);
        Assert.Equal("看一下 ", composer.Draft);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"inspector-composer-chip-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Full));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Composer_slash_menu_lists_device_commands_and_placeholders(double width, bool dark)
    {
        var rig = InspectorRig.Create();
        var (window, vm) = RenderShellWithLookups(rig, width, dark);
        var composer = vm.Conversation.Composer;

        composer.Draft = "/";
        for (var i = 0; i < 50 && !composer.SlashCommands.Any(c => c.IsAvailable); i++)
        {
            await Task.Delay(10);
        }

        Dispatch(8);

        Assert.True(composer.HasSlashQuery);
        Assert.Contains(composer.SlashCommands, c => c is { Command: "/review", IsAvailable: true });
        Assert.Contains(composer.SlashCommands, c => c is { Command: "/compact", IsAvailable: false });
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"inspector-composer-slash-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Full));
    }
}
