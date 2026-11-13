using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目栏的用户意图。每个意图只有这一个入口(AGENTS.md §3 规则 4):右键菜单、拖放、快捷键都调这里。
/// </summary>
public sealed class LayoutActions(ClientStores stores)
{
    /// <summary>项目组可选的颜色(用户数据,不是主题色)。</summary>
    public static readonly IReadOnlyList<string> GroupPalette = ["#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EF4444", "#14B8A6"];

    public void Select(ProjectRef project) =>
        stores.Selection.Update(s => s.Project == project ? s : new SelectionState(project, null));

    /// <summary>⌘1…⌘9:按项目栏显示顺序选第 n 个项目(从 1 开始)。</summary>
    public void SelectByIndex(int oneBased)
    {
        var project = stores.Layout.State.Rail.Projects().Skip(oneBased - 1).Cast<ProjectRef?>().FirstOrDefault();
        if (project is { } p)
        {
            Select(p);
        }
    }

    public void SelectSession(string sessionId) =>
        stores.Selection.Update(s => s with { SessionId = sessionId });

    public void Move(ProjectRef project, RailTarget target) =>
        UpdateRail(rail => RailLayoutOps.Move(rail, project, target));

    public void CreateGroup(ProjectRef onto, ProjectRef dragged, string name = "新项目组") =>
        UpdateRail(r => RailLayoutOps.CreateGroup(r, onto, dragged, NewGroup(r, name)));

    /// <summary>右键「新建项目组」:把这个项目包进一个新组。</summary>
    public void WrapInGroup(ProjectRef project, string name = "新项目组") =>
        UpdateRail(r => RailLayoutOps.WrapInGroup(r, project, NewGroup(r, name)));

    /// <summary>右键「移出项目组」:放到组的后面。</summary>
    public void LeaveGroup(ProjectRef project)
    {
        var rail = stores.Layout.State.Rail;
        if (rail.GroupOf(project) is not { } group)
        {
            return;
        }

        var after = rail.Items.IndexOf(group) + (group.Members.Length > 1 ? 1 : 0);
        Move(project, new RailTarget.TopLevel(after));
    }

    public void RecolorGroup(string groupId, string color) => UpdateRail(r => RailLayoutOps.Recolor(r, groupId, color));

    /// <summary>拖放的结果统一从这里落地;规则在 <see cref="RailDropPlanner"/>。</summary>
    public void Drop(RailDrop drop)
    {
        switch (drop)
        {
            case RailDrop.MoveProject m:
                Move(m.Project, m.Target);
                break;
            case RailDrop.GroupWith g:
                CreateGroup(g.Onto, g.Dragged);
                break;
            case RailDrop.MoveGroup g:
                UpdateRail(r => RailLayoutOps.MoveGroup(r, g.GroupId, g.Index));
                break;
        }
    }

    private static ProjectGroup NewGroup(RailLayout rail, string name)
    {
        var index = rail.Items.OfType<RailNode.Group>().Count();
        return new ProjectGroup($"g{Guid.NewGuid():N}", name, GroupPalette[index % GroupPalette.Count], Collapsed: false);
    }

    public void ToggleGroup(string groupId)
    {
        var group = stores.Layout.State.Rail.FindGroup(groupId);
        if (group is not null)
        {
            UpdateRail(r => RailLayoutOps.SetCollapsed(r, groupId, !group.Info.Collapsed));
        }
    }

    public void RenameGroup(string groupId, string name) => UpdateRail(r => RailLayoutOps.Rename(r, groupId, name));

    public void DissolveGroup(string groupId) => UpdateRail(r => RailLayoutOps.Dissolve(r, groupId));

    public void RemoveFromRail(ProjectRef project) => UpdateRail(r => RailLayoutOps.Remove(r, project));

    private void UpdateRail(Func<RailLayout, RailLayout> change) =>
        stores.Layout.Update(state =>
        {
            var rail = change(state.Rail);
            return ReferenceEquals(rail, state.Rail) ? state : state with { Rail = rail };
        });
}
