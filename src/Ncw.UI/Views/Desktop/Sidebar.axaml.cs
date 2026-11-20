using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ncw.Client.Shortcuts;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Layout;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop.Tree;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 左侧边栏(docs/design/03 §1「左侧边栏」)。项目行和项目组标题都可以拖动;「拖到哪里会怎样」由 <see cref="RailDropPlanner"/> 决定(和原来的项目栏同一套规则),
/// 这里只把指针位置换算成 <see cref="RailHit"/>、画落点提示,把结果交给 <see cref="SidebarViewModel.Drop"/>。
/// </summary>
public partial class Sidebar : UserControl
{
    // 用应用自定义格式而不是进程内格式:Avalonia 12.1.5 在 macOS 上不把进程内格式放进粘贴板,
    // 只有一个没有类型的拖动项时 AppKit 会直接终止应用(Octopus 踩过的坑)。内容只是一个 id,暴露出去无妨。
    private static readonly DataFormat<string> ProjectFormat = DataFormat.CreateStringApplicationFormat("nextcowork.rail-project");
    private static readonly DataFormat<string> GroupFormat = DataFormat.CreateStringApplicationFormat("nextcowork.rail-group");

    /// <summary>按下后移动超过这个距离才算拖动。</summary>
    private const double DragThreshold = 6;

    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private (DataFormat<string> Format, string Id)? _pressed;
    private bool _dragging;

    public Sidebar()
    {
        InitializeComponent();
        // 项目行和组标题本身是按钮,会自己处理按下:在隧道阶段监听。
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(TreeScroll, true);
        TreeScroll.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        TreeScroll.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideMark());
        TreeScroll.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private SidebarViewModel? Model => DataContext as SidebarViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShortcutText.CurrentChanged += UpdateKeyHints;
        UpdateKeyHints();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ShortcutText.CurrentChanged -= UpdateKeyHints;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>入口右侧的快捷键提示跟着用户在设置里的修改走。</summary>
    private void UpdateKeyHints()
    {
        NewSessionKey.Text = ShortcutText.Format(ShortcutText.CurrentGesture(AppCommand.NewSession));
        SearchKey.Text = ShortcutText.Format(ShortcutText.CurrentGesture(AppCommand.CommandPalette));
    }

    // ───────────────────────── 账户卡片 ─────────────────────────

    /// <summary>头像 / 名字:打开 / 关上账户卡片(开关是纯 UI 状态)。关上时撤掉没确认的「退出登录」。</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (AccountPopup.IsOpen)
        {
            AccountPopup.IsOpen = false;
            return;
        }

        if (AccountArea.DataContext is AccountViewModel { ConfirmingSignOut: true } account)
        {
            account.CancelSignOutCommand.Execute(null);
        }

