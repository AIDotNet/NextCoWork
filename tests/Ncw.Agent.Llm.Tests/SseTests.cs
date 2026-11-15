using System.Text;
using Ncw.Agent.Llm.Sse;
using Ncw.Agent.Llm.Tests.Support;

namespace Ncw.Agent.Llm.Tests;

/// <summary>SSE 解析(01-llm §9):去 BOM、末尾孤立的 \r 保留到下一块、UTF-8 解码必须是流式的。移植自 2.x sse.test.ts。</summary>
public sealed class SseTests
{
    private static List<SseEvent> Parse(params string[] chunks)
    {
        var parser = new SseParser();
        var events = new List<SseEvent>();
        foreach (var chunk in chunks)
        {
            events.AddRange(parser.Feed(chunk));
        }

        events.AddRange(parser.Flush());
        return events;
    }

    [Fact]
    public void Parses_a_basic_event()
    {
        var events = Parse("event: ping\ndata: {\"a\":1}\n\n");

        var ev = Assert.Single(events);
        Assert.Equal("ping", ev.Event);
        Assert.Equal("{\"a\":1}", ev.Data);
    }

    [Fact]
    public void Default_event_name_is_message()
    {
        Assert.Equal("message", Assert.Single(Parse("data: hi\n\n")).Event);
    }

    [Fact]
    public void Any_split_position_gives_the_same_result()
    {
        const string stream = "event: a\ndata: one\n\n: comment\r\nevent: b\r\ndata: two\r\ndata: three\r\n\r\nid: 7\ndata: four\n\n";
        var expected = Parse(stream);

        for (var i = 0; i <= stream.Length; i++)
        {
            var actual = Parse(stream[..i], stream[i..]);
            Assert.Equal(expected, actual);
        }

        for (var i = 0; i < stream.Length; i++)
        {
            Assert.Equal(expected, Parse(stream.Select(c => c.ToString()).ToArray()));
        }
    }

    [Fact]
    public void A_crlf_split_across_chunks_does_not_make_a_ghost_event()
    {
        // 末尾孤立的 \r 可能是被切开的 \r\n 的前半:当成行尾的话下一块的 \n 会被读成空行,凭空劈出一个空事件。
        var events = Parse("data: x\r", "\n\r", "\n");

        var ev = Assert.Single(events);
        Assert.Equal("x", ev.Data);
    }

    [Fact]
    public void All_three_line_endings_are_recognised()
    {
        Assert.Equal(["a", "b", "c"], Parse("data: a\n\ndata: b\r\n\r\ndata: c\r\r").Select(e => e.Data));
    }

    [Fact]
    public void A_bom_at_the_very_start_is_stripped_but_not_later_ones()
    {
        // 不削掉 BOM,第一个字段名会变成 "﻿event",整个第一个事件(message_start)被当成未知字段丢掉。
        var first = Assert.Single(Parse("﻿event: message_start\ndata: {}\n\n"));
        Assert.Equal("message_start", first.Event);

        var body = Assert.Single(Parse("data: a﻿b\n\n"));
        Assert.Equal("a﻿b", body.Data);
    }

    [Fact]
    public void Exactly_one_space_after_the_colon_is_removed()
    {
        Assert.Equal("  x", Assert.Single(Parse("data:   x\n\n")).Data);
        Assert.Equal("x", Assert.Single(Parse("data:x\n\n")).Data);
    }

    [Fact]
    public void Comment_lines_are_heartbeats_and_ignored()
    {
        Assert.Equal("x", Assert.Single(Parse(": keep-alive\ndata: x\n\n")).Data);
    }

    [Fact]
    public void Multi_line_data_is_joined_with_newlines()
    {
        Assert.Equal("a\nb", Assert.Single(Parse("data: a\ndata: b\n\n")).Data);
    }

    [Fact]
    public void An_event_without_data_is_dropped_and_does_not_pollute_the_next()
    {
        var events = Parse("event: orphan\n\ndata: x\n\n");

        var ev = Assert.Single(events);
        Assert.Equal("message", ev.Event);
    }

    [Fact]
    public void Id_persists_across_events_and_an_id_with_nul_is_ignored()
    {
        var events = Parse("id: 5\ndata: a\n\ndata: b\n\nid: bad\0id\ndata: c\n\n");

        Assert.Equal(["5", "5", "5"], events.Select(e => e.Id));
    }

    [Fact]
    public void Flush_hands_over_a_trailing_event_that_has_no_blank_line()
    {
        // 上游经常在最后的 data: [DONE] 后不带空行就关连接:交出来,让「少发一个空行」不至于变成「最后一个事件丢了」。
        var events = Parse("data: a\n\ndata: [DONE]");

        Assert.Equal(["a", "[DONE]"], events.Select(e => e.Data));
    }

    [Fact]
    public void Nothing_is_left_after_a_flush()
    {
        var parser = new SseParser();
        parser.Feed("data: x");
        Assert.Single(parser.Flush());
        Assert.Empty(parser.Flush());
    }

    [Fact]
    public async Task Multibyte_utf8_characters_split_across_chunks_do_not_become_replacement_characters()
    {
        // 中文是 3 字节,逐块解码会在接缝处产生 U+FFFD,而它出现的位置取决于网络分片,永远复现不了。
        var text = "data: 你好,世界🙂\n\n";
        var bytes = Encoding.UTF8.GetBytes(text);

        foreach (var size in new[] { 1, 2, 3, 5 })
        {
            var events = await Decoding.CollectAsync(SseReader.ReadAsync(new ChunkedStream(bytes, size)));

            Assert.Equal("你好,世界🙂", Assert.Single(events).Data);
        }
    }

    [Fact]
    public async Task Reader_handles_a_bom_split_from_the_first_line()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("event: x\ndata: 1\n\n")).ToArray();

        var events = await Decoding.CollectAsync(SseReader.ReadAsync(new ChunkedStream(bytes, 1)));

        Assert.Equal("x", Assert.Single(events).Event);
    }

    [Fact]
    public async Task A_json_response_is_delivered_as_one_message_event()
    {
        var body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"x\"}"));

        var events = await Decoding.CollectAsync(SseReader.ReadResponseAsync(body, "application/json; charset=utf-8"));

        var ev = Assert.Single(events);
        Assert.Equal("{\"id\":\"x\"}", ev.Data);
    }
}
