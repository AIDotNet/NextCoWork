using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>终端镜像的归约规则:状态单调(终态压过运行中、更老的段忽略)、key 带 deviceId、设备断开标为未知(不判结束)、列表是全量。</summary>
public sealed class TerminalMirrorTests
{
    private static readonly TerminalEnvDto Env = new("dev1", "local", "Local", "local");

    private static TerminalDto Running(string id = "t1", string project = "p1", long createdAt = 1000, int columns = 80) =>
        new(id, project, Env, "ws", "/p", "/bin/sh", columns, 24, "running", createdAt);

    private static TerminalDto Exited(string id = "t1", string project = "p1", long createdAt = 1000) =>
        Running(id, project, createdAt) with { Status = "exited", ExitCode = 0, ExitReason = "exited", ExitedAt = createdAt + 5 };

    [Fact]
    public void A_late_running_snapshot_cannot_bring_an_exited_terminal_back()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running(), 3);
        m = TerminalReducer.ApplyExit(m, "d1", "t1", new TerminalExitDto(130, 2, "killed"), 9);
        Assert.Equal(("exited", 130, "killed", 9L), (m.Of("d1", "t1")!.Info.Status, m.Of("d1", "t1")!.Info.ExitCode, m.Of("d1", "t1")!.Info.ExitReason, m.Of("d1", "t1")!.Seq));

        var replay = TerminalReducer.ApplyInfo(m, "d1", Running(columns: 120), 4);
        Assert.Equal("exited", replay.Of("d1", "t1")!.Info.Status);
        Assert.Equal(80, replay.Of("d1", "t1")!.Info.Columns);
        Assert.Equal(9L, replay.Of("d1", "t1")!.Seq);
    }

    [Fact]
    public void The_first_terminal_state_wins()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running(), 1);
        m = TerminalReducer.ApplyExit(m, "d1", "t1", new TerminalExitDto(0, null, "exited"), 5);
        var second = TerminalReducer.ApplyExit(m, "d1", "t1", new TerminalExitDto(null, 9, "killed"), 6);
        Assert.Same(m, second);
        Assert.Equal("exited", second.Of("d1", "t1")!.Info.ExitReason);
    }

    [Fact]
    public void A_new_segment_on_the_same_id_replaces_an_old_one_and_an_older_segment_is_ignored()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Exited(createdAt: 1000), 8);
        var recreated = TerminalReducer.ApplyInfo(m, "d1", Running(createdAt: 2000), 9);
        Assert.Equal(("running", 2000L), (recreated.Of("d1", "t1")!.Info.Status, recreated.Of("d1", "t1")!.Info.CreatedAt));

        var stale = TerminalReducer.ApplyInfo(recreated, "d1", Exited(createdAt: 1000), 8);
        Assert.Same(recreated, stale);
    }

    [Fact]
    public void An_exit_for_a_terminal_that_is_not_mirrored_yet_does_nothing()
    {
        var m = TerminalReducer.ApplyExit(TerminalMirror.Empty, "d1", "t1", new TerminalExitDto(0, null, "exited"), 3);
        Assert.Same(TerminalMirror.Empty, m);
    }

    [Fact]
    public void The_same_terminal_id_on_two_devices_is_two_terminals()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running(), 1);
        m = TerminalReducer.ApplyInfo(m, "d2", Exited(), 2);
        Assert.Equal("running", m.Of("d1", "t1")!.Info.Status);
        Assert.Equal("exited", m.Of("d2", "t1")!.Info.Status);
    }

    [Fact]
    public void A_disconnect_marks_terminals_unknown_without_ending_them_and_new_metadata_restores_them()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running(), 1);
        m = TerminalReducer.ApplyInfo(m, "d2", Running(), 1);
        var down = TerminalReducer.MarkDeviceUnknown(m, "d1");
        Assert.False(down.Of("d1", "t1")!.Known);
        Assert.Equal("running", down.Of("d1", "t1")!.Info.Status); // 不判成结束
        Assert.True(down.Of("d2", "t1")!.Known);

        var back = TerminalReducer.ApplyInfo(down, "d1", Running(), 5);
        Assert.True(back.Of("d1", "t1")!.Known);

        // 已退出的也一样:断开标未知,重连后一份「已退出」把它补回来
        var exited = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Exited(), 7);
        var unknownExited = TerminalReducer.MarkDeviceUnknown(exited, "d1");
        Assert.True(TerminalReducer.ApplyInfo(unknownExited, "d1", Exited(), 7).Of("d1", "t1")!.Known);
    }

    [Fact]
    public void A_project_list_is_the_full_set_for_that_project_on_that_device()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running("a"), 1);
        m = TerminalReducer.ApplyInfo(m, "d1", Running("b"), 1);
        m = TerminalReducer.ApplyInfo(m, "d1", Running("c", project: "p2"), 1);

        var next = TerminalReducer.ApplyList(m, "d1", "p1", [Running("b", columns: 100)]);
        Assert.Null(next.Of("d1", "a"));
        Assert.Equal(100, next.Of("d1", "b")!.Info.Columns);
        Assert.NotNull(next.Of("d1", "c")); // 别的项目不受影响
        Assert.Equal(["b"], next.InProject("d1", "p1").Select(v => v.Info.Id));
    }

    [Fact]
    public void Removing_a_terminal_forgets_it()
    {
        var m = TerminalReducer.ApplyInfo(TerminalMirror.Empty, "d1", Running(), 1);
        Assert.Null(TerminalReducer.ApplyRemoved(m, "d1", "t1").Of("d1", "t1"));
        Assert.Same(m, TerminalReducer.ApplyRemoved(m, "d1", "other"));
    }
}
