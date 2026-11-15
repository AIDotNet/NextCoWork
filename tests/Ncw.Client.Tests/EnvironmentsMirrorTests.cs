using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>执行环境镜像的折叠规则(规则 5–7):全量状态只收不更旧的、旧快照不能盖掉新的、断开是未知(不判成断开 / 失败)、设备重启之后第一份快照照收、key 带 deviceId、没有任何凭据。</summary>
public class EnvironmentsMirrorTests
{
    private const string Dev = "dev-1";

    private static EnvironmentDto Local() => new("local", "Local", EnvironmentKindNames.Local, EnvironmentStateNames.Ready, 0, true, true, 0);

    private static EnvironmentDto Ssh(string id, string state = EnvironmentStateNames.Idle, string? errorKey = null) => new(
        id, id, EnvironmentKindNames.Ssh, state, 0, false, true, 1,
        Error: errorKey is null ? null : new EnvironmentErrorDto(errorKey),
        Target: new SshTargetDto("manual", "10.0.0.5", 22, "dev", true, null, false), HasSavedPassword: true);

    private static EnvironmentsTopicState Topic(long rev, params EnvironmentDto[] environments) => new(rev, "local", [Local(), .. environments]);

    [Fact]
    public void A_newer_snapshot_replaces_the_state_and_an_older_replay_is_ignored()
    {
        var m = EnvironmentsReducer.ApplyTopic(EnvironmentsMirror.Empty, Dev, Topic(5, Ssh("a")));
        Assert.Equal((true, 5L, "local"), (m.Of(Dev).Known, m.Of(Dev).Rev, m.Of(Dev).DefaultId));
        Assert.Equal(["local", "a"], m.Of(Dev).Environments.Select(e => e.Id));

        Assert.Same(m, EnvironmentsReducer.ApplyTopic(m, Dev, Topic(4, Ssh("zzz", EnvironmentStateNames.Ready)))); // 重放的旧快照:不变
        Assert.Same(m, EnvironmentsReducer.ApplyTopic(m, Dev, Topic(5, Ssh("a"))));                                // 同一个版本 = 同一份内容:不改 Store

        m = EnvironmentsReducer.ApplyTopic(m, Dev, Topic(9, Ssh("a", EnvironmentStateNames.Failed, EnvironmentErrorKeys.HostKeyChanged)));
        Assert.Equal(EnvironmentStateNames.Failed, m.Of(Dev).Find("a")!.State);
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, m.Of(Dev).Find("a")!.Error!.Key);
    }

    [Fact]
    public void A_disconnect_marks_the_state_unknown_not_ended_and_the_first_snapshot_after_a_restart_is_accepted()
    {
        var ready = EnvironmentsReducer.ApplyTopic(EnvironmentsMirror.Empty, Dev, Topic(8, Ssh("a", EnvironmentStateNames.Ready)));
        var unknown = EnvironmentsReducer.MarkUnknown(ready, Dev);
        Assert.False(unknown.Of(Dev).Known);
        Assert.Null(unknown.Find(Dev, "a")); // 未知:不把旧的「已连接」当现状,也不判成别的
        Assert.Same(unknown, EnvironmentsReducer.MarkUnknown(unknown, Dev)); // 幂等
        Assert.Same(EnvironmentsMirror.Empty, EnvironmentsReducer.MarkUnknown(EnvironmentsMirror.Empty, Dev));

        // 设备重启了:版本号从头来过,第一份快照照收(未知之后不做「不更旧」的比较)
        var restarted = EnvironmentsReducer.ApplyTopic(unknown, Dev, Topic(1, Ssh("a")));
        Assert.Equal((true, 1L), (restarted.Of(Dev).Known, restarted.Of(Dev).Rev));
        Assert.Equal(EnvironmentStateNames.Idle, restarted.Find(Dev, "a")!.State);
    }

    [Fact]
    public void Environments_are_keyed_by_device_and_a_tool_card_can_resolve_its_environment_by_ids()
    {
        var m = EnvironmentsReducer.ApplyTopic(EnvironmentsMirror.Empty, "dev-1", Topic(1, Ssh("shared-id")));
        m = EnvironmentsReducer.ApplyTopic(m, "dev-2", Topic(1, Ssh("shared-id", EnvironmentStateNames.Ready), Ssh("only-on-2")));

        Assert.Equal(EnvironmentStateNames.Idle, m.Find("dev-1", "shared-id")!.State);
        Assert.Equal(EnvironmentStateNames.Ready, m.Find("dev-2", "shared-id")!.State);
        Assert.Null(m.Find("dev-1", "only-on-2"));
        Assert.Null(m.Find("dev-3", "shared-id")); // 没见过的设备:未知
        // 设备断开只影响这台设备
        var after = EnvironmentsReducer.MarkUnknown(m, "dev-1");
        Assert.Null(after.Find("dev-1", "shared-id"));
        Assert.NotNull(after.Find("dev-2", "shared-id"));
    }

    [Fact]
    public void The_mirror_has_no_place_for_a_credential()
    {
        var state = EnvironmentsReducer.ApplyTopic(EnvironmentsMirror.Empty, Dev, Topic(1, Ssh("a"))).Of(Dev).Find("a")!;
        // 只有「有没有」
        Assert.True(state.HasSavedPassword);
        Assert.True(state.Target!.HasIdentityFile);
        Assert.DoesNotContain("pw", state.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
