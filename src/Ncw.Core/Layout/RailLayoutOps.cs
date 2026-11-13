using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Layout;

/// <summary>
/// 项目栏布局的全部修改操作。每个用户意图对应一个函数(AGENTS.md §3 规则 4),
/// 客户端的 Action 只负责把结果写回 Store。
/// 不变量:同一个项目在布局里最多出现一次;不存在空组;组只有一层。
/// </summary>
public static class RailLayoutOps
{
    /// <summary>把设备上新出现的项目追加到末尾。布局里引用但设备上已不存在的项目保留,由界面显示为「已失效」。</summary>
    public static RailLayout Reconcile(RailLayout layout, IEnumerable<ProjectRef> known)
    {
        var builder = layout.Items.ToBuilder();
        foreach (var project in known)
        {
            if (!layout.Contains(project))
            {
                builder.Add(new RailNode.Project(project));
            }
        }

        return builder.Count == layout.Items.Length ? layout : new RailLayout(builder.ToImmutable());
    }

    /// <summary>从布局里移除一个项目(用户确认移除失效项目、或关闭项目时)。</summary>
    public static RailLayout Remove(RailLayout layout, ProjectRef project) =>
        new(Normalize(Detach(layout.Items, project)));

    /// <summary>把项目移动到目标位置。目标组不存在时什么都不做。</summary>
    public static RailLayout Move(RailLayout layout, ProjectRef project, RailTarget target)
    {
        if (!layout.Contains(project))
        {
            return layout;
        }

        if (target is RailTarget.InGroup inGroup && layout.FindGroup(inGroup.GroupId) is null)
        {
            return layout;
        }

        var items = Detach(layout.Items, project);
        items = target switch
        {
            RailTarget.TopLevel top => items.Insert(Clamp(top.Index, items.Length), new RailNode.Project(project)),
            RailTarget.InGroup into => ReplaceGroup(items, into.GroupId, g =>
                g with { Members = g.Members.Insert(Clamp(into.Index, g.Members.Length), project) }),
            _ => items,
        };

        return new RailLayout(Normalize(items));
    }

    /// <summary>
    /// 把 <paramref name="dragged"/> 拖到 <paramref name="onto"/> 上:新建一个组,放在 <paramref name="onto"/> 原来的位置,
    /// 组内顺序为 [onto, dragged]。两者相同,或 onto 已经在组里时不做任何事(那种情况应当用 <see cref="Move"/> 放进那个组)。
    /// </summary>
    public static RailLayout CreateGroup(RailLayout layout, ProjectRef onto, ProjectRef dragged, ProjectGroup group)
    {
        if (onto == dragged || !layout.Contains(onto) || !layout.Contains(dragged) || layout.GroupOf(onto) is not null)
        {
            return layout;
        }

        if (layout.FindGroup(group.Id) is not null)
        {
            return layout;
        }

        var items = Detach(layout.Items, dragged);
        var index = items.IndexOf(new RailNode.Project(onto));
        var node = new RailNode.Group(group, ImmutableArray.Create(onto, dragged));
        return new RailLayout(Normalize(items.SetItem(index, node)));
    }

    /// <summary>解散组:组内项目按原顺序展开到组原来的位置。</summary>
    public static RailLayout Dissolve(RailLayout layout, string groupId)
    {
        var index = IndexOfGroup(layout.Items, groupId);
        if (index < 0)
        {
            return layout;
        }

        var group = (RailNode.Group)layout.Items[index];
        var items = layout.Items.RemoveAt(index)
            .InsertRange(index, group.Members.Select(m => (RailNode)new RailNode.Project(m)));
        return new RailLayout(items);
    }

    public static RailLayout Rename(RailLayout layout, string groupId, string name) =>
        Update(layout, groupId, g => g with { Info = g.Info with { Name = name } });

    public static RailLayout Recolor(RailLayout layout, string groupId, string color) =>
        Update(layout, groupId, g => g with { Info = g.Info with { Color = color } });

    public static RailLayout SetCollapsed(RailLayout layout, string groupId, bool collapsed) =>
        Update(layout, groupId, g => g with { Info = g.Info with { Collapsed = collapsed } });

    /// <summary>移动整个组到顶层的某个位置。</summary>
    public static RailLayout MoveGroup(RailLayout layout, string groupId, int index)
    {
        var from = IndexOfGroup(layout.Items, groupId);
        if (from < 0)
        {
            return layout;
        }

        var node = layout.Items[from];
        var items = layout.Items.RemoveAt(from);
        return new RailLayout(items.Insert(Clamp(index, items.Length), node));
    }

    private static RailLayout Update(RailLayout layout, string groupId, Func<RailNode.Group, RailNode.Group> change) =>
        IndexOfGroup(layout.Items, groupId) < 0 ? layout : new RailLayout(ReplaceGroup(layout.Items, groupId, change));

    /// <summary>把项目从顶层或所在组里拿出来;不存在时原样返回。</summary>
    private static ImmutableArray<RailNode> Detach(ImmutableArray<RailNode> items, ProjectRef project)
    {
        var builder = ImmutableArray.CreateBuilder<RailNode>(items.Length);
        foreach (var node in items)
        {
            switch (node)
            {
                case RailNode.Project p when p.Ref == project:
                    continue;
                case RailNode.Group g when g.Members.Contains(project):
                    builder.Add(g with { Members = g.Members.Remove(project) });
                    continue;
                default:
                    builder.Add(node);
                    break;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<RailNode> ReplaceGroup(
        ImmutableArray<RailNode> items, string groupId, Func<RailNode.Group, RailNode.Group> change)
    {
        var index = IndexOfGroup(items, groupId);
        return index < 0 ? items : items.SetItem(index, change((RailNode.Group)items[index]));
    }

    /// <summary>不存在空组:成员被移光的组直接删除。</summary>
    private static ImmutableArray<RailNode> Normalize(ImmutableArray<RailNode> items) =>
        items.RemoveAll(n => n is RailNode.Group { Members.IsEmpty: true });

    private static int IndexOfGroup(ImmutableArray<RailNode> items, string groupId)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is RailNode.Group g && g.Info.Id == groupId)
            {
                return i;
            }
        }

        return -1;
    }

    private static int Clamp(int index, int length) => Math.Clamp(index, 0, length);
}
