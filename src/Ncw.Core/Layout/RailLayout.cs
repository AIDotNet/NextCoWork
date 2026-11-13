using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Layout;

/// <summary>项目组。纯布局数据,通过账户配置同步到所有控制端(决策 D10)。</summary>
public sealed record ProjectGroup(string Id, string Name, string Color, bool Collapsed);

/// <summary>项目栏上的一项:单个项目,或一个项目组(只有一层,组里不能再套组)。</summary>
public abstract record RailNode
{
    private RailNode() { }

    public sealed record Project(ProjectRef Ref) : RailNode;

    public sealed record Group(ProjectGroup Info, ImmutableArray<ProjectRef> Members) : RailNode;
}

/// <summary>拖放的目标位置。</summary>
public abstract record RailTarget
{
    private RailTarget() { }

    /// <summary>放到顶层第 <paramref name="Index"/> 个位置(按移除源项目之后的顺序计算)。</summary>
    public sealed record TopLevel(int Index) : RailTarget;

    /// <summary>放进某个组的第 <paramref name="Index"/> 个位置。</summary>
    public sealed record InGroup(string GroupId, int Index) : RailTarget;
}

/// <summary>
/// 项目栏布局。不可变;所有修改都是 <see cref="RailLayoutOps"/> 里的纯函数,返回新的布局。
/// 布局只保存「顺序和分组」,项目本身的事实(名字、路径、状态)由设备持有。
/// </summary>
public sealed record RailLayout(ImmutableArray<RailNode> Items)
{
    public static readonly RailLayout Empty = new(ImmutableArray<RailNode>.Empty);

    /// <summary>按显示顺序展开所有项目(⌘1…⌘9 的编号依据)。</summary>
    public IEnumerable<ProjectRef> Projects()
    {
        foreach (var node in Items)
        {
            switch (node)
            {
                case RailNode.Project p:
                    yield return p.Ref;
                    break;
                case RailNode.Group g:
                    foreach (var member in g.Members)
                    {
                        yield return member;
                    }

                    break;
            }
        }
    }

    public bool Contains(ProjectRef project) => Projects().Contains(project);

    public RailNode.Group? FindGroup(string groupId) =>
        Items.OfType<RailNode.Group>().FirstOrDefault(g => g.Info.Id == groupId);

    /// <summary>项目所在的组;在顶层时返回 null。</summary>
    public RailNode.Group? GroupOf(ProjectRef project) =>
        Items.OfType<RailNode.Group>().FirstOrDefault(g => g.Members.Contains(project));
}
