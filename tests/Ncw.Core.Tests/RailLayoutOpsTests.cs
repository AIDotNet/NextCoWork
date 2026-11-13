using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Core.Tests;

public class RailLayoutOpsTests
{
    private static readonly ProjectRef A = new("local", "a");
    private static readonly ProjectRef B = new("local", "b");
    private static readonly ProjectRef C = new("cloud", "c");
    private static readonly ProjectRef D = new("local", "d");

    private static ProjectGroup Group(string id = "g1") => new(id, "工作", "#3B82F6", false);

    private static RailLayout Flat(params ProjectRef[] projects) =>
        new(projects.Select(p => (RailNode)new RailNode.Project(p)).ToImmutableArray());

    [Fact]
    public void Reconcile_appends_new_projects_and_keeps_missing_ones()
    {
        var layout = RailLayoutOps.Reconcile(Flat(A, B), [B, C]);

        Assert.Equal([A, B, C], layout.Projects());
    }

    [Fact]
    public void Reconcile_returns_same_instance_when_nothing_is_new()
    {
        var layout = Flat(A, B);

        Assert.Same(layout, RailLayoutOps.Reconcile(layout, [A]));
    }

    [Fact]
    public void CreateGroup_puts_group_at_target_position_with_target_first()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B, C), onto: B, dragged: C, Group());

        var group = Assert.IsType<RailNode.Group>(layout.Items[1]);
        Assert.Equal([B, C], group.Members);
        Assert.Equal(2, layout.Items.Length);
        Assert.Equal([A, B, C], layout.Projects());
    }

    [Fact]
    public void CreateGroup_is_a_no_op_when_target_is_already_grouped_or_same_project()
    {
        var grouped = RailLayoutOps.CreateGroup(Flat(A, B, C), A, B, Group());

        Assert.Same(grouped, RailLayoutOps.CreateGroup(grouped, A, C, Group("g2")));
        Assert.Same(grouped, RailLayoutOps.CreateGroup(grouped, C, C, Group("g2")));
    }

    [Fact]
    public void Moving_last_member_out_removes_the_empty_group()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B, C), A, B, Group());
        layout = RailLayoutOps.Move(layout, A, new RailTarget.TopLevel(0));
        layout = RailLayoutOps.Move(layout, B, new RailTarget.TopLevel(0));

        Assert.DoesNotContain(layout.Items, n => n is RailNode.Group);
        Assert.Equal([B, A, C], layout.Projects());
    }

    [Fact]
    public void Move_into_group_inserts_at_index_and_never_duplicates()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B, C, D), A, B, Group());
        layout = RailLayoutOps.Move(layout, D, new RailTarget.InGroup("g1", 1));

        Assert.Equal([A, D, B], layout.FindGroup("g1")!.Members);
        Assert.Equal(4, layout.Projects().Count());
        Assert.Equal(4, layout.Projects().Distinct().Count());
    }

    [Fact]
    public void Move_to_unknown_group_or_unknown_project_is_a_no_op()
    {
        var layout = Flat(A, B);

        Assert.Same(layout, RailLayoutOps.Move(layout, A, new RailTarget.InGroup("missing", 0)));
        Assert.Same(layout, RailLayoutOps.Move(layout, C, new RailTarget.TopLevel(0)));
    }

    [Fact]
    public void Dissolve_expands_members_in_place()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B, C, D), B, C, Group());
        layout = RailLayoutOps.Dissolve(layout, "g1");

        Assert.Equal([A, B, C, D], layout.Projects());
        Assert.All(layout.Items, n => Assert.IsType<RailNode.Project>(n));
    }

    [Fact]
    public void Group_attributes_change_without_touching_members()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B), A, B, Group());
        layout = RailLayoutOps.Rename(layout, "g1", "个人");
        layout = RailLayoutOps.Recolor(layout, "g1", "#10B981");
        layout = RailLayoutOps.SetCollapsed(layout, "g1", true);

        var group = layout.FindGroup("g1")!;
        Assert.Equal(new ProjectGroup("g1", "个人", "#10B981", true), group.Info);
        Assert.Equal([A, B], group.Members);
    }

    [Fact]
    public void MoveGroup_reorders_top_level()
    {
        var layout = RailLayoutOps.CreateGroup(Flat(A, B, C), B, C, Group());
        layout = RailLayoutOps.MoveGroup(layout, "g1", 0);

        Assert.IsType<RailNode.Group>(layout.Items[0]);
        Assert.Equal([B, C, A], layout.Projects());
    }
}