        AccountPopup.IsOpen = true;
    }

    /// <summary>卡片里的「打开账户设置」:卡片在覆盖层里,够不到外壳的 DataContext,所以从这边转给 <see cref="ShellViewModel.OpenAccountSettingsCommand"/>。</summary>
    private void OnOpenAccountSettings(object? sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        if (this.FindAncestorOfType<DesktopShell>()?.DataContext is ShellViewModel shell)
        {
            shell.OpenAccountSettingsCommand.Execute(null);
        }
    }

    /// <summary>底部主题切换按钮:在 Arc 的页面过渡里把偏好写进 <see cref="AppearanceViewModel.Theme"/> 并应用到 Arc。</summary>
    private void OnThemeChangeRequested(object? sender, ThemeSwitcherChangeRequestedEventArgs e)
    {
        if (this.FindAncestorOfType<DesktopShell>()?.DataContext is not ShellViewModel shell)
        {
            return;
        }

        e.Handled = true;
        var next = e.Next == ThemeSwitcherAppearance.Light ? UiThemePreference.Light : UiThemePreference.Dark;
        _ = ThemeSwitcher.RunTransitionAsync(ThemeButton, e.Variant, () =>
        {
            shell.Settings.Appearance.Theme = next;
            NcwAppearance.Apply(next);
            return Task.CompletedTask;
        });
    }

    // ───────────────────────── 拖放 ─────────────────────────

    /// <summary>算出一次拖放的结果(供测试和 DragOver / Drop 共用)。</summary>
    public RailDrop Plan(string kind, string id, Visual? over, Point position)
    {
        if (Model is not { } model)
        {
            return new RailDrop.None();
        }

        var layout = model.Layout;
        var hit = HitOf(over, position);
        return kind switch
        {
            "project" when model.FindProject(id) is { } project => RailDropPlanner.ForProject(layout, project, hit),
            "group" => RailDropPlanner.ForGroup(layout, id, hit),
            _ => new RailDrop.None(),
        };
    }

    /// <summary>
    /// 指针在哪:项目标题行 → 按行高的比例;展开出来的会话区 → 算在这个项目的下沿(放到它后面);
    /// 项目组标题 → 组;组里的空白 → 组的下沿;都不是 → 空白(移到最后)。
    /// </summary>
    private RailHit HitOf(Visual? over, Point position)
    {
        for (var visual = over; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case SidebarProjectNode { DataContext: SidebarProjectViewModel project } node:
                    return new RailHit.Project(project.Project, IsWithin(over, node.HeaderRow) ? Fraction(node.HeaderRow, position) : 1);
                case SidebarGroupNode { DataContext: SidebarGroupViewModel group } node:
                    return new RailHit.Group(group.Group.GroupId, IsWithin(over, node.HeaderRow) ? Fraction(node.HeaderRow, position) : 1);
            }
        }

        return new RailHit.Empty();
    }

    private static bool IsWithin(Visual? visual, Visual container)
    {
        for (var v = visual; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, container))
            {
                return true;
            }
        }

        return false;
    }

    private double Fraction(Visual row, Point position)
    {
        var top = row.TranslatePoint(default, this) ?? default;
        return row.Bounds.Height <= 0 ? 0.5 : Math.Clamp((position.Y - top.Y) / row.Bounds.Height, 0, 1);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<SidebarProjectNode>(includeSelf: true) is { DataContext: SidebarProjectViewModel project } projectNode
            && IsWithin(source, projectNode.HeaderRow))
        {
            _pressed = (ProjectFormat, project.Project.ToString());
        }
        else if (source.FindAncestorOfType<SidebarGroupNode>(includeSelf: true) is { DataContext: SidebarGroupViewModel group } groupNode
                 && IsWithin(source, groupNode.HeaderRow))
        {
            _pressed = (GroupFormat, group.Group.GroupId);
        }
        else
        {
            return;
        }

        _press = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed is not { } pressed || _dragging)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _press = null;
            return;
        }

        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _press = null;
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(pressed.Format, pressed.Id));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            HideMark();
        }
    }

    private RailDrop PlanOf(DragEventArgs e)
    {
        var position = e.GetPosition(this);
        if (e.DataTransfer.TryGetValue(ProjectFormat) is { } project)
        {
            return Plan("project", project, e.Source as Visual, position);
        }

        return e.DataTransfer.TryGetValue(GroupFormat) is { } group
            ? Plan("group", group, e.Source as Visual, position)
            : new RailDrop.None();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var plan = PlanOf(e);
        e.DragEffects = plan is RailDrop.None ? DragDropEffects.None : DragDropEffects.Move;
        ShowMark(e.Source as Visual, plan, e.GetPosition(this));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideMark();
        var plan = PlanOf(e);
        e.DragEffects = plan is RailDrop.None ? DragDropEffects.None : DragDropEffects.Move;
        Model?.Drop(plan);
    }

    /// <summary>落点提示:放到前后时画一条横线,成组 / 进组时给目标行描一圈。</summary>
    private void ShowMark(Visual? over, RailDrop plan, Point position)
    {
        var projectNode = over?.FindAncestorOfType<SidebarProjectNode>(includeSelf: true);
        var groupNode = projectNode is null ? over?.FindAncestorOfType<SidebarGroupNode>(includeSelf: true) : null;
        Control? target = projectNode?.HeaderRow ?? groupNode?.HeaderRow;
        var groupRow = groupNode?.HeaderRow;
        if (target is null || plan is RailDrop.None)
        {
            HideMark();
            return;
        }

        var topLeft = target.TranslatePoint(default, MarkLayer) ?? default;
        var ring = plan is RailDrop.GroupWith || (plan is RailDrop.MoveProject { Target: RailTarget.InGroup } && groupRow is not null);
        if (ring)
        {
            DropLine.IsVisible = false;
            DropRing.IsVisible = true;
            DropRing.Width = target.Bounds.Width;
            DropRing.Height = target.Bounds.Height;
            Canvas.SetLeft(DropRing, topLeft.X);
            Canvas.SetTop(DropRing, topLeft.Y);
            return;
        }

        // 与 RailDropPlanner 的半区规则一致:指针在目标下半就画在它下面;在展开出来的会话区 = 放到这个项目(连同它的会话)后面。
        var inRow = IsWithin(over, target);
        var after = !inRow || position.Y - (target.TranslatePoint(default, this) ?? default).Y >= target.Bounds.Height / 2;
        Control? whole = projectNode is not null ? projectNode : groupNode;
        if (!inRow && whole is not null)
        {
            target = whole;
            topLeft = whole.TranslatePoint(default, MarkLayer) ?? default;
        }

        DropRing.IsVisible = false;
        DropLine.IsVisible = true;
        DropLine.Width = target.Bounds.Width;
        Canvas.SetLeft(DropLine, topLeft.X);
        Canvas.SetTop(DropLine, topLeft.Y + (after ? target.Bounds.Height : 0) - 1);
    }

    private void HideMark()
    {
        DropLine.IsVisible = false;
        DropRing.IsVisible = false;
    }
}
