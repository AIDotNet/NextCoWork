using Ncw.Client.Transcript;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>「一轮」的边界:可见提问的判定、引出一条消息的提问、一轮里 AI 回复的范围。</summary>
public class MessageTurnsTests
{
    private static MessageDto Q(string id, string text = "q") => new(id, "user", 1, false, $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]");

    private static MessageDto A(string id, string text = "a") => new(id, "assistant", 1, false, $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]");

    /// <summary>只有工具调用的助手消息(没有文字,转录里没有助手文字行)。</summary>
    private static MessageDto Call(string id) => new(id, "assistant", 1, false, "[{\"type\":\"tool_call\",\"callId\":\"c\",\"name\":\"Write\",\"input\":{}}]");

    private static MessageDto Receipt(string id) => new(id, "user", 1, false, "[{\"type\":\"tool_result\",\"callId\":\"c\",\"output\":{\"content\":\"ok\"},\"isError\":false}]");

    [Fact]
    public void A_visible_question_has_text_files_or_images_and_is_not_internal()
    {
        Assert.True(MessageTurns.IsQuestion(Q("u")));
        Assert.True(MessageTurns.IsQuestion(new MessageDto("u", "user", 1, false, "[{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://x\"}]")));
        Assert.True(MessageTurns.IsQuestion(new MessageDto("u", "user", 1, false, "[{\"type\":\"file_ref\",\"path\":\"a\",\"name\":\"a\"}]")));
        Assert.False(MessageTurns.IsQuestion(new MessageDto("u", "user", 1, true, "[{\"type\":\"text\",\"text\":\"compaction boundary\"}]"))); // 内部消息
        Assert.False(MessageTurns.IsQuestion(new MessageDto("u", "user", 1, false, "[{\"type\":\"tool_result\",\"callId\":\"c\",\"output\":{},\"isError\":false}]"))); // 只有工具回执
        Assert.False(MessageTurns.IsQuestion(A("a")));
        Assert.False(MessageTurns.IsQuestion(new MessageDto("u", "user", 1, false, "not json")));
    }

    [Fact]
    public void The_question_of_a_reply_is_the_nearest_visible_question_before_it()
    {
        var history = new[] { Q("q1"), A("a1"), Q("q2"), Call("a2"), Receipt("t2"), A("a3") };

        Assert.Equal("q1", MessageTurns.QuestionOf(history, "a1"));
        Assert.Equal("q2", MessageTurns.QuestionOf(history, "a2"));
        Assert.Equal("q2", MessageTurns.QuestionOf(history, "t2")); // 工具回执也属于这一轮
        Assert.Equal("q2", MessageTurns.QuestionOf(history, "a3"));
        Assert.Equal("q2", MessageTurns.QuestionOf(history, "q2")); // 提问自己
        Assert.Null(MessageTurns.QuestionOf(history, "nope"));
    }

    [Fact]
    public void A_reply_before_any_question_belongs_to_no_turn()
    {
        Assert.Null(MessageTurns.QuestionOf([A("a0"), Q("q1")], "a0"));
    }

    [Fact]
    public void The_reply_of_a_turn_spans_every_assistant_message_between_two_questions()
    {
        var history = new[] { Q("q1"), A("a1", "first"), Q("q2"), A("a2", "let me look"), Call("a3"), Receipt("t3"), A("a4", "done"), Q("q3"), A("a5") };

        // 点第一轮的任何一段 → 第一轮的全部(这里只有一条)
        Assert.Equal(("a1", "a1"), MessageTurns.ReplyOf(history, "a1"));
        // 点第二轮里的任何一段(文字 / 只有工具调用的 / 回执 / 最后一段)→ 第二轮 AI 的全部回复:第一条到最后一条助手消息
        foreach (var id in new[] { "a2", "a3", "t3", "a4" })
        {
            Assert.Equal(("a2", "a4"), MessageTurns.ReplyOf(history, id));
        }

        Assert.Equal(("a5", "a5"), MessageTurns.ReplyOf(history, "a5"));
    }

    [Fact]
    public void A_question_an_unknown_message_or_a_turn_without_a_reply_has_no_reply_range()
    {
        var history = new[] { Q("q1"), Q("q2"), A("a2") };
        Assert.Null(MessageTurns.ReplyOf(history, "q1")); // 提问不是回复
        Assert.Null(MessageTurns.ReplyOf(history, "nope"));
        Assert.Null(MessageTurns.ReplyOf([Q("q"), Receipt("t")], "t")); // 这一轮没有助手消息
    }

    [Fact]
    public void Internal_user_messages_do_not_split_a_reply()
    {
        // 压缩边界 / 后台汇报是内部「用户」消息,不是可见提问:不切断一轮
        var boundary = new MessageDto("b", "user", 1, true, "[{\"type\":\"text\",\"text\":\"summary\"}]");
        var history = new[] { Q("q1"), A("a1"), boundary, A("a2") };

        Assert.Equal(("a1", "a2"), MessageTurns.ReplyOf(history, "a1"));
        Assert.Equal("q1", MessageTurns.QuestionOf(history, "a2"));
    }
}
