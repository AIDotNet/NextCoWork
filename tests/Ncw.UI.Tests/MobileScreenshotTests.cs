using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Themes;
using Ncw.UI.Views;
using Ncw.UI.Views.Mobile;
using TKCode.Arc;
using TKCode.Arc.Motion;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 手机壳(docs/design/03 §3、05 M5)的 Headless 截图:手机宽度 360 / 390 / 430 与平板 768,深浅两种主题。
/// 覆盖收件箱(空 / 有待办 / 多设备 / 设备断开 / 已在另一台设备上处理)、会话页的审批卡片(长按进度)、设备页、扫码配对(预览占位 / 解析成功 / 失败 / 成功)、
/// 设置导航栈、附件上传进度。截图在 <c>bin/Debug/net11.0/screenshots/mobile-*.png</c>。数据全部经 Store 进来,不经过连接;动画关掉(MotionLevel.Off),页面切换立即完成。
/// </summary>
public class MobileScreenshotTests : IDisposable
{
    /// <summary>别的测试类默认深色、完整动效:每个用例结束后还原,不被这里的浅色 / 关动效带偏。</summary>
    public void Dispose() => ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(ArcMotionLevel.Off));

    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 360, 390, 430 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    private sealed record Harness(Window Window, AdaptiveShell Shell, ShellViewModel Vm, ClientStores Stores, ManualClock Clock)
    {
        public MobileShell Mobile => Shell.Mobile!;
    }

    private static Harness Render(ClientStores stores, double width, bool dark, double height = 800, MobileHostServices? host = null, IAttachmentUploader? uploader = null)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(ArcMotionLevel.Off));
        var clock = new ManualClock();
        Ncw.UI.Converters.RelativeTimeConverter.Clock = clock;
        var vm = new ShellViewModel(stores, clock, host: host, attachmentUploader: uploader);
        var shell = new AdaptiveShell();
        var window = new Window { Width = width, Height = height, Content = shell };
        window.DataContext = vm;
        window.Show();
        Dispatch();
        return new Harness(window, shell, vm, stores, clock);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 5; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Settle(Func<bool> until, int rounds = 20)
    {
        for (var i = 0; i < rounds && !until(); i++)
        {
            Dispatch();
        }
    }

    /// <summary>页面推入 / 弹出的滑动动画按真实时间走:等栈顶页面停在 x = 0(最多约 1.5 秒)再截图。桌面外壳没有栈,直接返回。</summary>
    private static void LetNavigationLand(Harness h)
    {
        if (h.Shell.Mobile is not { } mobile)
        {
            return;
        }

        var tab = h.Vm.Navigation.SelectedTab;
        for (var i = 0; i < 60; i++)
        {
            Dispatch();
            if (mobile.StackDepth(tab) == h.Vm.Navigation.StackOf(tab).Count && mobile.TopPage(tab) is { } top && Avalonia.VisualExtensions.TranslatePoint(top, default, h.Window) is { } at && Math.Abs(at.X) < 0.5)
            {
                // 落定之后再让动画的收尾(下面一页退场)跑完
                Thread.Sleep(150);
                Dispatch();
                return;
            }

            Thread.Sleep(25);
        }
    }

    private static void Snapshot(Harness h, string scenario, double width, bool dark)
    {
        LetNavigationLand(h);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        h.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"mobile-{scenario}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static IEnumerable<T> Descendants<T>(Window window) where T : Control => window.GetVisualDescendants().OfType<T>();

    // ───────────────────────── 宽度切换 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(360, ShellKind.Mobile)]
    [InlineData(430, ShellKind.Mobile)]
    [InlineData(719, ShellKind.Mobile)]
    [InlineData(720, ShellKind.Desktop)]
    [InlineData(768, ShellKind.Desktop)]
    [InlineData(1500, ShellKind.Desktop)]
    public void The_shell_is_chosen_by_width_only(double width, ShellKind expected)
    {
        Assert.Equal(expected, AdaptiveShell.KindFor(width));
        var h = Render(MobileFixtures.Quiet(), width, dark: true, height: 900);

        Assert.Equal(expected, h.Shell.Kind);
        Assert.Equal(expected == ShellKind.Mobile, h.Shell.Mobile is not null);
        if (width is 768 or 390)
        {
            Snapshot(h, width == 768 ? "tablet" : "phone-home", width, dark: true);
        }
    }

    [AvaloniaFact]
    public void Resizing_across_the_breakpoint_swaps_shells_over_one_view_model_without_losing_state()
    {
        var h = Render(MobileFixtures.Pending(), 900, dark: true);
        Assert.Equal(ShellKind.Desktop, h.Shell.Kind);
        var desktop = h.Shell.Desktop;

        h.Window.Width = 400;
        Dispatch();
        Assert.Equal(ShellKind.Mobile, h.Shell.Kind);
        Assert.Same(h.Vm, h.Shell.Mobile!.DataContext);
        h.Vm.Navigation.OpenProject(new ProjectRef("local", "nextcowork"));
        Dispatch();
        Assert.Equal(1, h.Shell.Mobile.Depth(MobileTab.Sessions));

        h.Window.Width = 900;
        Dispatch();
        Assert.Equal(ShellKind.Desktop, h.Shell.Kind);
        Assert.Same(desktop, h.Shell.Desktop); // 外壳按需创建、创建后留着
        Assert.Same(h.Vm, h.Shell.Desktop!.DataContext);

        h.Window.Width = 400;
        Dispatch();
        Assert.Equal(1, h.Shell.Mobile.Depth(MobileTab.Sessions)); // 手机壳的导航栈也还在
    }

    // ───────────────────────── 收件箱 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Inbox_when_nothing_is_pending_is_an_empty_state(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark);

        Assert.True(h.Vm.Inbox.IsEmpty);
        Assert.Equal(0, h.Vm.Inbox.BadgeCount);
        Snapshot(h, "inbox-empty", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Inbox_lists_pending_items_most_urgent_first_with_notifications_below(double width, bool dark)
    {
        var h = Render(MobileFixtures.Pending(), width, dark);

        var group = Assert.Single(h.Vm.Inbox.Groups);
        Assert.Equal(["i:i1", "i:i2", "i:i3", "r:run-bad", "r:run-done"], group.Entries.Select(e => e.Key));
        Assert.Equal(3, h.Vm.Inbox.BadgeCount);
        Assert.True(group.Entries[0].IsDangerous);
        Assert.Equal("local · local", group.Entries[0].EnvLabel is { } env ? env.Replace("本机", "local") : null);
        Snapshot(h, "inbox-pending", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Inbox_groups_items_by_device(double width, bool dark)
    {
        var h = Render(MobileFixtures.MultiDevice(), width, dark);

        Assert.Equal(["cloud", "local", MobileFixtures.Studio], h.Vm.Inbox.Groups.Select(g => g.DeviceId)); // 等得最久的设备在前
        Assert.Equal(5, h.Vm.Inbox.BadgeCount);
        Snapshot(h, "inbox-devices", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Inbox_marks_a_disconnected_device_unknown_and_does_not_count_its_items(double width, bool dark)
    {
        var h = Render(MobileFixtures.Offline(), width, dark);

        var studio = h.Vm.Inbox.Groups.Single(g => g.DeviceId == MobileFixtures.Studio);
        Assert.True(studio.IsUnknown);
        Assert.True(studio.Entries.Single().IsUnknown);
        Assert.Equal(3, h.Vm.Inbox.BadgeCount); // 只有本机的三个
        Assert.True(h.Vm.Inbox.HasUnknown);
        Snapshot(h, "inbox-offline", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Inbox_shows_already_handled_on_another_device_when_a_pending_item_disappears_without_a_local_answer(double width, bool dark)
    {
        var h = Render(MobileFixtures.Pending(), width, dark);
        Assert.Equal(3, h.Vm.Inbox.BadgeCount);

        // 另一台控制端抢先批准了 i1:设备推来的全量交互里没有它了
        MobileFixtures.Interactions(h.Stores, "local", 10, h.Stores.Conversations.State.InteractionsOf("local").Pending.Where(i => i.Id != "i1").ToArray());
        Dispatch();

        var tomb = h.Vm.Inbox.Groups.Single().Entries.Single(e => e.IsHandledElsewhere);
        Assert.Equal("i:i1", tomb.Key);
        Assert.Equal(2, h.Vm.Inbox.BadgeCount);
        Snapshot(h, "inbox-handled", width, dark);
    }

    [AvaloniaFact]
    public void Tapping_an_inbox_item_opens_its_session_and_focuses_the_card()
    {
        var stores = MobileFixtures.Pending();
        Session(stores, "s-ncw-1", [User("u1", "清理并发布"), Assistant("a1", 3, Text("先确认。"), Call("c-i1", "Bash", "{\"command\":\"rm -rf out/\"}"))], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v);
        var h = Render(stores, 390, dark: true);
        var entry = h.Vm.Inbox.Groups.Single().Entries.First(e => e.Key == "i:i1");

        h.Vm.Inbox.OpenCommand.Execute(entry);
        Dispatch();

        Assert.Equal(MobileTab.Sessions, h.Vm.Navigation.SelectedTab);
        Assert.IsType<SessionRoute>(h.Vm.Navigation.Top);
        Assert.False(h.Vm.Navigation.IsTabBarVisible);
        Assert.Equal(2, h.Mobile.Depth(MobileTab.Sessions)); // 项目 → 对话
        var card = h.Vm.Conversation.Rows.OfType<InteractionRowViewModel>().Single(r => r.InteractionId == "i1");
        Assert.True(card.IsHighlighted);
    }

    // ───────────────────────── 会话页与长按允许 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Session_page_shows_a_hold_to_allow_approval_card_with_progress(double width, bool dark)
    {
        var h = Render(MobileFixtures.SessionWithApproval(), width, dark);
        h.Vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
        Dispatch();
        var row = h.Vm.Conversation.Rows.OfType<InteractionRowViewModel>().Single();

        // 触控版式:有「长按允许」按钮,桌面的按钮行藏起来;会话页不显示底部标签栏
        var hold = Descendants<NcwHoldButton>(h.Window).Single();
        Assert.True(hold.IsEffectivelyVisible);
        Assert.True(hold.IsDangerous);
        Assert.False(h.Mobile.FindControl<Grid>("TabBarHost")!.IsVisible);
        Snapshot(h, "session-approval", width, dark);

        // 按住 40%:危险审批要按 1.5s,按了 0.6s
        row.BeginAllowHold();
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        row.TickAllowHold();
        Dispatch();
        Assert.InRange(row.HoldProgress, 0.39, 0.41);
        Assert.True(hold.IsHolding);
        Snapshot(h, "session-approval-holding", width, dark);
    }

    [AvaloniaFact]
    public void Releasing_early_cancels_and_the_fill_rewinds_while_holding_long_enough_allows()
    {
        var h = Render(MobileFixtures.SessionWithApproval(), 390, dark: true);
        h.Vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
        Dispatch();
        var row = h.Vm.Conversation.Rows.OfType<InteractionRowViewModel>().Single();

        row.BeginAllowHold();
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        row.TickAllowHold();
        row.EndAllowHold();
        Assert.Equal(HoldPhase.Cancelled, row.AllowHold.Phase);
        Assert.Equal(0, row.HoldProgress);

        var completed = 0;
        row.AllowHold.Completed += () => completed++;
        row.BeginAllowHold();
        h.Clock.Advance(InteractionRowViewModel.DangerousHoldDuration);
        row.TickAllowHold();
        Assert.Equal(1, completed); // 按满只触发一次回答(这里没有设备,回答失败后手势解锁,可以再按)
    }

    // ───────────────────────── 会话列表 / 项目 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Sessions_tab_lists_projects_by_group_and_a_project_pushes_its_session_list(double width, bool dark)
    {
        var h = Render(MobileFixtures.Pending(), width, dark);
        h.Vm.Navigation.SelectTabCommand.Execute(MobileTab.Sessions);
        Dispatch();
        Snapshot(h, "projects", width, dark);

        h.Vm.Navigation.OpenProject(new ProjectRef("local", "nextcowork"));
        Dispatch();
        Assert.Equal(1, h.Mobile.Depth(MobileTab.Sessions));
        Assert.IsType<ProjectSessionsPage>(h.Mobile.TopPage(MobileTab.Sessions));
        Snapshot(h, "project-sessions", width, dark);
    }

    // ───────────────────────── 设备页与扫码配对 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Devices_page_lists_every_device_with_its_connection_state(double width, bool dark)
    {
        var h = Render(MobileFixtures.Offline(), width, dark);
        h.Vm.Navigation.SelectTabCommand.Execute(MobileTab.Devices);
        Dispatch();

        Assert.Equal(["local", "cloud", "studio"], h.Vm.DevicesPage.Items.Select(i => i.DeviceId));
        Assert.True(h.Vm.DevicesPage.Items.Single(i => i.DeviceId == "studio").Unknown);
        Snapshot(h, "devices", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Qr_pairing_page_shows_a_preview_placeholder_then_the_parsed_device(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark, host: new MobileHostServices { QrScanner = new FakeQrScanner() });
        h.Vm.DevicesPage.AddByQrCommand.Execute(null);
        Dispatch();

        Assert.IsType<QrPairPage>(h.Mobile.TopPage(MobileTab.Devices));
        Assert.Equal(QrPairingPhase.Idle, h.Vm.QrPairing.Phase);
        Snapshot(h, "qr-idle", width, dark);

        h.Vm.QrPairing.Submit(MobileFixtures.Qr(h.Clock.GetUtcNow().AddMinutes(10)));
        Dispatch();
        Assert.Equal(QrPairingPhase.Parsed, h.Vm.QrPairing.Phase);
        Snapshot(h, "qr-parsed", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Qr_pairing_page_reports_an_unreadable_code_and_a_denied_camera(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark, host: new MobileHostServices { QrScanner = new FakeQrScanner(access: CameraAccess.Denied) });
        h.Vm.DevicesPage.AddByQrCommand.Execute(null);
        Dispatch();

        h.Vm.QrPairing.Submit("https://example.com/not-a-pairing-code");
        Dispatch();
        Assert.Equal(QrPairingFailure.InvalidCode, h.Vm.QrPairing.Failure);
        Snapshot(h, "qr-invalid", width, dark);

        h.Vm.QrPairing.StartScanCommand.Execute(null);
        Settle(() => h.Vm.QrPairing.Failure == QrPairingFailure.CameraDenied);
        Assert.Equal(QrPairingFailure.CameraDenied, h.Vm.QrPairing.Failure);
        Snapshot(h, "qr-denied", width, dark);
    }

    [AvaloniaTheory]
    [InlineData(390, true)]
    [InlineData(390, false)]
    public void Qr_pairing_page_after_a_successful_pairing(double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(ArcMotionLevel.Off));
        var clock = new ManualClock();
        var pairing = new QrPairingViewModel(() => null, (_, _) => Task.FromResult(PairOutcome.Paired), clock);
        var page = new QrPairPage { DataContext = pairing };
        var host = new UserControl { Content = page };
        host.Resources.MergedDictionaries.Add(new NcwTheme());
        host.Styles.Add(new NcwStyles());
        var window = new Window { Width = width, Height = 800, Content = host };
        window.Show();
        pairing.Submit(MobileFixtures.Qr(clock.GetUtcNow().AddMinutes(10)));
        pairing.ConfirmCommand.Execute(null);
        Dispatch();

        Assert.Equal(QrPairingPhase.Paired, pairing.Phase);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"mobile-qr-paired-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Manual_add_page_uses_the_same_pairing_form_as_desktop_settings(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark);
        h.Vm.DevicesPage.AddManuallyCommand.Execute(null);
        Dispatch();

        Assert.IsType<ManualAddPage>(h.Mobile.TopPage(MobileTab.Devices));
        Snapshot(h, "manual-add", width, dark);
    }

    // ───────────────────────── 设置导航栈 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Settings_are_a_navigation_stack_with_a_list_of_entries_and_pushed_pages(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark);
        h.Vm.Navigation.SelectTabCommand.Execute(MobileTab.Settings);
        Dispatch();
        Assert.IsType<SettingsHomePage>(h.Mobile.TopPage(MobileTab.Settings));
        Snapshot(h, "settings-home", width, dark);

        h.Vm.Navigation.OpenSettingsSection(SettingsSection.Models);
        Dispatch();
        Assert.Equal(1, h.Mobile.Depth(MobileTab.Settings));
        Snapshot(h, "settings-models", width, dark);

        // 返回:栈弹出,回到入口列表
        Assert.True(h.Vm.Navigation.Pop());
        Dispatch();
        Assert.Equal(0, h.Mobile.Depth(MobileTab.Settings));
        Assert.IsType<SettingsHomePage>(h.Mobile.TopPage(MobileTab.Settings));
    }

    [AvaloniaTheory]
    [InlineData(SettingsSection.Devices)]
    [InlineData(SettingsSection.General)]
    [InlineData(SettingsSection.Skills)]
    [InlineData(SettingsSection.Commands)]
    [InlineData(SettingsSection.Hooks)]
    [InlineData(SettingsSection.Mcp)]
    [InlineData(SettingsSection.Usage)]
    [InlineData(SettingsSection.Storage)]
    [InlineData(SettingsSection.Project)]
    public void Every_implemented_settings_page_opens_in_the_phone_stack_without_horizontal_overflow(SettingsSection section)
    {
        var h = Render(MobileFixtures.Quiet(), 360, dark: true);
        h.Vm.Navigation.SelectTabCommand.Execute(MobileTab.Settings);
        h.Vm.Navigation.OpenSettingsSection(section);
        Dispatch();

        var page = Assert.IsType<NcwPage>(h.Mobile.TopPage(MobileTab.Settings));
        Assert.True(page.Bounds.Width <= 360.5);
        var content = (Control)page.Content!;
        Assert.True(content.Bounds.Width <= 360, $"{section} content is {content.Bounds.Width}px wide");
        if (section is SettingsSection.Hooks or SettingsSection.Storage or SettingsSection.Project)
        {
            Snapshot(h, $"settings-{section.ToString().ToLowerInvariant()}", 360, dark: true);
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Notifications_page_shows_the_push_registration_state(double width, bool dark)
    {
        var h = Render(MobileFixtures.Quiet(), width, dark);
        h.Vm.Navigation.SelectTabCommand.Execute(MobileTab.Settings);
        h.Vm.Navigation.Push(new NotificationsRoute());
        Dispatch();

        Assert.Equal(PushStatus.Unsupported, h.Vm.Push.Status); // 没有宿主提供推送
        Snapshot(h, "settings-notifications", width, dark);
    }

    // ───────────────────────── 附件上传进度 ─────────────────────────

    private static PickedAttachment Photo(string name = "IMG_2041.jpg", long size = 2_400_000) =>
        new(name, "image/jpeg", size, _ => Task.FromResult(new byte[8]));

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Composer_shows_attachment_upload_progress_then_done_and_failure(double width, bool dark)
    {
        var uploader = new FakeUploader();
        var h = Render(MobileFixtures.SessionWithApproval(), width, dark, host: new MobileHostServices { AttachmentPicker = new FakePicker() }, uploader: uploader);
        h.Vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
        Dispatch();
        var tray = h.Vm.Conversation.Composer.Attachments;

        var upload = tray.AddAsync(Photo());
        _ = tray.AddAsync(Photo("report-final-q3.pdf", 12_000_000) with { Mime = "application/pdf" });
        Settle(() => uploader.Progress is not null);
        uploader.ProgressOf["IMG_2041.jpg"]!.Report(1_080_000); // 45%
        Dispatch();
        Assert.True(tray.IsUploading);
        Assert.InRange(tray.Items[0].Progress, 0.44, 0.46);
        Assert.False(h.Vm.Conversation.Composer.State.CanSend); // 传完再发
        Snapshot(h, "attachments-uploading", width, dark);

        uploader.Gate.SetResult(new ActionResult<AttachmentDto>(ActionOutcome.Ok, new AttachmentDto("a1", "session", "s-ncw-1", "IMG_2041.jpg", "image/jpeg", 2_400_000, null, 0, "ncw://attachments/sessions/s-ncw-1/01A.jpg")));
        await upload;
        Dispatch();
        Assert.True(tray.Items[0].IsDone);
        Snapshot(h, "attachments-done", width, dark);
    }

    [AvaloniaFact]
    public async Task A_failed_upload_shows_the_reason_and_can_be_retried_or_removed()
    {
        var uploader = new FakeUploader();
        var h = Render(MobileFixtures.SessionWithApproval(), 390, dark: true, uploader: uploader);
        h.Vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
        Dispatch();
        var tray = h.Vm.Conversation.Composer.Attachments;

        var upload = tray.AddAsync(Photo());
        uploader.Gate.SetResult(new ActionResult<AttachmentDto>(ActionOutcome.Unavailable));
        await upload;
        Dispatch();

        Assert.Equal(AttachmentFailure.DeviceUnavailable, tray.Items[0].Failure);
        Snapshot(h, "attachments-failed", 390, dark: true);

        await tray.RemoveCommand.ExecuteAsync(tray.Items[0]);
        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void Composer_is_compact_on_a_phone_and_roomy_on_a_wide_window()
    {
        var phone = Render(MobileFixtures.SessionWithApproval(), 360, dark: true);
        phone.Vm.Navigation.OpenSession(new ProjectRef("local", "nextcowork"), "s-ncw-1");
        Dispatch();
        Assert.True(Descendants<Ncw.UI.Views.Desktop.Conversation.ComposerView>(phone.Window).Single().IsCompact);

        var desktop = Render(MobileFixtures.SessionWithApproval(), 1500, dark: true, height: 900);
        Assert.All(Descendants<Ncw.UI.Views.Desktop.Conversation.ComposerView>(desktop.Window), c => Assert.False(c.IsCompact));
    }
}
