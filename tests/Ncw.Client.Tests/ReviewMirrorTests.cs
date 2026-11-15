using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>改动审查镜像的折叠规则:key 带 deviceId、撤销态以更新时间较新的为准(旧响应不改回去)、一份「没有」盖不掉已有的摘要、设备断开是未知。</summary>
public class ReviewMirrorTests
{
    private static ReviewChangeSetDto Set(string runId, string state, long at, int files = 1) =>
        new(runId, "s1", "ws", state, files, 3, 1, [new ReviewFileEntryDto("a.ts", "modified", 3, 1, false, true)], at);

    [Fact]
    public void Summaries_are_keyed_by_device_and_run()
    {
        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r1", Set("r1", "applied", 10));
        mirror = ReviewReducer.Apply(mirror, "d2", "r1", Set("r1", "reverted", 20));

        Assert.Equal("applied", mirror.ChangeSetOf("d1", "r1")!.State);
        Assert.Equal("reverted", mirror.ChangeSetOf("d2", "r1")!.State);
        Assert.Null(mirror.Of("d1", "r2"));
    }

    [Fact]
    public void An_older_response_cannot_turn_a_reverted_run_back_into_applied()
    {
        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r1", Set("r1", "reverted", 20));

        var replay = ReviewReducer.Apply(mirror, "d1", "r1", Set("r1", "applied", 10));

        Assert.Same(mirror, replay);
        Assert.Equal("reverted", replay.ChangeSetOf("d1", "r1")!.State);
    }

    [Fact]
    public void A_newer_or_equal_summary_replaces_the_old_one()
    {
        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r1", Set("r1", "applied", 10));

        mirror = ReviewReducer.Apply(mirror, "d1", "r1", Set("r1", "reverted", 20));
        Assert.Equal("reverted", mirror.ChangeSetOf("d1", "r1")!.State);

        mirror = ReviewReducer.Apply(mirror, "d1", "r1", Set("r1", "applied", 20));
        Assert.Equal("applied", mirror.ChangeSetOf("d1", "r1")!.State);
    }

    [Fact]
    public void A_missing_change_set_is_remembered_as_known_empty_but_never_erases_a_real_one()
    {
        var empty = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r0", null);
        Assert.True(empty.Of("d1", "r0")!.Known);
        Assert.Null(empty.ChangeSetOf("d1", "r0"));

        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r1", Set("r1", "applied", 10));
        Assert.NotNull(ReviewReducer.Apply(mirror, "d1", "r1", null).ChangeSetOf("d1", "r1"));
    }

    [Fact]
    public void A_disconnect_marks_the_devices_summaries_unknown_but_keeps_other_devices_and_reconfirmation_makes_them_known_again()
    {
        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r1", Set("r1", "applied", 10));
        mirror = ReviewReducer.Apply(mirror, "d2", "r1", Set("r1", "applied", 10));

        var down = ReviewReducer.MarkDeviceUnknown(mirror, "d1");

        Assert.False(down.Of("d1", "r1")!.Known);
        Assert.NotNull(down.ChangeSetOf("d1", "r1")); // 内容保留,界面不当现状
        Assert.True(down.Of("d2", "r1")!.Known);
        Assert.Same(down, ReviewReducer.MarkDeviceUnknown(down, "d1"));

        var back = ReviewReducer.Apply(down, "d1", "r1", Set("r1", "applied", 10));
        Assert.True(back.Of("d1", "r1")!.Known);
    }

    [Fact]
    public void A_session_list_merges_every_run_under_the_same_rules()
    {
        var mirror = ReviewReducer.Apply(ReviewMirror.Empty, "d1", "r2", Set("r2", "reverted", 50));

        mirror = ReviewReducer.ApplyMany(mirror, "d1", [Set("r1", "applied", 10), Set("r2", "applied", 5)]);

        Assert.Equal("applied", mirror.ChangeSetOf("d1", "r1")!.State);
        Assert.Equal("reverted", mirror.ChangeSetOf("d1", "r2")!.State);
    }
}
