using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>生成媒体的回执从实时事件和会话历史派生,且完成后不藏进工作区。</summary>
public class GeneratedMediaTests
{
    private const string Device = "local";
    private const string SessionId = "s1";
    private const string ImageUrl = "ncw://attachments/sessions/s1/output.png";
    private static readonly SessionRef Session = new(Device, SessionId);

    [Fact]
    public void A_live_image_has_no_preview_until_tool_end()
    {
        var state = Snapshot();
        state = ConversationReducer.ApplyRunEvent(state, Device, "run-1", 1, new RunStartEventDto("run-1", SessionId, "model"));
        state = ConversationReducer.ApplyRunEvent(state, Device, "run-1", 2, new ToolStartEventDto("media", "generate_image", "{}"));
        var running = Assert.Single(Build(state).OfType<ToolRow>());
        Assert.Equal(ToolRowStatus.Running, running.Status);
        Assert.Equal(GeneratedMediaKind.Image, running.MediaKind);
        Assert.Null(running.Media);

        state = ConversationReducer.ApplyRunEvent(state, Device, "run-1", 3,
            new ToolEndEventDto("media", "ok", null, false, null, Receipt("image", "image/png", ImageUrl), false, 100));
        var finished = Assert.Single(Build(state).OfType<ToolRow>());

        Assert.Equal(running.Key, finished.Key);
        Assert.Equal(ToolRowStatus.Done, finished.Status);
        Assert.Equal(GeneratedMediaKind.Image, finished.MediaKind);
        Assert.Equal(new GeneratedMedia(ImageUrl, "image/png"), finished.Media);
    }

    [Theory]
    [InlineData("image", "image/png", "output.png", GeneratedMediaKind.Image)]
    [InlineData("video", "video/mp4", "output.mp4", GeneratedMediaKind.Video)]
    [InlineData("audio", "audio/mpeg", "output.mp3", GeneratedMediaKind.Audio)]
    public void A_history_snapshot_restores_generated_media_without_a_live_run(string mediaKind, string mime, string file, GeneratedMediaKind expected)
    {
        var url = $"ncw://attachments/sessions/s1/{file}";
        var state = Snapshot(Message("a1", "assistant", false, Call("media", $"generate_{mediaKind}")),
            Message("r1", "user", true, Result("media", Receipt(mediaKind, mime, url))));

        var row = Assert.Single(Build(state).OfType<ToolRow>());

        Assert.Equal(ToolRowStatus.Done, row.Status);
        Assert.Equal(expected, row.MediaKind);
        Assert.Equal(new GeneratedMedia(url, mime), row.Media);
    }

    [Theory]
    [InlineData("image", "image/png", "https://provider.example/output.png")]
    [InlineData("video", "image/png", ImageUrl)]
    [InlineData("image", "video/mp4", ImageUrl)]
    [InlineData("image", "image/png", "ncw://attachments/themes/output.png")]
    [InlineData("image", "image/png", ImageUrl + "?token=secret")]
    public void Untrusted_receipts_do_not_become_preview_sources(string mediaKind, string mime, string url)
    {
        var state = Snapshot(Message("a1", "assistant", false, Call("media", "generate_image")),
            Message("r1", "user", true, Result("media", Receipt(mediaKind, mime, url))));

        var row = Assert.Single(Build(state).OfType<ToolRow>());

        Assert.Equal(ToolRowStatus.Done, row.Status);
        Assert.Equal(GeneratedMediaKind.Image, row.MediaKind);
        Assert.Null(row.Media);
    }

    [Fact]
    public void A_finished_turn_groups_ordinary_tools_but_keeps_generated_media_outside_the_group()
    {
        var state = Snapshot(
            Message("u1", "user", false, """{"type":"text","text":"Make an image"}"""),
            Message("a1", "assistant", false, Call("read", "Read"), Call("media", "generate_image")),
            Message("r1", "user", true, Result("read", "{}"), Result("media", Receipt("image", "image/png", ImageUrl))),
            Message("a2", "assistant", false, """{"type":"text","text":"Here it is"}"""));

        var rows = Build(state, groupFinishedWork: true);

        Assert.Collection(rows,
            row => Assert.IsType<UserRow>(row),
            row => Assert.Equal(1, Assert.IsType<WorkGroupRow>(row).Members),
            row => Assert.Equal("read", Assert.IsType<ToolRow>(row).CallId),
            row => Assert.Equal(new GeneratedMedia(ImageUrl, "image/png"), Assert.IsType<ToolRow>(row).Media),
            row => Assert.IsType<AssistantRow>(row));
    }

    private static ImmutableArray<TranscriptRow> Build(ConversationState state, bool groupFinishedWork = false) =>
        TranscriptBuilder.Build(state, Session, null, DateTimeOffset.UnixEpoch, groupFinishedWork: groupFinishedWork);

    private static ConversationState Snapshot(params MessageDto[] messages)
    {
        var meta = new SessionDto(SessionId, "project", null, "user", "title", null, "model", null, "code", "auto", false, false, 1, 2);
        return ConversationReducer.ApplySession(ConversationState.Empty, Device,
            new SessionTopicState(new SessionPageDto(meta, messages, false, [], [], []), new QueueDto(SessionId, 1, []), null));
    }

    private static MessageDto Message(string id, string role, bool isInternal, params string[] parts) =>
        new(id, role, 1, isInternal, "[" + string.Join(',', parts) + "]");

    private static string Call(string id, string name) =>
        $$$"""{"type":"tool_call","callId":"{{{id}}}","name":"{{{name}}}","input":{}}""";

    private static string Result(string id, string details) =>
        $$$"""{"type":"tool_result","callId":"{{{id}}}","output":{"content":"ok","details":{{{details}}}},"isError":false}""";

    private static string Receipt(string kind, string mime, string url) =>
        $$$"""{"kind":"media-generation","mediaKind":"{{{kind}}}","mime":"{{{mime}}}","url":"{{{url}}}"}""";
}
