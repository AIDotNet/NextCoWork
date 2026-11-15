using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary><c>extensions</c> 修订主题的线上形状:往返、冻结向量(<see cref="GoldenV1Extensions"/>)、主题声明。</summary>
public class ExtensionsTopicTests
{
    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    [Fact]
    public void The_revision_state_round_trips_and_matches_the_frozen_vector()
    {
        var state = new ExtensionsTopicState(7, 3, 12);
        var shape = PayloadShapes.GetShape<ExtensionsTopicState>();

        Assert.Equal(state, FrameCodec.DecodePayload(FrameCodec.EncodePayload(state, shape), shape));
        Assert.Equal(GoldenV1Extensions.TopicState, Hex(state));
    }

    [Fact]
    public void The_topic_is_named_extensions_and_open_to_viewers_on_every_transport()
    {
        Assert.Equal("extensions", ExtensionsMethods.Topic.Name);
        Assert.Equal(Role.Viewer, ExtensionsMethods.Topic.MinRole);
        Assert.Equal(Transports.Any, ExtensionsMethods.Topic.Allowed);
        Assert.Equal("extensions.changed", ExtensionsEvents.Changed);
    }
}
