using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Core.Tests;

public class RailDropPlannerTests
{
    private static readonly ProjectRef A = new("d", "a");
    private static readonly ProjectRef B = new("d", "b");
    private static readonly ProjectRef C = new("d", "c");
    private static readonly ProjectRef D = new("d", "d");
    private static readonly ProjectRef E = new("d", "e");

    /// <summary>[A, B, G1(C, D), E]</summary>
    private static RailLayout Layout()
    {
        var flat = new RailLayout([.. new[] { A, B, C, D, E }.Select(p => (RailNode)new RailNode.Project(p))]);
        return RailLayoutOps.CreateGroup(flat, C, D, new ProjectGroup("g1", "工作", "#3B82F6", false));
    }

    private static RailLayout Apply(RailLayout layout, RailDrop drop) => drop switch
    {
        RailDrop.MoveProject m => RailLayoutOps.Move(layout, m.Project, m.Target),
        RailDrop.GroupWith g => RailLayoutOps.CreateGroup(layout, g.Onto, g.Dragged, new ProjectGroup("new", "新组", "#10B981", false)),
        RailDrop.MoveGroup g => RailLayoutOps.MoveGroup(layout, g.GroupId, g.Index),
        _ => layout,
    };

    [Fact]
    public void Middle_of_top_level_project_creates_a_group()
    {
        var drop = RailDropPlanner.ForProject(Layout(), A, new RailHit.Project(E, 0.5));

        Assert.Equal(new RailDrop.GroupWith(E, A), drop);
        Assert.Equal([B, C, D, E, A], Apply(Layout(), drop).Projects());
    }

    [Fact]
    public void Lower_edge_of_project_places_after_it()
    {
        var drop = RailDropPlanner.ForProject(Layout(), A, new RailHit.Project(B, 0.9));

        Assert.Equal(["b", "a", "c", "d", "e"], Apply(Layout(), drop).Projects().Select(p => p.WorkspaceId));
    }

    [Fact]
    public void Upper_edge_of_project_places_before_it()
    {
        var drop = RailDropPlanner.ForProject(Layout(), E, new RailHit.Project(B, 0.1));

        Assert.Equal(["a", "e", "b", "c", "d"], Apply(Layout(), drop).Projects().Select(p => p.WorkspaceId));
    }

    [Fact]
    public void Project_inside_group_places_into_that_group()
    {
        var drop = RailDropPlanner.ForProject(Layout(), A, new RailHit.Project(D, 0.2));
        var result = Apply(Layout(), drop);

        Assert.Equal([C, A, D], result.FindGroup("g1")!.Members);
    }

    [Fact]
    public void Reordering_within_the_same_group()
    {
        var drop = RailDropPlanner.ForProject(Layout(), C, new RailHit.Project(D, 0.8));

        Assert.Equal([D, C], Apply(Layout(), drop).FindGroup("g1")!.Members);
    }

    [Fact]
    public void Dropping_on_group_appends_and_on_empty_leaves_the_group()
    {
        var into = Apply(Layout(), RailDropPlanner.ForProject(Layout(), A, new RailHit.Group("g1", 0.5)));
        Assert.Equal([C, D, A], into.FindGroup("g1")!.Members);

        var outOf = Apply(Layout(), RailDropPlanner.ForProject(Layout(), C, new RailHit.Empty()));
        Assert.Equal([A, B, D, E, C], outOf.Projects());
        Assert.Null(outOf.GroupOf(C));
    }

    [Fact]
    public void Dropping_on_itself_does_nothing()
    {
        Assert.IsType<RailDrop.None>(RailDropPlanner.ForProject(Layout(), A, new RailHit.Project(A, 0.5)));
        Assert.IsType<RailDrop.None>(RailDropPlanner.ForGroup(Layout(), "g1", new RailHit.Group("g1", 0.5)));
        Assert.IsType<RailDrop.None>(RailDropPlanner.ForGroup(Layout(), "g1", new RailHit.Project(C, 0.5)));
    }

    [Fact]
    public void Group_moves_before_or_after_items_and_never_into_another_group()
    {
        var first = Apply(Layout(), RailDropPlanner.ForGroup(Layout(), "g1", new RailHit.Project(A, 0.2)));
        Assert.IsType<RailNode.Group>(first.Items[0]);

        var last = Apply(Layout(), RailDropPlanner.ForGroup(Layout(), "g1", new RailHit.Empty()));
        Assert.IsType<RailNode.Group>(last.Items[^1]);
        Assert.Equal(ImmutableArray.Create(C, D), last.FindGroup("g1")!.Members);
    }

    [Fact]
    public void WrapInGroup_makes_a_single_member_group_in_place()
    {
        var layout = RailLayoutOps.WrapInGroup(Layout(), B, new ProjectGroup("solo", "单独", "#F59E0B", false));

        var group = Assert.IsType<RailNode.Group>(layout.Items[1]);
        Assert.Equal([B], group.Members);
        Assert.Equal([A, B, C, D, E], layout.Projects());
    }
}
