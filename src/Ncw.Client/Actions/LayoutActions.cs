using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目栏的用户意图。每个意图只有这一个入口(AGENTS.md §3 规则 4):右键菜单、拖放、快捷键都调这里。
/// </summary>
public sealed class LayoutActions(ClientStores stores)
{
    private static readonly string[] GroupPalette = ["#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EF4444", "#14B8A6"];

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

    public void CreateGroup(ProjectRef onto, ProjectRef dragged, string name)
    {
        var rail = stores.Layout.State.Rail;
        var index = rail.Items.OfType<RailNode.Group>().Count();
        var group = new ProjectGroup($"g{Guid.NewGuid():N}", name, GroupPalette[index % GroupPalette.Length], Collapsed: false);
        UpdateRail(r => RailLayoutOps.CreateGroup(r, onto, dragged, group));
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
