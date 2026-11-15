using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary><c>extensions</c> 主题的修订号镜像:key 带 deviceId、逐域取最大、重放的旧事件不改回去、断开标未知。</summary>
public sealed class ExtensionRevisionsTests
{
    [Fact]
    public void Revisions_are_kept_per_device_and_only_ever_move_forward_per_domain()
    {
        var mirror = ExtensionRevisionsMirror.Empty;
        mirror = ExtensionRevisionsReducer.Apply(mirror, "dev-a", new ExtensionsTopicState(3, 1, 0));
        mirror = ExtensionRevisionsReducer.Apply(mirror, "dev-b", new ExtensionsTopicState(9, 9, 9));
        // 重放的旧事件:Skills 回到 2、Hooks 前进到 4 —— 逐域取最大
        mirror = ExtensionRevisionsReducer.Apply(mirror, "dev-a", new ExtensionsTopicState(2, 1, 4));

        Assert.Equal(new DeviceExtensionRevisions(true, 3, 1, 4), mirror.Of("dev-a"));
        Assert.Equal(new DeviceExtensionRevisions(true, 9, 9, 9), mirror.Of("dev-b"));
        Assert.False(mirror.Of("dev-c").Known);
    }

    [Fact]
    public void A_disconnect_marks_the_device_unknown_and_the_next_snapshot_brings_it_back()
    {
        var mirror = ExtensionRevisionsReducer.Apply(ExtensionRevisionsMirror.Empty, "dev-a", new ExtensionsTopicState(1, 1, 1));

        mirror = ExtensionRevisionsReducer.MarkUnknown(mirror, "dev-a");
        Assert.False(mirror.Of("dev-a").Known);
        Assert.Equal(1, mirror.Of("dev-a").Skills);

        mirror = ExtensionRevisionsReducer.Apply(mirror, "dev-a", new ExtensionsTopicState(1, 2, 1));
        Assert.Equal(new DeviceExtensionRevisions(true, 1, 2, 1), mirror.Of("dev-a"));
        Assert.Same(mirror, ExtensionRevisionsReducer.MarkUnknown(mirror, "nobody"));
    }
}
