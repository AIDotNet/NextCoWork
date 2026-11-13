using Ncw.Core.Domain;

namespace Ncw.Core.Layout;

/// <summary>拖放时指针下面是什么。<paramref name="Fraction"/> 是指针在该元素里的纵向位置(0 = 顶,1 = 底)。</summary>
public abstract record RailHit
{
    private RailHit() { }

    /// <summary>某个项目图标(顶层或组内)。</summary>
    public sealed record Project(ProjectRef Ref, double Fraction) : RailHit;

    /// <summary>项目组(收起的 2×2 方块,或展开胶囊的头部 / 空白处)。</summary>
    public sealed record Group(string GroupId, double Fraction) : RailHit;

    /// <summary>项目栏里所有项目下面的空白处。</summary>
    public sealed record Empty : RailHit;
}

/// <summary>一次拖放的结果。</summary>
public abstract record RailDrop
{
    private RailDrop() { }

    public sealed record None : RailDrop;

    public sealed record MoveProject(ProjectRef Project, RailTarget Target) : RailDrop;

    public sealed record GroupWith(ProjectRef Onto, ProjectRef Dragged) : RailDrop;

    public sealed record MoveGroup(string GroupId, int Index) : RailDrop;
}

/// <summary>
/// 项目栏拖放的规则(参考 Octopus):
/// <list type="bullet">
/// <item>拖项目到另一个<b>顶层</b>项目上:上下 30% 边缘 = 放到它前面 / 后面;中间 = 两者新建一个组。</item>
/// <item>拖项目到<b>组内</b>项目上:上半 = 放到它前面,下半 = 放到它后面(进入那个组)。</item>
/// <item>拖项目到组上:进入该组末尾。拖到所有项目下面的空白处:移出组,放到最后。</item>
/// <item>拖组到另一个顶层项目或组上:上半 = 前面,下半 = 后面;拖到空白处:放到最后。组不能进组。</item>
/// </list>
/// 纯函数,界面只负责算出 <see cref="RailHit"/>。
/// </summary>
public static class RailDropPlanner
{
    public const double EdgeShare = 0.3;

    public static RailDrop ForProject(RailLayout layout, ProjectRef dragged, RailHit hit)
    {
        if (!layout.Contains(dragged))
        {
            return new RailDrop.None();
        }

        switch (hit)
        {
            case RailHit.Project p when p.Ref == dragged:
                return new RailDrop.None();

            case RailHit.Project p when layout.GroupOf(p.Ref) is { } group:
            {
                var members = group.Members.Remove(dragged);
                var at = members.IndexOf(p.Ref) + (p.Fraction >= 0.5 ? 1 : 0);
                return new RailDrop.MoveProject(dragged, new RailTarget.InGroup(group.Info.Id, at));
            }

            case RailHit.Project p:
            {
                if (p.Fraction is > EdgeShare and < 1 - EdgeShare)
                {
                    return new RailDrop.GroupWith(p.Ref, dragged);
                }

                var index = TopLevelIndexAfterDetach(layout, dragged, n => n is RailNode.Project q && q.Ref == p.Ref);
                return new RailDrop.MoveProject(dragged, new RailTarget.TopLevel(index + (p.Fraction >= 0.5 ? 1 : 0)));
            }

            case RailHit.Group g when layout.FindGroup(g.GroupId) is { } group:
            {
                var end = group.Members.Remove(dragged).Length;
                return new RailDrop.MoveProject(dragged, new RailTarget.InGroup(g.GroupId, end));
            }

            case RailHit.Empty:
                return new RailDrop.MoveProject(dragged, new RailTarget.TopLevel(int.MaxValue));

            default:
                return new RailDrop.None();
        }
    }

    public static RailDrop ForGroup(RailLayout layout, string dragged, RailHit hit)
    {
        if (layout.FindGroup(dragged) is null)
        {
            return new RailDrop.None();
        }

        int? target = hit switch
        {
            RailHit.Group g when g.GroupId == dragged => null,
            RailHit.Group g => IndexWithoutGroup(layout, dragged, n => n is RailNode.Group x && x.Info.Id == g.GroupId) + Half(g.Fraction),
            // 组内项目:按它所在的组算
            RailHit.Project p when layout.GroupOf(p.Ref) is { } owner && owner.Info.Id == dragged => null,
            RailHit.Project p when layout.GroupOf(p.Ref) is { } owner =>
                IndexWithoutGroup(layout, dragged, n => n is RailNode.Group x && x.Info.Id == owner.Info.Id) + Half(p.Fraction),
            RailHit.Project p => IndexWithoutGroup(layout, dragged, n => n is RailNode.Project x && x.Ref == p.Ref) + Half(p.Fraction),
            RailHit.Empty => int.MaxValue,
            _ => null,
        };

        return target is { } index ? new RailDrop.MoveGroup(dragged, index) : new RailDrop.None();
    }

    /// <summary>与 <see cref="RailLayoutOps.Move"/> 相同的口径:先把被拖的项目拿出来(组保留),再找目标的下标。</summary>
    private static int TopLevelIndexAfterDetach(RailLayout layout, ProjectRef dragged, Func<RailNode, bool> isTarget)
    {
        var index = 0;
        foreach (var node in layout.Items)
        {
            if (node is RailNode.Project p && p.Ref == dragged)
            {
                continue;
            }

            if (isTarget(node))
            {
                return index;
            }

            index++;
        }

        return index;
    }

    private static int IndexWithoutGroup(RailLayout layout, string dragged, Func<RailNode, bool> isTarget)
    {
        var index = 0;
        foreach (var node in layout.Items)
        {
            if (node is RailNode.Group g && g.Info.Id == dragged)
            {
                continue;
            }

            if (isTarget(node))
            {
                return index;
            }

            index++;
        }

        return index;
    }

    private static int Half(double fraction) => fraction >= 0.5 ? 1 : 0;
}
