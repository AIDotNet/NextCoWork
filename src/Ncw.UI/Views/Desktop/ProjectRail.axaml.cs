using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.Core.Layout;
using Ncw.UI.Views.Desktop.Rail;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 项目栏。项目图标和项目组都可以拖动;「拖到哪里会怎样」由 <see cref="RailDropPlanner"/> 决定,
/// 这里只负责把指针位置换算成 <see cref="RailHit"/>、画落点提示,并把结果交给 LayoutActions.Drop。
/// </summary>
public partial class ProjectRail : UserControl
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

    public ProjectRail()
    {
        InitializeComponent();
        // 图标和组本身是按钮,会自己处理按下:在隧道阶段监听。
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideMark());
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private RailViewModel? Model => DataContext as RailViewModel;

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

    private RailHit HitOf(Visual? over, Point position)
    {
        for (var visual = over; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case RailProjectTile { DataContext: RailProjectViewModel tile } view:
                    return new RailHit.Project(tile.Project, Fraction(view, position));
                case RailGroupView { DataContext: RailGroupViewModel group } view:
                    return new RailHit.Group(group.GroupId, Fraction(view, position));
            }
        }

        return new RailHit.Empty();
    }

    private double Fraction(Visual view, Point position)
    {
        var top = view.TranslatePoint(default, this) ?? default;
        return view.Bounds.Height <= 0 ? 0.5 : Math.Clamp((position.Y - top.Y) / view.Bounds.Height, 0, 1);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<RailProjectTile>(includeSelf: true)?.DataContext is RailProjectViewModel tile)
        {
            _pressed = (ProjectFormat, tile.Project.ToString());
        }
        else if (source.FindAncestorOfType<RailGroupView>(includeSelf: true)?.DataContext is RailGroupViewModel group)
        {
            _pressed = (GroupFormat, group.GroupId);
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

    /// <summary>落点提示:放到前后时画一条线,成组 / 进组时给目标描一圈。</summary>
    private void ShowMark(Visual? over, RailDrop plan, Point position)
    {
        var target = over?.FindAncestorOfType<RailProjectTile>(includeSelf: true) as Control
                     ?? over?.FindAncestorOfType<RailGroupView>(includeSelf: true);
        if (target is null || plan is RailDrop.None)
        {
            HideMark();
            return;
        }

        var topLeft = target.TranslatePoint(default, MarkLayer) ?? default;
        var ring = plan is RailDrop.GroupWith || (plan is RailDrop.MoveProject { Target: RailTarget.InGroup } && target is RailGroupView);
        if (ring)
        {
            DropLine.IsVisible = false;
            DropRing.IsVisible = true;
            DropRing.Width = 46;
            DropRing.Height = Math.Min(target.Bounds.Height, 46);
            Canvas.SetLeft(DropRing, (Bounds.Width - DropRing.Width) / 2);
            Canvas.SetTop(DropRing, topLeft.Y + (target.Bounds.Height - DropRing.Height) / 2);
            return;
        }

        // 与 RailDropPlanner 的半区规则一致:指针在目标下半就画在它下面。
        var top = target.TranslatePoint(default, this) ?? default;
        var after = position.Y - top.Y >= target.Bounds.Height / 2;
        DropRing.IsVisible = false;
        DropLine.IsVisible = true;
        Canvas.SetTop(DropLine, topLeft.Y + (after ? target.Bounds.Height : 0) - 1);
    }

    private void HideMark()
    {
        DropLine.IsVisible = false;
        DropRing.IsVisible = false;
    }
}
