using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 改动审查的线上形状:每个类型往返、「没有」是 null 而不是错误、方法表声明齐全(种类 / 角色 / 能力 / 承载,command 带幂等键由路由检查)。
/// 冻结的向量在 <see cref="GoldenV1Review"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class ReviewDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static ReviewChangeSetDto Set() => new(
        "r1", "s1", "ws", ReviewStates.Applied, 2, 5, 1,
        [
            new ReviewFileEntryDto("a.ts", ReviewChangeKinds.Created, 3, 0, false, true),
            new ReviewFileEntryDto("/etc/hosts", ReviewChangeKinds.Modified, 2, 1, true, false),
        ],
        1_700_000_000_000);

    [Fact]
    public void Every_review_payload_round_trips()
    {
        Assert.Equal(Hex(new ReviewChangeSetResult(Set())), Hex(RoundTrip(new ReviewChangeSetResult(Set()))));
        Assert.Equal(Hex(new ReviewListResult([Set()])), Hex(RoundTrip(new ReviewListResult([Set()]))));
        var diff = new ReviewFileDiffResult(new ReviewFileDiffDto("a.ts", ReviewChangeKinds.Modified, "before", "after", false));
        Assert.Equal(Hex(diff), Hex(RoundTrip(diff)));
        var mutation = new ReviewMutationDto("r1", ReviewStates.Reverted,
            [new ReviewFileResultDto("a.ts", ReviewFileStatuses.Ok), new ReviewFileResultDto("big.ts", ReviewFileStatuses.Skipped, "oversize")]);
        Assert.Equal(Hex(mutation), Hex(RoundTrip(mutation)));
        Assert.Equal(Hex(new ReviewUndoParams("r1", true)), Hex(RoundTrip(new ReviewUndoParams("r1", true))));
        Assert.Equal(["x.ts"], RoundTrip(new ReviewPrecheckResult(["x.ts"])).Conflicts);
    }

    [Fact]
    public void No_change_set_and_no_diff_are_null_on_the_wire()
    {
        Assert.Null(RoundTrip(new ReviewChangeSetResult(null)).ChangeSet);
        Assert.Null(RoundTrip(new ReviewFileDiffResult(null)).Diff);
    }

    [Fact]
    public void The_method_table_declares_kind_role_capability_and_transport()
    {
        foreach (var method in ReviewMethods.All)
        {
            Assert.Equal(Capabilities.Review, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
        }

        Assert.All<MethodDef>([ReviewMethods.ChangeSet, ReviewMethods.List, ReviewMethods.FileDiff, ReviewMethods.PrecheckUndo], m =>
        {
            Assert.Equal(MethodKind.Query, m.Kind);
            Assert.Equal(Role.Viewer, m.MinRole);
        });
        Assert.All<MethodDef>([ReviewMethods.Undo, ReviewMethods.Redo], m =>
        {
            Assert.Equal(MethodKind.Command, m.Kind);
            Assert.Equal(Role.Member, m.MinRole);
        });
        Assert.Equal(6, ReviewMethods.All.Count);
        Assert.Equal(6, ReviewMethods.All.Select(m => m.Name).Distinct().Count());
    }

    [Fact]
    public void Golden_vectors_v1_review()
    {
        Assert.Equal(GoldenV1Review.ChangeSetResult, Hex(new ReviewChangeSetResult(Set())));
        Assert.Equal(GoldenV1Review.NoChangeSetResult, Hex(new ReviewChangeSetResult(null)));
        Assert.Equal(GoldenV1Review.FileDiffResult, Hex(new ReviewFileDiffResult(new ReviewFileDiffDto("a.ts", ReviewChangeKinds.Modified, "before", "after", false))));
        Assert.Equal(GoldenV1Review.PrecheckResult, Hex(new ReviewPrecheckResult(["a.ts", "b.ts"])));
        Assert.Equal(GoldenV1Review.UndoParams, Hex(new ReviewUndoParams("r1", true)));
        Assert.Equal(GoldenV1Review.MutationResult, Hex(new ReviewMutationDto("r1", ReviewStates.Reverted,
            [new ReviewFileResultDto("a.ts", ReviewFileStatuses.Ok), new ReviewFileResultDto("big.ts", ReviewFileStatuses.Skipped, "oversize")])));
    }
}
